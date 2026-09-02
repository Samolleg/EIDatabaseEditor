namespace EvilIslands.DatabaseEditor.Core;

internal static class DatabaseSchemas
{
    private static readonly DatabaseValueKind S = DatabaseValueKind.String;
    private static readonly DatabaseValueKind I = DatabaseValueKind.Int32;
    private static readonly DatabaseValueKind U = DatabaseValueKind.UInt32;
    private static readonly DatabaseValueKind F = DatabaseValueKind.Float32;
    private static readonly DatabaseValueKind B = DatabaseValueKind.Byte;
    private static readonly DatabaseValueKind SL = DatabaseValueKind.StringList;
    private static readonly DatabaseValueKind FL = DatabaseValueKind.Float32List;
    private static readonly DatabaseValueKind BL = DatabaseValueKind.ByteList;
    private static readonly DatabaseValueKind Nested = DatabaseValueKind.Nested;
    private static readonly DatabaseValueKind Fixed = DatabaseValueKind.FixedString;

    public static IReadOnlyList<SectionSchema> Items { get; } =
    [
        Section(1, "items.materials", "Материалы", 0,
            EditableField(0, S, "Имя"),
            EditableField(1, S, "Тип"),
            EditableField(2, S, "Код"),
            EditableField(3, I, "ID"),
            EditableField(4, F, "Цена"),
            EditableField(5, F, "Вес"),
            EditableField(6, F, "Мана"),
            EditableField(7, I, "Слоты"),
            EditableField(8, F, "Прочность"),
            EditableField(9, I, "Навык"),
            EditableField(10, F, "Урон"),
            ReadOnlyField(11, FL, "Поглощение",
                ListElement(0, "Колющий", "Absorbtion: Piercing"),
                ListElement(1, "Рубящий", "Absorbtion: Slashing"),
                ListElement(2, "Дробящий", "Absorbtion: Bludgeoning"),
                ListElement(3, "Термический", "Absorbtion: Termal"),
                ListElement(4, "Химический", "Absorbtion: Chemic"),
                ListElement(5, "Электрический", "Absorbtion: Electric"),
                ListElement(6, "Общий", "Absorbtion: General")),
            EditableField(12, I, "Маска травм"),
            EditableField(13, U, "Магазины",
                Bit(0, "Магазин 1", "Shop 1"),
                Bit(1, "Магазин 2", "Shop 2"),
                Bit(2, "Магазин 3", "Shop 3"),
                Bit(3, "Магазин 4", "Shop 4"),
                Bit(4, "Магазин 5", "Shop 5"))),
        Section(2, "items.weapons", "Оружие", 0,
            (0, S, "Имя"), (1, S, "Тип"), (2, I, "ID типа"), (3, S, "Материал"),
            (4, I, "Индекс типа материала"), (5, I, "Индекс текстуры"), (6, I, "Индекс текстуры 2"),
            (7, F, "Цена"), (8, F, "Вес"), (9, F, "Размер"), (10, F, "Мана"),
            (11, I, "Слоты"), (12, F, "Прочность"), (13, I, "Компоненты"),
            (15, B, "Разбираемый"), (21, I, "Действия"), (23, F, "Дальность"),
            (22, DatabaseValueKind.UInt32List, "Модификаторы характеристик", false),
            (24, F, "Мин. урон"), (25, F, "Макс. урон"),
            (27, BL, "Ручные навыки", false), (28, BL, "Автонавыки", false),
            (29, F, "Модификатор атаки"), (30, F, "Модификатор защиты")),
        Section(3, "items.armors", "Броня", 0,
            (0, S, "Имя"), (1, S, "Тип"), (2, I, "ID типа"), (3, S, "Материал"),
            (4, I, "Индекс типа материала"), (5, I, "Индекс текстуры"), (6, I, "Индекс текстуры 2"),
            (7, F, "Цена"), (8, F, "Вес"), (9, F, "Размер"), (10, F, "Мана"),
            (11, I, "Слоты"), (12, F, "Прочность"), (13, I, "Компоненты"),
            (15, B, "Разбираемый"), (23, B, "Наносит раны"), (24, I, "Порядок надевания"),
            (25, BL, "Ручные навыки", false), (26, BL, "Автонавыки", false)),
        ReadOnlySection(4, "items.quick_items", "Быстрые предметы", 0),
        ReadOnlySection(5, "items.quest_items", "Квестовые предметы", 0),
        ReadOnlySection(6, "items.loot_items", "Лут", 0),
    ];

    public static IReadOnlyList<SectionSchema> Levers { get; } =
    [
        ReadOnlySection(1, "levers.prototypes", "Прототипы рычагов", 0),
    ];

    public static IReadOnlyList<SectionSchema> Perks { get; } =
    [
        ReadOnlySection(1, "perks.skills", "Навыки", 0),
        ReadOnlySection(2, "perks.perks", "Перки", 0),
    ];

    public static IReadOnlyList<SectionSchema> Prints { get; } =
    [
        ReadOnlySection(1, "prints.blood", "Следы крови", 1, "Тип поверхности"),
        ReadOnlySection(2, "prints.foot", "Следы ног", 1, "Тип поверхности"),
        ReadOnlySection(3, "prints.fire", "Следы огня", 1, "Тип поверхности"),
    ];

    public static IReadOnlyList<SectionSchema> Units { get; } =
    [
        Section(1, "units.hit_locations", "Типы зон попаданий", 0,
            (0, S, "Имя"), (1, FL, "Сопротивления", false),
            (2, FL, "Поглощения", false), (3, U, "Маска травм"), (4, U, "Тип")),
        Section(2, "units.races", "Расы и модели", 0,
            (0, S, "Имя"), (1, U, "Тип существа"), (2, F, "Регенерация HP"),
            (3, F, "Регенерация MP"), (4, U, "Язык"), (5, U, "Тип движения"),
            (6, F, "Угол зрения"), (7, FL, "Скорости", false), (8, F, "Дистанция атаки"),
            (9, U, "Класс ИИ стоя"), (10, U, "Класс ИИ лёжа"), (11, FL, "Типы атаки", false),
            (12, Nested, "Зона: голова", false), (13, Nested, "Зона: торс", false),
            (14, Nested, "Зона: правая рука", false), (15, Nested, "Зона: левая рука", false),
            (16, Nested, "Зона: правая нога", false), (17, Nested, "Зона: левая нога", false),
            (30, S, "Маска модели"), (31, SL, "Основные текстуры", false),
            (32, SL, "Доп. текстуры", false), (33, F, "Сдвиг центра"), (34, S, "Путь SFX"),
            (35, SL, "Звуки шагов", false), (36, FL, "Коэффициенты анимаций", false),
            (37, U, "Шанс idle-звука"), (38, U, "Шанс звука атаки"),
            (39, FL, "Базовая защита", false), (40, U, "Тип крови"), (41, U, "Тип колдовства"),
            (42, I, "Тип следов"), (43, U, "Сегменты ног"), (44, S, "Тип кожи"),
            (45, B, "Первый шаг правой"), (46, F, "Высота головы"),
            (47, U, "Рот X"), (48, U, "Рот Y"), (49, U, "Размер рта X"), (50, U, "Размер рта Y")),
        Section(3, "units.prototypes", "Прототипы монстров", 0,
            (0, S, "Имя"), (1, S, "Раса"), (2, I, "Сохранённый индекс расы"),
            (3, U, "Индекс кожи"), (4, I, "Волосы"), (5, F, "Телосложение"),
            (6, F, "Комплекция"), (7, F, "Рост"), (8, S, "Путь SFX"), (9, F, "Макс. HP"),
            (10, F, "Макс. MP"), (11, F, "Множитель поглощения"), (12, F, "Очки действия"),
            (13, F, "Очки движения"), (14, F, "Дальность атаки"), (15, F, "Попадание"),
            (16, F, "Парирование"), (17, F, "Вес атаки"), (18, U, "Тип оружия"),
            (19, F, "Мин. урон"), (20, F, "Случайный урон"), (21, F, "Базовый навык"),
            (22, F, "Кража"), (23, F, "Приручение"), (24, F, "Периферическое зрение"),
            (25, FL, "Органы чувств", false), (26, FL, "Заметность", false),
            (27, Nested, "Обычный лут", false), (28, Nested, "Редкий лут", false),
            (29, SL, "Гарантированный лут", false), (30, FL, "Навыки магии", false),
            (31, SL, "Заклинания", false), (32, SL, "Варианты доспехов", false),
            (33, S, "Оружие 1"), (34, F, "Масштаб в окне"), (35, F, "Высота над поверхностью"),
            (36, F, "Случайное попадание"), (37, F, "Дистанция камеры"),
            (38, F, "Высота камеры"), (39, U, "Реальный тип оружия"),
            (40, F, "Детонация"), (41, U, "Уровень"), (42, S, "Оружие 2"), (43, F, "Опыт за убийство")),
        Section(4, "units.npcs", "Прототипы NPC", 0,
            (0, S, "Имя"), (1, U, "Очки персонажа"), (2, F, "Опыт"), (3, F, "Сила"),
            (4, F, "Ловкость"), (5, F, "Разум"), (6, BL, "Ручные навыки", false),
            (7, BL, "Автонавыки", false), (8, SL, "Перки", false), (9, SL, "Оружие", false),
            (10, SL, "Быстрые предметы", false), (11, SL, "Заклинания", false),
            (12, F, "Опыт к распределению"), (13, U, "Деньги"), (14, B, "Голосовой прототип")),
    ];

    public static IReadOnlyList<SectionSchema> Spells { get; } =
    [
        Section(1, "spells.prototypes", "Прототипы заклинаний", 0,
            (0, S, "Имя", true, "Name"), (1, S, "Код", true, "CodeName"),
            (2, S, "Подтип", true, "SubtypeName"), (3, F, "Цена", true, "Price"),
            (4, I, "Школа", true, "School"), (5, F, "Расход MP", true, "BaseManaCost"),
            (6, I, "Слоты", true, "Slots"), (7, F, "Скорость", true, "Speed"),
            (8, F, "Дальность", true, "Range"), (9, F, "Область", true, "Area"),
            (10, F, "Сила эффекта", true, "Effect"),
            (11, I, "Цель"), (12, I, "Число целей"), (13, I, "Длительность"),
            (14, I, "Цена действия"), (15, U, "Требует прямой видимости"),
            (16, DatabaseValueKind.Hex, "Базовые модификаторы", false),
            (17, DatabaseValueKind.Hex, "Спец. модификаторы", false),
            (18, I, "Индекс иконки"), (19, I, "Подтип магии"),
            (20, BL, "Разрешённые руны", false), (21, I, "Сложность"),
            (22, DatabaseValueKind.Hex, "Доступность в магазинах", false),
            (23, F, "Свет: красный"), (24, F, "Свет: зелёный"), (25, F, "Свет: синий"),
            (26, F, "Радиус света", true, "LightRadius"),
            (27, F, "Время затухания", true, "FadeoutTime")),
        Section(2, "spells.modifiers", "Модификаторы заклинаний", 0,
            (0, S, "Имя"), (1, Fixed, "Код"), (2, F, "Цена"), (3, I, "Тип"),
            (4, F, "Вклад в MP"), (5, F, "Значение"), (6, I, "Сложность"),
            (7, S, "Аллод"), (8, DatabaseValueKind.Hex, "Доступность", false)),
        TemplateSection(3, "spells.templates", "Шаблоны заклинаний"),
        TemplateSection(4, "spells.armor_templates", "Шаблоны магии брони"),
        TemplateSection(5, "spells.weapon_templates", "Шаблоны магии оружия"),
    ];

    public static IReadOnlyList<SectionSchema> Acks { get; } =
    [
        ReadOnlySection(1, "acks.answers", "Ответы и команды", 1),
        ReadOnlySection(2, "acks.cries", "Боевые крики", 1),
        ReadOnlySection(3, "acks.others", "Прочие реплики", 1),
    ];

    public static IReadOnlyList<SectionSchema> Quests { get; } =
    [
        ReadOnlySection(1, "quests.quests", "Квесты", 0),
        ReadOnlySection(2, "quests.briefings", "Брифинги", 0),
    ];

    public static IReadOnlyList<DatabaseGroupSchema> Groups { get; } =
    [
        new("items", "Предметы", "items.idb", false, Items),
        new("levers", "Рычаги", "levers.ldb", false, Levers),
        new("perks", "Навыки и перки", "perks.pdb", false, Perks),
        new("prints", "Следы", "prints.db", false, Prints),
        new("spells", "Заклинания", "spells.sdb", true, Spells),
        new("units", "Юниты", "units.udb", true, Units),
        new("acks", "Реплики", "acks.db", false, Acks),
        new("quests", "Задания", "quests.qdb", false, Quests),
    ];

    private static SectionSchema TemplateSection(byte id, string key, string name) => Section(id, key, name, 1,
        (1, S, "Прототип"), (2, SL, "Обязательные руны", false),
        (3, SL, "Необязательные руны", false), (4, S, "Сила"),
        (5, DatabaseValueKind.Hex, "Доступность", false));

    private static SectionSchema ReadOnlySection(
        byte id,
        string key,
        string name,
        byte nameFieldId,
        string nameFieldTitle = "Имя") =>
        Section(id, key, name, nameFieldId, (nameFieldId, S, nameFieldTitle, false));

    private static SchemaFieldSpec ReadOnlyField(
        byte id,
        DatabaseValueKind kind,
        string name,
        params DatabaseFieldProjection[] projections) =>
        new(id, kind, name, false, null, projections);

    private static SchemaFieldSpec EditableField(
        byte id,
        DatabaseValueKind kind,
        string name,
        params DatabaseFieldProjection[] projections) =>
        new(id, kind, name, true, null, projections);

    private static DatabaseFieldProjection ListElement(
        int index,
        string displayName,
        string? description = null) =>
        new(DatabaseFieldProjectionKind.ListElement, index, displayName, description);

    private static DatabaseFieldProjection Bit(
        int index,
        string displayName,
        string? description = null) =>
        new(DatabaseFieldProjectionKind.Bit, index, displayName, description);

    private static SectionSchema Section(
        byte id,
        string key,
        string name,
        byte nameFieldId,
        params SchemaFieldSpec[] fields)
    {
        var dictionary = fields.ToDictionary(
            field => field.Id,
            field => new FieldSchema(
                field.Kind,
                field.Name,
                field.Editable,
                field.Key,
                field.ProjectionMetadata));
        byte identityFieldId = key is "spells.prototypes" or "spells.modifiers"
            ? (byte)1
            : nameFieldId;
        IReadOnlyDictionary<byte, FieldSchema> mergedFields = LegacySchemaCatalog.Merge(key, id, dictionary);
        IReadOnlyDictionary<byte, FieldSchema> editableFields = mergedFields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Editable = true });
        return new SectionSchema(
            id,
            key,
            name,
            nameFieldId,
            identityFieldId,
            true,
            editableFields);
    }

}

internal sealed record DatabaseGroupSchema(
    string Key,
    string Title,
    string ResourceName,
    bool IsEditable,
    IReadOnlyList<SectionSchema> Sections);

internal readonly record struct SchemaFieldSpec(
    byte Id,
    DatabaseValueKind Kind,
    string Name,
    bool Editable,
    string? Key,
    IReadOnlyList<DatabaseFieldProjection>? ProjectionMetadata)
{
    public static implicit operator SchemaFieldSpec((byte Id, DatabaseValueKind Kind, string Name) value) =>
        new(value.Id, value.Kind, value.Name, true, null, null);

    public static implicit operator SchemaFieldSpec((byte Id, DatabaseValueKind Kind, string Name, bool Editable) value) =>
        new(value.Id, value.Kind, value.Name, value.Editable, null, null);

    public static implicit operator SchemaFieldSpec(
        (byte Id, DatabaseValueKind Kind, string Name, bool Editable, string Key) value) =>
        new(value.Id, value.Kind, value.Name, value.Editable, value.Key, null);
}
