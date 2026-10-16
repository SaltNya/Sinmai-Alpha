using System;
using System.Threading;

namespace SinmaiAlpha.Notes.Libs;

// A call owns its routing state. Nested calls (including native-chart calls)
// cannot inherit it, and finalizers restore the outer call after exceptions.
internal sealed class OwnerCallScope<T> where T : class
{
    private sealed class Lease
    {
        internal object Owner;
        internal T Value;
        internal Lease Previous;
    }
    private readonly ThreadLocal<Lease> current = new();
    internal T Current => current.Value?.Value;
    internal T For(object owner) => current.Value is Lease lease && ReferenceEquals(lease.Owner, owner) ? lease.Value : null;
    internal object Enter(object owner, T value)
    {
        var lease = new Lease { Owner = owner, Value = value, Previous = current.Value };
        current.Value = lease;
        return lease;
    }
    internal void Exit(object state)
    {
        if (state is Lease lease && ReferenceEquals(current.Value, lease)) current.Value = lease.Previous;
    }
    internal void Clear() => current.Value = null;
}
