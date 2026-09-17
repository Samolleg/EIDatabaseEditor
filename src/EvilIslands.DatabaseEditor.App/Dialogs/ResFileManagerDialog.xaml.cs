using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.App.Infrastructure;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;
using Microsoft.Win32;

namespace EvilIslands.DatabaseEditor.App.Dialogs;

public partial class ResFileManagerDialog : Window
{
    private readonly HashSet<string> _writeLockedPaths;
    private readonly ObservableCollection<ResManagerFileRow> _rows = [];
    private readonly Dictionary<string, ResFileEdit> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Dictionary<string, ResFileEdit>> _undo = [];
    private readonly Stack<Dictionary<string, ResFileEdit>> _redo = [];
    private readonly ObservableCollection<ResManagerChangeRow> _changes = [];
    private readonly ICollectionView _filesView;
    private readonly EditorPreferencesStore _preferences;
    private readonly EditorLayoutState _layout;
    private ResFileManagerArchive? _archive;
    private string _archivePath;
    private string _selectedFolder = string.Empty;
    private bool _isBusy;
    private bool _allowClose;
    private bool _detailsVisible;
    private double _detailsWidth;
    internal string? InitialResourceName { get; init; }

    public ResFileManagerDialog(string archivePath, IEnumerable<string>? writeLockedPaths = null)
        : this(archivePath, writeLockedPaths, new EditorPreferencesStore())
    {
    }

    internal ResFileManagerDialog(
        string archivePath,
        IEnumerable<string>? writeLockedPaths,
        EditorPreferencesStore preferences)
    {
        InitializeComponent();
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _layout = _preferences.GetLayout();
        _archivePath = Path.GetFullPath(archivePath);
        _writeLockedPaths = new HashSet<string>(
            (writeLockedPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        _filesView = CollectionViewSource.GetDefaultView(_rows);
        _filesView.Filter = FilterRow;
        FilesGrid.ItemsSource = _filesView;
        _filesView.CollectionChanged += (_, _) => { _tilePage = 0; QueueTilesRefresh(); };
        Closed += (_, _) => _tileCancellation.Cancel();
        ChangesGrid.ItemsSource = _changes;
        ApplyStoredLayout();
        InitializeWindowBounds();
        Closed += (_, _) => CancelManagerPreview();
        Closed += (_, _) => StopResAudio();
        Closed += (_, _) => CleanupConvertedMmp();
    }

    private bool HasPendingChanges => _edits.Count > 0;

    private bool IsCurrentWriteLocked =>
        _archive is not null && _writeLockedPaths.Contains(Path.GetFullPath(_archive.SourcePath));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_layout.ResManagerWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        await LoadArchiveAsync(_archivePath);
        SelectInitialResource();
    }

    internal void SelectInitialResource()
    {
        if (InitialResourceName is not { } name) return;
        var row = _rows.FirstOrDefault(r => r.ResourceName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        int separator = row.ResourceName.LastIndexOf('\\');
        NavigateToFolder(separator < 0 ? "" : row.ResourceName[..separator]);
        FilesGrid.SelectedItem = row;
        FilesGrid.ScrollIntoView(row);
    }

    private async Task LoadArchiveAsync(string path)
    {
        SetBusy(true, "Читаю каталог RES…");
        try
        {
            ResFileManagerArchive archive = await ResFileManagerArchive.OpenAsync(path);
            _archive = archive;
            _archivePath = archive.SourcePath;
            _preferences.RememberResPath(archive.SourcePath);
            _selectedFolder = string.Empty;
            _edits.Clear();
            _undo.Clear();
            _redo.Clear();
            RebuildRows();
            ShowMessage(
                $"RES открыт безопасно: {archive.Entries.Count:N0} файлов. До сохранения исходный архив не изменяется.",
                isError: false);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось открыть RES", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildRows()
    {
        _rows.Clear();
        if (_archive is null)
        {
            UpdateUiState();
            return;
        }

        var originalByName = _archive.Entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (ResEntry entry in _archive.Entries)
        {
            _edits.TryGetValue(entry.Name, out ResFileEdit? edit);
            string state = edit?.Kind switch
            {
                ResFileEditKind.Delete => "Будет удалён",
                ResFileEditKind.AddOrReplace => "Заменён",
                _ => "Без изменений",
            };
            long size = edit?.ImportedFile?.FileLength ?? entry.Size;
            _rows.Add(new ResManagerFileRow(entry.Name, size, entry.Offset.ToString("N0"), state));
        }

        foreach (ResFileEdit edit in _edits.Values
                     .Where(edit => edit.Kind == ResFileEditKind.AddOrReplace &&
                                    !originalByName.ContainsKey(edit.ResourceName))
                     .OrderBy(edit => edit.ResourceName, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new ResManagerFileRow(
                edit.ResourceName,
                edit.ImportedFile?.FileLength ?? 0,
                "—",
                "Добавлен"));
        }

        List<ResManagerFileRow> ordered = _rows
            .OrderBy(row => row.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _rows.Clear();
        foreach (ResManagerFileRow row in ordered)
        {
            _rows.Add(row);
        }

        RebuildFolderTree();
        RebuildChanges();
        _filesView.Refresh();
        UpdateUiState();
    }

    private void RebuildFolderTree()
    {
        var root = new ResFolderNode("Корень RES", string.Empty) { IsExpanded = true };
        var byPath = new Dictionary<string, ResFolderNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        foreach (string folderPath in _rows
                     .Select(row => row.FolderPath)
                     .Where(path => !string.IsNullOrEmpty(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string currentPath = string.Empty;
            ResFolderNode parent = root;
            foreach (string segment in folderPath.Split('\\'))
            {
                currentPath = string.IsNullOrEmpty(currentPath)
                    ? segment
                    : currentPath + "\\" + segment;
                if (!byPath.TryGetValue(currentPath, out ResFolderNode? node))
                {
                    node = new ResFolderNode(segment, currentPath);
                    byPath.Add(currentPath, node);
                    parent.Children.Add(node);
                }

                parent = node;
            }
        }

        foreach ((string path, ResFolderNode node) in byPath)
        {
            node.FileCount = _rows.Count(row =>
                string.IsNullOrEmpty(path) ||
                row.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                row.FolderPath.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
        }

        if (!byPath.ContainsKey(_selectedFolder))
        {
            _selectedFolder = string.Empty;
        }

        byPath[_selectedFolder].IsSelected = true;

        FoldersTree.ItemsSource = new[] { root };
        FolderSummaryText.Text = $"{byPath.Count - 1:N0} папок · {_rows.Count:N0} файлов";
        RebuildBreadcrumb();
    }

    private void RebuildChanges()
    {
        bool firstChange = _changes.Count == 0 && _edits.Count > 0;
        _changes.Clear();
        foreach (ResFileEdit edit in _edits.Values.OrderBy(edit => edit.ResourceName, StringComparer.OrdinalIgnoreCase))
        {
            string action = edit.Kind == ResFileEditKind.Delete
                ? "Удаление"
                : _archive?.Contains(edit.ResourceName) == true ? "Замена" : "Добавление";
            _changes.Add(new ResManagerChangeRow(action, edit.ResourceName));
        }

        ChangesCountText.Text = _changes.Count.ToString("N0");
        ChangesExpander.Visibility = _changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (firstChange)
        {
            ChangesExpander.IsExpanded = true;
        }
    }

    private void RebuildBreadcrumb()
    {
        BreadcrumbPanel.Children.Clear();
        AddBreadcrumbPart("Корень RES", string.Empty);
        if (!string.IsNullOrEmpty(_selectedFolder))
        {
            string current = string.Empty;
            foreach (string segment in _selectedFolder.Split('\\'))
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "  ›  ",
                    Foreground = (Brush)FindResource("MutedTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                current = string.IsNullOrEmpty(current) ? segment : current + "\\" + segment;
                AddBreadcrumbPart(segment, current);
            }
        }

        UpFolderButton.IsEnabled = !string.IsNullOrEmpty(_selectedFolder) && !_isBusy;
    }

    private void AddBreadcrumbPart(string caption, string path)
    {
        var button = new Button
        {
            Content = caption,
            Tag = path,
            Style = (Style)FindResource("RoundedButton"),
            Padding = new Thickness(3, 0, 3, 0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextBrush"),
        };
        button.Click += BreadcrumbButton_Click;
        BreadcrumbPanel.Children.Add(button);
    }

    private bool FilterRow(object item)
    {
        if (item is not ResManagerFileRow row)
        {
            return false;
        }

        string search = SearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            return row.ResourceName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                   row.State.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        }

        return row.FolderPath.Equals(_selectedFolder, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateUiState()
    {
        bool loaded = _archive is not null;
        List<ResManagerFileRow> selected = GetSelectedRows();
        bool canEdit = loaded && !_isBusy;
        bool hasExtractableSelection = selected.Any(row => row.State != "Будет удалён");

        DeleteFolderButton.IsEnabled = canEdit && GetFolderDeletionRows(_selectedFolder).Count > 0;
        AddFilesButton.IsEnabled = canEdit;
        AddFolderButton.IsEnabled = canEdit;
        ReplaceButton.IsEnabled = canEdit && selected.Count == 1;
        DeleteButton.IsEnabled = canEdit && selected.Any(row => row.State != "Будет удалён");
        ExtractButton.IsEnabled = canEdit && hasExtractableSelection;
        UndoButton.IsEnabled = canEdit && _undo.Count > 0;
        RedoButton.IsEnabled = canEdit && _redo.Count > 0;
        SaveCopyButton.IsEnabled = canEdit && HasPendingChanges;
        SaveCurrentButton.IsEnabled = canEdit && HasPendingChanges && !IsCurrentWriteLocked;
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        FolderPathColumn.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_archive is null)
        {
            ArchiveTitleText.Text = "Файловый менеджер RES";
            ArchivePathText.Text = _archivePath;
            ArchiveSummaryText.Text = "RES не открыт";
            SafetyText.Text = "Выберите корректный RES-контейнер.";
            UpdateSelectionDetails(selected);
            return;
        }

        ArchiveTitleText.Text = Path.GetFileName(_archive.SourcePath);
        ArchivePathText.Text = _archive.SourcePath;
        ArchivePathText.ToolTip = _archive.SourcePath;
        int visible = _filesView.Cast<object>().Count();
        CurrentFolderText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? (string.IsNullOrEmpty(_selectedFolder) ? "Корень RES" : _selectedFolder)
            : "Результаты поиска по всему RES";
        SelectionSummaryText.Text = selected.Count > 0
            ? $"Выбрано: {selected.Count:N0}"
            : $"Показано: {visible:N0}";
        ArchiveSummaryText.Text =
            $"{Path.GetFileName(_archive.SourcePath)} · {_rows.Count:N0} файлов · подготовлено изменений: {_edits.Count:N0}";
        SafetyText.Text = IsCurrentWriteLocked
            ? "🔒 Текущий RES используется основным редактором — доступно только безопасное сохранение копии."
            : "Запись выполняется через временный RES, повторный разбор и SHA-256; при сохранении в текущий файл создаётся проверенная .bak-копия.";
        SaveCurrentButton.Content = IsCurrentWriteLocked
            ? "🔒 Текущий RES занят"
            : "Перезаписать текущий RES…";
        SaveCurrentButton.ToolTip = IsCurrentWriteLocked
            ? "Сначала закройте основной редактор и откройте RES-менеджер до загрузки database.res."
            : "Пересобрать открытый RES после подтверждения и создать резервную копию";
        UpdateSelectionDetails(selected);
        RebuildBreadcrumb();
    }

    private void UpdateSelectionDetails(IReadOnlyList<ResManagerFileRow> selected)
    {
        ResManagerFileRow? row = selected.Count == 1 ? selected[0] : null;
        QueueManagerPreview(row);
        UpdateAudioSelection(row);
        DetailsEmptyState.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null)
        {
            return;
        }

        DetailsNameText.Text = row.FileName;
        DetailsPathText.Text = row.ResourceName;
        DetailsTypeText.Text = row.FileType;
        DetailsSizeText.Text = FormatFileSize(row.Size);
        DetailsOffsetText.Text = row.OffsetText;
        DetailsStateText.Text = row.State;
        DetailsStateBadge.Background = row.State switch
        {
            "Добавлен" => new SolidColorBrush(Color.FromRgb(24, 53, 40)),
            "Заменён" => new SolidColorBrush(Color.FromRgb(51, 44, 29)),
            "Будет удалён" => (Brush)FindResource("DangerSurfaceBrush"),
            _ => new SolidColorBrush(Color.FromRgb(40, 45, 41)),
        };
    }

    private static string FormatFileSize(long size)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ"];
        double value = size;
        int unit = 0;
        while (value >= 1024 && unit + 1 < units.Length)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private List<ResManagerFileRow> GetSelectedRows() =>
        (TileMode.IsChecked == true ? TilesList.SelectedItems : FilesGrid.SelectedItems).Cast<ResManagerFileRow>()
            .OrderBy(row => row.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void CaptureUndo()
    {
        _undo.Push(CloneEdits(_edits));
        _redo.Clear();
    }

    private static Dictionary<string, ResFileEdit> CloneEdits(
        IReadOnlyDictionary<string, ResFileEdit> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);

    private void RestoreEdits(Dictionary<string, ResFileEdit> snapshot)
    {
        _edits.Clear();
        foreach ((string name, ResFileEdit edit) in snapshot)
        {
            _edits.Add(name, edit);
        }

        RebuildRows();
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить файлы в RES",
            Filter = "Все файлы (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) is true)
        {
            await AddCandidatesAsync(dialog.FileNames.Select(path =>
                new ImportCandidate(path, CombineResourcePath(_selectedFolder, Path.GetFileName(path)))));
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Добавить папку в RES",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) is true)
        {
            try
            {
                await AddCandidatesAsync(CollectDirectoryCandidates(dialog.FolderName));
            }
            catch (Exception exception)
            {
                ShowError("Не удалось подготовить папку", exception);
            }
        }
    }

    private async Task AddCandidatesAsync(IEnumerable<ImportCandidate> sourceCandidates)
    {
        if (_archive is null)
        {
            return;
        }

        try
        {
            List<ImportCandidate> candidates = sourceCandidates
                .Select(candidate => candidate with
                {
                    LocalPath = Path.GetFullPath(candidate.LocalPath),
                    ResourceName = ResFileManagerArchive.NormalizeResourceName(candidate.ResourceName),
                })
                .ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("В выбранной папке нет обычных файлов.");
            }

            string? duplicateInBatch = candidates
                .GroupBy(candidate => candidate.ResourceName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateInBatch is not null)
            {
                throw new InvalidOperationException(
                    $"В добавляемом наборе путь '{duplicateInBatch}' встречается больше одного раза.");
            }

            if (candidates.Any(candidate =>
                    candidate.LocalPath.Equals(_archive.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Нельзя вложить открытый RES сам в себя.");
            }

            int replacementCount = candidates.Count(candidate =>
                _archive.Contains(candidate.ResourceName) ||
                (_edits.TryGetValue(candidate.ResourceName, out ResFileEdit? edit) &&
                 edit.Kind == ResFileEditKind.AddOrReplace));
            if (replacementCount > 0 && !Confirm(
                    "Заменить файлы в RES?",
                    $"Совпадающих путей: {replacementCount:N0}. Их содержимое будет заменено выбранными файлами.",
                    "Изменения пока останутся в памяти и попадут в архив только после нажатия кнопки сохранения.",
                    "Подготовить замену",
                    "↻"))
            {
                return;
            }

            var imported = new List<(ImportCandidate Candidate, ResImportedFile File)>(candidates.Count);
            SetBusy(true, "Проверяю добавляемые файлы…");
            for (var index = 0; index < candidates.Count; index++)
            {
                BusyText.Text = $"Проверяю файл {index + 1:N0} из {candidates.Count:N0}…";
                ResImportedFile file = await ResFileManagerArchive.InspectImportAsync(candidates[index].LocalPath);
                imported.Add((candidates[index], file));
            }

            CaptureUndo();
            foreach ((ImportCandidate candidate, ResImportedFile file) in imported)
            {
                _edits[candidate.ResourceName] = new ResFileEdit(
                    ResFileEditKind.AddOrReplace,
                    candidate.ResourceName,
                    file);
            }

            RebuildRows();
            ShowMessage($"Подготовлено файлов: {imported.Count:N0}. Исходный RES ещё не изменён.", false);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось добавить файлы", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private IEnumerable<ImportCandidate> CollectDirectoryCandidates(string selectedDirectory)
    {
        string root = Path.GetFullPath(selectedDirectory);
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            DatabaseSafetyPolicy.ContainsReparsePoint(root))
        {
            throw new InvalidOperationException("Нельзя добавлять папку через символическую ссылку или junction.");
        }

        string archiveRoot = CombineResourcePath(_selectedFolder, rootInfo.Name);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string childDirectory in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(childDirectory);
                }
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('/', '\\');
                yield return new ImportCandidate(file, CombineResourcePath(archiveRoot, relative));
            }
        }
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        List<ResManagerFileRow> selected = GetSelectedRows();
        if (selected.Count != 1)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Заменить {selected[0].ResourceName}",
            Filter = "Все файлы (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        await AddCandidatesAsync([new ImportCandidate(dialog.FileName, selected[0].ResourceName)]);
    }

    private List<ResManagerFileRow> GetFolderDeletionRows(string folder) =>
        string.IsNullOrEmpty(folder) ? [] : _rows.Where(row =>
            row.ResourceName.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
            row.State != "Будет удалён").ToList();

    private void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetFolderDeletionRows(_selectedFolder);
        if (_archive is null || _isBusy || selected.Count == 0) return;
        if (!Confirm("Удалить папку из RES?",
            $"Папка: {_selectedFolder}. Файлов вместе с вложенными папками: {selected.Count:N0}.",
            "Удаление применяется после сохранения. До сохранения всё действие можно отменить через Ctrl+Z.",
            "Подготовить удаление", "−")) return;
        StageDeletion(selected);
    }
    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null)
        {
            return;
        }

        List<ResManagerFileRow> selected = GetSelectedRows()
            .Where(row => row.State != "Будет удалён")
            .ToList();
        if (selected.Count == 0 || !Confirm(
                "Убрать файлы из RES?",
                $"Будет удалено после сохранения: {selected.Count:N0}.",
                "До сохранения действие можно отменить через Ctrl+Z. При записи исходный RES получит проверенную резервную копию.",
                "Подготовить удаление",
                "−"))
        {
            return;
        }

        StageDeletion(selected);
    }

    private void StageDeletion(List<ResManagerFileRow> selected)
    {
        if (_archive is null || _isBusy || selected.Count == 0) return;
        CaptureUndo();
        foreach (ResManagerFileRow row in selected)
        {
            if (!_archive.Contains(row.ResourceName))
            {
                _edits.Remove(row.ResourceName);
            }
            else
            {
                _edits[row.ResourceName] = new ResFileEdit(ResFileEditKind.Delete, row.ResourceName);
            }
        }

        RebuildRows();
        ShowMessage($"Подготовлено удаление файлов: {selected.Count:N0}. Исходный RES ещё не изменён.", false);
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null)
        {
            return;
        }

        List<ResManagerFileRow> selected = GetSelectedRows()
            .Where(row => row.State != "Будет удалён")
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            if (selected.Count == 1)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Извлечь файл из RES",
                    FileName = selected[0].FileName,
                    Filter = "Все файлы (*.*)|*.*",
                    OverwritePrompt = true,
                    RestoreDirectory = true,
                };
                if (dialog.ShowDialog(this) is not true)
                {
                    return;
                }

                SetBusy(true, "Извлекаю файл…");
                await ExtractRowAsync(selected[0], dialog.FileName, overwrite: true);
                ShowMessage($"Файл извлечён: {dialog.FileName}", false);
                return;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = $"Извлечь {selected.Count:N0} файлов с сохранением папок",
                Multiselect = false,
            };
            if (folderDialog.ShowDialog(this) is not true)
            {
                return;
            }

            string root = Path.GetFullPath(folderDialog.FolderName);
            var targets = selected.Select(row => new
            {
                Row = row,
                Path = GetSafeExtractionTarget(root, row.ResourceName),
            }).ToList();
            int existingCount = targets.Count(target => File.Exists(target.Path) || Directory.Exists(target.Path));
            if (existingCount > 0 && !Confirm(
                    "Перезаписать извлечённые файлы?",
                    $"В выбранной папке уже существуют пути: {existingCount:N0}.",
                    "Совпадающие обычные файлы будут заменены. Папка RES сохранится.",
                    "Извлечь и заменить",
                    "⇩"))
            {
                return;
            }

            SetBusy(true, "Извлекаю файлы…");
            for (var index = 0; index < targets.Count; index++)
            {
                BusyText.Text = $"Извлекаю файл {index + 1:N0} из {targets.Count:N0}…";
                string? directory = Path.GetDirectoryName(targets[index].Path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new DirectoryNotFoundException("Не удалось определить папку извлечения.");
                }

                Directory.CreateDirectory(directory);
                await ExtractRowAsync(targets[index].Row, targets[index].Path, overwrite: true);
            }

            ShowMessage($"Извлечено файлов: {targets.Count:N0} · {root}", false);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось извлечь файлы", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task ExtractRowAsync(ResManagerFileRow row, string targetPath, bool overwrite)
    {
        if (_archive is null)
        {
            throw new InvalidOperationException("RES не открыт.");
        }

        if (_edits.TryGetValue(row.ResourceName, out ResFileEdit? edit) &&
            edit.Kind == ResFileEditKind.AddOrReplace &&
            edit.ImportedFile is ResImportedFile imported)
        {
            return ResFileManagerArchive.ExtractImportedAsync(imported, targetPath, overwrite);
        }

        return _archive.ExtractAsync(row.ResourceName, targetPath, overwrite);
    }

    private static string GetSafeExtractionTarget(string root, string resourceName)
    {
        string target = Path.GetFullPath(Path.Combine(root, resourceName.Replace('\\', Path.DirectorySeparatorChar)));
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Путь ресурса выходит за пределы выбранной папки.");
        }

        return target;
    }

    private async void SaveCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null || !HasPendingChanges)
        {
            return;
        }

        string sourceName = Path.GetFileNameWithoutExtension(_archive.SourcePath);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить проверенную копию RES",
            Filter = "Evil Islands RES-контейнер (*.res)|*.res",
            DefaultExt = ".res",
            AddExtension = true,
            OverwritePrompt = false,
            CreatePrompt = false,
            CheckPathExists = true,
            RestoreDirectory = true,
            FileName = sourceName + ".edited.res",
        };
        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        try
        {
            SetBusy(true, "Пересобираю и проверяю копию RES…");
            ResFileManagerSaveResult result = await _archive.SaveCopyAsync(_edits.Values.ToArray(), dialog.FileName);
            ShowMessage(
                $"Проверенная копия создана: {result.TargetPath} · файлов: {result.ResourceCount:N0} · SHA-256 {result.Sha256}",
                false);
        }
        catch (Exception exception)
        {
            ShowError("Копия RES не создана", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null || !HasPendingChanges || IsCurrentWriteLocked)
        {
            return;
        }

        bool gamePath = DatabaseSafetyPolicy.IsLikelyGameInstallationPath(_archive.SourcePath);
        string message = gamePath
            ? "Открытый RES находится в каталоге игры. После проверки он будет заменён подготовленной версией."
            : "Открытый RES будет заменён подготовленной и повторно проверенной версией.";
        var saveDialog = new ConfirmDialog("Сохранить изменения в текущий RES?", message,
            "По умолчанию создаётся резервная копия .bak. При выборе пункта ниже постоянный бэкап не создаётся.",
            "Сохранить с резервной копией", "✓") { Owner = this };
        saveDialog.AllowSavingWithoutBackup();
        if (saveDialog.ShowDialog() is not true) return;
        bool createBackup = saveDialog.CreateBackup;
        try
        {
            string sourcePath = _archive.SourcePath;
            SetBusy(true, "Проверяю и сохраняю новый RES…");
            ResFileManagerSaveResult result = await _archive.SaveInPlaceAsync(
                _edits.Values.ToArray(),
                allowGameInstallationPath: gamePath, createBackup: createBackup);
            _archive = await ResFileManagerArchive.OpenAsync(sourcePath);
            _edits.Clear();
            _undo.Clear();
            _redo.Clear();
            RebuildRows();
            ShowMessage(
                createBackup ? $"RES сохранён и проверен. Резервная копия: {result.BackupPath}" : "RES сохранён и проверен без бэкапа.",
                false);
        }
        catch (Exception exception)
        {
            ShowError("Текущий RES не сохранён", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0 || _isBusy)
        {
            return;
        }

        _redo.Push(CloneEdits(_edits));
        RestoreEdits(_undo.Pop());
        ShowMessage("Последнее подготовленное изменение отменено.", false);
    }

    private void Redo()
    {
        if (_redo.Count == 0 || _isBusy)
        {
            return;
        }

        _undo.Push(CloneEdits(_edits));
        RestoreEdits(_redo.Pop());
        ShowMessage("Изменение возвращено.", false);
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (HasPendingChanges && !Confirm(
                "Открыть другой RES?",
                "Подготовленные, но не записанные изменения текущего архива будут забыты.",
                "При необходимости сначала сохраните копию RES.",
                "Открыть другой",
                "▦"))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Открыть RES как архив",
            InitialDirectory = _preferences.GetLastResDirectory() ?? Path.GetDirectoryName(_archivePath),
            Filter = "Evil Islands RES-контейнер (*.res)|*.res|Все файлы (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(this) is true)
        {
            await LoadArchiveAsync(dialog.FileName);
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filesView.Refresh();
        UpdateUiState();
    }

    private void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            NavigateToFolder(path);
        }
    }

    private void UpFolderButton_Click(object sender, RoutedEventArgs e)
    {
        int separator = _selectedFolder.LastIndexOf('\\');
        NavigateToFolder(separator < 0 ? string.Empty : _selectedFolder[..separator]);
    }

    private void NavigateToFolder(string path)
    {
        _selectedFolder = path;
        SearchBox.Clear();
        RebuildFolderTree();
        _filesView.Refresh();
        UpdateUiState();
    }

    private void DetailsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetDetailsVisible(!_detailsVisible);
        RememberLayout();
    }

    private void SetDetailsVisible(bool visible)
    {
        _detailsVisible = visible;
        if (visible)
        {
            DetailsColumn.Width = new GridLength(Math.Clamp(_detailsWidth, 240, 620));
            DetailsSplitterColumn.Width = new GridLength(7);
            DetailsPanel.Visibility = Visibility.Visible;
            DetailsSplitter.Visibility = Visibility.Visible;
            DetailsToggleButton.Content = "Скрыть свойства";
        }
        else
        {
            if (DetailsColumn.ActualWidth >= 240)
            {
                _detailsWidth = DetailsColumn.ActualWidth;
            }

            DetailsPanel.Visibility = Visibility.Collapsed;
            DetailsSplitter.Visibility = Visibility.Collapsed;
            DetailsSplitterColumn.Width = new GridLength(0);
            DetailsColumn.Width = new GridLength(0);
            DetailsToggleButton.Content = "Свойства";
        }
    }

    private void GridSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => RememberLayout();

    private void ChangesExpander_StateChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RememberLayout();
        }
    }

    private void RevertChangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string resourceName } || !_edits.ContainsKey(resourceName))
        {
            return;
        }

        CaptureUndo();
        _edits.Remove(resourceName);
        RebuildRows();
        ShowMessage($"Изменение ресурса '{resourceName}' отменено.", false);
    }

    private void FoldersTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ResFolderNode node)
        {
            _selectedFolder = node.FullPath;
            SearchBox.Clear();
            _filesView.Refresh();
            UpdateUiState();
        }
    }

    private void FilesGrid_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => UpdateUiState();

    private void FilesGrid_DragEnter(object sender, DragEventArgs e)
    {
        bool containsFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = containsFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = containsFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void FilesGrid_DragLeave(object sender, DragEventArgs e) =>
        DropHint.Visibility = Visibility.Collapsed;

    private async void FilesGrid_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        try
        {
            var candidates = new List<ImportCandidate>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    candidates.Add(new ImportCandidate(
                        path,
                        CombineResourcePath(_selectedFolder, Path.GetFileName(path))));
                }
                else if (Directory.Exists(path))
                {
                    candidates.AddRange(CollectDirectoryCandidates(path));
                }
            }

            await AddCandidatesAsync(candidates);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось обработать перетаскивание", exception);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && (FilesGrid.IsKeyboardFocusWithin || TilesList.IsKeyboardFocusWithin))
        {
            DeleteButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !_isBusy)
        {
            Close();
            e.Handled = true;
        }
    }

    private void SetBusy(bool value, string? text = null)
    {
        _isBusy = value;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(text))
        {
            BusyText.Text = text;
        }

        UpdateUiState();
    }

    private void ShowMessage(string message, bool isError)
    {
        if (!isError)
        {
            OperationStatusText.Text = message;
            MessageBorder.Visibility = Visibility.Collapsed;
            return;
        }

        MessageText.Text = message;
        MessageBorder.Background = (Brush)FindResource("DangerSurfaceBrush");
        MessageBorder.BorderBrush = (Brush)FindResource("DangerBrush");
        MessageBorder.Visibility = Visibility.Visible;
    }

    private void ShowError(string heading, Exception exception)
    {
        ShowMessage($"{heading}: {exception.Message}", true);
        var dialog = new ConfirmDialog(
            heading,
            exception.Message,
            exception.GetType().Name,
            "Понятно",
            "!")
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private bool Confirm(
        string heading,
        string message,
        string detail,
        string confirmText,
        string icon)
    {
        var dialog = new ConfirmDialog(heading, message, detail, confirmText, icon)
        {
            Owner = this,
        };
        return dialog.ShowDialog() is true && dialog.IsConfirmed;
    }

    private static string CombineResourcePath(string folder, string child) =>
        string.IsNullOrEmpty(folder) ? child : folder + "\\" + child;

    private void DismissMessageButton_Click(object sender, RoutedEventArgs e) =>
        MessageBorder.Visibility = Visibility.Collapsed;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2) ToggleWindow_Click(sender, e);
            else DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyStoredLayout()
    {
        Width = _layout.ResManagerWindowWidth;
        Height = _layout.ResManagerWindowHeight;
        FoldersColumn.Width = new GridLength(_layout.ResManagerFoldersWidth);
        _detailsWidth = _layout.ResManagerDetailsWidth;
        ChangesExpander.IsExpanded = _layout.ResManagerChangesExpanded;
        SetDetailsVisible(_layout.ResManagerDetailsVisible);
    }

    private void RememberLayout()
    {
        if (WindowState == WindowState.Normal)
        {
            _layout.ResManagerWindowWidth = ActualWidth >= MinWidth ? ActualWidth : Width;
            _layout.ResManagerWindowHeight = ActualHeight >= MinHeight ? ActualHeight : Height;
        }

        if (FoldersColumn.ActualWidth >= 190)
        {
            _layout.ResManagerFoldersWidth = FoldersColumn.ActualWidth;
        }

        if (_detailsVisible && DetailsColumn.ActualWidth >= 240)
        {
            _detailsWidth = DetailsColumn.ActualWidth;
        }

        _layout.ResManagerDetailsWidth = _detailsWidth;
        _layout.ResManagerDetailsVisible = _detailsVisible;
        _layout.ResManagerChangesExpanded = ChangesExpander.IsExpanded;
        _layout.ResManagerWindowMaximized = WindowState == WindowState.Maximized;
        _preferences.RememberLayout(_layout);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_isBusy)
        {
            e.Cancel = true;
            return;
        }

        if (HasPendingChanges && !Confirm(
                "Закрыть RES-менеджер?",
                "Подготовленные изменения не записаны в открытый RES.",
                "Если проверенная копия уже сохранена, исходный архив всё равно остался без изменений.",
                "Закрыть без записи",
                "×"))
        {
            e.Cancel = true;
            return;
        }

        RememberLayout();
        _allowClose = true;
    }

    private sealed record ImportCandidate(string LocalPath, string ResourceName);
}

internal sealed class ResManagerFileRow
{
    public ResManagerFileRow(string resourceName, long size, string offsetText, string state)
    {
        ResourceName = resourceName;
        Size = size;
        OffsetText = offsetText;
        State = state;
        int separator = resourceName.LastIndexOf('\\');
        FolderPath = separator < 0 ? string.Empty : resourceName[..separator];
        FileName = separator < 0 ? resourceName : resourceName[(separator + 1)..];
        string extension = Path.GetExtension(FileName);
        FileType = string.IsNullOrEmpty(extension) ? "Файл" : extension[1..].ToUpperInvariant();
    }

    public string ResourceName { get; }

    public string FolderPath { get; }

    public string FileName { get; }

    public string FileType { get; }

    public long Size { get; }

    public string OffsetText { get; }

    public string State { get; }
}

internal sealed record ResManagerChangeRow(string Action, string ResourceName);

internal sealed class ResFolderNode : ObservableObject
{
    private int _fileCount;
    private bool _isExpanded;
    private bool _isSelected;

    public ResFolderNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }

    public string FullPath { get; }

    public ObservableCollection<ResFolderNode> Children { get; } = [];

    public int FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
