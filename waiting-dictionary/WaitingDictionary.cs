using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nito.AsyncEx;

namespace Drenalol.WaitingDictionary;

/// <summary>
/// An item holding the <see cref="TaskCompletionSource{TResult}"/> and the UTC moment it was placed into the dictionary.
/// </summary>
/// <param name="DelayedTask">The source completing the <see cref="WaitingDictionary{TKey,TValue}.WaitAsync"/> call.</param>
/// <param name="PlaceDateTime">UTC moment when the item was created, used by the stale items cleanup.</param>
public sealed record WaitItem<TValue>(TaskCompletionSource<TValue> DelayedTask, DateTime PlaceDateTime);

/// <summary>
/// Represents a thread-safe collection of keys and <see cref="TaskCompletionSource{TResult}"/> as values. <para></para>
/// Manage items with two methods: <see cref="WaitAsync"/>, <see cref="SetAsync"/>.
/// Completed, canceled and faulted entries are never returned to the next caller: <see cref="WaitAsync"/> consumes
/// the stored result once and replaces dead items, so a canceled wait never poisons the following one.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public class WaitingDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, WaitItem<TValue>>, IDisposable where TKey : notnull
{
    // ConcurrentDictionary is thread-safe for single operations, but the check-then-act sequences between
    // WaitAsync, SetAsync, TryRemoveAsync and the stale items cleanup must be atomic as a whole.
    // That atomicity is provided by _dataLossPrevention, not by ConcurrentDictionary itself.
    private readonly ConcurrentDictionary<TKey, WaitItem<TValue>> _dictionary;
    private readonly AsyncLock _dataLossPrevention;
    private readonly CancellationTokenSource _internalCts;
    private readonly MiddlewareSnapshot<TValue> _middleware;
    private readonly TaskCreationOptions _creationOptions;
    private readonly ILogger _logger;
    private readonly TimeSpan _staleItemTtl;
    private readonly Timer? _job;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
    /// The registered middleware is captured as a snapshot, later mutations of the builder do not affect this instance.
    /// <param name="middlewareBuilder">Middleware builder using it to add some logic to the Wait or Set methods</param>
    /// <param name="creationOptions">Specifies flags that control optional behavior for the creation and execution of tasks.
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> is always added to avoid running
    /// waiter continuations synchronously under the internal lock.</param>
    /// <param name="staleItemTimeToLiveMs">TTL period in milliseconds for stale items, 0 = disabled.
    /// An item is cleared no earlier than TTL and no later than 2 x TTL after it was created.</param>
    /// <param name="logger"></param>
    /// </summary>
    public WaitingDictionary(
        MiddlewareBuilder<TValue>? middlewareBuilder = null,
        TaskCreationOptions creationOptions = TaskCreationOptions.None,
        int staleItemTimeToLiveMs = 0,
        ILogger? logger = null
    )
    {
        _middleware = middlewareBuilder?.Snapshot() ?? new MiddlewareSnapshot<TValue>(null, null, null, null);
        _creationOptions = creationOptions | TaskCreationOptions.RunContinuationsAsynchronously;
        _logger = logger ?? NullLogger<WaitingDictionary<TKey, TValue>>.Instance;
        _dictionary = new ConcurrentDictionary<TKey, WaitItem<TValue>>();
        _dataLossPrevention = new AsyncLock();
        _internalCts = new CancellationTokenSource();

        if (staleItemTimeToLiveMs > 0)
        {
            _staleItemTtl = TimeSpan.FromMilliseconds(staleItemTimeToLiveMs);
            _job = new Timer(_ => ClearStaleItems(), null, staleItemTimeToLiveMs, staleItemTimeToLiveMs);
        }
    }

    private WaitItem<TValue> CreateWaitItem(TKey key)
    {
        var waitItem = new WaitItem<TValue>(new TaskCompletionSource<TValue>(_creationOptions), DateTime.UtcNow);
        _dictionary[key] = waitItem;

        return waitItem;
    }

    /// <summary>
    /// Filters a sequence of values based on a predicate.
    /// </summary>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <returns>An <see cref="IEnumerable{T}"/> that contains elements from the input sequence that satisfy the condition.</returns>
    /// <exception cref="ArgumentNullException">source or predicate is null.</exception>
    public IEnumerable<KeyValuePair<TKey, WaitItem<TValue>>> Filter(Func<KeyValuePair<TKey, WaitItem<TValue>>, bool> predicate) => GetFastEnumerable().Where(predicate);

    /// <summary>
    /// Attempts to remove the item with the specified key from the <see cref="WaitingDictionary{TKey,TValue}"/>.
    /// A waiter waiting on the removed item is canceled.
    /// </summary>
    /// <param name="key"></param>
    /// <returns><see langword="true"/> if the item was found and removed; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    public async Task<bool> TryRemoveAsync(TKey key)
    {
        if (key is null)
            throw new ArgumentNullException(nameof(key));

        using (await _dataLossPrevention.LockAsync())
        {
            if (!_dictionary.TryRemove(key, out var waitItem))
                return false;

            waitItem.DelayedTask.TrySetCanceled();

            return true;
        }
    }

    /// <summary>
    /// Asynchronously waiting or immediately completing <see cref="Task{TValue}"/> if a result exists associated with the specified key.
    /// A previous wait canceled by token, or a dead item left after such a cancellation, is replaced by a fresh one.
    /// </summary>
    /// <param name="key">The key of the element.</param>
    /// <param name="token">A cancellation token to observe.</param>
    /// <returns><see cref="Task{TValue}"/></returns>
    /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled or the dictionary is disposed.</exception>
    /// <exception cref="InvalidOperationException">If item with the same key has already been added in dictionary.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The dictionary has been disposed.</exception>
    public async Task<TValue> WaitAsync(TKey key, CancellationToken token = default)
    {
        if (key is null)
            throw new ArgumentNullException(nameof(key));

        var hasOwnToken = token.CanBeCanceled;
        var internalToken = hasOwnToken ? token : _internalCts.Token;

        WaitItem<TValue> waitItem;

        using (await _dataLossPrevention.LockAsync())
        {
            if (_dictionary.TryGetValue(key, out var existing))
            {
                switch (existing.DelayedTask.Task.Status)
                {
                    case TaskStatus.WaitingForActivation:
                        throw new InvalidOperationException($"An item in wait state with the same key ({key}) has already been added.");

                    case TaskStatus.RanToCompletion:
                        // Consume the stored result exactly once.
                        waitItem = existing;
                        _dictionary.TryRemove(key, out _);
                        break;

                    default:
                        // Canceled or faulted ghosts must not leak their exception into the new waiter.
                        _dictionary.TryRemove(key, out _);
                        waitItem = CreateWaitItem(key);
                        break;
                }
            }
            else
                waitItem = CreateWaitItem(key);
        }

        await using (internalToken.Register(
                         () =>
                         {
                             if (waitItem.DelayedTask.Task.Status != TaskStatus.WaitingForActivation)
                                 return;

                             if (_middleware.CancellationActionInWait != null)
                                 _middleware.CancellationActionInWait(waitItem.DelayedTask, hasOwnToken);
                             else
                                 waitItem.DelayedTask.TrySetCanceled();

                             // Remove only if this exact item is still in the dictionary (netstandard2.1 has no CAS TryRemove).
                             if (_dictionary.TryGetValue(key, out var current) && ReferenceEquals(current, waitItem))
                                 _dictionary.TryRemove(key, out _);
                         }
                     ))
        {
            var waitResult = await waitItem.DelayedTask.Task;
            _middleware.CompletionActionInWait?.Invoke();

            return waitResult;
        }
    }

    /// <summary>
    /// Asynchronously completing task returned by <see cref="WaitAsync"/> or storing an already completed task
    /// if <see cref="WaitAsync"/> is not executed by the specified key.
    /// </summary>
    /// <param name="key">The key of the element.</param>
    /// <param name="value">Value element that specified with key.</param>
    /// <param name="ignoreError">Ignoring the error when the underlying task is already completed</param>
    /// <exception cref="ArgumentException">If item with the same key has already been added in dictionary and Duplication Middleware was not set it.</exception>
    /// <exception cref="InvalidOperationException">
    /// The underlying <see cref="T:System.Threading.Tasks.Task{TResult}"/> was already completed
    /// (for example, the waiter was canceled while <see cref="SetAsync"/> was running)
    /// and <paramref name="ignoreError"/> is <see langword="false"/>.
    /// </exception>
    public async Task SetAsync(TKey key, TValue value, bool ignoreError = false)
    {
        if (key is null)
            throw new ArgumentNullException(nameof(key));

        var result = value;

        using (await _dataLossPrevention.LockAsync())
        {
            var status = _dictionary.TryGetValue(key, out var existing)
                ? existing!.DelayedTask.Task.Status
                : (TaskStatus?)null;

            WaitItem<TValue> waitItem;
            switch (status)
            {
                case TaskStatus.RanToCompletion:
                    if (_middleware.DuplicateActionInSet == null)
                        throw new ArgumentException($"An item with the same key ({key}) has already been added.");

                    var oldValue = await existing.DelayedTask.Task;
                    result = _middleware.DuplicateActionInSet(oldValue, value);

                    _dictionary.TryRemove(key, out _);
                    waitItem = CreateWaitItem(key);
                    break;

                case TaskStatus.WaitingForActivation:
                    // Complete the waiter, the entry leaves the dictionary together with the result.
                    _dictionary.TryRemove(key, out _);
                    waitItem = existing!;
                    break;

                default:
                    // Absent, canceled or faulted item: start from a fresh one.
                    waitItem = CreateWaitItem(key);
                    break;
            }

            if (!waitItem.DelayedTask.TrySetResult(result) && !ignoreError)
                throw new InvalidOperationException($"The underlying task for the key ({key}) was already completed (canceled or faulted).");
        }

        _middleware.CompletionActionInSet?.Invoke();
    }

    /// <summary>
    /// This trick is cheaper (speed) than calling Where directly on the ConcurrentDictionary.
    /// </summary>
    private IEnumerable<KeyValuePair<TKey, WaitItem<TValue>>> GetFastEnumerable() => [.. _dictionary];

    private void ClearStaleItems()
    {
        try
        {
            var minExpDateTime = DateTime.UtcNow - _staleItemTtl;

            List<KeyValuePair<TKey, WaitItem<TValue>>>? toRemove = null;

            foreach (var entry in _dictionary)
                if (entry.Value.PlaceDateTime <= minExpDateTime)
                    (toRemove ??= []).Add(entry);

            if (toRemove is null)
                return;

            using (_dataLossPrevention.Lock())
            {
                foreach (var (key, waitItem) in toRemove)
                {
                    // The item could be replaced by a fresh one between the snapshot and taking the lock.
                    if (!_dictionary.TryGetValue(key, out var current) || !ReferenceEquals(current, waitItem))
                        continue;

                    var state = waitItem.DelayedTask.Task.Status;

                    _dictionary.TryRemove(key, out _);
                    waitItem.DelayedTask.TrySetCanceled();

                    _logger.LogWarning("Cleared stale item {StaleItemKey}:{StaleItemState}", key, state);
                }
            }
        }
        catch (Exception exception)
        {
            // Never let the timer callback crash the process.
            _logger.LogError(exception, "Clearing stale items failed");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Wait for a running stale items callback so it cannot touch the lock after disposal.
        // The handle must stay alive until the timer signals it, hence WaitOne before leaving the scope.
        if (_job is not null)
        {
            using var timerStopped = new ManualResetEvent(false);
            _job.Dispose(timerStopped);
            timerStopped.WaitOne();
        }

        _internalCts.Cancel();
        _internalCts.Dispose();

        foreach (var (_, waitItem) in _dictionary)
            waitItem.DelayedTask.TrySetCanceled();

        _dictionary.Clear();
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<TKey, WaitItem<TValue>>> GetEnumerator() => _dictionary.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Gets the number of key/value pairs contained in the <see cref="WaitingDictionary{TKey,TValue}"/>.
    /// </summary>
    public int Count => _dictionary.Count;

    /// <summary>
    /// Determines whether the <see cref="WaitingDictionary{TKey, TValue}"/> contains the specified key.
    /// </summary>
    /// <param name="key">The key to locate in the <see cref="WaitingDictionary{TKey, TValue}"/></param>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(TKey key, out WaitItem<TValue> value) => _dictionary.TryGetValue(key, out value);

    /// <summary>
    /// Gets the item associated with the specified key.
    /// </summary>
    /// <param name="key">The key of the item to get.</param>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    /// <exception cref="KeyNotFoundException">The item with the specified key is not present.</exception>
    public WaitItem<TValue> this[TKey key] => _dictionary.TryGetValue(key, out var waitItem)
        ? waitItem
        : throw new KeyNotFoundException($"The given key ({key}) is not present in the {nameof(WaitingDictionary<,>)}.");

    /// <inheritdoc cref="IReadOnlyDictionary{TKey,TValue}"/>
    IEnumerable<TKey> IReadOnlyDictionary<TKey, WaitItem<TValue>>.Keys => _dictionary.Keys;

    /// <inheritdoc cref="IReadOnlyDictionary{TKey,TValue}"/>
    IEnumerable<WaitItem<TValue>> IReadOnlyDictionary<TKey, WaitItem<TValue>>.Values => _dictionary.Values;
}
