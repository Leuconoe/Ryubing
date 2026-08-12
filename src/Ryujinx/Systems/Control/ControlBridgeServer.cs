using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using SkiaSharp;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems.Control
{
    internal sealed class ControlBridgeServer : IDisposable
    {
        private const int MaximumRequestLength = 1024 * 1024;
        private const string ProtocolVersion = "0.1";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly TcpListener _listener;
        private readonly byte[] _tokenHash;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Task _acceptTask;

        public ControlBridgeLogTarget LogTarget { get; } = new();

        public ControlBridgeServer(int port, string token)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }

        public void Start()
        {
            _listener.Start();
            _acceptTask = Task.Run(() => AcceptLoop(_cancellationTokenSource.Token));
        }

        private async Task AcceptLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = Task.Run(() => HandleClient(client, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Application, $"Control bridge listener failed: {exception.Message}");
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new(stream, Encoding.UTF8, false, leaveOpen: true))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            {
                client.NoDelay = true;

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync(cancellationToken);
                        if (line is null)
                        {
                            return;
                        }

                        if (line.Length > MaximumRequestLength)
                        {
                            await WriteError(writer, null, -32600, "Request is too large");
                            return;
                        }

                        await HandleLine(writer, line);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                    await WriteError(writer, null, -32700, "Parse error");
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Control bridge client failed: {exception.Message}");
                }
                finally
                {
                    try
                    {
                        AppHost appHost = await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                                lifetime.MainWindow is MainWindow mainWindow)
                            {
                                return mainWindow.ViewModel.AppHost;
                            }

                            return null;
                        });
                        appHost?.NpadManager.ClearInjectedInputs();
                        appHost?.ClearControlTouch();
                    }
                    catch
                    {
                        // The UI may already be shutting down.
                    }
                }
            }
        }

        private async Task HandleLine(StreamWriter writer, string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            JsonElement? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.Clone() : null;

            if (!IsAuthorized(root))
            {
                await WriteError(writer, id, -32001, "Unauthorized");
                return;
            }

            if (!root.TryGetProperty("jsonrpc", out JsonElement version) || version.GetString() != "2.0" ||
                !root.TryGetProperty("method", out JsonElement methodElement))
            {
                await WriteError(writer, id, -32600, "Invalid request");
                return;
            }

            string method = methodElement.GetString();
            JsonElement parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement
                : default;

            try
            {
                object result = method switch
                {
                    "control.status" => await GetStatus(),
                    "input.press" => await PressButtons(parameters),
                    "input.setSticks" => await SetSticks(parameters),
                    "touch.gesture" => await SendTouchGesture(parameters),
                    "screen.capture" => await CaptureScreenshot(parameters),
                    "logs.read" => ReadLogs(parameters),
                    "game.launch" => await LaunchGame(parameters),
                    _ => throw new ControlBridgeException(-32601, $"Method not found: {method}"),
                };

                await WriteResult(writer, id, result);
            }
            catch (ControlBridgeException exception)
            {
                await WriteError(writer, id, exception.Code, exception.Message);
            }
            catch (ArgumentException exception)
            {
                await WriteError(writer, id, -32602, exception.Message);
            }
        }

        private bool IsAuthorized(JsonElement root)
        {
            if (!root.TryGetProperty("auth", out JsonElement auth) ||
                !auth.TryGetProperty("token", out JsonElement tokenElement) ||
                tokenElement.GetString() is not string token)
            {
                return false;
            }

            byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return CryptographicOperations.FixedTimeEquals(_tokenHash, candidateHash);
        }

        private static async Task<object> GetStatus()
        {
            return await Dispatcher.UIThread.InvokeAsync<object>(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime ||
                    lifetime.MainWindow is not MainWindow mainWindow ||
                    mainWindow.ViewModel.AppHost is not AppHost appHost)
                {
                    return new
                    {
                        ProtocolVersion,
                        EmulatorVersion = Program.Version,
                        ProcessId = Environment.ProcessId,
                        State = "idle",
                    };
                }

                var activeApplication = appHost.Device?.Processes.ActiveApplication;
                string state = activeApplication is null
                    ? "loading"
                    : mainWindow.ViewModel.IsPaused ? "paused" : "running";

                return new
                {
                    ProtocolVersion,
                    EmulatorVersion = Program.Version,
                    ProcessId = Environment.ProcessId,
                    State = state,
                    TitleId = activeApplication?.ProgramIdText,
                    TitleName = activeApplication?.Name,
                    FrameWidth = appHost.Width > 0 ? (int?)appHost.Width : null,
                    FrameHeight = appHost.Height > 0 ? (int?)appHost.Height : null,
                };
            });
        }

        private object ReadLogs(JsonElement parameters)
        {
            string cursorValue = GetOptionalString(parameters, "cursor");
            long? cursor = null;
            if (cursorValue is not null)
            {
                if (!long.TryParse(cursorValue, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedCursor) ||
                    parsedCursor < 0)
                {
                    throw new ArgumentException("cursor must be an unsigned integer string");
                }

                cursor = parsedCursor;
            }

            int limit = GetOptionalInt32(parameters, "limit") ?? 200;
            if (limit is < 1 or > 2000)
            {
                throw new ArgumentException("limit must be between 1 and 2000");
            }

            string minimumLevel = GetOptionalString(parameters, "minimumLevel") ?? "info";
            return LogTarget.Read(cursor, limit, minimumLevel);
        }

        private static async Task<object> PressButtons(JsonElement parameters)
        {
            AppHost appHost = await GetRunningAppHost();
            PlayerIndex player = GetPlayer(parameters);
            int durationMs = GetRequiredInt32(parameters, "durationMs");
            if (durationMs is < 16 or > 10000)
            {
                throw new ArgumentException("durationMs must be between 16 and 10000");
            }

            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("buttons", out JsonElement buttonsElement) ||
                buttonsElement.ValueKind != JsonValueKind.Array ||
                buttonsElement.GetArrayLength() == 0)
            {
                throw new ArgumentException("buttons must be a non-empty array");
            }

            ControllerKeys buttons = 0;
            foreach (JsonElement buttonElement in buttonsElement.EnumerateArray())
            {
                buttons |= MapButton(buttonElement.GetString());
            }

            appHost.NpadManager.PressInjectedButtons(player, buttons);
            try
            {
                await Task.Delay(durationMs);
            }
            finally
            {
                appHost.NpadManager.ReleaseInjectedButtons(player, buttons);
            }

            return new { Ok = true };
        }

        private static async Task<object> SetSticks(JsonElement parameters)
        {
            AppHost appHost = await GetRunningAppHost();
            PlayerIndex player = GetPlayer(parameters);
            float? leftX = GetOptionalSingle(parameters, "leftX");
            float? leftY = GetOptionalSingle(parameters, "leftY");
            float? rightX = GetOptionalSingle(parameters, "rightX");
            float? rightY = GetOptionalSingle(parameters, "rightY");
            int durationMs = GetOptionalInt32(parameters, "durationMs") ?? 0;

            if (!leftX.HasValue && !leftY.HasValue && !rightX.HasValue && !rightY.HasValue)
            {
                throw new ArgumentException("At least one stick coordinate is required");
            }
            if (durationMs is < 0 or > 60000)
            {
                throw new ArgumentException("durationMs must be between 0 and 60000");
            }

            foreach (float? value in new[] { leftX, leftY, rightX, rightY })
            {
                if (value is < -1 or > 1)
                {
                    throw new ArgumentException("Stick coordinates must be between -1 and 1");
                }
            }

            long generation = appHost.NpadManager.SetInjectedSticks(player, leftX, leftY, rightX, rightY);
            if (durationMs > 0)
            {
                await Task.Delay(durationMs);
                appHost.NpadManager.ClearInjectedSticks(player, generation);
            }

            return new { Ok = true };
        }

        private static async Task<object> SendTouchGesture(JsonElement parameters)
        {
            AppHost appHost = await GetRunningAppHost();
            string action = GetRequiredString(parameters, "action");
            int x = GetCoordinate(parameters, "x", 1279);
            int y = GetCoordinate(parameters, "y", 719);
            int durationMs = GetRequiredInt32(parameters, "durationMs");
            if (durationMs is < 16 or > 10000)
            {
                throw new ArgumentException("durationMs must be between 16 and 10000");
            }

            try
            {
                switch (action)
                {
                    case "tap":
                    case "long_press":
                        appHost.SetControlTouch(CreateTouchPoint(x, y, TouchAttribute.Start));
                        await Task.Delay(durationMs);
                        appHost.SetControlTouch(CreateTouchPoint(x, y, TouchAttribute.End));
                        await Task.Delay(20);
                        break;
                    case "swipe":
                        int endX = GetCoordinate(parameters, "endX", 1279);
                        int endY = GetCoordinate(parameters, "endY", 719);
                        const int steps = 10;
                        appHost.SetControlTouch(CreateTouchPoint(x, y, TouchAttribute.Start));
                        for (int step = 1; step < steps; step++)
                        {
                            await Task.Delay(Math.Max(1, durationMs / steps));
                            int currentX = x + ((endX - x) * step / steps);
                            int currentY = y + ((endY - y) * step / steps);
                            appHost.SetControlTouch(CreateTouchPoint(currentX, currentY, TouchAttribute.None));
                        }
                        appHost.SetControlTouch(CreateTouchPoint(endX, endY, TouchAttribute.End));
                        await Task.Delay(20);
                        break;
                    default:
                        throw new ArgumentException($"Unknown touch action: {action}");
                }
            }
            finally
            {
                appHost.ClearControlTouch();
            }

            return new { Ok = true };
        }

        private static async Task<object> CaptureScreenshot(JsonElement parameters)
        {
            AppHost appHost = await GetRunningAppHost();
            string format = GetOptionalString(parameters, "format") ?? "png";
            int quality = GetOptionalInt32(parameters, "quality") ?? 90;

            if (format is not ("png" or "jpeg"))
            {
                throw new ArgumentException("format must be png or jpeg");
            }
            if (quality is < 1 or > 100)
            {
                throw new ArgumentException("quality must be between 1 and 100");
            }
            if (!appHost.IsControlScreenshotReady)
            {
                throw new ControlBridgeException(-32002, "Renderer frame is not ready for capture");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                    lifetime.MainWindow is MainWindow mainWindow)
                {
                    if (mainWindow.WindowState == WindowState.Minimized)
                    {
                        mainWindow.WindowState = WindowState.Normal;
                    }
                    mainWindow.Activate();
                }
            });
            await Task.Delay(50);

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            ScreenCaptureImageInfo capture;
            try
            {
                capture = await appHost.CaptureControlScreenshot(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ControlBridgeException(-32007, "Screenshot capture timed out");
            }
            catch (InvalidOperationException exception)
            {
                throw new ControlBridgeException(-32002, exception.Message);
            }

            if (capture.Data.Length == 0 || capture.Width <= 0 || capture.Height <= 0)
            {
                throw new ControlBridgeException(-32007, "Renderer returned an empty screenshot");
            }

            SKColorType colorType = capture.IsBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
            using SKBitmap source = new(new SKImageInfo(capture.Width, capture.Height, colorType, SKAlphaType.Premul));
            Marshal.Copy(capture.Data, 0, source.GetPixels(), capture.Data.Length);

            using SKBitmap output = new(source.Width, source.Height);
            using (SKCanvas canvas = new(output))
            {
                canvas.Clear(SKColors.Black);
                SKMatrix matrix = SKMatrix.CreateScale(
                    capture.FlipX ? -1 : 1,
                    capture.FlipY ? -1 : 1,
                    source.Width / 2f,
                    source.Height / 2f);
                canvas.SetMatrix(matrix);
                canvas.DrawBitmap(source, SKPoint.Empty);
            }

            SKEncodedImageFormat encodedFormat = format == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            using SKData data = output.Encode(encodedFormat, format == "png" ? 100 : quality);

            return new
            {
                MimeType = format == "png" ? "image/png" : "image/jpeg",
                Data = Convert.ToBase64String(data.ToArray()),
                Width = output.Width,
                Height = output.Height,
            };
        }

        private static async Task<object> LaunchGame(JsonElement parameters)
        {
            string basePath = GetRequiredString(parameters, "basePath");
            string updatePath = GetOptionalString(parameters, "updatePath");

            if (!File.Exists(basePath) && !Directory.Exists(basePath))
            {
                throw new ControlBridgeException(-32005, $"Game path does not exist: {basePath}");
            }
            if (updatePath is not null && !File.Exists(updatePath))
            {
                throw new ControlBridgeException(-32005, $"Update path does not exist: {updatePath}");
            }

            TaskCompletionSource<object> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime ||
                        lifetime.MainWindow is not MainWindow mainWindow)
                    {
                        throw new ControlBridgeException(-32002, "Ryubing UI is not ready");
                    }

                    if (mainWindow.ViewModel.AppHost is not null)
                    {
                        throw new ControlBridgeException(-32003, "A game is already running");
                    }

                    if (!mainWindow.ApplicationLibrary.TryGetApplicationsFromFile(basePath, out List<ApplicationData> applications) ||
                        applications.Count == 0)
                    {
                        throw new ControlBridgeException(-32005, "No launchable application was found in the game file");
                    }

                    ApplicationData application = applications[0];
                    mainWindow.ViewModel.SelectedApplication = application;
                    await mainWindow.ViewModel.LoadApplication(application, customNacpData: null, sessionUpdatePath: updatePath);

                    if (mainWindow.ViewModel.AppHost is null)
                    {
                        throw new ControlBridgeException(-32005, "Ryubing could not start the application");
                    }

                    completion.TrySetResult(new
                    {
                        Accepted = true,
                        TitleId = application.IdString,
                        State = "loading",
                    });
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            return await completion.Task;
        }

        private static async Task<AppHost> GetRunningAppHost()
        {
            AppHost appHost = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                    lifetime.MainWindow is MainWindow mainWindow)
                {
                    return mainWindow.ViewModel.AppHost;
                }

                return null;
            });

            return appHost ?? throw new ControlBridgeException(-32004, "No game is running");
        }

        private static PlayerIndex GetPlayer(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("player", out JsonElement value))
            {
                return PlayerIndex.Player1;
            }

            if (value.ValueKind == JsonValueKind.String && value.GetString() == "handheld")
            {
                return PlayerIndex.Handheld;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int player) && player is >= 1 and <= 8)
            {
                return (PlayerIndex)(player - 1);
            }

            throw new ArgumentException("player must be 1 through 8 or handheld");
        }

        private static ControllerKeys MapButton(string button) => button switch
        {
            "A" => ControllerKeys.A,
            "B" => ControllerKeys.B,
            "X" => ControllerKeys.X,
            "Y" => ControllerKeys.Y,
            "L" => ControllerKeys.L,
            "R" => ControllerKeys.R,
            "ZL" => ControllerKeys.Zl,
            "ZR" => ControllerKeys.Zr,
            "PLUS" => ControllerKeys.Plus,
            "MINUS" => ControllerKeys.Minus,
            "L_STICK" => ControllerKeys.LStick,
            "R_STICK" => ControllerKeys.RStick,
            "DPAD_UP" => ControllerKeys.DpadUp,
            "DPAD_DOWN" => ControllerKeys.DpadDown,
            "DPAD_LEFT" => ControllerKeys.DpadLeft,
            "DPAD_RIGHT" => ControllerKeys.DpadRight,
            "SL_LEFT" => ControllerKeys.SlLeft,
            "SR_LEFT" => ControllerKeys.SrLeft,
            "SL_RIGHT" => ControllerKeys.SlRight,
            "SR_RIGHT" => ControllerKeys.SrRight,
            _ => throw new ArgumentException($"Unknown button: {button}"),
        };

        private static TouchPoint CreateTouchPoint(int x, int y, TouchAttribute attribute) => new()
        {
            Attribute = attribute,
            X = (uint)x,
            Y = (uint)y,
            DiameterX = 10,
            DiameterY = 10,
            Angle = 90,
        };

        private static int GetCoordinate(JsonElement parameters, string name, int maximum)
        {
            int value = GetRequiredInt32(parameters, name);
            if (value < 0 || value > maximum)
            {
                throw new ArgumentException($"{name} must be between 0 and {maximum}");
            }
            return value;
        }

        private static string GetRequiredString(JsonElement parameters, string name)
        {
            string value = GetOptionalString(parameters, name);
            return value ?? throw new ArgumentException($"{name} is required");
        }

        private static int GetRequiredInt32(JsonElement parameters, string name)
        {
            int? value = GetOptionalInt32(parameters, name);
            return value ?? throw new ArgumentException($"{name} is required");
        }

        private static string GetOptionalString(JsonElement parameters, string name)
        {
            return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value)
                ? value.GetString()
                : null;
        }

        private static int? GetOptionalInt32(JsonElement parameters, string name)
        {
            return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value)
                ? value.GetInt32()
                : null;
        }

        private static float? GetOptionalSingle(JsonElement parameters, string name)
        {
            return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value)
                ? value.GetSingle()
                : null;
        }

        private static Task WriteResult(StreamWriter writer, JsonElement? id, object result) =>
            writer.WriteLineAsync(JsonSerializer.Serialize(new { Jsonrpc = "2.0", Id = id, Result = result }, _jsonOptions));

        private static Task WriteError(StreamWriter writer, JsonElement? id, int code, string message) =>
            writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Jsonrpc = "2.0",
                Id = id,
                Error = new { Code = code, Message = message },
            }, _jsonOptions));

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _listener.Stop();
            _cancellationTokenSource.Dispose();
        }
    }

    internal sealed class ControlBridgeException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
