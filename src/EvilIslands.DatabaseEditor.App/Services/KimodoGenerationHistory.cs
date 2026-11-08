using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Services;

internal sealed record KimodoGenerationRecord
{
    public int Version { get; init; } = 2;
    public string Status { get; init; } = "prepared";
    public string? Error { get; init; }
    public string Model { get; init; } = "";
    public string Template { get; init; } = "";
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string UserPrompt { get; init; } = "";
    public string Avoid { get; init; } = "";
    public string KinematicConstraintsJson { get; init; } = "";
    public string ResolvedSpatialTargets { get; init; } = "";
    public string? ConstraintsSha256 { get; init; }
    public string? ResolvedTargetsSha256 { get; init; }
    public string EndpointReference { get; init; } = "";
    public int Frames { get; init; }
    public int Steps { get; init; }
    public ulong Seed { get; init; }
    public KimodoPlayback Playback { get; init; }
    public bool ReplaceCurrent { get; init; }
    public string SourceAnmSha256 { get; init; } = "";
    public string SkeletonSha256 { get; init; } = "";
    public string FootGeometrySha256 { get; init; } = "";
    public string EditorVersion { get; init; } = "";
    public string GeneratorPath { get; init; } = "";
    public string GeneratorSha256 { get; init; } = "";
    public string MotionWeightsPath { get; init; } = "";
    public string MotionWeightsSha256 { get; init; } = "";
    public string TextWeightsPath { get; init; } = "";
    public string TextWeightsSha256 { get; init; } = "";
    public string? RootStreamSha256 { get; init; }
    public string? RotationStreamSha256 { get; init; }
    public string? JointPositionsSha256 { get; init; }
    public string? ClipSha256 { get; init; }
    public string[] Issues { get; init; } = [];
}

// The raw SOMA streams are retained independently of game-rig conversion and diagnosis.
// A failed import is therefore replayable without running the inference process again.
internal sealed class KimodoGenerationHistory
{
    internal const string RootStream = "root_positions.f32";
    internal const string RotationStream = "local_rotations_xyzw.f32";
    internal const string JointStream = "joint_positions.f32";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal string DirectoryPath { get; }
    internal KimodoGenerationRecord Record { get; private set; }
    private KimodoGenerationHistory(string directory, KimodoGenerationRecord record)
    { DirectoryPath = directory; Record = record; }

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string GeometrySha(IReadOnlyList<FigureMesh> feet) => Sha(JsonSerializer.SerializeToUtf8Bytes(
        feet.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new
        {
            m.Name, Positions = m.Positions.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), m.SourceVertices
        }).ToArray(), Json));
    private static string FileSha(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    private static string PathSha(string path)
    {
        if (File.Exists(path)) return FileSha(path);
        if (!System.IO.Directory.Exists(path)) throw new FileNotFoundException("Файл или папка модели Kimodo не найдены.", path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .OrderBy(p => Path.GetRelativePath(path, p), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n" + FileSha(file) + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static KimodoGenerationHistory Begin(string directory, KimodoGenerationRecord input)
    {
        if (System.IO.Directory.Exists(directory)) throw new IOException("Папка истории Kimodo уже существует: " + directory);
        var record = input with
        {
            Status = "prepared", Error = null,
            GeneratorSha256 = PathSha(input.GeneratorPath),
            MotionWeightsSha256 = PathSha(input.MotionWeightsPath),
            TextWeightsSha256 = PathSha(input.TextWeightsPath),
            ConstraintsSha256 = input.KinematicConstraintsJson.Length == 0 ? null : Sha(Encoding.UTF8.GetBytes(input.KinematicConstraintsJson)),
            ResolvedTargetsSha256 = input.ResolvedSpatialTargets.Length == 0 ? null : Sha(Encoding.UTF8.GetBytes(input.ResolvedSpatialTargets))
        };
        System.IO.Directory.CreateDirectory(directory);
        var history = new KimodoGenerationHistory(directory, record);
        history.WriteRecord(record);
        File.WriteAllText(Path.Combine(directory, "prompt.txt"), record.Prompt, new UTF8Encoding(false));
        if (record.KinematicConstraintsJson.Length > 0)
        {
            File.WriteAllText(Path.Combine(directory, "constraints.json"), record.KinematicConstraintsJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "spatial-targets.txt"), record.ResolvedSpatialTargets, new UTF8Encoding(false));
        }
        return history;
    }

    private void WriteRecord(KimodoGenerationRecord record)
    {
        string destination = Path.Combine(DirectoryPath, "generation.json");
        string temporary = destination + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json), new UTF8Encoding(false));
            File.Move(temporary, destination, true);
            Record = record;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void Mark(string status, string? reason = null, byte[]? clip = null,
        IReadOnlyList<AnimationIssue>? issues = null)
    {
        string? clipHash = Record.ClipSha256;
        if (clip is not null)
        {
            string temporary = Path.Combine(DirectoryPath, "clip.res.tmp");
            File.WriteAllBytes(temporary, clip);
            File.Move(temporary, Path.Combine(DirectoryPath, "clip.res"), true);
            clipHash = Sha(clip);
        }
        WriteRecord(Record with { Status = status, Error = reason, ClipSha256 = clipHash,
            Issues = issues?.Select(i => i.ToString()).ToArray() ?? Record.Issues });
    }

    internal void CaptureRaw(string output)
    {
        foreach (string file in new[] { RootStream, RotationStream })
        {
            string source = Path.Combine(output, file);
            string target = Path.Combine(DirectoryPath, file);
            File.Copy(source, target, true);
            if (FileSha(source) != FileSha(target)) throw new IOException("Ошибка проверки сохранённого потока Kimodo: " + file);
        }
        if (File.Exists(Path.Combine(output, JointStream)))
        {
            File.Copy(Path.Combine(output, JointStream), Path.Combine(DirectoryPath, JointStream), true);
            if (FileSha(Path.Combine(output, JointStream)) != FileSha(Path.Combine(DirectoryPath, JointStream)))
                throw new IOException("Ошибка проверки суставов Kimodo.");
        }
        WriteRecord(Record with { Status = "generated", Error = null,
            RootStreamSha256 = FileSha(Path.Combine(DirectoryPath, RootStream)),
            RotationStreamSha256 = FileSha(Path.Combine(DirectoryPath, RotationStream)),
            JointPositionsSha256 = File.Exists(Path.Combine(DirectoryPath, JointStream))
                ? FileSha(Path.Combine(DirectoryPath, JointStream)) : null });
    }

    internal static KimodoGenerationHistory Open(string directory)
    {
        var record = JsonSerializer.Deserialize<KimodoGenerationRecord>(File.ReadAllBytes(Path.Combine(directory, "generation.json")))
            ?? throw new InvalidDataException("История Kimodo не содержит параметров генерации.");
        if (record.Version != 2 || record.Frames is < 2 or > 1000 || record.Steps is < 1 or > 200 ||
            string.IsNullOrWhiteSpace(record.Model) || string.IsNullOrWhiteSpace(record.Id))
            throw new InvalidDataException("Неподдерживаемая или неполная история Kimodo.");
        if (record.KinematicConstraintsJson.Length > 0 &&
            (record.ConstraintsSha256 != Sha(Encoding.UTF8.GetBytes(record.KinematicConstraintsJson)) ||
             record.ResolvedTargetsSha256 != Sha(Encoding.UTF8.GetBytes(record.ResolvedSpatialTargets)) ||
             !File.Exists(Path.Combine(directory, "constraints.json")) ||
             !File.Exists(Path.Combine(directory, "spatial-targets.txt")) ||
             FileSha(Path.Combine(directory, "constraints.json")) != record.ConstraintsSha256 ||
             FileSha(Path.Combine(directory, "spatial-targets.txt")) != record.ResolvedTargetsSha256))
            throw new InvalidDataException("JSON или разрешённые цели Kimodo отсутствуют либо изменены.");
        return new KimodoGenerationHistory(directory, record);
    }

    internal void VerifySource(string model, FigureAnimationEditor editor)
    {
        if (!Record.Model.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            !Record.SkeletonSha256.Equals(FigureAnimationEditor.SkeletonFingerprint(editor.Preview()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сохранённое движение относится к другой модели или несовместимому скелету.");
        if (!Record.SourceAnmSha256.Equals(Sha(editor.ExportBytes()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Исходный ANM изменился после генерации. Откройте исходную редакцию для повторного импорта; текущий файл не будет перезаписан.");
    }

    internal KimodoDraft ReadRaw()
    {
        if (Record.RootStreamSha256 is null || Record.RotationStreamSha256 is null ||
            !FileSha(Path.Combine(DirectoryPath, RootStream)).Equals(Record.RootStreamSha256, StringComparison.OrdinalIgnoreCase) ||
            !FileSha(Path.Combine(DirectoryPath, RotationStream)).Equals(Record.RotationStreamSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Исходные потоки Kimodo отсутствуют или изменены.");
        if (Record.JointPositionsSha256 is not null &&
            (!File.Exists(Path.Combine(DirectoryPath, JointStream)) ||
             FileSha(Path.Combine(DirectoryPath, JointStream)) != Record.JointPositionsSha256))
            throw new InvalidDataException("Сырые положения суставов Kimodo изменены.");
        return KimodoDraft.ReadSoma30(DirectoryPath, Record.Frames);
    }

    internal async Task<IReadOnlyList<AnimationIssue>> ImportAsync(FigureAnimationEditor created,
        IReadOnlyList<FigureMesh> meshes)
    {
        try
        {
            await created.ValidateKimodoTargetAsync(Record.Id, Record.Frames);
            var feet = created.KimodoFootGeometry(meshes);
            if (!Record.FootGeometrySha256.Equals(GeometrySha(feet), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Геометрия стоп отличается от использованной при генерации. Откройте исходную модель и экипировку.");
            KimodoDraft draft = ReadRaw();
            var working = new FigureAnimationEditor(created.Preview())
            { MotionPins = created.MotionPins, Contacts = created.Contacts };
            working.LockedParts.UnionWith(created.LockedParts);
            await working.ApplyKimodoGroundedDraftAsync(Record.Id, draft, meshes, Record.Playback);
            if (Record.ResolvedSpatialTargets.Length > 0)
                await working.AlignKimodoSpatialTargetsAsync(Record.Id, Record.ResolvedSpatialTargets);
            if (!string.IsNullOrWhiteSpace(Record.EndpointReference))
                await working.AnchorGeneratedEndpointsAsync(Record.Id, Record.EndpointReference);
            var issues = await working.DiagnoseAsync(Record.Id, Record.Playback == KimodoPlayback.StationaryLoop);
            Mark("imported", clip: working.ExportClipTracks(Record.Id), issues: issues);
            created.AdoptSavedBytes(working.ExportBytes());
            return issues;
        }
        catch (Exception ex)
        {
            try { Mark("import-failed", ex.Message); }
            catch (Exception markError)
            {
                try { File.WriteAllText(Path.Combine(DirectoryPath, "import-failure.txt"),
                    "Причина импорта: " + ex + Environment.NewLine + "Ошибка записи истории: " + markError,
                    new UTF8Encoding(false)); }
                catch (IOException) { /* The UI still reports the retained history directory. */ }
                throw new IOException("Импорт не удался; исходные потоки находятся в " + DirectoryPath +
                    ". Не удалось обновить журнал: " + markError.Message, ex);
            }
            throw;
        }
    }
}
