using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using EvilIslands.DatabaseEditor.Core;
using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.App.ViewModels;

internal sealed record TextureRow(string TableKey, int RecordIndex, string RecordName, string Purpose,
    string ResourceName, TextureCheckState State, string Archive, string Explanation, string Versions)
{
    public TextureCheckState FileState { get; init; }
    public bool HasMatch { get; init; }
    public bool NeedsContext { get; init; }
    public string DisplayName => ResourceName.Length == 0 ? Purpose : ResourceName;
    public string PresenceText => FileState switch
    {
        TextureCheckState.Found => "Файл найден",
        TextureCheckState.Missing => NeedsContext ? "Кандидат не найден" : "Файл отсутствует",
        TextureCheckState.Corrupt => "Файл повреждён",
        TextureCheckState.Incomplete => HasMatch ? "Есть · набор неполный" : "Проверка неполная",
        _ => "Имя не определено",
    };
    public string PresenceColor => FileState switch
    {
        TextureCheckState.Found => "#78D69B",
        TextureCheckState.Missing when !NeedsContext => "#FF7B86",
        TextureCheckState.Corrupt => "#FF7B86",
        _ => "#F2C96D",
    };
    public string ApplicabilityText => NeedsContext ? "Применимость требует уточнения" : "Связь определена по правилу БД";
    public string SourceText => Archive.Length == 0 ? "Источник не найден или не подключён." : Archive;
    public string VersionsText => Versions.Length == 0 ? "Других сведений об источниках нет." : Versions;
    public string StateText => State switch
    {
        TextureCheckState.Found => "Найдена",
        TextureCheckState.Missing => "Не найдена",
        TextureCheckState.Corrupt => "Повреждена",
        TextureCheckState.Context => "Требуется контекст",
        _ => "Проверка неполная",
    };
    public string ArchiveName => Path.GetFileName(Archive);
}

internal sealed class TextureRowCollection : ObservableCollection<TextureRow>
{
    public void ReplaceWith(IEnumerable<TextureRow> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

internal sealed partial class MainWindowViewModel
{
    private TextureResourceCatalog? _textureCatalog;
    private TextureSources? _textureSources;
    private CancellationTokenSource? _textureLoadCancellation;
    private int _textureLoadRevision;
    private bool _isLoadingTextures;
    public bool IsLoadingTextures { get => _isLoadingTextures; private set { if (SetProperty(ref _isLoadingTextures, value)) OnPropertyChanged(nameof(AttachTexturesButtonText)); } }
    internal Task TextureLoadTask { get; private set; } = Task.CompletedTask;
    internal TextureSources? SpellPreviewTextureSources => _textureSources;
    private int _textureRevision;
    private CancellationTokenSource? _textureRefresh;
    private string _textureSummary = "Подключите ресурсы игры или отдельные RES.";
    private bool _textureShowAll;
    private string? _textureDocument;
    private string _textureSearch = "";
    private int _textureFilterIndex;
    private ICollectionView? _textureView;
    private TextureRow? _selectedTexture;
    public TextureRowCollection TextureRows { get; } = [];
    public ICollectionView TextureView => _textureView ??= CreateTextureView();
    private ICollectionView CreateTextureView()
    {
        var view = CollectionViewSource.GetDefaultView(TextureRows);
        view.Filter = item => item is TextureRow row &&
            (_textureFilterIndex switch
            {
                1 => row.FileState == TextureCheckState.Found,
                2 => row.FileState is TextureCheckState.Missing or TextureCheckState.Corrupt,
                3 => row.NeedsContext || row.FileState is TextureCheckState.Incomplete or TextureCheckState.Context,
                _ => true,
            }) && (_textureSearch.Length == 0 ||
                (row.ResourceName + " " + row.Purpose + " " + row.RecordName + " " + row.Archive)
                    .Contains(_textureSearch, StringComparison.OrdinalIgnoreCase));
        return view;
    }
    public string TextureSearch
    {
        get => _textureSearch;
        set { if (SetProperty(ref _textureSearch, value)) RefreshTextureView(); }
    }
    public int TextureFilterIndex
    {
        get => _textureFilterIndex;
        set { if (SetProperty(ref _textureFilterIndex, value)) RefreshTextureView(); }
    }
    public TextureRow? SelectedTexture
    {
        get => _selectedTexture;
        set { if (SetProperty(ref _selectedTexture, value)) _ = RefreshTexturePreviewAsync(); }
    }
    public string TextureVisibleSummary => $"Показано: {TextureView.Cast<TextureRow>().Count():N0} из {TextureRows.Count:N0}";
    private void RefreshTextureView()
    {
        TextureView.Refresh();
        if (SelectedTexture is null || !TextureView.Contains(SelectedTexture))
            SelectedTexture = TextureView.Cast<TextureRow>().FirstOrDefault();
        OnPropertyChanged(nameof(TextureVisibleSummary));
    }
    public string TextureSummary { get => _textureSummary; private set => SetProperty(ref _textureSummary, value); }
    public string TextureSourcesDescription => _textureSources is null ? "Источники не подключены." :
        string.Join(Environment.NewLine, (_textureSources.ConfigurationPath is { } config ? new[] { "Профиль мода: " + config } : Array.Empty<string>()).Concat(_textureSources.Archives).Concat(_textureSources.LooseFolders)
            .Concat(_textureSources.Notes).Concat(_textureCatalog?.Problems ?? []));
    public bool TextureShowAll
    {
        get => _textureShowAll;
        set { if (SetProperty(ref _textureShowAll, value)) QueueTextureRefresh(); }
    }
    public string? TextureGameFolder { get; private set; }
    public string AttachTexturesButtonText => IsLoadingTextures ? "▧  Поиск текстур в фоне… ▾" : _textureCatalog is null || _textureCatalog.ResourceCount == 0
        ? "▧  Подключить текстуры… ▾" : $"▧  Текстуры · {_textureCatalog.ResourceCount:N0} файлов ▾";

    private void NotifyTextureSources()
    {
        OnPropertyChanged(nameof(TextureSourcesDescription));
        OnPropertyChanged(nameof(AttachTexturesButtonText));
    }

    private void CancelTextureLoad()
    {
        if (IsLoadingTextures) TextureSummary = "Поиск текстур отменён.";
        _textureLoadRevision++;
        _textureLoadCancellation?.Cancel();
        _textureLoadCancellation?.Dispose();
        _textureLoadCancellation = null;
        IsLoadingTextures = false;
        _textureRefresh?.Cancel();
        _textureRevision++;
    }

    private Task StartTextureLoad(Func<CancellationToken, Task<TextureSources>> findSources)
    {
        CancelTextureLoad();
        if (_disposed || !HasDocument) return Task.CompletedTask;
        var cancellation = _textureLoadCancellation = new CancellationTokenSource();
        int revision = _textureLoadRevision;
        string document = SourcePath;
        _textureCatalog = null;
        _textureSources = null;
        _textureDocument = document;
        TextureGameFolder = null;
        SelectedTexture = null;
        IsLoadingTextures = true;
        TextureSummary = "Поиск текстур выполняется в фоне. Таблицы доступны для работы.";
        NotifyTextureSources();
        return TextureLoadTask = LoadTextureSourcesAsync(findSources, document, revision, cancellation.Token);
    }

    private async Task LoadTextureSourcesAsync(Func<CancellationToken, Task<TextureSources>> findSources, string document, int revision, CancellationToken token)
    {
        bool Current() => !_disposed && !token.IsCancellationRequested && revision == _textureLoadRevision && SourcePath == document;
        // Return control to OpenPathAsync before any optional resource work.
        await Task.Yield();
        try
        {
            var sources = await findSources(token);
            if (!Current()) return;
            _textureSources = sources;
            TextureGameFolder = sources.GameFolder;
            NotifyTextureSources();
            var catalog = await TextureResourceCatalog.OpenCachedAsync(sources, token);
            if (!Current()) return;
            _textureCatalog = catalog;
            await RecheckTexturesAsync(token);
            if (Current() && catalog.Problems.Count > 0) TextureSummary += $" · Не удалось прочитать источников: {catalog.Problems.Count}. Подробности — в списке источников.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) TextureSummary = "Не удалось подключить текстуры: " + ex.Message;
        }
        finally
        {
            if (Current()) { IsLoadingTextures = false; NotifyTextureSources(); }
        }
    }

    private Task StartNearbyTextures()
    {
        string path = SourcePath;
        return StartTextureLoad(token => TextureSources.FromDatabaseFolderAsync(path, token));
    }

    public Task ConnectNearbyTexturesAsync() => !HasDocument || IsBusy ? Task.CompletedTask : StartNearbyTextures();

    public Task ConnectTextureGameAsync(string gameFolder, string? configPath = null)
    {
        TextureSources sources = TextureSources.FromGame(gameFolder, configPath);
        TextureGameFolder = gameFolder;
        return ConnectTextureSourcesAsync(sources);
    }

    public Task ConnectTextureArchivesAsync(IReadOnlyList<string> paths) =>
        ConnectTextureSourcesAsync(TextureSources.Manual(
            (_textureSources?.Archives ?? []).Concat(paths)));

    public Task ConnectTextureSourcesAsync(TextureSources sources) => IsBusy ? Task.CompletedTask : StartTextureLoad(_ => Task.FromResult(sources));

    public Task ReloadTexturesAsync() => _textureSources is null || _textureSources.NearbyDatabasePath is not null ?
        ConnectNearbyTexturesAsync() : _textureSources.GameFolder is { } game ?
        ConnectTextureGameAsync(game, _textureSources.ConfigurationPath) : ConnectTextureSourcesAsync(_textureSources);

    private async void QueueTextureRefresh()
    {
        if (_disposed) return;
        _textureRefresh?.Cancel();
        _textureRefresh?.Dispose();
        var cancellation = _textureRefresh = new CancellationTokenSource();
        _textureRevision++;
        try
        {
            await Task.Delay(180, cancellation.Token);
            await RecheckTexturesAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TextureSummary = "Проверка текстур не завершена: " + ex.Message; }
    }

    public async Task RecheckTexturesAsync(CancellationToken token = default)
    {
        if (_disposed) return;
        if (!token.CanBeCanceled) _textureRefresh?.Cancel();
        int revision = ++_textureRevision;
        if (!HasDocument) { TextureRows.Clear(); SelectedTexture = null; RefreshTextureView(); return; }
        if (_textureDocument is not null && !string.Equals(_textureDocument, SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            _textureCatalog = null;
            _textureSources = null;
            _textureDocument = null;
            NotifyTextureSources();
        }
        var rows = Databases.SelectMany(group => group.Tables)
            .SelectMany(table => table.Records.Select(record => (table.Key, Record: record))).ToArray();
        TextureRecord[] captured = rows.Select(row => TextureRecord.Capture(row.Key, row.Record.CoreRecord)).ToArray();
        string? selectedTable = SelectedRecord?.TableKey;
        int? selectedIndex = SelectedRecord?.SourceRecordIndex;
        bool all = TextureShowAll;
        var catalog = _textureCatalog;
        TextureSummary = "Проверка связей…";
        var checkedRows = await Task.Run(() =>
        {
            bool current = catalog?.IsCurrent() ?? true;
            var results = new List<TextureRow>();
            foreach (TextureRecord record in captured.Where(record => all || (record.Table == selectedTable && record.Index == selectedIndex)))
            {
                token.ThrowIfCancellationRequested();
                foreach (TextureReference reference in DatabaseTextureReferences.Resolve(record, captured, catalog?.Sources.ModProfile ?? false))
                {
                    TextureCheck check = reference.Name.Length == 0
                        ? new("", TextureCheckState.Context, "", reference.Detail, [])
                        : catalog?.Check(reference.Name, current) ??
                            new(reference.Name, TextureCheckState.Incomplete, "", "Подключите текстурные ресурсы.", []);
                    TextureCheckState state = reference.NeedsContext && check.State is not TextureCheckState.Incomplete and not TextureCheckState.Corrupt
                        ? TextureCheckState.Context : check.State;
                    string explanation = reference.Detail + " " + check.Explanation;
                    if (reference.NeedsContext && reference.Name.Length > 0)
                        explanation += check.Matches.Count > 0 ? " Кандидат найден." : " Кандидат не найден; обязательность не подтверждена.";
                    string versions = string.Join(Environment.NewLine, check.Matches.Select((match, i) =>
                        $"{i + 1}. {match.Source} — {match.Name}"));
                    results.Add(new(record.Table, record.Index, record.Name, reference.Purpose, reference.Name, state,
                        check.Source, explanation, versions)
                    { FileState = check.State, HasMatch = check.Matches.Count > 0, NeedsContext = reference.NeedsContext });
                }
            }
            return results;
        }, token);
        if (revision != _textureRevision || token.IsCancellationRequested) return;
        var previous = SelectedTexture;
        TextureRows.ReplaceWith(checkedRows);
        SelectedTexture = checkedRows.FirstOrDefault(row => row.TableKey == previous?.TableKey &&
            row.RecordIndex == previous.RecordIndex && row.ResourceName == previous.ResourceName && row.Purpose == previous.Purpose);
        RefreshTextureView();
        UpdateTextureSummary();
        _ = RefreshTexturePreviewAsync();
    }

    private void UpdateTextureSummary() => TextureSummary =
        $"Найдены: {TextureRows.Count(r => r.FileState == TextureCheckState.Found):N0}" +
        $" · не найдены: {TextureRows.Count(r => r.FileState == TextureCheckState.Missing):N0}" +
        $" · повреждены: {TextureRows.Count(r => r.FileState == TextureCheckState.Corrupt):N0}" +
        $" · не проверены: {TextureRows.Count(r => r.FileState is TextureCheckState.Incomplete or TextureCheckState.Context):N0}";

    public void NavigateToTexture(TextureRow? row)
    {
        if (row is null) return;
        var table = Databases.SelectMany(group => group.Tables).FirstOrDefault(table => table.Key == row.TableKey);
        if (table is null) return;
        SelectTableCommand.Execute(table);
        SelectedRecord = table.Records.FirstOrDefault(record => record.SourceRecordIndex == row.RecordIndex);
    }
}
