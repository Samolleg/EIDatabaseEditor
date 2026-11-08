using System.Windows;
using System.Windows.Controls;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

internal sealed partial class AnimationDatabaseDialog
{
    private readonly List<(CheckBox Box, TextBox Source, uint Bit)> _flagChecks = new();
    private readonly StackPanel _modifiers = new();
    private readonly TextBlock _flagSummary = HelpBlock();
    private bool _syncingFlags;
    private uint? _modifierAction;
    private readonly List<(ComboBox Box, uint Mask)> _modifierChoices = new();

    private void BuildFlagControls(Panel fields)
    {
        fields.Children.Add(new TextBlock { Text = "Оружие — допустимые виды", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 8, 4, 4) });
        var weapons = new WrapPanel(); fields.Children.Add(weapons);
        string[] names = { "Меч", "Топор", "Кинжал", "Копьё", "Молот", "Лук", "Арбалет" };
        for (int i = 0; i < names.Length; i++) AddFlag(weapons, _flags, 1u << i, names[i], "Разрешить этот вид оружия при выборе клипа. Можно отметить несколько видов.");
        fields.Children.Add(new TextBlock { Text = "Ни одного оружейного бита — без оружия, а не любое оружие. Выбирайте только то оружие, под которое сделаны движения.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        fields.Children.Add(_modifiers);
        fields.Children.Add(new TextBlock { Text = "События внутри анимации", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 8, 4, 4) });
        AddFlag(fields, _flags2, 1, "Появление и скрытие стрелы", "Использовать два номера кадров в поле появления/скрытия предмета ниже.");
        AddFlag(fields, _flags2, 2, "Звуки шагов", "Использовать до четырёх кадров шагов. Ноль завершает последовательность.");
        AddFlag(fields, _flags2, 4, "Событие попадания", "Использовать кадр попадания ниже. Флаг сам по себе не задаёт урон.");
        AddFlag(fields, _flags2, 8, "Визуальные спецэффекты", "Использовать кадр эффекта и его индексы ниже. Флаг не создаёт сам эффект.");
        fields.Children.Add(new TextBlock { Text = "Совместимость с надетой экипировкой", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 8, 4, 4) });
        AddFlag(fields, _flags2, 0x10, "Разрешить с рубашкой, штанами, обувью и перчатками", "LOW_SHAPE: игра проверяет этот допуск, если надет хотя бы один из перечисленных предметов.");
        AddFlag(fields, _flags2, 0x20, "Разрешить со шлемом, кирасой и поножами", "HIGH_SHAPE: игра проверяет этот допуск, если надет хотя бы один из перечисленных предметов.");
        fields.Children.Add(new TextBlock { Text = "Допуски не надевают броню. Отмечайте их, если клип пригоден для соответствующей экипировки; без допуска игра может отклонить его для одетого персонажа.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        fields.Children.Add(_flagSummary);
        var raw = new StackPanel();
        Field(raw, "Код действия, состояния и оружия (hex)", _flags);
        Field(raw, "Код событий и допусков (hex)", _flags2);
        fields.Children.Add(new Expander { Header = "Для опытных пользователей — исходные коды", Content = raw, Margin = new Thickness(4) });
        _flags.TextChanged += (_, _) => SyncFlagControls();
        _flags2.TextChanged += (_, _) => SyncFlagControls();
    }

    private void AddFlag(Panel panel, TextBox source, uint bit, string title, string help)
    {
        var box = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, ToolTip = help, Margin = new Thickness(4, 3, 12, 3) };
        _flagChecks.Add((box, source, bit)); panel.Children.Add(box);
        void Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingFlags || _reading) return;
            try { uint old = Hex(source); source.Text = $"0x{(box.IsChecked == true ? old | bit : old & ~bit):X8}"; }
            catch (FormatException) { _status.Text = "Исправьте исходный код в разделе для опытных пользователей."; }
        }
        box.Checked += Changed; box.Unchecked += Changed;
    }

    private void Modifier(string title, uint mask, params FlagChoice[] choices)
    {
        var box = new ComboBox { Tag = choices };
        _modifierChoices.Add((box, mask)); Field(_modifiers, title, box);
        box.SelectionChanged += (_, _) => { if (!_syncingFlags) SetFlagBits(mask, box); };
    }

    private void SyncFlagControls()
    {
        if (_syncingFlags) return;
        _syncingFlags = true;
        bool reading = _reading; _reading = true;
        try
        {
            uint flags = Hex(_flags), events = Hex(_flags2), action = flags & 0x003C0000;
            foreach (var item in _flagChecks) item.Box.IsChecked = ((item.Source == _flags ? flags : events) & item.Bit) != 0;
            SelectChoice(_action, flags & 0x003C0000); SelectChoice(_stage, flags & 0xC0000000); SelectChoice(_state, flags & 0x00038000);
            if (_modifierAction != action)
            {
                _modifierAction = action; _modifiers.Children.Clear(); _modifierChoices.Clear();
                var none = new FlagChoice("Не задано (0)", 0);
                switch (action)
                {
                    case 0x00040000:
                        Modifier("Вариант особого действия — номер, запрашиваемый игрой", 0x3FC00000,
                            new[] { none }.Concat(Enumerable.Range(1, 16).Select(i => new FlagChoice($"Особое действие {i}", (uint)i * 0x400000))).ToArray()); break;
                    case 0x00080000: case 0x000C0000:
                        Modifier("Направление по горизонтали", 0x00C00000, none, new("Влево", 0x00400000), new("По центру", 0x00800000), new("Вправо", 0x00C00000));
                        Modifier("Направление по вертикали", 0x03000000, none, new("Вверх", 0x01000000), new("По центру", 0x02000000), new("Вниз", 0x03000000)); break;
                    case 0x00140000:
                        Modifier("Вариант ходьбы", 0x3FC00000, none, new("Хромота — вариант 1", 0x00400000), new("Хромота — вариант 2", 0x00800000)); break;
                    case 0x00180000:
                        Modifier("Вариант ожидания", 0x3FC00000, none, new("Стоять спокойно", 0x00400000), new("Оглядываться", 0x00800000)); break;
                    case 0x001C0000: case 0x00200000:
                        Modifier("Направление реакции", 0x01C00000, new("По центру", 0), new("Спереди", 0x00400000), new("Сзади", 0x00800000), new("Слева", 0x00C00000), new("Справа", 0x01000000));
                        Modifier("Движение перед реакцией", 0x06000000, none, new("Бег", 0x02000000), new("Ходьба", 0x04000000), new("Стоя на месте", 0x06000000)); break;
                    case 0x00240000:
                        Modifier("В какое состояние перейти", 0x3FC00000, none, new("Нейтральное", 0x00400000), new("Атакующая стойка", 0x00800000), new("Оборонительная стойка", 0x00C00000), new("Лёжа", 0x01000000), new("Отдых", 0x01400000)); break;
                }
                _modifiers.Children.Add(new TextBlock { Text = "Варианты зависят от действия. При смене действия прежние биты сохраняются — проверьте вариант заново. Номер особого действия не равен номеру записи ADB и не задаёт название движения.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
            }
            uint covered = 0;
            foreach (var item in _modifierChoices)
            {
                uint value = flags & item.Mask; covered |= item.Mask;
                var choices = (FlagChoice[])item.Box.Tag;
                item.Box.ItemsSource = choices.Any(c => c.Value == value) ? choices : choices.Append(new FlagChoice($"Неизвестное значение 0x{value:X8} — сохранено", value)).ToArray();
                SelectChoice(item.Box, value);
            }
            uint unknown = flags & ~(0xC03F807Fu | covered), unknownEvents = events & ~0x3Fu;
            _flagSummary.Text = unknown == 0 && unknownEvents == 0 ? "Коды рассчитываются автоматически из выбранных значений."
                : $"Есть дополнительные биты без расшифровки: основные 0x{unknown:X8}, события 0x{unknownEvents:X8}. Они сохраняются при изменении известных флагов.";
        }
        catch (FormatException) { _flagSummary.Text = "Некорректный исходный код. Исправьте его в разделе для опытных пользователей."; }
        finally { _reading = reading; _syncingFlags = false; }
    }

    private static void SelectChoice(ComboBox box, uint value) => box.SelectedItem =
        ((IEnumerable<FlagChoice>)box.ItemsSource).FirstOrDefault(c => c.Value == value);
}
