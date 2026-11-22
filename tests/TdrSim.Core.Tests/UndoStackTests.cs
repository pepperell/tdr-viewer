using TdrSim.Core.Project;

namespace TdrSim.Core.Tests;

/// <summary>M5 step 4: the snapshot stack semantics the shell's undo/redo rides on.</summary>
public class UndoStackTests
{
    [Fact]
    public void CommitUndoRedoSequence()
    {
        var stack = new UndoStack();
        stack.Reset("A");
        Assert.True(stack.Commit("B"));
        Assert.True(stack.Commit("C"));
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);

        Assert.Equal("B", stack.Undo());
        Assert.Equal("A", stack.Undo());
        Assert.False(stack.CanUndo);
        Assert.Null(stack.Undo());

        Assert.Equal("B", stack.Redo());
        Assert.Equal("C", stack.Redo());
        Assert.False(stack.CanRedo);
        Assert.Null(stack.Redo());
    }

    [Fact]
    public void IdenticalCommitIsACompleteNoOp()
    {
        var stack = new UndoStack();
        stack.Reset("A");
        stack.Commit("B");
        stack.Undo(); // Current = A, redo = [B]

        // A burst that lands where it started must not create a step - and must not
        // destroy the pending redo.
        Assert.False(stack.Commit("A"));
        Assert.True(stack.CanRedo);
        Assert.Equal("B", stack.Redo());
    }

    [Fact]
    public void NewCommitAfterUndoClearsRedo()
    {
        var stack = new UndoStack();
        stack.Reset("A");
        stack.Commit("B");
        stack.Undo();
        Assert.True(stack.Commit("C"));
        Assert.False(stack.CanRedo);
        Assert.Equal("A", stack.Undo());
    }

    [Fact]
    public void CapacityEvictsTheOldestStep()
    {
        var stack = new UndoStack(capacity: 3);
        stack.Reset("S0");
        for (int k = 1; k <= 5; k++)
            stack.Commit($"S{k}");

        Assert.Equal("S4", stack.Undo());
        Assert.Equal("S3", stack.Undo());
        Assert.Equal("S2", stack.Undo());
        Assert.Null(stack.Undo()); // S0/S1 evicted
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var stack = new UndoStack();
        stack.Reset("A");
        stack.Commit("B");
        stack.Undo();
        stack.Reset("X");

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal("X", stack.Current);
    }
}
