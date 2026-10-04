# WaitingDictionary

Represents a thread-safe collection of keys and `TaskCompletionSource<T>` as values, managed with two methods: `WaitAsync` and `SetAsync`.

[![NuGet Pre Release](https://img.shields.io/nuget/vpre/WaitingDictionary.svg?style=for-the-badge&logo=appveyor)](https://www.nuget.org/packages/WaitingDictionary/)
[![netstandard 2.1](https://img.shields.io/badge/netstandard-2.1-brightgreen.svg?style=for-the-badge&logo=appveyor)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
![CI](https://github.com/Drenalol/WaitingDictionary/actions/workflows/ci.yml/badge.svg)

![Image of Waiting Guy](https://cs9.pikabu.ru/post_img/big/2017/01/28/7/1485602875186056892.jpg)

## What is it for

Sometimes a value produced on one thread/task must be delivered to a caller waiting on another. `WaitingDictionary<T, V>` is a rendezvous point for them:

- `WaitAsync(key)` either returns a result that is already stored under the key, or parks the caller until someone provides it;
- `SetAsync(key, value)` either delivers the value to the caller already waiting on the key, or stores it for the next one.

Typical uses: request coalescing (one in-flight fetch per key, all callers await it), signaling between producers and consumers, per-key async locks with a payload.

## Installation

```
dotnet add package WaitingDictionary
```

## Quick start

Just a mock model:
```c#
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
```

Base example - wait first, set from somewhere else:
```c#
using var dict = new WaitingDictionary<int, Mock>();
// Starting waiting Task<Mock> (blocks until completed or cancelled) by key 1337
var result = await dict.WaitAsync(1337, CancellationToken.None);
//
//
// Some operations on other threads, or tasks..
//
//
// Set result by key 1337, it will release the previously started task that got it by WaitAsync
await dict.SetAsync(1337, new Mock());
```

Another example - set first, the value is stored until someone picks it up:
```c#
using var dict = new WaitingDictionary<int, Mock>();
// Set result by key 1337
await dict.SetAsync(1337, new Mock());
//
//
// Some operations on other threads, or tasks..
//
//
// Got result immediately
var result = await dict.WaitAsync(1337, CancellationToken.None);
```

## Behavior rules

- **Automatic removal.** `SetAsync` and `WaitAsync` automatically remove the element when it is completed. A stored result is consumed by `WaitAsync` exactly once.
- **One waiter per key.** If a key already has a waiter in progress, the next `WaitAsync` throws `InvalidOperationException`. The exception surfaces as a faulted task, since `WaitAsync` is an async method.
- **One set per key.** If a key already holds a completed result, the next `SetAsync` throws `ArgumentException` - unless a duplication middleware is registered.
- **Cancellation never poisons the key.** When a wait is canceled by token (or by `Dispose`), the dead item is dropped; the following `WaitAsync`/`SetAsync` for the same key starts from a fresh item and works normally.
- **Disposal.** `Dispose` cancels all pending waits, and `Dispose` is idempotent. Using the dictionary afterwards throws `ObjectDisposedException`. Register it as a singleton in your DI container.

## API

| Member | Description |
| --- | --- |
| `Task<TValue> WaitAsync(TKey key, CancellationToken token = default)` | Waits for (or immediately returns) the value stored by `SetAsync`. |
| `Task SetAsync(TKey key, TValue value, bool ignoreError = false)` | Completes the pending wait or stores the value for the next one. |
| `Task<bool> TryRemoveAsync(TKey key)` | Removes the item; a waiter on it is canceled. |
| `IEnumerable<KVP> Filter(Func<KVP, bool> predicate)` | Snapshot-based enumeration with a predicate. |
| `int Count` / `bool ContainsKey(TKey)` / `bool TryGetValue(TKey, out WaitItem<TValue>)` | Read-only inspection. The dictionary also implements `IReadOnlyDictionary<TKey, WaitItem<TValue>>`. |

Note: `WaitItem<TValue>` exposes the underlying `TaskCompletionSource<TValue> DelayedTask` and the creation timestamp `PlaceDateTime` (UTC).

## Stale items cleanup

A wait that never receives `SetAsync` and is never canceled would live forever. Set `staleItemTimeToLiveMs` to let a background job cancel and remove such items (the stored results age the same way):

```c#
// Items older than 30 seconds get canceled; cleanup runs every 30 seconds,
// so an item is cleared no earlier than 30 s and no later than 60 s after creation
using var dict = new WaitingDictionary<int, Mock>(staleItemTimeToLiveMs: 30_000);
```

## MiddlewareBuilder

```c#
var middlewares =
    new MiddlewareBuilder<Mock>()
        // Will run on every completion SetAsync
        // Default: none
        .RegisterCompletionActionInSet(() => Console.WriteLine("Set completed"))
        // Will run on every completion WaitAsync
        // Default: none
        .RegisterCompletionActionInWait(() => Console.WriteLine("Wait completed"))
        // Will run on every duplicated element found while executing SetAsync
        // Default: throw exception
        .RegisterDuplicateActionInSet((old, @new) => new Mock(old, @new)) // merge two values
        // Will run on every WaitAsync cancellation
        // Default: TrySetCanceled
        // hasOwnToken == true: the wait used its own cancellable token,
        // false: the cancellation comes from Dispose()
        .RegisterCancellationActionInWait((tcs, hasOwnToken) => tcs.SetException(new Exception("Something went wrong")));

var dict = new WaitingDictionary<int, Mock>(middlewares);
```

The builder is captured as a snapshot when the dictionary is constructed: registering middleware afterwards does not affect an already created dictionary, and each registration can be done only once (second call throws `InvalidOperationException`).

## Design and thread safety

- `ConcurrentDictionary` makes single operations safe; a `Nito.AsyncEx.AsyncLock` makes the check-then-act sequences between `WaitAsync`, `SetAsync`, `TryRemoveAsync` and the cleanup job atomic as a whole.
- Every `TaskCompletionSource` is created with `RunContinuationsAsynchronously`, so continuations of a waiter never run synchronously inside the setter's critical section.
- The cleanup timer callback never throws into the thread pool and re-validates each item under the lock before touching it.

## What's new in 2.0

Breaking changes:

- `IDictionary<TKey, …>` / `ICollection<…>` implementations are removed (their members mostly threw `NotSupportedException`). The dictionary now implements only `IReadOnlyDictionary<TKey, WaitItem<TValue>>` + `IDisposable`; the indexer is read-only and throws `KeyNotFoundException` for a missing key.
- `Add`, `Clear` and the dictionary-style `Remove` are gone - use `SetAsync`, `TryRemoveAsync` and `Dispose`.
- `WaitAsync` no longer throws a stored cancellation into a new waiter, and `SetAsync` no longer throws `InvalidOperationException` on a canceled ghost item - dead items are replaced transparently.
- The `[Obsolete]` constructors are removed; use the main constructor with optional arguments.
- A canceled wait now also removes its item from the dictionary.

Fixes and improvements:

- Stale-items cleanup no longer crashes the process when an item disappears concurrently, and no longer races with `Wait`/`Set`.
- `Dispose` is idempotent and waits for a running cleanup callback.
- `TryRemoveAsync` now cancels the orphaned waiter instead of letting it hang forever.
- `TaskCompletionSource` is always created with `RunContinuationsAsynchronously` (avoids running waiter continuations under the internal lock; `TaskCreationOptions` passed to the constructor is preserved).
- `System.Runtime.CompilerServices.IsExternalInit` polyfill allows modern C# records on `netstandard2.1`.

## Development

```
dotnet build waiting-dictionary.sln -c Release
dotnet test waiting-dictionary.tests -c Release
```

Library targets `netstandard2.1` (LangVersion latest), tests target `net10.0` (NUnit 4).

## Publishing (maintainers)

Every green push to `master` publishes the package to two registries:

- **nuget.org** via [NuGet Trusted Publishing (OIDC)](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) - there is no API key stored in the repository.
- **GitHub Packages** (`nuget.pkg.github.com/Drenalol`) with the built-in `GITHUB_TOKEN`, so the package also shows up on the repository's *Packages* page.

The package version is taken from `<Version>` in `waiting-dictionary.csproj`. To ship a new package: bump `<Version>` in the csproj, commit and push to `master`. The `publish` job in `ci.yml` runs after a successful build + test, pushes to GitHub Packages, exchanges the job's OIDC token for a short-lived API key with `NuGet/login@v1`, and pushes to nuget.org - both with `--skip-duplicate`. Consumers should install from nuget.org; the GitHub Packages copy mirrors it.

One-time setup:

1. On nuget.org: user menu -> *Trusted Publishing* -> *Add policy*: owner `Drenalol`, repository `WaitingDictionary`, workflow file `ci.yml` (filename only). Environment: leave empty unless the workflow uses `environment:`.
2. In the GitHub repository: add a `NUGET_USER` secret (your nuget.org profile name, not email) and remove the obsolete `NUGET_AUTH_TOKEN` secret.

## License

MIT (c) Bogdan Yanysh
