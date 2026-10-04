using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Drenalol.WaitingDictionary.Tests;

public class Test
{
    private const int Key = 1337;

    [Test]
    public async Task NormalTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>(
            new MiddlewareBuilder<Mock>()
                .RegisterCompletionActionInSet(() => TestContext.WriteLine("Set completed"))
                .RegisterCompletionActionInWait(() => TestContext.WriteLine("Wait completed"))
        );

        var mock = new Mock();
        var waitTask = dictionary.WaitAsync(Key);
        await dictionary.SetAsync(Key, mock);

        Assert.That(await waitTask, Is.SameAs(mock));
        Assert.That(dictionary.Count, Is.Zero, "Completed item should be removed automatically");
    }

    [Test]
    public async Task SetBeforeWaitTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var mock = new Mock();

        await dictionary.SetAsync(Key, mock);
        Assert.That(dictionary.ContainsKey(Key), Is.True, "Result should be stored until consumed");

        Assert.That(await dictionary.WaitAsync(Key), Is.SameAs(mock));
        Assert.That(dictionary.Count, Is.Zero, "Stored result should be consumed exactly once");
    }

    [Test]
    public async Task DuplicateTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>(
            new MiddlewareBuilder<Mock>()
                .RegisterDuplicateActionInSet((old, @new) => new Mock(old, @new))
        );

        var mock = new Mock();
        await dictionary.SetAsync(Key, mock);
        await dictionary.SetAsync(Key, mock);

        var waitMock = await dictionary.WaitAsync(Key);
        Assert.That(waitMock.Nodes, Is.Not.Empty);
    }

    [Test]
    public async Task DuplicateErrorTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var mock = new Mock();
        await dictionary.SetAsync(Key, mock);

        var exception = Assert.ThrowsAsync<ArgumentException>(() => dictionary.SetAsync(Key, mock));
        Assert.That(exception!.Message, Does.Contain("already been added"));
    }

    [TestCase(typeof(OperationCanceledException))]
    [TestCase(typeof(InvalidCastException))]
    [TestCase(typeof(AggregateException))]
    public async Task CancelTest(Type type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var dictionary = new WaitingDictionary<int, Mock>(
            new MiddlewareBuilder<Mock>()
                .RegisterCancellationActionInWait((tcs, hasOwnToken) => tcs.SetException((Exception) Activator.CreateInstance(type)!))
        );

        var waitTask = dictionary.WaitAsync(Key, cts.Token);

        var exception = Assert.ThrowsAsync(type, async () => await waitTask);
        Assert.That(exception, Is.InstanceOf(type));
        Assert.That(dictionary.ContainsKey(Key), Is.False, "Canceled item should not stay in the dictionary");
    }

    [Test]
    public async Task MultipleWaitTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var mock = new Mock();

        var waitTask1 = dictionary.WaitAsync(Key);
        await WaitUntilAsync(() => dictionary.ContainsKey(Key));

        Assert.ThrowsAsync<InvalidOperationException>(() => dictionary.WaitAsync(Key));

        await dictionary.SetAsync(Key, mock);
        Assert.That(await waitTask1, Is.SameAs(mock));
    }

    [Test]
    public async Task CanceledWaitDoesNotPoisonNextOperationsTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var mock = new Mock();

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            Assert.That(async () => await dictionary.WaitAsync(Key, cts.Token), Throws.InstanceOf<OperationCanceledException>());

        await dictionary.SetAsync(Key, mock);
        Assert.That(await dictionary.WaitAsync(Key), Is.SameAs(mock), "Next wait/set after cancellation must work normally");
    }

    [Test]
    public async Task EnumerateTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        await dictionary.SetAsync(1, new Mock());
        await dictionary.SetAsync(2, new Mock());

        var idx = 0;
        foreach (var entry in dictionary)
        {
            Assert.That(entry, Is.Not.Null);
            idx++;
        }

        Assert.That(dictionary, Is.Not.Empty);
        Assert.That(dictionary.Count, Is.EqualTo(2));
        Assert.That(idx, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadOnlyApiTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var readOnly = (IReadOnlyDictionary<int, WaitItem<Mock>>) dictionary;

        Assert.That(readOnly.TryGetValue(123, out var missing), Is.False);
        Assert.That(missing, Is.Null);
        Assert.Throws<KeyNotFoundException>(() => _ = readOnly[123]);

        await dictionary.SetAsync(1, new Mock());

        Assert.That(readOnly.ContainsKey(1), Is.True);
        Assert.That(readOnly.TryGetValue(1, out var stored), Is.True);
        Assert.That(stored!.DelayedTask.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(readOnly[1], Is.SameAs(stored));
        Assert.That(readOnly.Keys, Is.EqualTo([1]));
        Assert.That(readOnly.Values, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FilterTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        await dictionary.SetAsync(1, new Mock());

        Assert.That(dictionary.Filter(tcs => tcs.Value.DelayedTask.Task.Status == TaskStatus.Canceled), Is.Empty);
        Assert.That(dictionary.Filter(tcs => tcs.Value.DelayedTask.Task.Status == TaskStatus.RanToCompletion), Has.Exactly(1).Items);
    }

    [Test]
    public async Task RemoveTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        await dictionary.SetAsync(1, new Mock());

        Assert.That(await dictionary.TryRemoveAsync(1), Is.True);
        Assert.That(await dictionary.TryRemoveAsync(1), Is.False);
    }

    [Test]
    public async Task RemoveCancelsWaiterTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var waitTask = dictionary.WaitAsync(1);
        await WaitUntilAsync(() => dictionary.ContainsKey(1));

        Assert.That(await dictionary.TryRemoveAsync(1), Is.True);
        Assert.That(async () => await waitTask, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task StaleItemTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>(staleItemTimeToLiveMs: 100);

        var waitTask = dictionary.WaitAsync(1);
        await WaitUntilAsync(() => dictionary.ContainsKey(1));

        Assert.That(async () => await waitTask, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(dictionary.ContainsKey(1), Is.False, "Stale item should be removed by the cleanup job");
    }

    [Test]
    public async Task DisposeTest()
    {
        var dictionary = new WaitingDictionary<int, Mock>(staleItemTimeToLiveMs: 100);
        var waitTask = dictionary.WaitAsync(Key);
        await WaitUntilAsync(() => dictionary.ContainsKey(Key));

        dictionary.Dispose();
        Assert.DoesNotThrow(dictionary.Dispose, "Dispose should be idempotent");

        Assert.That(async () => await waitTask, Throws.InstanceOf<OperationCanceledException>());
        Assert.ThrowsAsync<ObjectDisposedException>(() => dictionary.WaitAsync(Key));
    }

    [Test]
    public async Task ParallelWaitAndSetTest()
    {
        using var dictionary = new WaitingDictionary<int, Mock>();
        var keys = Enumerable.Range(0, 100).ToArray();

        var waits = keys.ToDictionary(key => key, key => dictionary.WaitAsync(key));
        var sets = keys.ToDictionary(key => key, key => dictionary.SetAsync(key, new Mock()));

        await Task.WhenAll(waits.Values.Concat(sets.Values));

        Assert.That(waits.Values.Select(task => task.Result).Distinct(), Has.Exactly(keys.Length).Items);
        Assert.That(dictionary.Count, Is.Zero);
    }

    [Test]
    public async Task MiddlewareSnapshotTest()
    {
        var builder = new MiddlewareBuilder<Mock>();
        using var dictionary = new WaitingDictionary<int, Mock>(builder);

        // Registered after the dictionary was constructed, so it must not be picked up.
        builder.RegisterDuplicateActionInSet((old, @new) => new Mock(old, @new));
        await dictionary.SetAsync(1, new Mock());
        Assert.ThrowsAsync<ArgumentException>(() => dictionary.SetAsync(1, new Mock()));
    }

    [Test]
    public void MiddlewareDoubleRegistrationTest()
    {
        var builder = new MiddlewareBuilder<Mock>()
            .RegisterCompletionActionInSet(() => TestContext.WriteLine("Set completed"));

        Assert.Throws<InvalidOperationException>(() => builder.RegisterCompletionActionInSet(() => TestContext.WriteLine("Again")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(timeoutMs), "Condition was not met in time");
            await Task.Delay(5);
        }
    }
}

public class Mock
{
    public List<Mock>? Nodes { get; }

    public Mock(params Mock[] mock)
    {
        (Nodes ??= []).AddRange(mock);
    }

    public Mock()
    {
    }
}
