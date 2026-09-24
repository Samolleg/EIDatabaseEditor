using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed class AnimationDatabaseDialog : LuaToolWindow
{
    private sealed record Row(int? Index, string Id, string Russian, string State, string Action, string Length);
    private sealed record FlagChoice(string Title, uint Value) { public override string ToString() => Title; }
    private readonly UnitModelScene _scene;
    private readonly FigureAnimationSet _preview;
    private readonly AnimationDatabaseSource _source;
    private AnimationDatabase _draft;
    internal AnimationDatabase Draft => _draft;
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _search = new() { ToolTip = "Поиск по ID или русскому названию" };
    private readonly ComboBox _template = new() { MinWidth = 240 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _id = new(), _flags = new(), _flags2 = new(), _probability = new(), _length = new(), _speed = new();
    private readonly ComboBox _action = new(), _stage = new(), _state = new();
    private readonly TextBox _hides = new(), _steps = new(), _hit = new(), _sfxFrame = new(), _sfxIndices = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private bool _dirty;
    private bool _reading;

    public AnimationDatabaseDialog(UnitModelScene scene, FigureAnimationSet preview) : base("Игровой список анимаций ADB — " + scene.Model)
    {
        _scene = scene; _preview = preview; _source = scene.AnimationDatabase ?? throw new ArgumentException("ADB модели не найден."); _draft = _source.CreateDraft();
        Width = Math.Min(1250, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 32);
        var root = new DockPanel { Margin = new Thickness(8) };
        Body.Children.Add(root);
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        header.Children.Add(new TextBlock { Text = _source.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        header.Children.Add(_summary);
        var searchBar = new DockPanel { Margin = new Thickness(4) };
        searchBar.Children.Add(new TextBlock { Text = "Поиск", Width = 55, VerticalAlignment = VerticalAlignment.Center });
        searchBar.Children.Add(_search); header.Children.Add(searchBar);
        _search.TextChanged += (_, _) => RefreshRows();
        _list.Columns.Add(Column("№ ADB", "Index", 60));
        _list.Columns.Add(Column("ID клипа", "Id", 135));
        _list.Columns.Add(Column("Название RU", "Russian", 160));
        _list.Columns.Add(Column("Состояние", "State", 125));
        _list.Columns.Add(Column("Действие", "Action", 95));
        _list.Columns.Add(Column("Кадры ADB", "Length", 80));
        _list.SelectionChanged += (_, _) => ReadSelection();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(_list, 0); grid.Children.Add(_list);
        var fields = new StackPanel { Margin = new Thickness(10, 0, 2, 0) };
        fields.Children.Add(new TextBlock { Text = "Параметры выбранной записи", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 2, 4, 8) });
        fields.Children.Add(_hint);
        Field(fields, "Игровой ID (до 15 латинских символов)", _id);
        Field(fields, "Шаблон для новой записи", _template);
        _action.ItemsSource = new[] { new FlagChoice("Особая", 0x00040000), new FlagChoice("Атака", 0x00080000), new FlagChoice("Заклинание", 0x000C0000),
            new FlagChoice("Бег", 0x00100000), new FlagChoice("Ходьба", 0x00140000), new FlagChoice("Ожидание", 0x00180000),
            new FlagChoice("Смерть", 0x001C0000), new FlagChoice("Урон", 0x00200000), new FlagChoice("Переход", 0x00240000) };
        _stage.ItemsSource = new[] { new FlagChoice("Одиночная", 0), new FlagChoice("Начало", 0x40000000),
            new FlagChoice("Цикл", 0x80000000), new FlagChoice("Конец", 0xC0000000) };
        _state.ItemsSource = new[] { new FlagChoice("Нейтральное", 0), new FlagChoice("Отдых", 0x00008000),
            new FlagChoice("Атака", 0x00010000), new FlagChoice("Оборона", 0x00020000), new FlagChoice("Лёжа", 0x00030000) };
        Field(fields, "Действие", _action); Field(fields, "Стадия", _stage); Field(fields, "Состояние", _state);
        _action.SelectionChanged += (_, _) => SetFlagBits(0x003C0000, _action);
        _stage.SelectionChanged += (_, _) => SetFlagBits(0xC0000000, _stage);
        _state.SelectionChanged += (_, _) => SetFlagBits(0x00038000, _state);
        _template.SelectionChanged += (_, _) => { if (_list.SelectedItem is Row { Index: null }) ReadSelection(); };
        Field(fields, "Флаги действия / состояния / оружия, hex", _flags);
        Field(fields, "Флаги событий, hex", _flags2);
        Field(fields, "Вероятность 0–100", _probability);
        Field(fields, "Длительность ADB, кадров (игра: 15 кадр/с)", _length);
        Field(fields, "Скорость перемещения, м/с", _speed);
        Field(fields, "Появление / скрытие предмета — 2 кадра", _hides);
        Field(fields, "Звуки шагов — 4 кадра", _steps);
        Field(fields, "Кадр попадания", _hit);
        Field(fields, "Кадр визуального эффекта", _sfxFrame);
        Field(fields, "Индексы спецэффектов — 4 числа", _sfxIndices);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        Button apply = Button("Применить поля", () => Apply()); actions.Children.Add(apply);
        Button add = Button("Добавить запись", () => Add()); actions.Children.Add(add);
        Button save = Button("Сохранить ADB с копией", async () => await Save()); actions.Children.Add(save);
        footer.Children.Add(actions);
        fields.Children.Add(new TextBlock { Text = "Новая запись наследует действие, состояние, оружие и модификатор шаблона, поэтому может стать случайной альтернативой его анимации. Проверьте флаги перед сохранением. Старые номера не меняются; индекс выше 253 требует доработки addon.dll.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) });
        footer.Children.Add(_status);
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); root.Children.Add(grid);
        RefreshRows();
        Closing += (_, e) =>
        {
            if (_dirty && MessageBox.Show(this, "Закрыть без сохранения ADB?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else if (_dirty) DiscardDraft();
        };
    }

    internal void DiscardDraft() { _draft = _source.CreateDraft(); _dirty = false; }

    private static DataGridTextColumn Column(string title, string path, double width) => new()
    { Header = title, Binding = new Binding(path), Width = new DataGridLength(width) };
    private static void Field(Panel panel, string title, Control control)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(4, 5, 4, 2) });
        control.Margin = new Thickness(4, 0, 4, 0); control.MinHeight = 28; panel.Children.Add(control);
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 5, 8, 5) };
        button.Click += (_, _) => action(); return button;
    }
    private void RefreshRows(string? selectedId = null, int? selectedIndex = null)
    {
        int templateIndex = _template.SelectedIndex;
        var clips = _preview.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = _draft.Entries;
        var registered = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = entries.Select(e => new Row(e.Index, e.Name, AnimationNames.Shared.Get(_scene.Model, e.Name),
            clips.Contains(e.Name) ? e.Index > AnimationDatabase.MaximumLegacySafeIndex ? "Выше лимита" : "ADB + ANM" : "Нет клипа ANM",
            ActionLabel(e.Flags), e.Length.ToString(CultureInfo.InvariantCulture)))
            .Concat(clips.Where(name => !registered.Contains(name)).Select(name => new Row(null, name,
                AnimationNames.Shared.Get(_scene.Model, name), "Только ANM", "—", "—")))
            .Where(r => _search.Text.Length == 0 || r.Id.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) ||
                r.Russian.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.ItemsSource = rows;
        _summary.Text = $"ADB: {entries.Count} записей · ANM: {clips.Count} клипов · только в ANM: {clips.Count(name => !registered.Contains(name))} · записи без клипа: {entries.Count(e => !clips.Contains(e.Name))} · записи выше байтового предела: {entries.Count(e => e.Index > AnimationDatabase.MaximumLegacySafeIndex)}";
        _template.ItemsSource = entries.Select(e => $"{e.Index}: {e.Name} ({ActionLabel(e.Flags)})").ToArray();
        if (templateIndex >= 0 && entries.Count > 0) _template.SelectedIndex = Math.Clamp(templateIndex, 0, entries.Count - 1);
        _list.SelectedItem = selectedId is null ? rows.FirstOrDefault() :
            rows.FirstOrDefault(r => r.Index == selectedIndex && r.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
    }
    private static string ActionLabel(uint flags) => (flags & 0x003C0000) switch
    {
        0x00040000 => "Особая", 0x00080000 => "Атака", 0x000C0000 => "Заклинание", 0x00100000 => "Бег",
        0x00140000 => "Ходьба", 0x00180000 => "Ожидание", 0x001C0000 => "Смерть", 0x00200000 => "Урон",
        0x00240000 => "Переход", _ => "Неизвестно"
    };
    private void ReadSelection()
    {
        if (_list.SelectedItem is not Row row) return;
        _id.Text = row.Id;
        AnimationDatabaseEntry? entry = row.Index is int index ? _draft.Entries[index] : null;
        if (entry is null && _template.SelectedIndex >= 0) entry = _draft.Entries[_template.SelectedIndex];
        if (entry is null)
        {
            _hint.Text = "Клип не зарегистрирован. Выберите похожую запись-шаблон, затем проверьте её флаги и добавьте запись. Клип должен быть уже сохранён в ANM.";
            if (row.Index is null) _ = SetClipLength(row.Id);
            return;
        }
        _reading = true;
        _flags.Text = $"0x{entry.Flags:X8}"; _flags2.Text = $"0x{(row.Index is null ? entry.Flags2 & ~0xFu : entry.Flags2):X8}";
        _action.SelectedItem = ((IEnumerable<FlagChoice>)_action.ItemsSource).FirstOrDefault(x => x.Value == (entry.Flags & 0x003C0000));
        _stage.SelectedItem = ((IEnumerable<FlagChoice>)_stage.ItemsSource).FirstOrDefault(x => x.Value == (entry.Flags & 0xC0000000));
        _state.SelectedItem = ((IEnumerable<FlagChoice>)_state.ItemsSource).FirstOrDefault(x => x.Value == (entry.Flags & 0x00038000));
        _reading = false;
        _probability.Text = entry.Probability.ToString(); _length.Text = row.Index is null ? "" : entry.Length.ToString();
        _speed.Text = entry.Speed.ToString("R", CultureInfo.InvariantCulture);
        _hides.Text = Join(row.Index is null ? new uint[2] : entry.Hides);
        _steps.Text = Join(row.Index is null ? new uint[4] : entry.Steps);
        _hit.Text = row.Index is null ? "0" : entry.Hit.ToString();
        _sfxFrame.Text = row.Index is null ? "0" : entry.SfxFrame.ToString();
        _sfxIndices.Text = Join(row.Index is null ? new uint[4] : entry.SfxIndices);
        _hint.Text = row.Index is int i
            ? $"Запись #{i}: {row.State}. Действие: {ActionLabel(entry.Flags)}. Номер записи не меняется. Флаги можно редактировать в hex; неизвестные биты сохраняются."
            : "Клип пока не зарегистрирован в ADB. Выберите похожий шаблон, затем добавьте запись. Клип должен быть уже сохранён в исходный ANM.";
        if (row.Index is null) _ = SetClipLength(row.Id);
    }
    private async Task SetClipLength(string id)
    {
        try
        {
            int length = (await _preview.LoadAsync(id)).FrameCount;
            if (_list.SelectedItem is Row { Index: null } current && current.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                _length.Text = length.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or DatabaseFormatException)
        { _status.Text = "Не удалось определить длину клипа: " + ex.Message; }
    }
    private static string Join(uint[] values) => string.Join(", ", values);
    private void SetFlagBits(uint mask, ComboBox box)
    {
        if (_reading || box.SelectedItem is not FlagChoice choice) return;
        try { _flags.Text = $"0x{((Hex(_flags) & ~mask) | choice.Value):X8}"; }
        catch (FormatException) { _status.Text = "Исправьте шестнадцатеричные флаги перед выбором значения."; }
    }
    private static uint UInt(TextBox box) => uint.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n)
        ? n : throw new FormatException("Введите целое неотрицательное число: " + box.Text);
    private static uint Hex(TextBox box)
    {
        string text = box.Text.Trim(); if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint n)
            ? n : throw new FormatException("Введите флаги в hex, например 0x80180000.");
    }
    private static uint[] Array(TextBox box, int count)
    {
        var items = box.Text.Split(',', StringSplitOptions.TrimEntries);
        if (items.Length != count) throw new FormatException($"Ожидается {count} чисел через запятую.");
        return items.Select(s => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n)
            ? n : throw new FormatException("Неверный номер кадра/индекс: " + s)).ToArray();
    }
    private bool Apply()
    {
        try
        {
            if (_list.SelectedItem is not Row { Index: int index } row) throw new InvalidOperationException("Выберите запись ADB для изменения.");
            if (!_id.Text.Trim().Equals(row.Id, StringComparison.OrdinalIgnoreCase) && !SavedClipExists(_id.Text.Trim()))
                throw new InvalidOperationException("Новый ID должен указывать на уже сохранённый клип ANM.");
            float speed = float.TryParse(_speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : throw new FormatException("Скорость: число с точкой.");
            _draft.Update(index, _id.Text.Trim(), Hex(_flags), Hex(_flags2), UInt(_probability), UInt(_length), speed,
                Array(_hides, 2), Array(_steps, 4), UInt(_hit), UInt(_sfxFrame), Array(_sfxIndices, 4));
            _dirty = true; _status.Text = "Поля изменены в памяти. Нажмите «Сохранить ADB с копией».";
            RefreshRows(_id.Text.Trim(), index);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or DatabaseFormatException) { Error(ex); return false; }
    }
    private void Add()
    {
        try
        {
            if (_list.SelectedItem is not Row { Index: null } row) throw new InvalidOperationException("Выберите клип со статусом «Только ANM».");
            if (!_id.Text.Trim().Equals(row.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Для добавления выберите существующий клип ANM; переименовать его здесь нельзя.");
            if (_template.SelectedIndex < 0) throw new InvalidOperationException("Выберите похожую запись-шаблон.");
            if (!SavedClipExists(row.Id)) throw new InvalidOperationException("Сначала сохраните этот клип в исходный ANM и обновите предпросмотр.");
            uint length = UInt(_length);
            if (length == 0) throw new FormatException("Укажите число кадров клипа.");
            int index = _draft.Count;
            if (index > AnimationDatabase.MaximumLegacySafeIndex && MessageBox.Show(this,
                $"Новая запись получит индекс {index}. Штатная игра передаёт номер одним байтом и может воспроизвести другую анимацию. Добавить запись для будущей поддержки addon.dll?",
                "Индекс выше игрового предела", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            float speed = float.TryParse(_speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float n)
                ? n : throw new FormatException("Скорость: число с точкой.");
            index = _draft.AppendConfigured(_template.SelectedIndex, row.Id, Hex(_flags), Hex(_flags2),
                UInt(_probability), length, speed, Array(_hides, 2), Array(_steps, 4), UInt(_hit), UInt(_sfxFrame), Array(_sfxIndices, 4));
            _dirty = true; RefreshRows(row.Id, index);
            _status.Text = $"Добавлена запись #{index} по шаблону. Проверьте флаги и остальные параметры, затем сохраните ADB.";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or DatabaseFormatException) { Error(ex); }
    }
    private bool SavedClipExists(string id)
    {
        var target = _scene.Animations?.SaveTarget;
        if (target is null) return false;
        byte[] bytes;
        if (target.ResourceName is null) bytes = File.ReadAllBytes(target.Path);
        else
        {
            var archive = ResArchiveDocument.Open(target.Path);
            bytes = archive.GetResourceBytes(target.ResourceName).ToArray();
        }
        var anm = new FigureAnimationSet(_scene.Model, bytes, _preview.Parents, _preview.Bones, _preview.BindPositions);
        return anm.Names.Contains(id, StringComparer.OrdinalIgnoreCase);
    }
    private async Task Save()
    {
        try
        {
            if (_list.SelectedItem is Row { Index: int } && !Apply()) return;
            if (!_dirty) { _status.Text = "Изменений ADB нет."; return; }
            string? backup = await _source.SaveAsync(_draft);
            _draft = _source.CreateDraft(); _dirty = false;
            _status.Text = "ADB сохранён и проверен. Резервная копия: " + (backup ?? "не потребовалась");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or DatabaseFormatException) { Error(ex); }
    }
    private void Error(Exception ex) { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "ADB", MessageBoxButton.OK, MessageBoxImage.Error); }
}
