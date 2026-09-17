using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App;

public sealed class LuaDataView : UserControl
{
    private readonly TextBlock _source = LuaToolWindow.Paragraph("");
    private readonly TextBlock _status = LuaToolWindow.Paragraph("");
    private readonly TextBlock _hint = LuaToolWindow.Paragraph("");
    private readonly ListBox _groups = new() { DisplayMemberPath = nameof(LuaBoundGroup.Caption), Margin = new(4) };
    private readonly DataGrid _entries = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, Margin = new(4) };
    private readonly ComboBox _files = new() { MinWidth = 200, Margin = new(4), DisplayMemberPath = "Caption" };
    private readonly Button _edit, _add, _remove, _save, _undo, _redo;
    private MainWindowViewModel? _vm;
    private RecordRowViewModel? _record;
    private string? _attemptedSource;
    private bool _refreshing;
    private sealed record FileChoice(LuaSourceDocument Document)
    { public string Caption => Path.GetFileName(Document.Path) + (Document.HasChanges ? " · изменён" : ""); }
    internal string StatusText => _status.Text;
    internal IReadOnlyList<LuaBoundGroup> Groups => (_groups.ItemsSource as IReadOnlyList<LuaBoundGroup>) ?? [];

    public LuaDataView()
    {
        var layout = new DockPanel { Margin = new(6) }; Content = layout;
        var tools = new WrapPanel(); DockPanel.SetDock(tools, Dock.Top); layout.Children.Add(tools);
        tools.Children.Add(LuaToolWindow.ActionButton("Подключить Lua…", Connect));
        tools.Children.Add(_files);
        _save = LuaToolWindow.ActionButton("Сохранить Lua…", Save); tools.Children.Add(_save);
        _undo = LuaToolWindow.ActionButton("↶", () => Run(() => { if (SelectedDocument is { } d) _vm?.LuaWorkspace?.Undo(d); })); _undo.ToolTip = "Отменить последнее изменение выбранного Lua-файла"; tools.Children.Add(_undo);
        _redo = LuaToolWindow.ActionButton("↷", () => Run(() => { if (SelectedDocument is { } d) _vm?.LuaWorkspace?.Redo(d); })); _redo.ToolTip = "Повторить изменение Lua"; tools.Children.Add(_redo);
        tools.Children.Add(LuaToolWindow.ActionButton("Сохранить копию…", Export));
        DockPanel.SetDock(_source, Dock.Top); layout.Children.Add(_source);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom); bottom.Children.Add(_status); bottom.Children.Add(_hint);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(0.8, GridUnitType.Star), MinWidth = 175 }); grid.ColumnDefinitions.Add(new() { Width = new(6) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
        grid.Children.Add(_groups); var split = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch }; Grid.SetColumn(split, 1); grid.Children.Add(split);
        var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Top); right.Children.Add(actions);
        _edit = LuaToolWindow.ActionButton("Изменить…", Edit); actions.Children.Add(_edit);
        _add = LuaToolWindow.ActionButton("Добавить…", Add); actions.Children.Add(_add);
        _remove = LuaToolWindow.ActionButton("Удалить запись", Remove); actions.Children.Add(_remove);
        foreach (var (header, binding, size) in new[] { ("Запись", "Caption", 1d), ("Значения", "Summary", 1.5), ("Источник", "Location", .65) })
            _entries.Columns.Add(new DataGridTextColumn { Header = header, ElementStyle = (Style)FindResource("CenteredGridText"), Binding = new Binding(binding), Width = new DataGridLength(size, DataGridLengthUnitType.Star) });
        right.Children.Add(_entries); layout.Children.Add(grid);
        _entries.MouseDoubleClick += (_, _) => { if (_entries.SelectedItem is LuaBoundEntry) Edit(); };
        _groups.SelectionChanged += (_, _) => SelectGroup(); _entries.SelectionChanged += (_, _) => UpdateButtons(); _files.SelectionChanged += (_, _) => UpdateButtons();
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); }; UpdateButtons();
    }
    private LuaSourceDocument? SelectedDocument => (_files.SelectedItem as FileChoice)?.Document;
    private void Attach() { Detach(); _vm = DataContext as MainWindowViewModel; if (_vm is not null) _vm.PropertyChanged += Changed; if (IsVisible) Refresh(); }
    private void Detach() { if (_vm is not null) _vm.PropertyChanged -= Changed; if (_record is not null) _record.PropertyChanged -= RecordChanged; _vm = null; _record = null; }
    private void RecordChanged(object? sender, PropertyChangedEventArgs e)
    { if (IsVisible && e.PropertyName == nameof(RecordRowViewModel.SearchText)) Refresh(); }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRecord) or nameof(MainWindowViewModel.SourcePath) or nameof(MainWindowViewModel.LuaWorkspace))
            if (IsVisible) Refresh();
    }
    internal void Refresh()
    {
        if (_refreshing || _vm is null) return; _refreshing = true;
        try
        {
            if (_attemptedSource != _vm.SourcePath)
            {
                _attemptedSource = _vm.SourcePath;
                string? nearby = LuaDataWorkspace.FindNearby(_vm.SourcePath);
                if (!_vm.HasPendingLuaChanges && nearby is not null && _vm.LuaWorkspace?.DirectoryPath != nearby) _vm.ConnectLua(nearby);
            }
            var workspace = _vm.LuaWorkspace;
            string? previousGroup = (_groups.SelectedItem as LuaBoundGroup)?.Spec.Name;
            var previousDoc = SelectedDocument;
            _files.ItemsSource = workspace?.Documents.Select(d => new FileChoice(d)).ToArray();
            _files.SelectedItem = (_files.ItemsSource as FileChoice[])?.FirstOrDefault(f => f.Document == previousDoc) ?? (_files.ItemsSource as FileChoice[])?.FirstOrDefault();
            var selected = _vm.SelectedRecord;
            if (!ReferenceEquals(_record, selected))
            {
                if (_record is not null) _record.PropertyChanged -= RecordChanged;
                _record = selected; if (_record is not null) _record.PropertyChanged += RecordChanged;
            }
            _groups.ItemsSource = workspace is null || selected is null ? [] : workspace.ForRecord(ModelRecord.Capture(selected.TableKey, selected.CoreRecord));
            _groups.SelectedItem = Groups.FirstOrDefault(g => g.Spec.Name == previousGroup) ?? Groups.FirstOrDefault();
            _source.Text = workspace is null ? "Подключите папку lua нужного мода. Рядом с database.res источники находятся автоматически."
                : "Lua: " + workspace.DirectoryPath + "\n" + (selected is null ? "Выберите запись БД." : "БД: " + _vm.SourcePath);
            if (workspace is not null && LuaDataWorkspace.FindNearby(_vm.SourcePath) != workspace.DirectoryPath)
                _source.Text += "\nLua подключён отдельно. Проверьте соответствие выбранной БД и папки Lua.";
            _status.Text = workspace is null ? "" : Groups.Count == 0 ? "Выберите прототип монстра, предмет или прототип заклинания. NPC связывается через его прототип монстра."
                : workspace.HasChanges ? "Есть подготовленные изменения Lua. Сохранение БД их не записывает; выберите файл и нажмите «Сохранить Lua…»." : "Lua прочитан. Изменений не подготовлено.";
            if (workspace?.Problems.Count > 0) _status.Text += "\n" + string.Join("\n", workspace.Problems);
            SelectGroup();
        }
        catch (Exception ex) { _status.Text = "Не удалось подключить Lua: " + ex.Message; }
        finally { _refreshing = false; UpdateButtons(); }
    }
    private void SelectGroup()
    {
        var group = _groups.SelectedItem as LuaBoundGroup;
        _entries.ItemsSource = group?.Entries; _entries.SelectedIndex = group?.Entries.Count > 0 ? 0 : -1;
        _hint.Text = group is null ? "" : group.Spec.Name + " · ключ: " + group.LinkKey + "\n" +
            (group.Problem ?? (group.Entries.Count == 0 ? "Записи в Lua нет. Можно добавить её явно. " : "")) + group.Spec.Hint +
            (group.Entries.Count > 1 ? "\nНайдено несколько строк с этим ключом. Каждая редактируется отдельно." : "") +
            (group.Spec.Child is null ? "" : "\nРазделов zones в подключённом файле: " + (_vm?.LuaWorkspace?.ZoneRuleCount ?? 0));
        if (group?.Document is { } doc) _files.SelectedItem = (_files.ItemsSource as FileChoice[])?.FirstOrDefault(f => f.Document == doc);
        if (group?.Spec.EnumName is not null && _vm?.LuaWorkspace?.Schema(group.Spec).Count == 0)
            _hint.Text += "\nПодписи полей из main.lua недоступны; показаны числовые индексы.";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var g = _groups.SelectedItem as LuaBoundGroup;
        _edit.IsEnabled = _remove.IsEnabled = _entries.SelectedItem is LuaBoundEntry;
        _add.IsEnabled = g is { Problem: null, Table: not null } && (g.Spec.IsArray || g.Entries.Count == 0);
        _save.IsEnabled = SelectedDocument?.HasChanges == true;
        _undo.IsEnabled = SelectedDocument?.CanUndo == true; _redo.IsEnabled = SelectedDocument?.CanRedo == true;
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }
    private void Connect() => Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку lua или корень мода", InitialDirectory = _vm?.LuaWorkspace?.DirectoryPath ?? Path.GetDirectoryName(_vm?.SourcePath) ?? "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || _vm is null) return;
        if (_vm.HasPendingLuaChanges)
        {
            var confirm = new ConfirmDialog("Переподключить Lua?", "Подготовленные изменения Lua будут потеряны.", "Сохраните Lua или копию перед сменой источника.", "Отменить изменения и подключить") { Owner = Window.GetWindow(this) };
            if (confirm.ShowDialog() != true) return; _vm.LuaWorkspace!.Discard();
        }
        _vm.ConnectLua(dialog.FolderName); _attemptedSource = _vm.SourcePath; Refresh();
    });
    private void Edit() => Run(() =>
    {
        if (_entries.SelectedItem is not LuaBoundEntry entry || _groups.SelectedItem is not LuaBoundGroup group || _vm?.LuaWorkspace is not { } workspace) return;
        string before = entry.Document.Text[entry.Field.Value.Start..entry.Field.Value.End];
        var dialog = new LuaEntryEditorDialog(workspace, group, before) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.ResultText == before) return;
        string after = dialog.ResultText; int prefix = 0, suffix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        workspace.Edit(entry, [new(entry.Field.Value.Start + prefix, before.Length - prefix - suffix, after.Substring(prefix, after.Length - prefix - suffix))],
            "было: " + before + "\nстало: " + after);
        Refresh();
    });
    private void Add() => Run(() =>
    {
        if (_groups.SelectedItem is not LuaBoundGroup group || _vm?.LuaWorkspace is not { } workspace) return;
        string? initial = group.Spec.Name switch { "NpcCombatConsumables" => "{}", "DeathFearExcludedPrototypes" => "true", "ForbiddenItems" => "{ " + LuaLiteralReader.Quote(group.LinkKey) + ", 0 }", _ => null };
        if (initial is null)
        {
            if (workspace.Templates(group).Count == 0) throw new InvalidOperationException("В этой таблице нет записи-образца. Подключите Lua с заполненной таблицей: параметры новой записи нельзя определить автоматически.");
            var template = new LuaTemplateDialog(workspace, group) { Owner = Window.GetWindow(this) };
            if (template.ShowDialog() != true || template.Literal is null) return; initial = template.Literal;
        }
        var dialog = new LuaEntryEditorDialog(workspace, group, initial) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) workspace.Add(group, dialog.ResultText); Refresh();
    });
    private void Remove() => Run(() =>
    {
        if (_entries.SelectedItem is not LuaBoundEntry entry || _vm?.LuaWorkspace is not { } workspace) return;
        var confirm = new ConfirmDialog("Удалить связанную запись Lua?", entry.Spec.Name + " / " + entry.LinkKey + " · " + entry.Location,
            "Будет удалена только выбранная строка. Изменение можно отменить до сохранения Lua.", "Удалить запись") { Owner = Window.GetWindow(this) };
        if (confirm.ShowDialog() == true) workspace.Remove(entry); Refresh();
    });
    private void Save() => Run(() =>
    {
        if (SelectedDocument is not { HasChanges: true } doc || _vm?.LuaWorkspace is not { } workspace) return;
        if (new LuaSaveReviewDialog(doc) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        string backup = workspace.Save(doc); Refresh(); _status.Text = "Сохранён " + doc.Path + "\nРезервная копия: " + backup;
    });
    private void Export() => Run(() =>
    {
        if (SelectedDocument is not { } doc) return;
        var dialog = new SaveFileDialog { Title = "Сохранить Lua в новый файл", FileName = Path.GetFileNameWithoutExtension(doc.Path) + "-edited.lua", Filter = "Lua (*.lua)|*.lua", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        // A copy is always a new file; it cannot silently become an overwrite path bypassing conflict checks.
        using (var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write)) { file.Write(doc.Bytes); file.Flush(true); }
        _status.Text = "Копия сохранена: " + dialog.FileName + ". Исходный Lua остаётся с подготовленными изменениями.";
    });
}
