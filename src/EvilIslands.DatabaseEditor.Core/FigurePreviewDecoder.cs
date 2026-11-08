using System.Numerics;
using System.Text;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record FigureMesh(string Name, Vector3[] Positions, Vector2[] UV, int[] Indices, int TextureSlot)
{
    public int[] SourceVertices { get; init; } = [];
    public Vector3[] Normals { get; init; } = [];
}

// Read-only FIG decoder. Packed coordinates contain four vertices per group,
// with morph samples interleaved per axis, not eight complete meshes.
public static class FigurePreviewDecoder
{
    public static FigureMesh Decode(string name, byte[] bytes, Vector3 morph)
    {
        if (bytes.Length < 40) throw new InvalidDataException($"{name}: заголовок FIG обрезан.");
        using var r = new BinaryReader(new MemoryStream(bytes), Encoding.ASCII);
        string signature = Encoding.ASCII.GetString(r.ReadBytes(4));
        if (!signature.StartsWith("FIG", StringComparison.Ordinal) || !char.IsAsciiDigit(signature[3]))
            throw new InvalidDataException($"{name}: неверная сигнатура FIG.");
        int samples = signature switch { "FIG1" => 1, "FIG8" => 8, _ => throw new NotSupportedException($"{name}: формат {signature} пока не поддержан.") };
        int Count(int limit)
        {
            uint count = r.ReadUInt32();
            if (count > limit) throw new InvalidDataException($"{name}: превышен лимит геометрии.");
            return (int)count;
        }
        int vertices = Count(65536), normals = Count(65536), maps = Count(65536);
        int indices = Count(600000), components = Count(65536), lights = Count(65536);
        int user = Count(4096);
        if (user != 0) throw new NotSupportedException($"{name}: дополнительные данные FIG не поддержаны.");
        r.ReadUInt32();
        int slot = r.ReadInt32();
        long expected = 40L + 40 * samples + vertices * 48L * samples + normals * 64L + maps * 8L + indices * 2L + components * 6L + lights * 4L;
        if (expected != bytes.Length || indices % 3 != 0) throw new InvalidDataException($"{name}: неверные размеры блоков FIG.");
        r.BaseStream.Position += 40 * samples;
        var points = new Vector3[vertices * 4];
        var values = new float[samples];
        for (int group = 0; group < vertices; group++)
            for (int axis = 0; axis < 3; axis++)
            {
                long start = r.BaseStream.Position;
                for (int lane = 0; lane < 4; lane++)
                {
                    for (int sample = 0; sample < samples; sample++)
                    {
                        r.BaseStream.Position = start + (sample * 4 + lane) * 4;
                        values[sample] = Finite(r.ReadSingle());
                    }
                    float value = Interpolate(values, morph);
                    if (axis == 0) points[group * 4 + lane].X = value;
                    else if (axis == 1) points[group * 4 + lane].Y = value;
                    else points[group * 4 + lane].Z = value;
                }
                r.BaseStream.Position = start + samples * 16;
            }
        // FIG normals are packed four lanes per axis (x/y/z/padding).
        var normalVectors = new Vector3[normals * 4];
        for (int group = 0; group < normals; group++)
            for (int axis = 0; axis < 4; axis++)
                for (int lane = 0; lane < 4; lane++)
                {
                    float value = Finite(r.ReadSingle());
                    if (axis == 0) normalVectors[group * 4 + lane].X = value;
                    else if (axis == 1) normalVectors[group * 4 + lane].Y = value;
                    else if (axis == 2) normalVectors[group * 4 + lane].Z = value;
                }
        var uv = new Vector2[maps];
        for (int i = 0; i < maps; i++) uv[i] = new(Finite(r.ReadSingle()), Finite(r.ReadSingle()));
        var triangles = new int[indices];
        for (int i = 0; i < indices; i++)
        {
            triangles[i] = r.ReadUInt16();
            if (triangles[i] >= components) throw new InvalidDataException($"{name}: индекс треугольника вне массива.");
        }
        var result = new Vector3[components];
        var resultUV = new Vector2[components];
        var sourceVertices = new int[components];
        var resultNormals = new Vector3[components];
        for (int i = 0; i < components; i++)
        {
            int vertex = r.ReadUInt16(), normal = r.ReadUInt16(), map = r.ReadUInt16();
            if (vertex >= points.Length || normal >= normals * 4 || map >= maps) throw new InvalidDataException($"{name}: индекс компонента вне массива.");
            result[i] = points[vertex]; resultUV[i] = uv[map];
            sourceVertices[i] = vertex;
            resultNormals[i] = normalVectors[normal];
        }
        return new(name, result, resultUV, triangles, slot) { SourceVertices = sourceVertices, Normals = resultNormals };
    }

    public static Vector3 Bone(byte[] bytes, Vector3 morph)
    {
        if (bytes.Length is not (12 or 96)) throw new NotSupportedException("Неподдержанный размер BON.");
        int count = bytes.Length / 12;
        var coordinates = new float[count];
        var result = new float[3];
        for (int axis = 0; axis < 3; axis++)
        {
            for (int i = 0; i < count; i++) coordinates[i] = Finite(BitConverter.ToSingle(bytes, i * 12 + axis * 4));
            result[axis] = Interpolate(coordinates, morph);
        }
        return new(result[0], result[1], result[2]);
    }

    private static float Finite(float value) => float.IsFinite(value) && Math.Abs(value) < 100000
        ? value : throw new InvalidDataException("Некорректная координата модели.");

    private static float Interpolate(float[] v, Vector3 m)
    {
        if (v.Length == 1) return v[0];
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(v[0], v[1], m.X), L(v[2], v[3], m.X), m.Y),
                 L(L(v[4], v[5], m.X), L(v[6], v[7], m.X), m.Y), m.Z);
    }
}
