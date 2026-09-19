using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App;

public partial class MainWindow
{
    private sealed record LuaColumn(LuaBindingSpec Spec, string[] Path, string Label, LuaLiteralKind Kind);
    private readonly Dictionary<RecordRowViewModel, IReadOnlyList<LuaBoundGroup>> _luaRows = [];
    private readonly Dictionary<DataGridColumn, LuaColumn> _luaColumns = [];
    private string? _luaDatabasePath;
    // MaterialProp slots 2..14: labels confirmed by the owner, not stale Lua comments.
    private static readonly string[] MaterialLabels =
    [
        "Регенерация", "Восстановление запаса сил", "Шанс блокирования урона",
        "Поглощение урона блокированием", "Шанс критического урона", "Размер критического урона",
        "Шанс парирования", "Скорость передвижения", "Бонус силы", "Бонус ловкости",
        "Бонус разума", "Заметность", "Слышимость"
    ];

    private void ShowLuaColumns_Changed(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid is null) return;
        if (ShowLuaColumnsButton.IsChecked == true && !EnsureLuaSource(true)) ShowLuaColumnsButton.IsChecked = false;
        RebuildSpreadsheetColumns();
    }

    private bool EnsureLuaSource(bool choose)
    {
        try
        {
            if ((_luaDatabasePath != _viewModel.SourcePath || _viewModel.LuaWorkspace is null) && !_viewModel.HasPendingLuaChanges)
            {
                string? nearby = LuaDataWorkspace.FindNearby(_viewModel.SourcePath);
                if (nearby is not null) { _viewModel.ConnectLua(nearby); _luaDatabasePath = _viewModel.SourcePath; }
            }
            if (_viewModel.LuaWorkspace is not null && _luaDatabasePath == _viewModel.SourcePath) return true;
            if (!choose) return false;
            var folder = new OpenFolderDialog { Title = "Выберите папку lua или корень мода" };
            if (folder.ShowDialog(this) != true) return false;
            _viewModel.ConnectLua(folder.FolderName); _luaDatabasePath = _viewModel.SourcePath; return true;
        }
        catch (Exception ex) { _viewModel.ReportSpreadsheetError(ex.Message); return false; }
    }

    private void AddLuaColumns()
    {
        _luaRows.Clear();
        _luaColumns.Clear();
        if (ShowLuaColumnsButton?.IsChecked != true || _spreadsheetViewModel?.SelectedTable is not { } table || !EnsureLuaSource(false) || _viewModel.LuaWorkspace is not { } workspace) return;
        foreach (var spec in LuaDataWorkspace.Bindings.Where(s => s.Supports(table.Key)))
        {
            var doc = workspace.Documents.FirstOrDefault(d => System.IO.Path.GetFileName(d.Path) == spec.FileName);
            var source = doc?.Tables.GetValueOrDefault(spec.Name).Value;
            if (spec.Child is not null) source = source?.Field(spec.Child)?.Value;
            var paths = new Dictionary<string, string[]>();
            var kinds = new Dictionary<string, LuaLiteralKind>();
            if (spec.Name == "MaterialProp")
                for (int i = 0; i < MaterialLabels.Length; i++) paths["#" + (i + 2)] = ["#" + (i + 2)];
            foreach (string key in workspace.Schema(spec).Keys.Where(k => k != "#1")) paths[key] = [key];
            void Collect(LuaLiteral node, string[] path)
            {
                if (node.Kind != LuaLiteralKind.Table) { string key = string.Join("/", path); paths[key] = path; if (node.Kind != LuaLiteralKind.Nil) kinds[key] = node.Kind; return; }
                foreach (var f in node.Fields)
                    if (!(spec.IsArray && path.Length == 0 && f.Key == "#1")) Collect(f.Value, [..path, f.Key]);
            }
            foreach (var field in source?.Fields ?? []) Collect(field.Value, []);
            if (paths.Count == 0) paths[""] = [];
            foreach (var path in paths.Values)
            {
                string label = path.Length == 0 ? "Исключён" : string.Join(" · ", path.Select(key => workspace.FieldName(spec, key).Split(" — ")[0].Split(" -- ")[0]));
                if (spec.Name == "MaterialProp" && path.Length == 1 && int.TryParse(path[0].TrimStart('#'), out int slot) && slot >= 2 && slot <= 14)
                    label = MaterialLabels[slot - 2];
                if (spec.Name == "ForbiddenItems" && path.SequenceEqual(new[] { "#2" })) label = "ID магазина";
                var descriptor = new LuaColumn(spec, path, label, kinds.GetValueOrDefault(string.Join("/", path), LuaLiteralKind.Number));
                var header = new TextBlock { Text = "Lua · " + spec.Title + "\n" + label, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = spec.Name + " / " + string.Join(".", path) + "\n" + spec.Hint };
                if (spec.Name == "ForbiddenItems") header.Text = "Lua · Запрет размножения\n" + label;
                var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
                headerStyle.Setters.Add(new Setter(ToolTipProperty, header.ToolTip));
                var column = new DataGridTextColumn { Header = header.Text, HeaderTemplate = (DataTemplate)FindResource("LuaColumnHeaderTemplate"), HeaderStyle = headerStyle, Binding = new Binding(".") { Converter = new LuaColumnConverter(this, descriptor), Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit },
                    ElementStyle = (Style)FindResource("CenteredGridText"), EditingElementStyle = (Style)FindResource("CenteredGridEditor"), IsReadOnly = source is null, CanUserSort = false, Width = 190, MinWidth = 120 };
                RecordsGrid.Columns.Add(column); _luaColumns[column] = descriptor;
            }
        }
    }

    private void EditLuaCell(RecordRowViewModel row, LuaColumn column, string text, bool recordHistory = true)
    {
        var workspace = _viewModel.LuaWorkspace!;
        var group = workspace.ForRecord(ModelRecord.Capture(row.TableKey, row.CoreRecord)).Single(g => g.Spec.Name == column.Spec.Name);
        if (group.Problem is not null || group.Document is null) throw new InvalidOperationException(group.Problem ?? "Lua не подключён.");
        var doc = group.Document;
        string before = doc.Text;
        var entries = group.Entries;
        if (column.Spec.Name == "ForbiddenItems" && column.Path.SequenceEqual(new[] { "#2" }))
        {
            string input = text.Trim();
            string[] shops = input is "" or "—" or "nil" ? [] : input.Split(';').Select(s => s.Trim()).ToArray();
            if (shops.Any(s => s.Length == 0 || s.Any(c => c < '0' || c > '9') || !int.TryParse(s, out _)))
                throw new FormatException("Укажите целые ID магазинов через точку с запятой, например 1; 3. Для удаления запрета очистите ячейку.");
            var changes = new List<LuaPatch>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (i >= shops.Length) changes.AddRange(doc.Remove(entry.Parent, entry.Field));
                else
                {
                    var value = entry.Field.Value.Field("#2")!.Value;
                    if (value.Text != shops[i]) changes.Add(new(value.Start, value.End - value.Start, shops[i]));
                }
            }
            if (shops.Length > entries.Count)
            {
                string additions = string.Join(", ", shops.Skip(entries.Count).Select(s => "{" + LuaLiteralReader.Quote(group.LinkKey) + ", " + s + "}"));
                changes.AddRange(doc.Append(group.Table!, additions));
            }
            doc.Apply(changes, "ForbiddenItems / " + group.LinkKey + " → " + input);
            if (recordHistory) _viewModel.RecordLuaGridEdit(doc, before);
            return;
        }
        string[] values = entries.Count > 1 ? text.Split(';').Select(s => s.Trim()).ToArray() : [text];
        if (entries.Count > 1 && values.Length != entries.Count) throw new InvalidOperationException("Сохраните число повторных строк: введите " + entries.Count + " значений через точку с запятой.");
        string Scalar(string value) => value is "nil" or "—" || value.Length == 0 && column.Kind != LuaLiteralKind.String ? "nil" : LuaLiteralReader.Scalar(column.Kind, value);
        if (entries.Count == 0)
        {
            if (text is "" or "—" or "nil") return;
            string initial = column.Spec.IsArray ? "{" + LuaLiteralReader.Quote(group.LinkKey) + "}" : "{}";
            workspace.Add(group, SetLiteral(initial, column.Path, Scalar(text)));
        }
        else
        {
            var patches = new List<LuaPatch>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i]; string original = doc.Text[entry.Field.Value.Start..entry.Field.Value.End];
                string changed = SetLiteral(original, column.Path, Scalar(values[i]));
                LuaDataWorkspace.Validate(column.Spec, LuaLiteralReader.Read(changed), group.LinkKey);
                if (original != changed) patches.Add(new(entry.Field.Value.Start, original.Length, changed));
            }
            doc.Apply(patches, column.Spec.Name + " / " + group.LinkKey + " / " + column.Label + " → " + text);
        }
        if (recordHistory) _viewModel.RecordLuaGridEdit(doc, before);
    }

    // Returns false only when the normal DB/texts paste should handle the range.
    internal bool TryPasteLuaMatrix(IReadOnlyList<IReadOnlyList<string>> matrix)
    {
        var rows = RecordsGrid.Items.OfType<RecordRowViewModel>().ToArray();
        var columns = RecordsGrid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToArray();
        if (matrix.Count == 0 || matrix.All(r => r.Count == 0) ||
            !TryResolvePasteAnchor(RecordsGrid, rows, columns, matrix, out int row, out int col)) return false;
        var targets = new List<(RecordRowViewModel Row, DataGridColumn Column, string Text)>();
        if (matrix.Count == 1 && matrix[0].Count == 1 && RecordsGrid.SelectedCells.Count > 1)
            targets.AddRange(RecordsGrid.SelectedCells.Where(c => c.Item is RecordRowViewModel && columns.Contains(c.Column))
                .Select(c => ((RecordRowViewModel)c.Item, c.Column, matrix[0][0])));
        else
        {
            int width = matrix.Max(r => r.Count);
            if (row + matrix.Count > rows.Length || col + width > columns.Length) return false;
            for (int y = 0; y < matrix.Count; y++)
                for (int x = 0; x < width; x++) targets.Add((rows[row + y], columns[col + x], x < matrix[y].Count ? matrix[y][x] : ""));
        }
        if (!targets.Any(t => _luaColumns.ContainsKey(t.Column))) return false;
        if (RecordsGrid.Items is System.ComponentModel.IEditableCollectionView { IsEditingItem: true } &&
            (!RecordsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true))) return true;
        if (targets.Any(t => t.Column.IsReadOnly || !_luaColumns.ContainsKey(t.Column) && GetClipboardCellText(t.Row, t.Column) != t.Text))
        {
            _viewModel.ReportSpreadsheetError("Вставляйте изменённые значения Lua отдельно от столбцов БД и texts.res. Диапазон не изменён.");
            return true;
        }
        var before = _viewModel.LuaWorkspace!.Documents.ToDictionary(d => d, d => d.Text);
        try
        {
            foreach (var target in targets)
                if (_luaColumns.TryGetValue(target.Column, out var descriptor)) EditLuaCell(target.Row, descriptor, target.Text, false);
            _viewModel.RecordLuaGridBatch(before);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.IO.InvalidDataException)
        {
            foreach (var pair in before.Where(p => p.Key.Text != p.Value))
                pair.Key.Apply([new LuaPatch(0, pair.Key.Text.Length, pair.Value)], "Отмена неудачной вставки Lua");
            _viewModel.ReportSpreadsheetError(ex.Message);
        }
        _luaRows.Clear();
        RecordsGrid.Items.Refresh();
        return true;
    }

    private static string SetLiteral(string original, string[] path, string value)
    {
        if (path.Length == 0) return value;
        var node = LuaLiteralReader.Read(original);
        for (int i = 0; i < path.Length; i++)
        {
            var child = node.Field(path[i]);
            if (child is null)
            {
                for (int j = path.Length - 1; j > i; j--) value = "{ " + LuaLiteralReader.Key(path[j]) + " = " + value + " }";
                foreach (var patch in LuaSourceDocument.AppendTo(original, node, LuaLiteralReader.Key(path[i]) + " = " + value).OrderByDescending(p => p.Start))
                    original = original.Remove(patch.Start, patch.Length).Insert(patch.Start, patch.Text);
                return LuaRowFormatting.Compact(original);
            }
            if (i == path.Length - 1)
            {
                var parsed = LuaLiteralReader.Read(value);
                if (parsed.Kind == child.Value.Kind && parsed.Text == child.Value.Text) return original;
                return original.Remove(child.Value.Start, child.Value.End - child.Value.Start).Insert(child.Value.Start, value);
            }
            node = child.Value;
        }
        return original;
    }

    private sealed class LuaColumnConverter(MainWindow window, LuaColumn column) : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture)
        {
            if (value is not RecordRowViewModel row || window._viewModel.LuaWorkspace is not { } workspace) return "—";
            if (!window._luaRows.TryGetValue(row, out var groups))
                window._luaRows[row] = groups = workspace.ForRecord(ModelRecord.Capture(row.TableKey, row.CoreRecord));
            var group = groups.First(g => g.Spec.Name == column.Spec.Name);
            if (group.Problem is not null) return "Недоступно";
            return group.Entries.Count == 0 ? "—" : string.Join("; ", group.Entries.Select(entry =>
            {
                LuaLiteral? node = entry.Field.Value;
                foreach (string key in column.Path) node = node?.Field(key)?.Value;
                return node?.Text ?? "—";
            }));
        }
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
