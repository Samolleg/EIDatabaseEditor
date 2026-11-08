namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    public async Task<float> AlignRootHeightAsync(string name, string reference)
    {
        if (Contacts.Count > 0)
            throw new ArgumentException("Снимите диапазоны контактов перед изменением высоты всей анимации.");
        var animations = Preview();
        var clip = await animations.LoadAsync(name);
        var referenceClip = await animations.LoadAsync(reference);
        if (!clip.Tracks.TryGetValue("hp", out var root) || root.Positions.Length == 0 ||
            !referenceClip.Tracks.TryGetValue("hp", out var referenceRoot) || referenceRoot.Positions.Length == 0 ||
            animations.Parents.GetValueOrDefault("hp") != "")
            throw new ArgumentException("Оба клипа должны иметь корневую дорожку hp с положением таза.");
        float difference = referenceRoot.Position(0).Z - root.Position(0).Z;
        if (Math.Abs(difference) < .000001f) return 0;

        // Only change the Z floats. Preserve rotations, morphs, X/Y and every
        // unrelated ANM payload byte-for-byte; the whole edit is one Undo step.
        byte[] changed = RawTracks(name)["hp"].ToArray();
        int rotations = BitConverter.ToInt32(changed, 0);
        int positionsOffset = checked(4 + rotations * 16);
        int positions = BitConverter.ToInt32(changed, positionsOffset);
        if (positions != root.Positions.Length)
            throw new InvalidDataException("Число позиций корня не совпадает с дорожкой.");
        int first = checked(positionsOffset + 4);
        for (int frame = 0; frame < positions; frame++)
        {
            int zOffset = checked(first + frame * 12 + 8);
            float z = BitConverter.ToSingle(changed, zOffset) + difference;
            if (!float.IsFinite(z) || Math.Abs(z) >= 100000)
                throw new InvalidDataException("Высота корня после переноса вне допустимого диапазона.");
            BitConverter.GetBytes(z).CopyTo(changed, zOffset);
        }
        await ReplaceTracks(name, new Dictionary<string, byte[]> { ["hp"] = changed });
        return difference;
    }
}
