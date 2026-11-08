using System.Globalization;
using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

// ANM stays raw. Only the root XYZ receives the original ADB/tall factor;
// BON/FIG have already been morphed and must not be scaled a second time.
public sealed class FigureRootSpace
{
    public static FigureRootSpace Raw { get; } = new();
    private readonly AnimationDatabaseSource? _source;
    private readonly AnimationDatabase? _database;
    private readonly float? _fixedCoefficient;
    public float Tall { get; }
    public bool HasAnimationDatabase => _source is not null || _database is not null;
    private FigureRootSpace() { }
    public FigureRootSpace(AnimationDatabaseSource source, float tall) { _source = source; Tall = tall; }
    public FigureRootSpace(AnimationDatabase database, float tall) { _database = database; Tall = tall; }
    public static FigureRootSpace FromTransfer(float coefficient) => new(coefficient);
    private FigureRootSpace(float coefficient) { _fixedCoefficient = coefficient; }
    public float Coefficient
    {
        get
        {
            if (_fixedCoefficient is float value) return Finite(value);
            var db = _source?.Database ?? _database;
            if (db is null) return 1;
            // The original middle=0 branch returns one, including for tall=0.
            if (db.MiddleHeight == 0) return 1;
            if (!float.IsFinite(Tall) || !float.IsFinite(db.MinimumHeight) ||
                !float.IsFinite(db.MiddleHeight) || !float.IsFinite(db.MaximumHeight))
                throw new InvalidDataException("ADB содержит неконечные высоты или рост.");
            return Finite((db.MinimumHeight + Tall * (db.MaximumHeight - db.MinimumHeight)) / db.MiddleHeight);
        }
    }
    private static float Finite(float value) => float.IsFinite(value) ? value :
        throw new InvalidDataException("Поправка корня ADB не является конечным числом.");
    private static Vector3 FiniteVector(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) ? value :
        throw new InvalidDataException("Преобразование корня дало неконечные координаты; правка не применена.");
    public Vector3 ToGame(Vector3 raw) => FiniteVector(raw * Coefficient);
    public Vector3 ToRaw(Vector3 game)
    {
        float c = Coefficient;
        if (c <= 0) throw new InvalidDataException("Поправка корня ADB должна быть положительной для записи игровых координат. Ресурс можно читать; правка не применена.");
        return FiniteVector(game / c);
    }
    public string Identity => (HasAnimationDatabase ? "adb:" : _fixedCoefficient.HasValue ? "transfer:" : "raw:") +
        Coefficient.ToString("R", CultureInfo.InvariantCulture);
}
