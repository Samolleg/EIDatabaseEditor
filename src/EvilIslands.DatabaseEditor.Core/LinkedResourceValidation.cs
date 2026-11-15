using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record ResourceValidationProgress(int RecordsChecked, int RecordsTotal, int ResourcesChecked,
    int Skipped, int Errors, string State, int OptionalResources = 0, int OptionalFindings = 0)
{
    public string Description => $"Ресурсы: {State}; записей {RecordsChecked}/{RecordsTotal}, уникальных связанных ресурсов {ResourcesChecked}, пропущено проверок/вариантов {Skipped}, ошибок {Errors}. Примеры предпросмотра: ресурсов {OptionalResources}, информационных замечаний {OptionalFindings}.";
}

/// <summary>A fresh, bounded, sequential scan. Never retained between F5 passes.</summary>
public static class LinkedResourceValidation
{
    public const string Scope = "Проверяются модели, выбранная и запасная экипировка прототипов по отдельным ссылкам, явные текстуры рас, инвентарные модели с явно указанными материалами. Без ссылки материала/содержимого — информационная проверка начального примера предпросмотра; её замечания не считаются ошибками связанных ресурсов. " +
        "Прочие варианты выбора, нераспознанные элементы с вероятностями, внешность MOB/сохранений, ANM/ADB, эффекты и несвязанные ресурсы не проверяются. " +
        "Декодирование и известный контракт FIG не подтверждают визуальное совпадение с игрой.";

    public static async Task<IReadOnlyList<ValidationDiagnostic>> ScanAsync(string database,
        IReadOnlyList<(ModelRecord Record, DiagnosticLocation Location)> linked,
        CancellationToken token, IProgress<ResourceValidationProgress>? progress = null)
    {
        var records = linked.Select(p => p.Record).ToArray();
        var explicitValues = records.SelectMany(r => r.Fields.Values.Concat(r.Lists.Values.SelectMany(v => v)))
            .Select(v => StringListEditorCodec.AnalyzeElement(v, "Предмет").PrimaryValue).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = linked.Where(p => p.Record.Table == "units.prototypes" ||
            p.Record.Table == "units.races" || ItemModelPreview.Supports(p.Record.Table))
            .OrderBy(p => p.Record.Table, StringComparer.Ordinal)
            .ThenBy(p => p.Record.Table == "units.prototypes" ? p.Record.Text(1) : p.Record.Integer(5).ToString("D8"), StringComparer.Ordinal)
            .ToArray();
        var sources = await TextureSources.FromDatabaseFolderAsync(database, token).ConfigureAwait(false);
        using var session = await ResourceValidationSession.OpenAsync(database, sources, token).ConfigureAwait(false);
        int done = 0, variants = 0;
        LuaDataWorkspace? lua = null;
        try { lua = LuaDataWorkspace.FindNearby(database) is { } folder ? LuaDataWorkspace.Open(folder) : null; }
        catch (Exception ex) when (ResourceValidationSession.IsResourceFailure(ex)) { session.Skip("Lua", "Навыки экипировки не проверены: " + ex.Message); }
        void Report(string state) => progress?.Report(new(done, targets.Length, session.Checked.Count,
            session.Skipped.Count, session.Issues.Values.Count(p => p.Diagnostic.Severity == DiagnosticSeverity.Error), state,
            session.OptionalChecked.Count, session.Issues.Values.Count(p => p.Diagnostic.Code == "RESOURCE_PREVIEW_OPTIONAL")));
        Report("проверка");
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            session.Location = target.Location;
            session.OptionalPreview = false;
            session.Path = target.Record.Name;
            session.Context = target.Record.Table == "units.races" ? "Текстуры расы: " + target.Record.Name : "";
            try
            {
                if (target.Record.Table == "units.races")
                {
                    foreach (string raw in target.Record.Lists.GetValueOrDefault(31, []).Concat(target.Record.Lists.GetValueOrDefault(32, [])))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string stem = raw.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
                        string name = new[] { ".x4.mmp", ".x2.mmp", ".mmp" }.Select(s => stem + s)
                            .FirstOrDefault(n => session.Catalog.Check(n).Matches.Count > 0) ?? stem + ".mmp";
                        await session.TextureAsync(name, token).ConfigureAwait(false); variants++;
                    }
                }
                else if (target.Record.Table == "units.prototypes")
                {
                    await UnitModelPreview.ValidateResourcesAsync(database, target.Record, records, session, token, lua).ConfigureAwait(false);
                    variants++;
                    // Independent recorded alternatives, never a Cartesian product of equipment.
                    var noWeapons = target.Record.Fields.ToDictionary(p => p.Key, p => p.Value);
                    noWeapons[33] = ""; noWeapons[42] = "";
                    foreach (string armor in target.Record.Lists.GetValueOrDefault(32, []).Distinct())
                    {
                        var lists = target.Record.Lists.ToDictionary(p => p.Key, p => p.Value); lists[32] = [armor];
                        try { await UnitModelPreview.ValidateResourcesAsync(database, target.Record with { Fields = noWeapons, Lists = lists }, records, session, token, lua).ConfigureAwait(false); }
                        catch (Exception ex) when (ResourceValidationSession.IsResourceFailure(ex)) { session.Failure(ex); }
                        variants++;
                    }
                    foreach (int field in new[] { 33, 42 })
                    {
                        if (string.IsNullOrWhiteSpace(target.Record.Text(field))) continue;
                        var fields = noWeapons.ToDictionary(p => p.Key, p => p.Value); fields[33] = target.Record.Text(field);
                        var lists = target.Record.Lists.ToDictionary(p => p.Key, p => p.Value); lists[32] = [];
                        try { await UnitModelPreview.ValidateResourcesAsync(database, target.Record with { Fields = fields, Lists = lists }, records, session, token, lua).ConfigureAwait(false); }
                        catch (Exception ex) when (ResourceValidationSession.IsResourceFailure(ex)) { session.Failure(ex); }
                        variants++;
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(target.Record.Name)) session.Skip("empty:" + target.Location.TableKey + target.Location.RecordIndex, "Пустая служебная запись без модели.");
                    else
                    {
                        var options = ItemModelPreview.Options(target.Record, records);
                        ModelRecord?[] choices;
                        if (options.Label.Length == 0) choices = [null];
                        else
                        {
                            var explicitlyLinked = options.Label == "Материал" ? options.Choices.Where(m => explicitValues.Any(v =>
                                v.StartsWith(target.Record.Name + "." + m.Name, StringComparison.OrdinalIgnoreCase) &&
                                (v.Length == target.Record.Name.Length + m.Name.Length + 1 ||
                                    !char.IsLetterOrDigit(v[target.Record.Name.Length + m.Name.Length + 1]) && v[target.Record.Name.Length + m.Name.Length + 1] != '_'))).ToArray() : [];
                            choices = explicitlyLinked.Length > 0 ? explicitlyLinked.Cast<ModelRecord?>().ToArray() :
                                options.Choices.Count > 0 ? [ItemModelPreview.DefaultChoice(target.Record, records, session.Catalog, token)] : [];
                            session.OptionalPreview = explicitlyLinked.Length == 0;
                            if (options.Choices.Count > choices.Length)
                                session.Skip("inventory-variants:" + target.Record.Table + ":" + target.Record.Name,
                                    "Проверены инвентарные варианты: " + string.Join(", ", choices.Select(c => c?.Name ?? "без материала")) +
                                    $". Прочие {options.Choices.Count - choices.Length} вариантов выбора не проверены: явная ссылка на них не найдена.");
                        }
                        if (choices.Length == 0) session.Skip("choices:" + target.Record.Table + target.Record.Name, "Нет поддержанного варианта предмета: " + options.Label);
                        foreach (var choice in choices)
                        {
                            token.ThrowIfCancellationRequested();
                            try { await ItemModelPreview.ValidateResourcesAsync(database, target.Record, records, session, token, choice).ConfigureAwait(false); }
                            catch (Exception ex) when (ResourceValidationSession.IsResourceFailure(ex)) { session.Failure(ex); }
                            variants++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ResourceValidationSession.IsResourceFailure(ex)) { session.Failure(ex); }
            done++; Report("проверка");
        }
        token.ThrowIfCancellationRequested();
        if (!session.IsCurrent()) throw new IOException("Источники ресурсов изменились во время проверки. Результат не опубликован; повторите F5.");
        var diagnostics = session.Issues.Values.Select(p => p.Diagnostic with
        {
            Location = p.Locations.FirstOrDefault(),
            Message = p.Diagnostic.Message + " " + string.Join("; ", p.Contexts) + " Связанные записи (" + p.Locations.Count + "): " +
                string.Join("; ", p.Locations.Select(l => l.TableKey + "[" + (l.RecordIndex + 1) + "] " + l.RecordName))
        }).ToList();
        diagnostics.Add(new(DiagnosticSeverity.Information, "RESOURCE_SCAN_COVERAGE",
            new ResourceValidationProgress(done, targets.Length, session.Checked.Count, session.Skipped.Count,
                diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), "завершена", session.OptionalChecked.Count,
                diagnostics.Count(d => d.Code == "RESOURCE_PREVIEW_OPTIONAL")).Description +
                $" Фактически обработано вариантов/явных ссылок: {variants}. " + Scope + " " + string.Join(" ", sources.Notes),
            database, Domain: DiagnosticDomain.Resources));
        Report("завершена");
        return diagnostics;
    }
}

internal sealed class ResourceValidationSession : IDisposable
{
    internal sealed record Issue(ValidationDiagnostic Diagnostic, HashSet<DiagnosticLocation> Locations, HashSet<string> Contexts);
    public TextureSources Sources { get; }
    public TextureResourceCatalog Catalog { get; }
    public List<(string Path, IReadOnlyList<ResEntry> Entries, long Length, long Written)> Indexes { get; } = [];
    public Dictionary<string, Issue> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Checked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> OptionalChecked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DiagnosticLocation? Location { get; set; }
    public string Path { get; set; } = "";
    public string Context { get; set; } = "";
    public bool OptionalPreview { get; set; }
    private void MarkChecked(string path)
    {
        if (OptionalPreview) { if (!Checked.Contains(path)) OptionalChecked.Add(path); }
        else { Checked.Add(path); OptionalChecked.Remove(path); }
    }
    private readonly Dictionary<string, FigureMesh> _figures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _figureFailures = new(StringComparer.OrdinalIgnoreCase);
    private long _uvBytes;
    private readonly PakArchiveSet _pakFiles = new();
    public void Dispose() => _pakFiles.Dispose();
    private readonly Dictionary<string, ResArchiveDocument> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ValidationDiagnostic?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long?> _archiveStamps = new(StringComparer.OrdinalIgnoreCase);
    private ResourceValidationSession(TextureSources sources, TextureResourceCatalog catalog) { Sources = sources; Catalog = catalog; }
    public static async Task<ResourceValidationSession> OpenAsync(string database, TextureSources sources, CancellationToken token)
    {
        var session = new ResourceValidationSession(sources, await TextureResourceCatalog.OpenAsync(sources, token).ConfigureAwait(false));
        foreach (string path in sources.Archives)
            session._archiveStamps[path] = File.Exists(path) ? ResourceFileStamp.Read(path) : null;
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in UnitModelPreview.FigureArchives(database, sources))
        {
            token.ThrowIfCancellationRequested();
            session._archiveStamps[path] = File.Exists(path) ? ResourceFileStamp.Read(path) : null;
            if (!File.Exists(path)) continue;
            try
            {
                var file = new FileInfo(path); long length = file.Length, written = ResourceFileStamp.Read(path);
                if (PakArchive.IsPak(path)) foreach (string name in session._pakFiles.Get(path, token).Deleted) claimed.Add(PakArchive.LookupKey(name));
                IReadOnlyList<ResEntry> entries = (PakArchive.IsPak(path) ? session._pakFiles.Get(path, token).Entries : ResFileManagerArchive.ReadCatalog(path, token)).Where(e => claimed.Add(PakArchive.LookupKey(e.Name))).ToArray();
                file.Refresh();
                if (file.Length != length || ResourceFileStamp.Read(path) != written) throw new IOException("Архив изменился при индексировании.");
                session.Indexes.Add((path, entries, length, written));
            }
            catch (Exception ex) when (IsResourceFailure(ex)) { session.Path = path; session.Failure(ex); }
        }
        foreach (string note in session.Catalog.Problems) session.Add(new(DiagnosticSeverity.Warning, "RESOURCE_SOURCE_UNCHECKED", note,
            Domain: DiagnosticDomain.Resources));
        return session;
    }
    public bool IsCurrent() => Catalog.IsCurrent() && _archiveStamps.All(p =>
        (File.Exists(p.Key) ? ResourceFileStamp.Read(p.Key) : (long?)null) == p.Value);
    public string FigurePath(string name) => name + " — " + (Indexes.FirstOrDefault(a => a.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).Path
        ?? "поиск: " + string.Join("; ", _archiveStamps.Keys));
    public bool Contains(string name) => ResourceArchivePaths.FindLoose(Sources.ConfigurationPath,name) is not null||Indexes.Any(a => a.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    public byte[] Read(string name, CancellationToken token)
    {
        Path = FigurePath(name); token.ThrowIfCancellationRequested();
        if(ResourceArchivePaths.FindLoose(Sources.ConfigurationPath,name) is { } loose){Path=name+" — "+loose;_archiveStamps[loose]=ResourceFileStamp.Read(loose);return File.ReadAllBytes(loose);}
        foreach (var archive in Indexes)
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            using var stream = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != archive.Length || ResourceFileStamp.Read(archive.Path) != archive.Written) throw new IOException("Архив изменился во время проверки.");
            if (PakArchive.IsPak(archive.Path)) return _pakFiles.Get(archive.Path, token).Read(entry.Name, token);
            stream.Position = entry.Offset; var bytes = new byte[entry.Size]; stream.ReadExactly(bytes); return bytes;
        }
        throw new FileNotFoundException("Не найден ресурс модели: " + name);
    }
    public ResArchiveDocument Parse(string name, Func<byte[]> read, CancellationToken token)
    {
        Path = FigurePath(name);
        if (!_models.TryGetValue(name, out var model))
        {
            // One model pair at a time; never retain the complete figure library.
            if (_models.Count >= 2) { _models.Clear(); _figures.Clear(); _figureFailures.Clear(); _uvBytes = 0; }
            model = ResArchiveDocument.Parse(name, read(), token); _models[name] = model;
        }
        MarkChecked(Path);
        return model;
    }
    public FigureMesh Decode(string part, Func<byte[]> read, Vector3 morph, string path)
    {
        Path = path;
        if (_figures.TryGetValue(path, out var cached)) { MarkChecked(path); return cached; }
        if (_figureFailures.TryGetValue(path, out var failure)) throw failure;
        FigureMesh mesh;
        try { mesh = FigurePreviewDecoder.Decode(part, read(), morph); }
        catch (Exception ex) when (IsResourceFailure(ex)) { Path = path; _figureFailures[path] = ex; throw; }
        // Validation needs only UV, raw ID and presence, not vertex buffers/transforms.
        cached = new(part, [], mesh.UV, mesh.Indices.Length == 0 ? [] : [0], mesh.TextureSlot);
        if (_figures.Count >= 2048 || _uvBytes + mesh.UV.LongLength * 8 > 16 * 1024 * 1024)
        { _figures.Clear(); _uvBytes = 0; }
        _uvBytes += mesh.UV.LongLength * 8;
        _figures[path] = cached; MarkChecked(path); return cached;
    }
    public async Task TextureAsync(string name, CancellationToken token)
    {
        var check = Catalog.Check(name); Path = name + " — " + (check.Matches.FirstOrDefault()?.Source ?? "поиск: " + string.Join("; ", Sources.Archives.Concat(Sources.LooseFolders)));
        if (!_textures.TryGetValue(Path, out var diagnostic))
        {
            diagnostic = null;
            try { await Catalog.ReadPreviewAsync(name, token).ConfigureAwait(false); }
            catch (Exception ex) when (IsResourceFailure(ex))
            {
                diagnostic = Describe(ex, Path, check.Matches.Count == 0 && !Catalog.Complete);
            }
            _textures[Path] = diagnostic;
        }
        if (diagnostic is null) MarkChecked(Path);
        if (diagnostic is not null) Add(diagnostic);
    }
    public void Add(ValidationDiagnostic diagnostic)
    {
        if (OptionalPreview && diagnostic.Code != "RESOURCE_VARIANT_UNCHECKED") diagnostic = diagnostic with
        { Severity = DiagnosticSeverity.Information, Code = "RESOURCE_PREVIEW_OPTIONAL", Message = "Пример предпросмотра, без подтверждённой ссылки материала/содержимого: " + diagnostic.Message };
        string key = diagnostic.Code + "|" + diagnostic.Path + "|" + diagnostic.Message;
        if (!Issues.TryGetValue(key, out var issue)) Issues[key] = issue = new(diagnostic, [], []);
        if (Location is not null) issue.Locations.Add(Location);
        if (Context.Length > 0) issue.Contexts.Add(Context);
        if (diagnostic.Code.EndsWith("UNCHECKED", StringComparison.Ordinal)) Skipped.Add(key);
    }
    public void Skip(string key, string message)
    {
        Add(new(DiagnosticSeverity.Warning, "RESOURCE_VARIANT_UNCHECKED", message, key, Domain: DiagnosticDomain.Resources));
    }
    public void Failure(Exception ex) => Add(Describe(ex, Path, false));
    private static ValidationDiagnostic Describe(Exception ex, string path, bool incomplete) =>
        new(ex is NotSupportedException || incomplete ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            ex is NotSupportedException || incomplete ? "RESOURCE_LAYOUT_UNCHECKED" : ex is FileNotFoundException ? "RESOURCE_MISSING" :
            ex is InvalidDataException or DatabaseFormatException or ArgumentException ? "RESOURCE_DECODE_FAILED" : "RESOURCE_READ_FAILED",
            (ex is NotSupportedException || incomplete ? "Не проверено: " : ex is FileNotFoundException ? "Не найден ресурс: " : "Не удалось прочитать/декодировать ресурс: ") + ex.Message,
            path, Domain: DiagnosticDomain.Resources);
    public static bool IsResourceFailure(Exception ex) => ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or UnauthorizedAccessException;
}
