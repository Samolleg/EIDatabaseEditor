using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using EvilIslands.DatabaseEditor.App.Infrastructure;
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
    private readonly ICollectionView _filesView;
    private ResFileManagerArchive? _archive;
    private string _archivePath;
    private string _selectedFolder = string.Empty;
    private bool _isBusy;
    private bool _allowClose;

    public ResFileManagerDialog(string archivePath, IEnumerable<string>? writeLockedPaths = null)
    {
        InitializeComponent();
        _archivePath = Path.GetFullPath(archivePath);
        _writeLockedPaths = new HashSet<string>(
            (writeLockedPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        _filesView = CollectionViewSource.GetDefaultView(_rows);
        _filesView.Filter = FilterRow;
        FilesGrid.ItemsSource = _filesView;
    }

    private bool HasPendingChanges => _edits.Count > 0;

    private bool IsCurrentWriteLocked =>
        _archive is not null && _writeLockedPaths.Contains(Path.GetFullPath(_archive.SourcePath));

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await LoadArchiveAsync(_archivePath);

    private async Task LoadArchiveAsync(string path)
    {
        SetBusy(true, "Читаю каталог RES…");
        try
        {
            ResFileManagerArchive archive = await ResFileManagerArchive.OpenAsync(path);
            _archive = archive;
            _archivePath = archive.SourcePath;
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

        FoldersTree.ItemsSource = new[] { root };
        FolderSummaryText.Text = $"{byPath.Count - 1:N0} папок · {_rows.Count:N0} файлов";
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

        AddFilesButton.IsEnabled = canEdit;
        AddFolderButton.IsEnabled = canEdit;
        ReplaceButton.IsEnabled = canEdit && selected.Count == 1;
        DeleteButton.IsEnabled = canEdit && selected.Any(row => row.State != "Будет удалён");
        ExtractButton.IsEnabled = canEdit && hasExtractableSelection;
        UndoButton.IsEnabled = canEdit && _undo.Count > 0;
        RedoButton.IsEnabled = canEdit && _redo.Count > 0;
        SaveCopyButton.IsEnabled = canEdit && HasPendingChanges;
        SaveCurrentButton.IsEnabled = canEdit && HasPendingChanges && !IsCurrentWriteLocked;

        if (_archive is null)
        {
            ArchivePathText.Text = _archivePath;
            ArchiveSummaryText.Text = "RES не открыт";
            SafetyText.Text = "Выберите корректный RES-контейнер.";
            return;
        }

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
            ? "Этот RES сейчас подключён к основному редактору. Чтобы не получить две несогласованные версии, доступно только сохранение копии."
            : "Запись выполняется через временный RES, повторный разбор и SHA-256; при сохранении в текущий файл создаётся проверенная .bak-копия.";
        SaveCurrentButton.ToolTip = IsCurrentWriteLocked
            ? "Сначала закройте основной редактор и откройте RES-менеджер до загрузки database.res."
            : "Пересобрать открытый RES после подтверждения и создать резервную копию";
    }

    private List<ResManagerFileRow> GetSelectedRows() =>
        FilesGrid.SelectedItems.Cast<ResManagerFileRow>()
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
        if (!Confirm(
                "Сохранить изменения в текущий RES?",
                message,
                $"Сначала будет создана и проверена резервная копия .bak. Добавлено: {_edits.Values.Count(edit => edit.Kind == ResFileEditKind.AddOrReplace && !_archive.Contains(edit.ResourceName)):N0}; заменено: {_edits.Values.Count(edit => edit.Kind == ResFileEditKind.AddOrReplace && _archive.Contains(edit.ResourceName)):N0}; удалено: {_edits.Values.Count(edit => edit.Kind == ResFileEditKind.Delete):N0}.",
                gamePath ? "Записать в игровой RES" : "Сохранить RES",
                "✓"))
        {
            return;
        }

        try
        {
            string sourcePath = _archive.SourcePath;
            SetBusy(true, "Создаю резервную копию и проверяю новый RES…");
            ResFileManagerSaveResult result = await _archive.SaveInPlaceAsync(
                _edits.Values.ToArray(),
                allowGameInstallationPath: gamePath);
            _archive = await ResFileManagerArchive.OpenAsync(sourcePath);
            _edits.Clear();
            _undo.Clear();
            _redo.Clear();
            RebuildRows();
            ShowMessage(
                $"RES сохранён и проверен. Резервная копия: {result.BackupPath} · SHA-256 {result.Sha256}",
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
        else if (e.Key == Key.Delete && FilesGrid.IsKeyboardFocusWithin)
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
        MessageText.Text = message;
        MessageBorder.Background = isError
            ? (System.Windows.Media.Brush)FindResource("DangerSurfaceBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 53, 45));
        MessageBorder.BorderBrush = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 97, 79));
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
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
    }

    public string ResourceName { get; }

    public string FolderPath { get; }

    public string FileName { get; }

    public long Size { get; }

    public string OffsetText { get; }

    public string State { get; }
}

internal sealed class ResFolderNode : ObservableObject
{
    private int _fileCount;
    private bool _isExpanded;

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
}
