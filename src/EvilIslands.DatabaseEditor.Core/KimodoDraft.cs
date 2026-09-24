using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

// kmd-generate writes little-endian float32 streams, frame-major, with XYZW quaternions.
public sealed record KimodoDraft(int Frames, Vector3[] RootPositions, Quaternion[][] Rotations)
{
    public const int SomaJoints = 30;

    public static KimodoDraft ReadSoma30(string directory, int frames)
    {
        if (frames is < 2 or > 1000) throw new InvalidDataException("Kimodo: длина должна быть от 2 до 1000 кадров.");
        float[] roots = ReadFloats(Path.Combine(directory, "root_positions.f32"), checked(frames * 3));
        float[] rotations = ReadFloats(Path.Combine(directory, "local_rotations_xyzw.f32"), checked(frames * SomaJoints * 4));
        var positions = new Vector3[frames];
        var poses = new Quaternion[frames][];
        for (int f = 0; f < frames; f++)
        {
            positions[f] = new(roots[f * 3], roots[f * 3 + 1], roots[f * 3 + 2]);
            if (Math.Max(Math.Abs(positions[f].X), Math.Max(Math.Abs(positions[f].Y), Math.Abs(positions[f].Z))) > 1000)
                throw new InvalidDataException("Kimodo: координата корня вне допустимого диапазона.");
            poses[f] = new Quaternion[SomaJoints];
            for (int j = 0; j < SomaJoints; j++)
            {
                int i = (f * SomaJoints + j) * 4;
                var q = new Quaternion(rotations[i], rotations[i + 1], rotations[i + 2], rotations[i + 3]);
                float length = q.LengthSquared();
                if (length is < .25f or > 2.25f) throw new InvalidDataException("Kimodo: повреждён поворот сустава.");
                poses[f][j] = Quaternion.Normalize(q);
            }
        }
        return new(frames, positions, poses);
    }

    private static float[] ReadFloats(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != (long)count * sizeof(float)) throw new InvalidDataException("Kimodo: неверная длина " + Path.GetFileName(path));
        var values = new float[count];
        using var reader = new BinaryReader(stream);
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadSingle();
            if (!float.IsFinite(values[i])) throw new InvalidDataException("Kimodo: нечисловое значение в " + Path.GetFileName(path));
        }
        return values;
    }
}
