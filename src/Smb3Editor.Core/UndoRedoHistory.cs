namespace Smb3Editor.Core;

public sealed class UndoRedoHistory<T>
    where T : class
{
    private readonly int _capacity;
    private readonly Stack<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public UndoRedoHistory(int capacity = 200)
    {
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(T previous)
    {
        _undo.Push(previous);
        _redo.Clear();
        if (_undo.Count <= _capacity)
        {
            return;
        }

        var retained = _undo.Take(_capacity).Reverse().ToArray();
        _undo.Clear();
        foreach (var item in retained)
        {
            _undo.Push(item);
        }
    }

    public T Undo(T current)
    {
        if (!CanUndo)
        {
            return current;
        }

        _redo.Push(current);
        return _undo.Pop();
    }

    public T Redo(T current)
    {
        if (!CanRedo)
        {
            return current;
        }

        _undo.Push(current);
        return _redo.Pop();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

