namespace TdrSim.Core.Project;

/// <summary>
/// Snapshot-based undo/redo over serialized documents. The shell commits one snapshot
/// per settled mutation burst; undo/redo just hand back earlier/later snapshots to
/// restore. String identity is the change detector, so a burst that ends where it
/// started (a slider dragged back, a no-op property set) creates no undo step and -
/// deliberately - does not clear redo.
/// </summary>
public sealed class UndoStack(int capacity = 100)
{
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The last committed state - what undo/redo move away from.</summary>
    public string? Current { get; private set; }

    /// <summary>New/Open/load: both stacks clear, this snapshot becomes the baseline.</summary>
    public void Reset(string snapshot)
    {
        _undo.Clear();
        _redo.Clear();
        Current = snapshot;
    }

    /// <summary>
    /// A mutation burst settled on this state. Pushes the previous state onto undo and
    /// clears redo - unless nothing actually changed, which is a complete no-op.
    /// Returns whether a step was recorded.
    /// </summary>
    public bool Commit(string snapshot)
    {
        if (Current is null)
        {
            Current = snapshot;
            return false;
        }

        if (snapshot == Current)
            return false;

        _undo.Add(Current);
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
        _redo.Clear();
        Current = snapshot;
        return true;
    }

    /// <summary>Steps back; returns the snapshot to restore, or null when empty.</summary>
    public string? Undo()
    {
        if (_undo.Count == 0 || Current is null)
            return null;

        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return Current;
    }

    /// <summary>Steps forward; returns the snapshot to restore, or null when empty.</summary>
    public string? Redo()
    {
        if (_redo.Count == 0 || Current is null)
            return null;

        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return Current;
    }
}
