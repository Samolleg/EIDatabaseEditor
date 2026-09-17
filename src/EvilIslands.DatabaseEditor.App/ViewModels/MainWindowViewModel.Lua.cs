using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private LuaDataWorkspace? _luaWorkspace;
    public LuaDataWorkspace? LuaWorkspace => _luaWorkspace;
    public bool HasPendingLuaChanges => _luaWorkspace?.HasChanges == true;

    public void ConnectLua(string folder)
    {
        if (HasPendingLuaChanges) throw new InvalidOperationException("Сначала сохраните или отмените подготовленные изменения Lua.");
        var next = LuaDataWorkspace.Open(folder);
        if (next.Documents.Count == 0) throw new InvalidOperationException(string.Join(Environment.NewLine, next.Problems));
        if (_luaWorkspace is not null) _luaWorkspace.Changed -= LuaWorkspaceChanged;
        _luaWorkspace = next; _luaWorkspace.Changed += LuaWorkspaceChanged;
        LuaWorkspaceChanged(this, EventArgs.Empty);
    }
    private void LuaWorkspaceChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LuaWorkspace));
        OnPropertyChanged(nameof(HasPendingLuaChanges));
    }

    private sealed record LuaGridEditFrame(LuaSourceDocument Document, string Before, string After) : WorkspaceEditFrame;
    public void RecordLuaGridEdit(LuaSourceDocument doc, string before)
    {
        if (before == doc.Text) return;
        _undo.Push(new LuaGridEditFrame(doc, before, doc.Text)); _redo.Clear();
        StatusText = "Изменены параметры Lua. Для записи нажмите «Сохранить» (Ctrl+S).";
        LuaWorkspaceChanged(this, EventArgs.Empty);
        RaiseCommandStates();
    }
    private void SaveGridLua()
    {
        if (LuaWorkspace is not { } workspace) return;
        foreach (var doc in workspace.Documents.Where(d => d.HasChanges)) doc.VerifyUnchanged();
        foreach (var doc in workspace.Documents.Where(d => d.HasChanges).ToArray()) workspace.Save(doc);
        _undo.Clear(); _redo.Clear(); RaiseCommandStates();
    }

    public void RecordLuaGridBatch(IReadOnlyDictionary<LuaSourceDocument, string> before)
    {
        var frames = before.Where(p => p.Key.Text != p.Value)
            .Select(p => (WorkspaceEditFrame)new LuaGridEditFrame(p.Key, p.Value, p.Key.Text)).ToArray();
        if (frames.Length == 0) return;
        _undo.Push(new CompositeEditFrame(frames, "Вставка Lua отменена.", "Вставка Lua повторена."));
        _redo.Clear(); StatusText = "Значения Lua вставлены. Для записи нажмите «Сохранить».";
        LuaWorkspaceChanged(this, EventArgs.Empty); RaiseCommandStates();
    }
}
