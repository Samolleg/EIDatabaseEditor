namespace EvilIslands.DatabaseEditor.Core;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum DiagnosticDomain
{
    Structural,
    Semantic,
}

public sealed record DiagnosticLocation(
    string ResourceName,
    string TableKey,
    int RecordIndex,
    string RecordName,
    int? FieldId = null,
    string? FieldKey = null);

public sealed record ValidationDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null,
    long? Offset = null,
    DiagnosticDomain Domain = DiagnosticDomain.Structural,
    DiagnosticLocation? Location = null,
    bool? BlockingOverride = null)
{
    public bool IsBlocking => BlockingOverride ??
        (Domain == DiagnosticDomain.Structural && Severity == DiagnosticSeverity.Error);
}

public sealed class DatabaseFormatException : IOException
{
    public DatabaseFormatException(string message, string? path = null, long? offset = null)
        : base(message)
    {
        Path = path;
        Offset = offset;
    }

    public string? Path { get; }

    public long? Offset { get; }
}
