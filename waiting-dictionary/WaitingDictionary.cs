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

public record WaitItem<TValue>(TaskCompletionSource<TValue> DelayedTask, DateTime PlaceDateTime)
{
    public TaskCompletionSource<TValue> DelayedTask { get; } = DelayedTask;
    public DateTime PlaceDateTime { get; } = PlaceDateTime;
}

/// <summary>
/// Represents a thread-safe collection of keys and <see cref="TaskCompletionSource{TResult}"/> as values. <para></para>
/// Manage items with two methods: <see cref="WaitAsync"/>, <see cref="SetAsync"/>. 
/// </summary>
/// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public class WaitingDictionary<TKey, TValue> : IDictionary<TKey, WaitItem<TValue>>, IReadOnlyDictionary<TKey, WaitItem<TValue>>, IDisposable where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, WaitItem<TValue>> _dictionary;
    private readonly AsyncLock _dataLosePrevention;
    private readonly CancellationTokenSource _internalCts;
    private readonly MiddlewareBuilder<TValue>? _middleware;
    private readonly TaskCreationOptions _creationOptions;
    private readonly ILogger _logger;
    private readonly TimeSpan _staleItemTtl;
    private readonly Timer? _job;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
    /// <param name="middlewareBuilder">Middleware builder using it to add some logic to the Wait or Set methods</param>
    /// <param name="creationOptions">Specifies flags that control optional behavior for the creation and execution of tasks</param>
    /// <param name="staleItemTimeToLiveMs">TTL period in milliseconds for stale items, 0 = disabled</param>
    /// <param name="logger"></param>
    /// </summary>
    public WaitingDictionary(
        MiddlewareBuilder<TValue>? middlewareBuilder = null,
        TaskCreationOptions creationOptions = TaskCreationOptions.None,
        int staleItemTimeToLiveMs = 0,
        ILogger? logger = null
    )
    {
        _middleware = middlewareBuilder;
        _creationOptions = creationOptions;
        _logger = logger ?? NullLogger<WaitingDictionary<TKey, TValue>>.Instance;
        _dictionary = new ConcurrentDictionary<TKey, WaitItem<TValue>>();
        _dataLosePrevention = new AsyncLock();
        _internalCts = new CancellationTokenSource();
        
        if (staleItemTimeToLiveMs > 0)
        {
            _staleItemTtl = TimeSpan.FromMilliseconds(staleItemTimeToLiveMs);
            _job = new Timer(_ => ClearStaleItems(), null, staleItemTimeToLiveMs, staleItemTimeToLiveMs);
        }
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="middlewareBuilder">Middleware builder using it to add some logic to the Wait or Set methods</param>
    /// <param name="staleItemTimeToLive">TTL period for stale items, 0 = disabled</param>
    /// <param name="logger"></param>
    [Obsolete]
    public WaitingDictionary(MiddlewareBuilder<TValue> middlewareBuilder, int staleItemTimeToLive = 0, ILogger<WaitingDictionary<TKey, TValue>>? logger = null) : this(middlewareBuilder, TaskCreationOptions.None, staleItemTimeToLive, logger)
    {
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="creationOptions">Specifies flags that control optional behavior for the creation and execution of tasks</param>
    /// <param name="staleItemTimeToLive">TTL period for stale items, 0 = disabled</param>
    /// <param name="logger"></param>
    [Obsolete]
    public WaitingDictionary(TaskCreationOptions creationOptions, int staleItemTimeToLive = 0, ILogger<WaitingDictionary<TKey, TValue>>? logger = null) : this(null, creationOptions, staleItemTimeToLive, logger)
    {
    }
    
    private WaitItem<TValue> InternalCreateWaitItem(TKey key)
    {
        var tcs = new TaskCompletionSource<TValue>(_creationOptions);
        var waitItem = new WaitItem<TValue>(tcs, DateTime.UtcNow);
        _dictionary.TryAdd(key, waitItem);
        
        return waitItem;
    }
    
    private static NotSupportedException NotSupported() =>
        new(
            string.Concat(
                $"In {nameof(WaitingDictionary<TKey, TValue>)} collection it is necessary to use mainly {nameof(WaitAsync)} and {nameof(SetAsync)} methods.\n\n",
                "The rest of the methods are not supported, or partially supported."
            )
        );
    
    /// <summary>
    /// Filters a sequence of values based on a predicate.
    /// </summary>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <returns>An <see cref="IEnumerable{T}"/> that contains elements from the input sequence that satisfy the condition.</returns>
    /// <exception cref="ArgumentNullException">source or predicate is null.</exception>
    public IEnumerable<KeyValuePair<TKey, WaitItem<TValue>>> Filter(Func<KeyValuePair<TKey, WaitItem<TValue>>, bool> predicate) => GetFastEnumerable().Where(predicate);
    
    /// <summary>
    /// Attempts to remove value with the specified key from the <see cref="WaitingDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    public async Task<bool> TryRemoveAsync(TKey key)
    {
        // From MSDN: ConcurrentDictionary<TId,TValue> is designed for multi-threaded scenarios.
        // You do not have to use locks in your code to add or remove items from the collection.
        // However, it is always possible for one thread to retrieve a value, and another thread
        // to immediately update the collection by giving the same id a new value.
        using (await _dataLosePrevention.LockAsync())
            return _dictionary.TryRemove(key, out _);
    }
    
    /// <summary>
    /// Asynchronously waiting or immediately completing <see cref="Task{TValue}"/> if value exists associated with the specified key.
    /// </summary>
    /// <param name="key">The key of the element.</param>
    /// <param name="token">A cancellation token to observe.</param>
    /// <returns><see cref="Task{TValue}"/></returns>
    /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
    /// <exception cref="InvalidOperationException">If item with the same key has already been added in dictionary.</exception>
    public async Task<TValue> WaitAsync(TKey key, CancellationToken token = default)
    {
        var hasOwnToken = false;
        CancellationToken internalToken;
        
        if (token == default)
            internalToken = _internalCts.Token;
        else
        {
            internalToken = token;
            hasOwnToken = true;
        }
        
        WaitItem<TValue> waitItem;
        
        // From MSDN: ConcurrentDictionary<TId,TValue> is designed for multi-threaded scenarios.
        // You do not have to use locks in your code to add or remove items from the collection.
        // However, it is always possible for one thread to retrieve a value, and another thread
        // to immediately update the collection by giving the same id a new value.
        using (await _dataLosePrevention.LockAsync())
        {
            if (_dictionary.TryGetValue(key, out waitItem))
            {
                if (waitItem.DelayedTask.Task.Status == TaskStatus.WaitingForActivation)
                    throw new InvalidOperationException($"An item in wait state with the same key ({key}) has already been added.");
                
                _dictionary.TryRemove(key, out _);
            }
            else
                waitItem = InternalCreateWaitItem(key);
        }
        
        await using (internalToken.Register(
                         () =>
                         {
                             if (_middleware?.CancellationActionInWait != null)
                                 _middleware.CancellationActionInWait(waitItem.DelayedTask, hasOwnToken);
                             else
                                 waitItem.DelayedTask.TrySetCanceled();
                         }
                     ))
        {
            var waitResult = await waitItem.DelayedTask.Task;
            _middleware?.CompletionActionInWait?.Invoke();
            
            return waitResult;
        }
    }
    
    /// <summary>
    /// Asynchronously completing task returned by <see cref="WaitAsync"/> or adding already completed task if <see cref="WaitAsync"/> is not executed by the specified key.
    /// </summary>
    /// <param name="key">The key of the element.</param>
    /// <param name="value">Value element that specified with key.</param>
    /// <param name="ignoreError">Ignoring <see cref="InvalidOperationException"/> when task already in set state</param>
    /// <exception cref="ArgumentException">If item with the same key has already been added in dictionary and Duplication Middleware was not set it.</exception>
    /// <exception cref="InvalidOperationException">
    /// The underlying <see cref="T:System.Threading.Tasks.Task{TResult}"/> is already in one
    /// of the three final states:
    /// <see cref="System.Threading.Tasks.TaskStatus.RanToCompletion">RanToCompletion</see>, 
    /// <see cref="System.Threading.Tasks.TaskStatus.Faulted">Faulted</see>, or
    /// <see cref="System.Threading.Tasks.TaskStatus.Canceled">Canceled</see>.
    /// </exception>
    public async Task SetAsync(TKey key, TValue value, bool ignoreError = false)
    {
        var result = value;
        
        // From MSDN: ConcurrentDictionary<TId,TValue> is designed for multi-threaded scenarios.
        // You do not have to use locks in your code to add or remove items from the collection.
        // However, it is always possible for one thread to retrieve a value, and another thread
        // to immediately update the collection by giving the same id a new value.
        using (await _dataLosePrevention.LockAsync())
        {
            if (_dictionary.TryRemove(key, out var waitItem))
            {
                var tcs = waitItem.DelayedTask;
                
                if (tcs.Task.Status == TaskStatus.RanToCompletion)
                {
                    if (_middleware?.DuplicateActionInSet == null)
                        throw new ArgumentException($"An item with the same key ({key}) has already been added.");
                    
                    var oldValue = await tcs.Task;
                    var newValue = _middleware.DuplicateActionInSet(oldValue, value);
                    
                    result = newValue;
                    waitItem = InternalCreateWaitItem(key);
                }
            }
            else
                waitItem = InternalCreateWaitItem(key);
            
            if (ignoreError)
                waitItem.DelayedTask.TrySetResult(result);
            else
                waitItem.DelayedTask.SetResult(result);
        }
        
        _middleware?.CompletionActionInSet?.Invoke();
    }
    
    /// <summary>
    /// This trick is cheaper (speed) than calling Where direct in to ConcurrentDictionary.
    /// </summary>
    /// <returns></returns>
    private IEnumerable<KeyValuePair<TKey, WaitItem<TValue>>> GetFastEnumerable() => _dictionary.ToArray();
    
    private void ClearStaleItems()
    {
        var minExpDateTime = DateTime.UtcNow.Add(-_staleItemTtl);
        
        var toRemove = new List<TKey>();
        
        foreach (var (key, waitItem) in GetFastEnumerable())
        {
            if (waitItem.PlaceDateTime > minExpDateTime)
                continue;
            
            toRemove.Add(key);
        }
        
        foreach (var key in toRemove)
            ClearStaleItem(key);
        
        void ClearStaleItem(TKey key)
        {
            var isRemoved = _dictionary.TryRemove(key, out _);
            
            if (isRemoved)
                _logger.LogInformation("Cleared stale item {StaleItemKey}", key);
            else
                _logger.LogWarning("Clear stale item {StaleItemKey} failed", key);
        }
    }
    
    /// <inheritdoc/>
    public void Dispose()
    {
        _job?.Dispose();
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
    
    /// <inheritdoc/>
    public bool IsReadOnly => ((ICollection<KeyValuePair<TKey, WaitItem<TValue>>>)_dictionary).IsReadOnly;
    
    /// <summary>
    /// Removes all keys and values from the <see cref="WaitingDictionary{TKey,TValue}"/>.
    /// </summary>
    public void Clear() => _dictionary.Clear();
    
    /// <inheritdoc/>
    bool ICollection<KeyValuePair<TKey, WaitItem<TValue>>>.Contains(KeyValuePair<TKey, WaitItem<TValue>> item) => ((ICollection<KeyValuePair<TKey, WaitItem<TValue>>>)_dictionary).Contains(item);
    
    /// <inheritdoc/>
    void ICollection<KeyValuePair<TKey, WaitItem<TValue>>>.CopyTo(KeyValuePair<TKey, WaitItem<TValue>>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, WaitItem<TValue>>>)_dictionary).CopyTo(array, arrayIndex);
    
    /// <summary>
    /// Determines whether the <see cref="WaitingDictionary{TKey, TValue}"/> contains the specified key.
    /// </summary>
    /// <param name="key">The key to locate in the <see cref="WaitingDictionary{TKey, TValue}"/></param>
    /// <returns></returns>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);
    
    /// <inheritdoc/>
    bool IReadOnlyDictionary<TKey, WaitItem<TValue>>.TryGetValue(TKey key, out WaitItem<TValue> value) => _dictionary.TryGetValue(key, out value);
    
    /// <inheritdoc/>
    bool IDictionary<TKey, WaitItem<TValue>>.TryGetValue(TKey key, out WaitItem<TValue> value) => _dictionary.TryGetValue(key, out value);
    
    /// <summary>
    /// Not supported. Use SetAsync and WaitAsync instead.
    /// </summary>
    /// <param name="key"></param>
    public WaitItem<TValue> this[TKey key]
    {
        get
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            
            return _dictionary.TryGetValue(key, out var value) ? value : null!;
        }
        
        #region NotSupported
        
        set => throw NotSupported();
        
        #endregion
    }
    
    /// <inheritdoc/>
    IEnumerable<TKey> IReadOnlyDictionary<TKey, WaitItem<TValue>>.Keys => ((IReadOnlyDictionary<TKey, WaitItem<TValue>>)_dictionary).Keys;
    
    /// <inheritdoc/>
    IEnumerable<WaitItem<TValue>> IReadOnlyDictionary<TKey, WaitItem<TValue>>.Values => ((IReadOnlyDictionary<TKey, WaitItem<TValue>>)_dictionary).Values;
    
    /// <inheritdoc/>
    ICollection<TKey> IDictionary<TKey, WaitItem<TValue>>.Keys => ((IDictionary<TKey, WaitItem<TValue>>)_dictionary).Keys;
    
    /// <inheritdoc/>
    ICollection<WaitItem<TValue>> IDictionary<TKey, WaitItem<TValue>>.Values => ((IDictionary<TKey, WaitItem<TValue>>)_dictionary).Values;
    
    #region NotSupported
    
    /// <summary>
    /// Not supported. Use TryRemoveAsync instead.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool IDictionary<TKey, WaitItem<TValue>>.Remove(TKey key) => throw NotSupported();
    
    /// <summary>
    /// Not supported. Use TryRemoveAsync instead.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    bool ICollection<KeyValuePair<TKey, WaitItem<TValue>>>.Remove(KeyValuePair<TKey, WaitItem<TValue>> item) => throw NotSupported();
    
    /// <summary>
    /// Not supported. Use SetAsync instead.
    /// </summary>
    /// <param name="item"></param>
    public void Add(KeyValuePair<TKey, WaitItem<TValue>> item) => throw NotSupported();
    
    /// <summary>
    /// Not supported. Use SetAsync instead.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    public void Add(TKey key, WaitItem<TValue> value) => throw NotSupported();
    
    #endregion
}