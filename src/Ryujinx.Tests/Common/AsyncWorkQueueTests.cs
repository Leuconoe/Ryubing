using NUnit.Framework;
using Ryujinx.Common;

namespace Ryujinx.Tests.Common
{
    public class AsyncWorkQueueTests
    {
        [Test]
        public void DisposeIsIdempotent()
        {
            AsyncWorkQueue<int> queue = new(_ => { }, "AsyncWorkQueueTests");

            Assert.DoesNotThrow(queue.Dispose);
            Assert.DoesNotThrow(queue.Dispose);
        }
    }
}
