using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal class LuaToolWindow : Window
{
    protected DockPanel Body { get; } = new() { Margin = new(12) };
    public LuaToolWindow(string title)
    {
        Title = title; Width = Math.Min(880, SystemParameters.WorkArea.Width - 32); Height = Math.Min(730, SystemParameters.WorkArea.Height - 32);
        MinWidth = 520; MinHeight = 380; MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Style = (Style)FindResource(typeof(Window)); WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new(0), ResizeBorderThickness = new(6), UseAeroCaptionButtons = false });
        var root = new DockPanel(); Content = new Border { Child = root, Background = (Brush)FindResource("WindowBrush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new(1) };
        var bar = new DockPanel { Height = 42, Background = (Brush)FindResource("SidebarBrush") }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var close = ActionButton("×", Close); close.Width = 42; DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        bar.Children.Add(new TextBlock { Text = title, Margin = new(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        bar.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        root.Children.Add(Body);
    }
    internal static Button ActionButton(string caption, Action action)
    { var button = new Button { Content = caption, Margin = new(4), Padding = new(10, 6, 10, 6) }; button.Click += (_, _) => action(); return button; }
    internal static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(4, 4, 4, 8) };
}

internal sealed class LuaEntryEditorDialog : LuaToolWindow
{
    internal sealed class EditRow
    {
        public required string Field { get; init; }
        public required string Value { get; set; }
        public LuaLiteralKind Kind { get; set; }
        public LuaLiteral? Original { get; init; }
        public LuaField? OriginalField { get; init; }
        public string[] ParentPath { get; init; } = [];
        public required string Key { get; init; }
        public bool Identity { get; init; }
    }
    private readonly LuaDataWorkspace _workspace;
    private readonly LuaBoundGroup _group;
    private readonly string _original;
    private readonly LuaLiteral _root;
    private readonly List<EditRow> _rows = [];
    private readonly List<LuaField> _removed = [];
    private readonly Dictionary<string, List<EditRow>> _newKinds = new(StringComparer.Ordinal);
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _error = Paragraph("");
    private readonly ComboBox _optional = new() { MinWidth = 180, Margin = new(4), DisplayMemberPath = "Label" };
    private readonly ComboBox _kind = new() { ItemsSource = LuaDataWorkspace.ConsumableKinds, SelectedIndex = 0, Width = 160, Margin = new(4) };
    private sealed record OptionalField(string Key, string Label, LuaLiteralKind Kind, string[] ParentPath);
    public string ResultText { get; private set; } = "";
    internal IReadOnlyList<EditRow> Rows => _rows;

    public LuaEntryEditorDialog(LuaDataWorkspace workspace, LuaBoundGroup group, string literal) : base(group.Spec.Title + " · " + group.LinkKey)
    {
        _workspace = workspace; _group = group; _original = literal; _root = LuaLiteralReader.Read(literal);
        var header = Paragraph(group.Spec.Name + (group.Spec.Child is null ? "" : "." + group.Spec.Child) + " → " + group.LinkKey + "\n" + group.Spec.Hint);
        DockPanel.SetDock(header, Dock.Top); Body.Children.Add(header);
        var tools = new WrapPanel(); DockPanel.SetDock(tools, Dock.Top); Body.Children.Add(tools);
        if (group.Spec.Name == "NpcCombatConsumables")
        {
            tools.Children.Add(_kind); tools.Children.Add(ActionButton("Добавить расходник", AddKind)); tools.Children.Add(ActionButton("Удалить расходник", RemoveKind));
        }
        tools.Children.Add(_optional); tools.Children.Add(ActionButton("Добавить поле", AddOptional));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); Body.Children.Add(footer); footer.Children.Add(_error);
        footer.Children.Add(Paragraph("Изменения применяются в памяти. Для записи файла используйте «Сохранить Lua…» на вкладке Lua. Числа вводятся с точкой; nil означает отсутствие значения."));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        actions.Children.Add(ActionButton("Отмена", () => { DialogResult = false; })); actions.Children.Add(ActionButton("Применить", Apply));
        var textStyle = (Style)FindResource("CenteredGridText");
        var editorStyle = (Style)FindResource("CenteredGridEditor");
        var typeText = new FrameworkElementFactory(typeof(TextBlock));
        typeText.SetValue(StyleProperty, textStyle); typeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(EditRow.Kind)));
        var typeEditor = new FrameworkElementFactory(typeof(ComboBox));
        typeEditor.SetValue(StyleProperty, FindResource("CenteredGridChoice"));
        typeEditor.SetValue(ItemsControl.ItemsSourceProperty, new[] { LuaLiteralKind.Number, LuaLiteralKind.String, LuaLiteralKind.Boolean, LuaLiteralKind.Nil });
        typeEditor.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(EditRow.Kind)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Поле", ElementStyle = textStyle, Binding = new Binding(nameof(EditRow.Field)), IsReadOnly = true, MinWidth = 190, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTemplateColumn { Header = "Тип", CellTemplate = new DataTemplate { VisualTree = typeText }, CellEditingTemplate = new DataTemplate { VisualTree = typeEditor }, Width = 120 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Значение", ElementStyle = textStyle, EditingElementStyle = editorStyle, MinWidth = 220, Binding = new Binding(nameof(EditRow.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1.35, DataGridLengthUnitType.Star) });
        _grid.BeginningEdit += (_, e) => { if (e.Row.Item is EditRow { Identity: true }) e.Cancel = true; };
        Body.Children.Add(_grid); Flatten(_root, [], ""); RefreshRows();
    }
    private void Flatten(LuaLiteral node, string[] path, string prefix)
    {
        if (node.Kind != LuaLiteralKind.Table)
        { _rows.Add(new() { Field = "Исключить из Страха смерти", Key = "", Value = node.Text, Kind = node.Kind, Original = node }); return; }
        foreach (var field in node.Fields)
        {
            if (field.Value.Kind == LuaLiteralKind.Table) { Flatten(field.Value, [.. path, field.Key], prefix + field.Key + "."); continue; }
            _rows.Add(new() { Field = prefix + _workspace.FieldName(_group.Spec, field.Key), Key = field.Key, ParentPath = path, OriginalField = field,
                Original = field.Value, Kind = field.Value.Kind, Value = field.Value.Text, Identity = _group.Spec.IsArray && path.Length == 0 && field.Key == "#1" });
        }
    }
    private void RefreshRows()
    {
        _grid.ItemsSource = null; _grid.ItemsSource = _rows;
        List<OptionalField> optional = [];
        if (_group.Spec.IsArray)
            optional.AddRange(_workspace.Schema(_group.Spec).Where(k => k.Key != "#1" && !_rows.Any(r => r.Key == k.Key)).Select(k => new OptionalField(k.Key, k.Value + " [" + k.Key[1..] + "]", LuaLiteralKind.Number, [])));
        else if (_group.Spec.Name is "MeleeActiveSpells" or "RangedActiveSpells")
            optional.AddRange((_group.Table?.Fields ?? []).SelectMany(f => f.Value.Fields).Where(f => f.Value.Kind != LuaLiteralKind.Table).DistinctBy(f => f.Key)
                .Where(f => !_rows.Any(r => r.Key == f.Key)).Select(f => new OptionalField(f.Key, f.Key, f.Value.Kind, [])));
        else if (_group.Spec.Name == "NpcCombatConsumables")
            optional.AddRange(_root.Fields.Where(f => !_removed.Contains(f) && LuaDataWorkspace.ConsumableKinds.Contains(f.Key) && !_rows.Any(r => r.ParentPath.SequenceEqual(new[] { f.Key }) && r.Key == "registrationOnly"))
                .Select(f => new OptionalField("registrationOnly", f.Key + ".registrationOnly", LuaLiteralKind.Boolean, [f.Key])));
        _optional.ItemsSource = optional; _optional.SelectedIndex = optional.Count > 0 ? 0 : -1; _optional.IsEnabled = optional.Count > 0;
    }
    private void AddOptional()
    {
        if (_optional.SelectedItem is not OptionalField field) return;
        _rows.Add(new() { Key = field.Key, Field = field.Label, ParentPath = field.ParentPath, Kind = field.Kind, Value = field.Kind == LuaLiteralKind.Boolean ? "false" : "" }); RefreshRows();
    }
    private void AddKind()
    {
        string kind = (string)_kind.SelectedItem;
        if (_newKinds.ContainsKey(kind) || _root.Fields.Any(f => f.Key == kind && !_removed.Contains(f))) { _error.Text = "Этот расходник уже есть."; return; }
        var values = new List<EditRow>
        {
            new() { Field = kind + ".formula", Key = "formula", ParentPath = [kind], Kind = LuaLiteralKind.String, Value = "" },
            new() { Field = kind + ".charges", Key = "charges", ParentPath = [kind], Kind = LuaLiteralKind.Number, Value = "1" },
            new() { Field = kind + ".registrationOnly", Key = "registrationOnly", ParentPath = [kind], Kind = LuaLiteralKind.Boolean, Value = "false" }
        };
        _newKinds[kind] = values; _rows.AddRange(values); _error.Text = "Укажите формулу заклинания для " + kind + "."; RefreshRows();
    }
    private void RemoveKind()
    {
        string kind = (string)_kind.SelectedItem;
        _newKinds.Remove(kind);
        _removed.AddRange(_root.Fields.Where(f => f.Key == kind && !_removed.Contains(f)));
        _rows.RemoveAll(r => r.ParentPath.FirstOrDefault() == kind); RefreshRows();
    }
    internal string BuildResult()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        List<LuaPatch> patches = [];
        foreach (var r in _rows.Where(r => r.Original is not null && !r.Identity))
        {
            var original = r.Original!;
            if (r.Kind == original.Kind && r.Value == original.Text) continue;
            patches.Add(new(original.Start, original.End - original.Start, LuaLiteralReader.Scalar(r.Kind, r.Value)));
        }
        foreach (var removed in _removed)
        { patches.Add(new(removed.Start, removed.Value.End - removed.Start, "")); if (removed.Separator is int sep) patches.Add(new(sep, 1, "")); }
        string result = ApplyPatches(_original, patches);
        foreach (var kind in _newKinds)
        {
            var root = LuaLiteralReader.Read(result);
            string value = kind.Key + " = { " + string.Join(", ", kind.Value.Select(r => r.Key + " = " + LuaLiteralReader.Scalar(r.Kind, r.Value))) + " }";
            result = ApplyPatches(result, LuaSourceDocument.AppendTo(result, root, value));
        }
        foreach (var row in _rows.Where(r => r.Original is null && !(r.ParentPath.Length > 0 && _newKinds.ContainsKey(r.ParentPath[0]))))
        {
            var parent = LuaLiteralReader.Read(result);
            foreach (string key in row.ParentPath) parent = parent.Field(key)?.Value ?? throw new FormatException("Не найдена таблица " + key);
            result = ApplyPatches(result, LuaSourceDocument.AppendTo(result, parent, LuaLiteralReader.Key(row.Key) + " = " + LuaLiteralReader.Scalar(row.Kind, row.Value)));
        }
        LuaDataWorkspace.Validate(_group.Spec, LuaLiteralReader.Read(result), _group.LinkKey); return result;
    }
    private void Apply()
    {
        try { ResultText = BuildResult(); DialogResult = true; }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException) { _error.Text = ex.Message; }
    }
    internal static string ApplyPatches(string text, IEnumerable<LuaPatch> patches)
    { foreach (var p in patches.OrderByDescending(p => p.Start)) text = text.Remove(p.Start, p.Length).Insert(p.Start, p.Text); return text; }
}

internal sealed class LuaTemplateDialog : LuaToolWindow
{
    public string? Literal { get; private set; }
    public LuaTemplateDialog(LuaDataWorkspace workspace, LuaBoundGroup group) : base("Новая запись · " + group.LinkKey)
    {
        var info = Paragraph("Выберите существующую запись как образец. Её параметры будут скопированы с новым именем. Перед добавлением откроется редактор значений; проверьте все поля."); DockPanel.SetDock(info, Dock.Top); Body.Children.Add(info);
        var templates = workspace.Templates(group);
        var list = new ListBox { ItemsSource = templates.Select((t, i) => new TemplateItem(t.Name, i)).ToArray(), DisplayMemberPath = "Name", Margin = new(4) };
        var search = new TextBox { Margin = new(4), ToolTip = "Поиск образца по техническому имени" }; DockPanel.SetDock(search, Dock.Top); Body.Children.Add(search);
        search.TextChanged += (_, _) => list.ItemsSource = templates.Select((t, i) => new TemplateItem(t.Name, i)).Where(t => t.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        var button = ActionButton("Использовать как образец…", () =>
        {
            if (list.SelectedItem is not TemplateItem selected || group.Document is null) return;
            var node = templates[selected.Index].Value; string text = group.Document.Text[node.Start..node.End];
            if (group.Spec.IsArray && node.Field("#1") is { } name) text = text.Remove(name.Value.Start - node.Start, name.Value.End - name.Value.Start).Insert(name.Value.Start - node.Start, LuaLiteralReader.Quote(group.LinkKey));
            Literal = text; DialogResult = true;
        });
        DockPanel.SetDock(button, Dock.Bottom); Body.Children.Add(button); Body.Children.Add(list);
    }
    private sealed record TemplateItem(string Name, int Index);
}

internal sealed class LuaSaveReviewDialog : LuaToolWindow
{
    public LuaSaveReviewDialog(LuaSourceDocument doc) : base("Сохранить Lua с резервной копией")
    {
        var caption = Paragraph(doc.Path + "\n" + doc.EncodingName + "\nПодготовленные изменения ниже. Будут заменены выбранные значения; остальной текст файла сохраняется. Игра читает Lua при запуске."); DockPanel.SetDock(caption, Dock.Top); Body.Children.Add(caption);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(actions, Dock.Bottom); Body.Children.Add(actions);
        actions.Children.Add(ActionButton("Отмена", () => { DialogResult = false; })); actions.Children.Add(ActionButton("Сохранить с резервной копией", () => { DialogResult = true; }));
        Body.Children.Add(new TextBox { Text = string.Join("\n\n", doc.Changes), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
