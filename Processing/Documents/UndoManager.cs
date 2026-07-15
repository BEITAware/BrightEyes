using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BrightEyes.Processing.Documents;

public interface IUndoableAction
{
    string Description { get; }
    void Undo();
    void Redo();
}

public sealed class DelegateUndoableAction : IUndoableAction
{
    private readonly Action _undo;
    private readonly Action _redo;

    public DelegateUndoableAction(string description, Action undo, Action redo)
    {
        Description = description;
        _undo = undo;
        _redo = redo;
    }

    public string Description { get; }
    public void Undo() => _undo();
    public void Redo() => _redo();
}

public sealed class UndoManager : INotifyPropertyChanged
{
    private readonly Stack<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();
    private bool _isApplying;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsApplying => _isApplying;
    public string UndoDescription => _undo.TryPeek(out IUndoableAction? action) ? action.Description : "";
    public string RedoDescription => _redo.TryPeek(out IUndoableAction? action) ? action.Description : "";

    public void PushApplied(IUndoableAction action)
    {
        if (_isApplying) return;
        _undo.Push(action);
        _redo.Clear();
        NotifyChanged();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out IUndoableAction? action)) return;
        _isApplying = true;
        try { action.Undo(); }
        finally { _isApplying = false; }
        _redo.Push(action);
        NotifyChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out IUndoableAction? action)) return;
        _isApplying = true;
        try { action.Redo(); }
        finally { _isApplying = false; }
        _undo.Push(action);
        NotifyChanged();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
