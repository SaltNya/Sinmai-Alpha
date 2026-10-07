using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SinmaiAlpha.Notes.Libs;

// Chart identity is the reader/note instance, never a note index or player order.
// The scoped selection lets existing native callbacks share one chart's tables;
// nested calls and exceptional exits restore the caller's selection.
internal sealed class RuntimeChartStore<T> where T : class, new()
{
    private ConditionalWeakTable<object, T> readers = new();
    private ConditionalWeakTable<object, T> notes = new();
    private ConditionalWeakTable<object, T> owners = new();
    private readonly ThreadLocal<T> selected = new();
    private T fallback = new();
    private int generation;
    private sealed class SelectionLease
    {
        internal T Previous;
        internal int Generation;
    }
    internal bool HasSelection => selected.Value != null;
    internal T Current => selected.Value ?? fallback;
    internal T Reader(object reader, bool fresh = false)
    {
        if (reader == null) return fallback;
        if (fresh) readers.Remove(reader);
        return readers.GetValue(reader, _ => new T());
    }
    internal void BindNote(object note, T state)
    {
        if (note == null) return;
        notes.Remove(note); notes.Add(note, state);
    }
    internal T Note(object note) => note != null && notes.TryGetValue(note, out var state) ? state : Current;
    internal T BindOwner(object owner, object note)
    {
        var state = Note(note);
        owners.Remove(owner); owners.Add(owner, state);
        return state;
    }
    internal bool TryOwner(object owner, out T state) => owners.TryGetValue(owner, out state);
    internal object Enter(T state)
    {
        if (ReferenceEquals(Current, state)) return null;
        var previous = new SelectionLease { Previous = selected.Value, Generation = generation };
        selected.Value = state;
        return previous;
    }
    internal void Exit(object previous)
    {
        if (previous is SelectionLease lease && lease.Generation == generation) selected.Value = lease.Previous;
    }
    internal void Reset()
    {
        readers = new(); notes = new(); owners = new();
        generation++;
        fallback = new(); selected.Value = null;
    }
}
