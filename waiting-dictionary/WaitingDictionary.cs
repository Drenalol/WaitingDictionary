using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public class WaitingDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TaskCompletionSource<TValue>>>, IDisposable
    {
        private readonly ConcurrentDictionary<TKey, TaskCompletionSource<TValue>> _dictionary;
        private readonly AsyncLock _asyncLock;
        private readonly CancellationTokenSource _internalCts;
        private readonly MiddlewareBuilder<TValue> _middleware;

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitingDictionary{TKey,TValue}"/> class.
        /// </summary>
        public WaitingDictionary()
        {
            _dictionary = new ConcurrentDictionary<TKey, TaskCompletionSource<TValue>>();
            _asyncLock = new AsyncLock();
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

        /// <summary>
        /// Gets the number of key/value pairs contained in the <see cref="WaitingDictionary{TKey,TValue}"/>.
        /// </summary>
        public int Count => _dictionary.Count;
        
        /// <summary>
        /// Removes all keys and values from the <see cref="WaitingDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear() => _dictionary.Clear();

        /// <summary>
        /// Determines whether the <see cref="WaitingDictionary{TKey, TValue}"/> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="WaitingDictionary{TKey, TValue}"/></param>
        /// <returns></returns>
        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);
        
        /// <summary>
        /// Attempts to remove and return the value with the specified key from the <see cref="WaitingDictionary{TKey, TValue}"/>.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        public bool TryRemove(TKey key, out TaskCompletionSource<TValue> value) => _dictionary.TryRemove(key, out value);

        /// <summary>
        /// Begins an asynchronous request to receive <see cref="Task{TValue}"/> associated with the specified key.
        /// </summary>
        /// <param name="key">The key of the element.</param>
        /// <param name="token">A cancellation token to observe.</param>
        /// <returns><see cref="Task{TValue}"/></returns>
        /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken"/> is canceled.</exception>
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
            using (await _asyncLock.LockAsync())
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
        /// Asynchronously completing task returned by <see cref="WaitAsync"/> with a value associated with the specified key.
        /// </summary>
        /// <param name="key">The key of the element.</param>
        /// <param name="value">Value element that specified with key.</param>
        /// <exception cref="InvalidOperationException">If item with the same key has already been added in dictionary and Duplication Middleware was not set it.</exception>
        public async Task SetAsync(TKey key, TValue value)
        {
            var result = value;

            // From MSDN: ConcurrentDictionary<TId,TValue> is designed for multi-threaded scenarios.
            // You do not have to use locks in your code to add or remove items from the collection.
            // However, it is always possible for one thread to retrieve a value, and another thread
            // to immediately update the collection by giving the same id a new value.
            using (await _asyncLock.LockAsync())
            {
                if (_dictionary.TryRemove(key, out var tcs))
                {
                    if (tcs.Task.Status == TaskStatus.RanToCompletion)
                    {
                        if (_middleware?.DuplicateActionInSet == null)
                            throw new InvalidOperationException($"An item with the same key ({key}) has already been added.");

                        var oldValue = await tcs.Task;
                        var newValue = _middleware.DuplicateActionInSet(oldValue, value);

                        result = newValue;
                        tcs = InternalCreateTcs(key);
                    }
                }
                else
                    tcs = InternalCreateTcs(key);

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
    }
}