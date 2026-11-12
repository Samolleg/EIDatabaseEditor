using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationDatabaseDialog : LuaToolWindow
{
    private sealed record Row(int? Index, string Id, string Russian, string State, string Action, string Length);
    private sealed record FlagChoice(string Title, uint Value) { public override string ToString() => Title; }
    private readonly UnitModelScene _scene;
    private FigureAnimationSet _preview;
    internal FigureAnimationSet? SavedAnimationSet { get; private set; }
    private readonly AnimationDatabaseSource _source;
    private AnimationDatabase _draft;
    internal AnimationDatabase Draft => _draft;
    internal Task ClipLengthTask { get; private set; } = Task.CompletedTask;
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _search = new() { ToolTip = "Поиск по ID или русскому названию" };
    private readonly ComboBox _template = new() { MinWidth = 240 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _id = new(), _flags = new(), _flags2 = new(), _probability = new(), _length = new(), _speed = new();
    private readonly TextBlock _actionHelp = HelpBlock(), _stageHelp = HelpBlock(), _stateHelp = HelpBlock();
    private readonly ComboBox _action = new(), _stage = new(), _state = new();
    private readonly TextBox _hides = new(), _steps = new(), _hit = new(), _sfxFrame = new(), _sfxIndices = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private bool _dirty;
    private bool _reading;
    private readonly Button _applyButton;
    private readonly Stack<byte[]> _history = new();

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
        Field(fields, "Начальные настройки (копирование необязательно)", _template);
        _action.ItemsSource = new[] { new FlagChoice("Особая", 0x00040000), new FlagChoice("Атака", 0x00080000), new FlagChoice("Заклинание", 0x000C0000),
            new FlagChoice("Бег", 0x00100000), new FlagChoice("Ходьба", 0x00140000), new FlagChoice("Ожидание", 0x00180000),
            new FlagChoice("Смерть", 0x001C0000), new FlagChoice("Урон", 0x00200000), new FlagChoice("Переход", 0x00240000) };
        _stage.ItemsSource = new[] { new FlagChoice("Одиночная", 0), new FlagChoice("Начало", 0x40000000),
            new FlagChoice("Цикл", 0x80000000), new FlagChoice("Конец", 0xC0000000) };
        _state.ItemsSource = new[] { new FlagChoice("Нейтральное", 0), new FlagChoice("Отдых", 0x00008000),
            new FlagChoice("Атака", 0x00010000), new FlagChoice("Оборона", 0x00020000), new FlagChoice("Лёжа", 0x00030000) };
        fields.Children.Add(new TextBlock { Text = "Эти параметры описывают, когда игра выбирает клип. Они не меняют позы ANM и сами не запускают анимацию. Выбор также зависит от оружия и дополнительных флагов.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) });
        Field(fields, "Действие — что делает персонаж", _action); fields.Children.Add(_actionHelp);
        Field(fields, "Стадия — какая часть действия", _stage); fields.Children.Add(_stageHelp);
        Field(fields, "Состояние — в какой позе/режиме персонаж", _state); fields.Children.Add(_stateHelp);
        _flags.TextChanged += (_, _) => UpdateFlagHelp();
        fields.Children.Add(new TextBlock { Text = "Пример: танец по отдельной команде обычно оформляют как «Особая». «Ожидание» означает движение во время бездействия — танец может попасть в обычный выбор анимаций покоя при совпадении остальных флагов. Для повторяющегося танца нужна стадия «Цикл» и согласованные начало/конец клипа; одной регистрации ADB недостаточно для запуска.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) });
        _action.SelectionChanged += (_, _) => SetFlagBits(0x003C0000, _action);
        _stage.SelectionChanged += (_, _) => SetFlagBits(0xC0000000, _stage);
        _state.SelectionChanged += (_, _) => SetFlagBits(0x00038000, _state);
        _template.SelectionChanged += (_, _) => { if (!_reading && _list.SelectedItem is Row { Index: null }) ReadSelection(); };
        BuildFlagControls(fields);
        Field(fields, "Вероятность 0–100", _probability);
        Field(fields, "Длительность ADB, кадров (игра: 15 кадр/с)", _length);
        Field(fields, "Скорость перемещения, м/с", _speed);
        Field(fields, "Появление / скрытие предмета — 2 кадра", _hides);
        Field(fields, "Звуки шагов — 4 кадра", _steps);
        Field(fields, "Кадр попадания", _hit);
        Field(fields, "Кадр визуального эффекта", _sfxFrame);
        Field(fields, "Индексы спецэффектов — 4 числа", _sfxIndices);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        _applyButton = Button("Выберите анимацию", ApplySelectedFields); actions.Children.Add(_applyButton);
        actions.Children.Add(Button("Удалить запись ADB…", Delete));
        actions.Children.Add(Button("Удалить клип ANM…", async () => await DeleteClip()));
        actions.Children.Add(Button("Отменить изменение", UndoDraft));
        Button save = Button("Сохранить ADB с копией", async () => await Save()); actions.Children.Add(save);
        footer.Children.Add(actions);
        fields.Children.Add(new TextBlock { Text = "Без шаблона: особое одиночное действие, нейтральное состояние, вероятность 100, скорость 0, события выключены. Настройте назначение и оружие перед сохранением. Копирование переносит флаги и скорость, но очищает события. Удаляется только последняя запись ADB: номера остальных сохраняются. В середине списка можно заменить ID клипа. Клип ANM удаляется отдельной кнопкой, если на него нет ссылок в сохранённом ADB.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) });
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

    internal void DiscardDraft() { _draft = _source.CreateDraft(); _dirty = false; _history.Clear(); }

    private void Remember(byte[] before)
    {
        if (before.AsSpan().SequenceEqual(_draft.ExportBytes())) return;
        _history.Push(before); _dirty = true;
    }

    internal void UndoDraft()
    {
        if (!_history.TryPop(out var before)) { _status.Text = "Нет изменений для отмены."; return; }
        _draft = AnimationDatabase.Parse(before);
        _dirty = !_draft.ExportBytes().AsSpan().SequenceEqual(_source.Database.ExportBytes());
        RefreshRows(); _status.Text = "Последнее изменение отменено.";
    }

    internal void DeleteRecord(int index)
    {
        byte[] before = _draft.ExportBytes();
        _draft.RemoveAt(index); Remember(before);
        _template.SelectedIndex = 0;
        RefreshRows();
        _status.Text = "Запись удалена из черновика ADB. Клип ANM сохранён. Доступна отмена; для записи на диск нажмите «Сохранить ADB с копией».";
    }

    private void Delete()
    {
        if (_list.SelectedItem is not Row { Index: int index } row)
        { _status.Text = "Выберите зарегистрированную запись ADB. Строка «Только ANM» не является записью ADB."; return; }
        if (index != _draft.Count - 1)
        { _status.Text = "Запись в середине ADB: удаление сдвинет номера. Замените ID клипа в этой записи, чтобы сохранить номер. Удалять можно только последнюю запись."; return; }
        if (MessageBox.Show(this, $"Удалить последнюю запись #{index} «{row.Id}» из ADB?\nНомера остальных записей сохранятся. Клип ANM останется. Ссылки на удаляемый номер перестанут работать.\nДо сохранения доступна отмена.",
            "Удаление записи ADB", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            DeleteRecord(index);
    }

    private static TextBlock HelpBlock() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 8), Opacity = .85 };
    private void UpdateFlagHelp()
    {
        uint flags;
        try { flags = Hex(_flags); }
        catch (FormatException) { _actionHelp.Text = _stageHelp.Text = _stateHelp.Text = "Введите корректные флаги, чтобы увидеть их значение."; return; }
        _actionHelp.Text = (flags & 0x003C0000) switch
        {
            0x00040000 => "Особая — отдельное специальное действие: например жест или танец. Нужен соответствующий вызов игры или скрипта; добавление записи не запускает его автоматически.",
            0x00080000 => "Атака — движение при выполнении удара или выстрела. Сам клип не задаёт урон: момент попадания и подходящий тип оружия настраиваются отдельно.",
            0x000C0000 => "Заклинание — движение при применении магии. Этот флаг не добавляет заклинание и не определяет его эффект.",
            0x00100000 => "Бег — движение для бега. Позы должны соответствовать бегу, а скорость согласовываться с перемещением персонажа.",
            0x00140000 => "Ходьба — движение для шага. Этот выбор не превращает танец в ходьбу и не создаёт траекторию.",
            0x00180000 => "Ожидание — движение во время бездействия: стоять, дышать, оглядываться. Это не пауза перед запуском и не задержка в секундах.",
            0x001C0000 => "Смерть — движение умирания персонажа. Назначение этого типа само по себе не убивает персонажа.",
            0x00200000 => "Урон — реакция на полученный удар: вздрагивание, отшатывание. Это получение удара, а не нанесение урона противнику.",
            0x00240000 => "Переход — смена состояния/стойки, например из обычной в боевую или в положение лёжа. Цель перехода задаётся дополнительными флагами.",
            _ => "Неизвестный код действия. Редактор сохраняет его, но не приписывает ему неподтверждённый смысл."
        };
        _stageHelp.Text = (flags & 0xC0000000) switch
        {
            0 => "Одиночная — самостоятельный клип действия, без обозначения начала, повторяющейся части или конца. Это не число кадров.",
            0x40000000 => "Начало — вступительная часть действия перед основной фазой, например принять нужную стойку.",
            0x80000000 => "Цикл — повторяемая основная часть действия. Первый и последний кадры должны стыковаться. Это игровой признак ADB, отдельный от флажка «Повтор» в предпросмотре.",
            _ => "Конец — завершающая часть действия, например выйти из стойки. Это не номер последнего кадра."
        };
        _stateHelp.Text = (flags & 0x00038000) switch
        {
            0 => "Нейтральное — обычное состояние персонажа вне специальных стоек. Это условие выбора клипа, а не команда выйти из боя.",
            0x8000 => "Отдых — клип для состояния отдыха. Отличается от действия «Ожидание»: здесь задаётся состояние персонажа, а там — вид движения.",
            0x10000 => "Атака — атакующая боевая стойка/режим. В этой стойке персонаж может ожидать, идти или атаковать: конкретное движение задаёт поле «Действие».",
            0x20000 => "Оборона — защитная боевая стойка/режим. Сам флаг не даёт бонус к защите и не задаёт блокирование удара.",
            0x30000 => "Лёжа — клип для персонажа в положении лёжа. Флаг не поворачивает модель автоматически: нужная поза должна быть в ANM.",
            _ => "Неизвестный код состояния. Существующие биты сохраняются без изменения."
        };
    }

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
        _reading = true;
        _template.ItemsSource = new[] { "Без шаблона — новая запись" }.Concat(entries.Select(e => $"Копировать #{e.Index}: {e.Name} ({ActionLabel(e.Flags)})")).ToArray();
        _template.SelectedIndex = Math.Clamp(templateIndex, 0, entries.Count);
        _reading = false;
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
        _applyButton.IsEnabled = _list.SelectedItem is Row;
        _applyButton.Content = _list.SelectedItem is Row selected
            ? selected.Index is null ? "Добавить в ADB" : "Применить изменения"
            : "Выберите анимацию";
        _applyButton.ToolTip = _list.SelectedItem is Row { Index: null }
            ? "Создать запись ADB с указанными справа параметрами. Затем сохраните ADB."
            : "Применить параметры к выбранной записи в черновике. Затем сохраните ADB.";
        if (_list.SelectedItem is not Row row) return;
        _id.Text = row.Id;
        AnimationDatabaseEntry? entry = row.Index is int index ? _draft.Entries[index] : null;
        if (entry is null && _template.SelectedIndex > 0) entry = _draft.Entries[_template.SelectedIndex - 1];
        uint flags = entry?.Flags ?? 0x00040000;
        _reading = true;
        _flags.Text = $"0x{flags:X8}"; _flags2.Text = $"0x{(row.Index is null ? (entry?.Flags2 ?? 0) & ~0xFu : entry!.Flags2):X8}";
        _action.SelectedItem = ((IEnumerable<FlagChoice>)_action.ItemsSource).FirstOrDefault(x => x.Value == (flags & 0x003C0000));
        _stage.SelectedItem = ((IEnumerable<FlagChoice>)_stage.ItemsSource).FirstOrDefault(x => x.Value == (flags & 0xC0000000));
        _state.SelectedItem = ((IEnumerable<FlagChoice>)_state.ItemsSource).FirstOrDefault(x => x.Value == (flags & 0x00038000));
        _reading = false;
        _probability.Text = (entry?.Probability ?? 100).ToString(); _length.Text = row.Index is null ? "" : entry!.Length.ToString();
        _speed.Text = (entry?.Speed ?? 0).ToString("R", CultureInfo.InvariantCulture);
        _hides.Text = Join(row.Index is null ? new uint[2] : entry!.Hides);
        _steps.Text = Join(row.Index is null ? new uint[4] : entry!.Steps);
        _hit.Text = row.Index is null ? "0" : entry!.Hit.ToString();
        _sfxFrame.Text = row.Index is null ? "0" : entry!.SfxFrame.ToString();
        _sfxIndices.Text = Join(row.Index is null ? new uint[4] : entry!.SfxIndices);
        _hint.Text = row.Index is int i
            ? $"Запись #{i}: {row.State}. Действие: {ActionLabel(flags)}. Выбирайте флаги ниже; неизвестные биты сохраняются."
            : "Новая запись: настройте поля и нажмите «Добавить в ADB». Шаблон необязателен. Длина берётся из ANM; клип должен быть сохранён.";
        if (row.Index is null) ClipLengthTask = SetClipLength(row.Id);
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
            byte[] before = _draft.ExportBytes();
            _draft.Update(index, _id.Text.Trim(), Hex(_flags), Hex(_flags2), UInt(_probability), UInt(_length), speed,
                Array(_hides, 2), Array(_steps, 4), UInt(_hit), UInt(_sfxFrame), Array(_sfxIndices, 4));
            Remember(before); _status.Text = "Поля изменены в памяти. Нажмите «Сохранить ADB с копией».";
            RefreshRows(_id.Text.Trim(), index);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or DatabaseFormatException) { Error(ex); return false; }
    }
    private void ApplySelectedFields()
    {
        if (_list.SelectedItem is Row { Index: null }) Add();
        else if (_list.SelectedItem is Row) Apply();
    }
    private void Add()
    {
        try
        {
            if (_list.SelectedItem is not Row { Index: null } row) throw new InvalidOperationException("Выберите клип со статусом «Только ANM».");
            if (!_id.Text.Trim().Equals(row.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Для добавления выберите существующий клип ANM; переименовать его здесь нельзя.");
            if (!SavedClipExists(row.Id)) throw new InvalidOperationException("Клип отсутствует в текущем ANM. Обновите предпросмотр.");
            uint length = UInt(_length);
            if (length == 0) throw new FormatException("Укажите число кадров клипа.");
            int index = _draft.Count;
            if (index > AnimationDatabase.MaximumLegacySafeIndex && MessageBox.Show(this,
                $"Новая запись получит индекс {index}. Штатная игра передаёт номер одним байтом и может воспроизвести другую анимацию. Добавить запись для будущей поддержки addon.dll?",
                "Индекс выше игрового предела", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            float speed = float.TryParse(_speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float n)
                ? n : throw new FormatException("Скорость: число с точкой.");
            byte[] before = _draft.ExportBytes();
            index = _draft.AppendConfigured(_template.SelectedIndex > 0 ? _template.SelectedIndex - 1 : null, row.Id, Hex(_flags), Hex(_flags2),
                UInt(_probability), length, speed, Array(_hides, 2), Array(_steps, 4), UInt(_hit), UInt(_sfxFrame), Array(_sfxIndices, 4));
            Remember(before); RefreshRows(row.Id, index);
            _status.Text = $"Добавлена запись #{index}. Проверьте параметры, затем сохраните ADB.";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or DatabaseFormatException) { Error(ex); }
    }
    private async Task DeleteClip()
    {
        if (_list.SelectedItem is not Row row) return;
        if (_dirty) { _status.Text = "Сначала сохраните или отмените изменения ADB, затем удалите клип ANM."; return; }
        if (_draft.Entries.Any(e => e.Name.Equals(row.Id, StringComparison.OrdinalIgnoreCase)))
        { _status.Text = "Клип используется в ADB. Сначала удалите или замените все ссылки на него и сохраните ADB."; return; }
        if (MessageBox.Show(this, $"Удалить клип «{row.Id}» и все его дорожки из ANM?\nИзменение сразу запишется с резервной копией. Отмена ADB его не возвращает.\nВнешние обращения к этому имени не проверяются.", "Удаление клипа ANM",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            SavedAnimationSet = await AnimationClipDeletion.SaveAsync(_preview, _source, row.Id);
            _preview = SavedAnimationSet; RefreshRows();
            _status.Text = "Клип удалён из ANM. Файл проверен, резервная копия создана. Остальные клипы и номера ADB сохранены.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or DatabaseFormatException) { Error(ex); }
    }

    private bool SavedClipExists(string id)
    {
        // The paired save publishes this preview's ANM together with the ADB.
        // Disk snapshot conflicts are checked under writer locks by the transaction.
        return _preview.SaveTarget is not null && _preview.Names.Contains(id, StringComparer.OrdinalIgnoreCase);
    }
    private async Task Save()
    {
        try
        {
            if (_list.SelectedItem is Row { Index: int } && !Apply()) return;
            if (!_dirty) { _status.Text = "Изменений ADB нет."; return; }
            string? backup;
            if (_preview.SaveTarget is { } target)
            {
                byte[] animation = _preview.ExportBytes();
                backup = await AnimationPairSave.SaveAsync(target, target.OriginalBytes, animation, _source, _draft);
                SavedAnimationSet = _preview = _preview.WithSavedBytes(animation);
            }
            else backup = await _source.SaveAsync(_draft);
            _draft = _source.CreateDraft(); _dirty = false; _history.Clear();
            _status.Text = "ADB сохранён и проверен. Резервная копия: " + (backup ?? "не потребовалась");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or DatabaseFormatException) { Error(ex); }
    }
    private void Error(Exception ex) { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "ADB", MessageBoxButton.OK, MessageBoxImage.Error); }
}
