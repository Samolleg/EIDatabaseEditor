using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed partial class FigureAnimationEditor
{
    // SOMA30 paths are collapsed onto the game's fifteen-part humanoid rig.
    // Unrepresented fingers, toes, eyes and jaw are intentionally left in bind pose.
    private static readonly IReadOnlyDictionary<string, int[]> SomaHumanoid = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["hp"] = [0], ["bd"] = [1, 2, 3], ["hd"] = [4, 5, 6],
        ["lh1"] = [10, 11], ["lh2"] = [12], ["lh3"] = [13],
        ["rh1"] = [16, 17], ["rh2"] = [18], ["rh3"] = [19],
        ["ll1"] = [22], ["ll2"] = [23], ["ll3"] = [24],
        ["rl1"] = [26], ["rl2"] = [27], ["rl3"] = [28]
    };
    // Kimodo SOMA30 hierarchy (upstream skeleton.hpp); parent orientation precedes local orientation.
    private static readonly int[] SomaParents = [-1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28];
    // SOMA is Y-up and walks along +Z; the game is Z-up and faces -Y.
    private static readonly Quaternion SomaToGame = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);

    public async Task ApplyKimodoDraftAsync(string name, KimodoDraft draft)
    {
        var clip = await Preview().LoadAsync(name);
        if (draft.Frames != clip.FrameCount || draft.RootPositions.Length != draft.Frames ||
            draft.Rotations.Length != draft.Frames || draft.Rotations.Any(row => row.Length != KimodoDraft.SomaJoints))
            throw new ArgumentException("Длина или скелет движения Kimodo не совпадают с клипом.");
        if (Contacts.Count > 0) throw new ArgumentException("Снимите контакты перед переносом движения.");
        if (SomaHumanoid.Keys.Any(part => !clip.Tracks.ContainsKey(part) || !_source.Bones.ContainsKey(part)))
            throw new ArgumentException("Для Kimodo требуется гуманоид с частями hp, bd, hd, lh/rh1–3 и ll/rl1–3.");
        if (_source.Parents.GetValueOrDefault("hp") != "" || _source.Parents.GetValueOrDefault("bd") != "hp" ||
            _source.Parents.GetValueOrDefault("hd") != "bd" ||
            new[] { "lh1", "rh1" }.Any(p => _source.Parents.GetValueOrDefault(p) != "bd") ||
            new[] { "ll1", "rl1" }.Any(p => _source.Parents.GetValueOrDefault(p) != "hp"))
            throw new ArgumentException("Иерархия гуманоида отличается от проверяемой схемы переноса Kimodo.");
        foreach (string side in new[] { "lh", "rh", "ll", "rl" })
            if (_source.Parents.GetValueOrDefault(side + "2") != side + "1" || _source.Parents.GetValueOrDefault(side + "3") != side + "2")
                throw new ArgumentException("Иерархия конечности отличается от схемы переноса Kimodo: " + side);

        float gameLeg = _source.Bones["ll1"].Length() + _source.Bones["ll2"].Length() + _source.Bones["ll3"].Length();
        // SOMA30 reference offsets: left hip, shin and foot (metres).
        const float somaLeg = .0844f + .4322f + .4216f;
        if (gameLeg is < .01f or > 100f) throw new InvalidDataException("Не удалось определить масштаб ног модели.");
        float scale = gameLeg / somaLeg;
        var raw = RawTracks(name);
        var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Vector3 rootStart = clip.Tracks["hp"].Positions.Length > 0 ? clip.Tracks["hp"].Position(0) : _source.Bones["hp"];
        var convertedRoot = draft.RootPositions.Select(p => Vector3.Transform(p, SomaToGame)).ToArray();
        var somaGlobals = new Quaternion[draft.Frames][];
        for (int f = 0; f < draft.Frames; f++)
        {
            somaGlobals[f] = new Quaternion[KimodoDraft.SomaJoints];
            for (int joint = 0; joint < KimodoDraft.SomaJoints; joint++)
            {
                int parent = SomaParents[joint];
                somaGlobals[f][joint] = Quaternion.Normalize(parent < 0
                    ? draft.Rotations[f][joint]
                    : somaGlobals[f][parent] * draft.Rotations[f][joint]);
            }
        }
        foreach (var (part, path) in SomaHumanoid)
        {
            var rotations = new Quaternion[draft.Frames];
            for (int f = 0; f < draft.Frames; f++)
            {
                Quaternion global = ConvertSomaRotation(somaGlobals[f][path[^1]]);
                string parent = _source.Parents.GetValueOrDefault(part) ?? "";
                rotations[f] = parent.Length == 0 ? global : Quaternion.Normalize(global * Quaternion.Inverse(
                    ConvertSomaRotation(somaGlobals[f][SomaHumanoid[parent][^1]])));
            }
            var positions = part == "hp"
                ? Enumerable.Range(0, draft.Frames).Select(f => rootStart + (convertedRoot[f] - convertedRoot[0]) * scale).ToArray()
                : Array.Empty<Vector3>();
            var original = clip.Tracks[part];
            changed[part] = EncodeTrack(new FigureAnimationTrack(rotations, positions, original.Morphs));
        }
        // A single checked archive replacement gives one Undo step; all other clips and tracks keep their bytes.
        await ReplaceTracks(name, changed);
    }

    private static Quaternion ConvertSomaRotation(Quaternion q) =>
        Quaternion.Normalize(SomaToGame * q * Quaternion.Inverse(SomaToGame));
}
