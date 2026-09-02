using System.Text.Json;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record DatabaseChangeProject(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    string CurrentFileName,
    string CurrentSha256,
    string ComparedFileName,
    string ComparedSha256,
    IReadOnlyList<DatabaseChangeProjectEntry> Changes)
{
    public const int CurrentFormatVersion = 1;

    public static DatabaseChangeProject Create(
        DatabaseComparisonResult comparison,
        IEnumerable<DatabaseDifference> selectedDifferences)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(selectedDifferences);

        DatabaseChangeProjectEntry[] changes = selectedDifferences
            .Where(difference => difference.CanApplyFromCompared)
            .DistinctBy(difference => (
                difference.TableKey,
                difference.CurrentRecordIndex,
                difference.FieldId,
                difference.FieldOccurrence))
            .Select(difference => new DatabaseChangeProjectEntry(
                difference.TableKey,
                difference.TableTitle,
                difference.RecordIdentity,
                difference.RecordName,
                difference.CurrentRecordIndex!.Value,
                difference.FieldId!.Value,
                difference.FieldOccurrence,
                difference.FieldName,
                difference.CurrentValue,
                difference.ComparedValue,
                Convert.ToBase64String(difference.CurrentPayload!),
                Convert.ToBase64String(difference.ComparedPayload!)))
            .ToArray();

        return new DatabaseChangeProject(
            CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            Path.GetFileName(comparison.CurrentPath),
            comparison.CurrentSha256,
            Path.GetFileName(comparison.ComparedPath),
            comparison.ComparedSha256,
            changes);
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(
                stream,
                this,
                DatabaseChangeProjectJsonContext.Default.DatabaseChangeProject,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DatabaseChangeProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        DatabaseChangeProject? project = await JsonSerializer.DeserializeAsync(
                stream,
                DatabaseChangeProjectJsonContext.Default.DatabaseChangeProject,
                cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException("Файл не является поддерживаемым планом переноса database.res.");
        }

        return project;
    }
}

public sealed record DatabaseChangeProjectEntry(
    string TableKey,
    string TableTitle,
    string RecordIdentity,
    string RecordName,
    int CurrentRecordIndex,
    int FieldId,
    int FieldOccurrence,
    string FieldName,
    string BeforeValue,
    string AfterValue,
    string BeforePayloadBase64,
    string AfterPayloadBase64)
{
    public byte[] GetBeforePayload() => Convert.FromBase64String(BeforePayloadBase64);

    public byte[] GetAfterPayload() => Convert.FromBase64String(AfterPayloadBase64);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(DatabaseChangeProject))]
internal partial class DatabaseChangeProjectJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
