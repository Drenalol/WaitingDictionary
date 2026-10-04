using System.ComponentModel;

namespace System.Runtime.CompilerServices;

// Required to compile positional records (init-only setters) when targeting netstandard2.1.
// The runtime provides its own copy since .NET 5+, so the polyfill is internal and assembly-local.
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit;
