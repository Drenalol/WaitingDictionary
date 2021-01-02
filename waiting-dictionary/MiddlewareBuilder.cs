using System;
using System.Threading;
using System.Threading.Tasks;

namespace Drenalol.WaitingDictionary
{
    /// <summary>
    /// Middleware Builder
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    public sealed class MiddlewareBuilder<TValue>
    {
        internal Func<TValue, TValue> DuplicateActionInSet;
        internal Action CompletionActionInSet;
        internal Action<TaskCompletionSource<TValue>> CancellationActionInWait;
        internal Action CompletionActionInWait;

        /// <summary>
        /// Registers a middleware that is executed when duplicate found in <see cref="WaitingDictionary{TKey,TValue}.SetAsync"/>.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If already registered</exception>
        public MiddlewareBuilder<TValue> RegisterDuplicateActionInSet(Func<TValue, TValue> action)
        {
            if (DuplicateActionInSet != null)
                throw new InvalidOperationException($"{nameof(DuplicateActionInSet)} already registered");
            
            DuplicateActionInSet = action;
            return this;
        }

        /// <summary>
        /// Registers a middleware that is executed when <see cref="WaitingDictionary{TKey,TValue}.SetAsync"/> completed.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If already registered</exception>
        public MiddlewareBuilder<TValue> RegisterCompletionActionInSet(Action action)
        {
            if (CompletionActionInSet != null)
                throw new InvalidOperationException($"{nameof(CompletionActionInSet)} already registered");
            
            CompletionActionInSet = action;
            return this;
        }

        /// <summary>
        /// Registers a middleware that is executed if <see cref="WaitingDictionary{TKey,TValue}.WaitAsync"/> canceled by <see cref="CancellationToken"/>.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If already registered</exception>
        public MiddlewareBuilder<TValue> RegisterCancellationActionInWait(Action<TaskCompletionSource<TValue>> action)
        {
            if (CancellationActionInWait != null)
                throw new InvalidOperationException($"{nameof(CancellationActionInWait)} already registered");
            
            CancellationActionInWait = action;
            return this;
        }

        /// <summary>
        /// Registers a middleware that is executed when <see cref="WaitingDictionary{TKey,TValue}.WaitAsync"/> completed.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If already registered</exception>
        public MiddlewareBuilder<TValue> RegisterCompletionActionInWait(Action action)
        {
            if (CompletionActionInWait != null)
                throw new InvalidOperationException($"{nameof(CompletionActionInWait)} already registered");
            
            CompletionActionInWait = action;
            return this;
        }
    }
}