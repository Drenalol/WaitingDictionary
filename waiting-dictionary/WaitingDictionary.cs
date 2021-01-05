using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Drenalol.WaitingDictionary
{
    /// <summary>
    /// Represents a thread-safe collection of keys and <see cref="TaskCompletionSource{TResult}"/> as values. <para></para>
    /// Manage items with two methods: <see cref="WaitAsync"/>, <see cref="SetAsync"/>. 
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    public class WaitingDictionary<TKey, TValue> : IDictionary<TKey, TaskCompletionSource<TValue>>, IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>, IDisposable where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TaskCompletionSource<TValue>> _dictionary;
        private readonly AsyncLock _dataLosePrevention;
        private readonly CancellationTokenSource _internalCts;
        private readonly MiddlewareBuilder<TValue> _middleware;

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
        /// </summary>
        public WaitingDictionary()
        {
            _dictionary = new ConcurrentDictionary<TKey, TaskCompletionSource<TValue>>();
            _dataLosePrevention = new AsyncLock();
            _internalCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
        /// </summary>
        /// <param name="middlewareBuilder">Middleware builder using it to add some logic to the Wait or Set methods</param>
        public WaitingDictionary(MiddlewareBuilder<TValue> middlewareBuilder) : this()
        {
            _middleware = middlewareBuilder;
        }

        private TaskCompletionSource<TValue> InternalCreateTcs(TKey key)
        {
            var tcs = new TaskCompletionSource<TValue>();
            _dictionary.TryAdd(key, tcs);
            return tcs;
        }

        private static NotSupportedException NotSupported() =>
            new NotSupportedException(
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
        public IEnumerable<KeyValuePair<TKey, TaskCompletionSource<TValue>>> Filter(Func<KeyValuePair<TKey, TaskCompletionSource<TValue>>, bool> predicate)
            => _dictionary.ToArray().Where(predicate); // This trick is cheaper (speed) than calling Where direct in to ConcurrentDictionary.

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
            {
                return _dictionary.TryRemove(key, out _);
            }
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

            TaskCompletionSource<TValue> tcs;

            // From MSDN: ConcurrentDictionary<TId,TValue> is designed for multi-threaded scenarios.
            // You do not have to use locks in your code to add or remove items from the collection.
            // However, it is always possible for one thread to retrieve a value, and another thread
            // to immediately update the collection by giving the same id a new value.
            using (await _dataLosePrevention.LockAsync())
            {
                if (_dictionary.TryGetValue(key, out tcs))
                {
                    if (tcs.Task.Status == TaskStatus.WaitingForActivation)
                        throw new InvalidOperationException($"An item in wait state with the same key ({key}) has already been added.");

                    _dictionary.TryRemove(key, out _);
                }
                else
                    tcs = InternalCreateTcs(key);
            }

            await using (internalToken.Register(() =>
            {
                if (_middleware?.CancellationActionInWait != null)
                    _middleware.CancellationActionInWait(tcs, hasOwnToken);
                else
                    tcs.TrySetCanceled();
            }))
            {
                var waitResult = await tcs.Task;
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
                if (_dictionary.TryRemove(key, out var tcs))
                {
                    if (tcs.Task.Status == TaskStatus.RanToCompletion)
                    {
                        if (_middleware?.DuplicateActionInSet == null)
                            throw new ArgumentException($"An item with the same key ({key}) has already been added.");

                        var oldValue = await tcs.Task;
                        var newValue = _middleware.DuplicateActionInSet(oldValue, value);

                        result = newValue;
                        tcs = InternalCreateTcs(key);
                    }
                }
                else
                    tcs = InternalCreateTcs(key);

                if (ignoreError)
                    tcs.TrySetResult(result);
                else
                    tcs.SetResult(result);
            }

            _middleware?.CompletionActionInSet?.Invoke();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _internalCts.Cancel();
            _internalCts.Dispose();

            foreach (var (_, tcs) in _dictionary)
                tcs.TrySetCanceled();

            _dictionary.Clear();
        }

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<TKey, TaskCompletionSource<TValue>>> GetEnumerator() => _dictionary.GetEnumerator();

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Gets the number of key/value pairs contained in the <see cref="WaitingDictionary{TKey,TValue}"/>.
        /// </summary>
        public int Count => _dictionary.Count;

        /// <inheritdoc/>
        public bool IsReadOnly => ((ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>) _dictionary).IsReadOnly;

        /// <summary>
        /// Removes all keys and values from the <see cref="WaitingDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear() => _dictionary.Clear();

        /// <inheritdoc/>
        bool ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>.Contains(KeyValuePair<TKey, TaskCompletionSource<TValue>> item)
            => ((ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>) _dictionary).Contains(item);

        /// <inheritdoc/>
        void ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>.CopyTo(KeyValuePair<TKey, TaskCompletionSource<TValue>>[] array, int arrayIndex)
            => ((ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>) _dictionary).CopyTo(array, arrayIndex);

        /// <summary>
        /// Determines whether the <see cref="WaitingDictionary{TKey, TValue}"/> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="WaitingDictionary{TKey, TValue}"/></param>
        /// <returns></returns>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        /// <inheritdoc/>
        bool IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>.TryGetValue(TKey key, out TaskCompletionSource<TValue> value)
            => _dictionary.TryGetValue(key, out value);

        /// <inheritdoc/>
        bool IDictionary<TKey, TaskCompletionSource<TValue>>.TryGetValue(TKey key, out TaskCompletionSource<TValue> value)
            => _dictionary.TryGetValue(key, out value);

        /// <summary>
        /// Not supported. Use SetAsync and WaitAsync instead.
        /// </summary>
        /// <param name="key"></param>
        public TaskCompletionSource<TValue> this[TKey key]
        {
            get
            {
                if (key == null)
                    throw new ArgumentNullException(nameof(key));

                return _dictionary.TryGetValue(key, out var value) ? value : null;
            }

            #region NotSupported

            set => throw NotSupported();

            #endregion
        }

        /// <inheritdoc/>
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>.Keys
            => ((IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>) _dictionary).Keys;

        /// <inheritdoc/>
        IEnumerable<TaskCompletionSource<TValue>> IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>.Values
            => ((IReadOnlyDictionary<TKey, TaskCompletionSource<TValue>>) _dictionary).Values;

        /// <inheritdoc/>
        ICollection<TKey> IDictionary<TKey, TaskCompletionSource<TValue>>.Keys
            => ((IDictionary<TKey, TaskCompletionSource<TValue>>) _dictionary).Keys;

        /// <inheritdoc/>
        ICollection<TaskCompletionSource<TValue>> IDictionary<TKey, TaskCompletionSource<TValue>>.Values
            => ((IDictionary<TKey, TaskCompletionSource<TValue>>) _dictionary).Values;

        #region NotSupported

        /// <summary>
        /// Not supported. Use TryRemoveAsync instead.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        bool IDictionary<TKey, TaskCompletionSource<TValue>>.Remove(TKey key) => throw NotSupported();

        /// <summary>
        /// Not supported. Use TryRemoveAsync instead.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        bool ICollection<KeyValuePair<TKey, TaskCompletionSource<TValue>>>.Remove(KeyValuePair<TKey, TaskCompletionSource<TValue>> item) => throw NotSupported();

        /// <summary>
        /// Not supported. Use SetAsync instead.
        /// </summary>
        /// <param name="item"></param>
        public void Add(KeyValuePair<TKey, TaskCompletionSource<TValue>> item) => throw NotSupported();

        /// <summary>
        /// Not supported. Use SetAsync instead.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Add(TKey key, TaskCompletionSource<TValue> value) => throw NotSupported();

        #endregion
    }
}