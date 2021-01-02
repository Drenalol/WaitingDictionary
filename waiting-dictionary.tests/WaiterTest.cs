using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Drenalol.WaitingDictionary.Tests
{
    public class Test
    {
        [Test]
        public async Task NormalTest()
        {
            const int key = 1337;
            using var waiters = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<int, Mock>()
                    .AddCompletionActionInSet(() => TestContext.WriteLine("Set completed"))
                    .AddCompletionActionInWait(() => TestContext.WriteLine("Wait completed"))
            );
            var waitTask = waiters.WaitAsync(key);
            Assert.IsTrue(!waitTask.IsCompletedSuccessfully);
            var mock = new Mock();
            await waiters.SetAsync(key, mock);
            Assert.IsTrue(waitTask.IsCompletedSuccessfully);
        }

        [Test]
        public async Task DuplicateTest()
        {
            const int key = 1337;
            using var waiters = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<int, Mock>()
                    .AddDuplicateActionInSet(oldMock => new Mock(oldMock))
            );
            var mock = new Mock();
            await waiters.SetAsync(key, mock);
            await waiters.SetAsync(key, mock);
            var waitMock = await waiters.WaitAsync(key);
            Assert.IsNotEmpty(waitMock.Nodes);
        }

        [Test]
        public async Task DuplicateErrorTest()
        {
            const int key = 1337;
            var waiters = new WaitingDictionary<int, Mock>();
            var mock = new Mock();
            await waiters.SetAsync(key, mock);
            Assert.CatchAsync<InvalidOperationException>(() => waiters.SetAsync(key, mock));
        }

        [TestCase(typeof(OperationCanceledException))]
        [TestCase(typeof(InvalidCastException))]
        [TestCase(typeof(AggregateException))]
        public async Task CancelTest(Type type)
        {
            const int key = 1337;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var waiters = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<int, Mock>()
                    .RegisterCancellationActionInWait(tcs => tcs.SetException((Exception) Activator.CreateInstance(type)))
            );
            var waitTask = waiters.WaitAsync(key, cts.Token);
            await Task.Delay(2000, CancellationToken.None);
            
            try
            {
                await waitTask;
            }
            catch (Exception e)
            {
                Assert.IsTrue(e.GetType() == type);
            }
        }
        
        [Test]
        public async Task MultipleWaitTest()
        {
            const int key = 1337;
            using var waiters = new WaitingDictionary<int, Mock>();
            var waitTask1 = waiters.WaitAsync(key);
            var waitTask2 = waiters.WaitAsync(key);
            Assert.IsTrue(!waitTask1.IsCompletedSuccessfully);
            Assert.IsTrue(!waitTask2.IsCompletedSuccessfully);
            await waiters.SetAsync(key, new Mock());
            await Task.Delay(200);
            Assert.IsTrue(waitTask1.IsCompletedSuccessfully);
            Assert.IsTrue(waitTask2.IsFaulted);
        }
    }

    public class Mock
    {
        public List<Mock> Nodes { get; }

        public Mock(Mock mock)
        {
            (Nodes ??= new List<Mock>()).Add(mock);
        }

        public Mock()
        {
        }
    }
}