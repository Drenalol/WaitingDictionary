using System;
using System.Threading;
using System.Threading.Tasks;

namespace Drenalol.WaitingDictionary;

/// <summary>
/// Immutable view of the middleware registrations, captured when the <see cref="WaitingDictionary{TKey,TValue}"/>
/// is constructed. Mutating the builder afterwards does not affect an already created dictionary.
/// </summary>
internal sealed record MiddlewareSnapshot<TValue>(
    Func<TValue, TValue, TValue>? DuplicateActionInSet,
    Action? CompletionActionInSet,
    Action<TaskCompletionSource<TValue>, bool>? CancellationActionInWait,
    Action? CompletionActionInWait
);

/// <summary>
/// Middleware Builder
/// </summary>
/// <typeparam name="TValue"></typeparam>
public sealed class MiddlewareBuilder<TValue>
{
    internal Func<TValue, TValue, TValue>? DuplicateActionInSet { get; private set; }
    internal Action? CompletionActionInSet { get; private set; }
    internal Action<TaskCompletionSource<TValue>, bool>? CancellationActionInWait { get; private set; }
    internal Action? CompletionActionInWait { get; private set; }

    internal MiddlewareSnapshot<TValue> Snapshot() => new(DuplicateActionInSet, CompletionActionInSet, CancellationActionInWait, CompletionActionInWait);

    /// <summary>
    /// Registers a middleware that is executed when duplicate found in <see cref="WaitingDictionary{TKey,TValue}.SetAsync"/>.
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">If already registered</exception>
    public MiddlewareBuilder<TValue> RegisterDuplicateActionInSet(Func<TValue, TValue, TValue> action)
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
    /// The second callback parameter is <see langword="true"/> when the wait used its own cancellable token
    /// and <see langword="false"/> when the cancellation comes from <see cref="WaitingDictionary{TKey,TValue}.Dispose"/>.
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">If already registered</exception>
    public MiddlewareBuilder<TValue> RegisterCancellationActionInWait(Action<TaskCompletionSource<TValue>, bool> action)
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
