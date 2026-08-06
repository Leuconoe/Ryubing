using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Ryujinx.Ava.Systems.Control
{
    internal sealed class ControlBridgeLogTarget : ILogTarget
    {
        private const int Capacity = 10000;
        private readonly Lock _lock = new();
        private readonly Queue<ControlBridgeLogEntry> _entries = new(Capacity);
        private long _cursor;

        public string Name => "control-bridge";

        public void Log(object sender, LogEventArgs args)
        {
            lock (_lock)
            {
                ControlBridgeLogEntry entry = new(
                    Cursor: (++_cursor).ToString(CultureInfo.InvariantCulture),
                    Timestamp: DateTimeOffset.UtcNow,
                    Level: MapLevel(args.Level),
                    Category: args.Data?.ToString(),
                    Message: args.Message);

                _entries.Enqueue(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }
        }

        public ControlBridgeLogsResult Read(long? cursor, int limit, string minimumLevel)
        {
            int minimumSeverity = Severity(minimumLevel);

            lock (_lock)
            {
                long oldestCursor = _entries.TryPeek(out ControlBridgeLogEntry oldest)
                    ? long.Parse(oldest.Cursor, CultureInfo.InvariantCulture)
                    : _cursor + 1;
                bool truncated = cursor.HasValue && cursor.Value < oldestCursor - 1;

                ControlBridgeLogEntry[] result = _entries
                    .Where(entry => long.Parse(entry.Cursor, CultureInfo.InvariantCulture) > (cursor ?? 0))
                    .Where(entry => Severity(entry.Level) >= minimumSeverity)
                    .Take(limit)
                    .ToArray();

                string nextCursor = result.Length == limit
                    ? result[^1].Cursor
                    : _cursor.ToString(CultureInfo.InvariantCulture);

                return new ControlBridgeLogsResult(result, nextCursor, truncated);
            }
        }

        public void Dispose()
        {
        }

        private static string MapLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug or LogLevel.Stub => "debug",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Notice => "notice",
            _ => "info",
        };

        private static int Severity(string level) => level switch
        {
            "trace" => 0,
            "debug" => 1,
            "info" => 2,
            "notice" => 3,
            "warning" => 4,
            "error" => 5,
            "critical" => 6,
            _ => throw new ArgumentException($"Unknown log level: {level}"),
        };
    }

    internal sealed record ControlBridgeLogEntry(
        string Cursor,
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        string Message);

    internal sealed record ControlBridgeLogsResult(
        ControlBridgeLogEntry[] Entries,
        string NextCursor,
        bool Truncated);
}
