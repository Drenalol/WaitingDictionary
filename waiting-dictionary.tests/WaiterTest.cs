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
            using var dictionary = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<Mock>()
                    .RegisterCompletionActionInSet(() => TestContext.WriteLine("Set completed"))
                    .RegisterCompletionActionInWait(() => TestContext.WriteLine("Wait completed"))
            );
            var waitTask = dictionary.WaitAsync(key);
            Assert.IsTrue(!waitTask.IsCompletedSuccessfully);
            var mock = new Mock();
            await dictionary.SetAsync(key, mock);
            Assert.IsTrue(waitTask.IsCompletedSuccessfully);
        }

        [Test]
        public async Task DuplicateTest()
        {
            const int key = 1337;
            using var dictionary = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<Mock>()
                    .RegisterDuplicateActionInSet((old, @new) => new Mock(old, @new))
            );
            var mock = new Mock();
            await dictionary.SetAsync(key, mock);
            await dictionary.SetAsync(key, mock);
            var waitMock = await dictionary.WaitAsync(key);
            Assert.IsNotEmpty(waitMock.Nodes);
        }

        [Test]
        public async Task DuplicateErrorTest()
        {
            const int key = 1337;
            var dictionary = new WaitingDictionary<int, Mock>();
            var mock = new Mock();
            await dictionary.SetAsync(key, mock);
            Assert.CatchAsync<InvalidOperationException>(() => dictionary.SetAsync(key, mock));
        }

        [TestCase(typeof(OperationCanceledException))]
        [TestCase(typeof(InvalidCastException))]
        [TestCase(typeof(AggregateException))]
        public async Task CancelTest(Type type)
        {
            const int key = 1337;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var dictionary = new WaitingDictionary<int, Mock>(
                new MiddlewareBuilder<Mock>()
                    .RegisterCancellationActionInWait((tcs, hasOwnToken) => tcs.SetException((Exception) Activator.CreateInstance(type)))
            );
            var waitTask = dictionary.WaitAsync(key, cts.Token);
            await Task.Delay(200, CancellationToken.None);

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
            using var dictionary = new WaitingDictionary<int, Mock>();
            var waitTask1 = dictionary.WaitAsync(key);
            var waitTask2 = dictionary.WaitAsync(key);
            Assert.IsTrue(!waitTask1.IsCompletedSuccessfully);
            Assert.IsTrue(!waitTask2.IsCompletedSuccessfully);
            await dictionary.SetAsync(key, new Mock());
            await Task.Delay(200);
            Assert.IsTrue(waitTask1.IsCompletedSuccessfully);
            Assert.IsTrue(waitTask2.IsFaulted);
        }

        [Test]
        public void InterfacesTest()
        {
            using var dictionary = new WaitingDictionary<int, Mock>();
            Assert.Catch<NotSupportedException>(() => dictionary.Add(new KeyValuePair<int, TaskCompletionSource<Mock>>()));
            Assert.Catch<NotSupportedException>(() => dictionary.Add(123, new TaskCompletionSource<Mock>()));
            Assert.Catch<NotSupportedException>(() => ((ICollection<KeyValuePair<int, TaskCompletionSource<Mock>>>) dictionary).Remove(new KeyValuePair<int, TaskCompletionSource<Mock>>()));
            Assert.Catch<NotSupportedException>(() => ((IDictionary<int, TaskCompletionSource<Mock>>) dictionary).Remove(123));
            Assert.Catch<NotSupportedException>(() => dictionary[123] = new TaskCompletionSource<Mock>());
            Assert.Catch<NotSupportedException>(() =>
            {
                using var _ = new WaitingDictionary<int, Mock>
                {
                    {123, new TaskCompletionSource<Mock>()}
                };
            });
            Assert.IsNull(((IDictionary<int, TaskCompletionSource<Mock>>) dictionary).TryGetValue(123, out var test) ? test : null);
            Assert.IsNull(dictionary[123]);
        }

        [Test]
        public void EnumerateTest()
        {
            using var dictionary = new WaitingDictionary<int, Mock>();
            dictionary.SetAsync(1, new Mock());
            dictionary.SetAsync(2, new Mock());

            var idx = 0;
            foreach (var taskCompletionSource in dictionary)
            {
                Assert.NotNull(taskCompletionSource);
                idx++;
            }

            Assert.IsNotEmpty(dictionary);
            Assert.Greater(dictionary.Count, 1);
            Assert.Greater(idx, 1);
        }

        [Test]
        public async Task RemoveTest()
        {
            using var dictionary = new WaitingDictionary<int, Mock>();
            await dictionary.SetAsync(1, new Mock());
            Assert.IsTrue(await dictionary.TryRemoveAsync(1));
        }
        
        [Test]
        public async Task FilterTest()
        {
            using var dictionary = new WaitingDictionary<int, Mock>();
            await dictionary.SetAsync(1, new Mock());
            Assert.IsEmpty(dictionary.Filter(tcs => tcs.Value.Task.Status == TaskStatus.Canceled));
        }
    }

    public class Mock
    {
        public List<Mock> Nodes { get; }

        public Mock(params Mock[] mock)
        {
            (Nodes ??= new List<Mock>()).AddRange(mock);
        }

        public Mock()
        {
        }
    }
}