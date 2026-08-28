using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");

        try
        {
            var application = new EvilIslands.DatabaseEditor.App.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            application.InitializeComponent();
            var window = new MainWindow();
            VerifyContrastStyles(application, window);
            VerifySpreadsheetGridDefinition(window);
            VerifySpreadsheetClipboardText();
            VerifyNavigationRequery(application);
            VerifyDatabaseTableNavigation();
            VerifyPreferencesStore();
            if (args.Length > 0)
            {
                VerifyRealWorkspace(window, args[0], args.Length > 1 ? args[1] : null);
            }
            window.Close();
            application.Shutdown();
            Console.WriteLine("PASS: MainWindow XAML загружен при культуре ru-RU.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: MainWindow XAML не загрузился при культуре ru-RU.");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyContrastStyles(Application application, MainWindow window)
    {
        var expected = (SolidColorBrush)application.Resources["TextBrush"];
        VerifyForegroundSetter((Style)window.Resources["PageHeading"], expected, "PageHeading");
        VerifyForegroundSetter((Style)window.Resources["MetricValue"], expected, "MetricValue");
        var cellStyle = (Style)application.Resources[typeof(DataGridCell)];
        var selectedTrigger = cellStyle.Triggers.OfType<Trigger>()
            .FirstOrDefault(trigger => trigger.Property == DataGridCell.IsSelectedProperty && Equals(trigger.Value, true));
        var selectedBackground = selectedTrigger?.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.BackgroundProperty)?.Value;
        var expectedSelection = (SolidColorBrush)application.Resources["SpreadsheetSelectionBrush"];
        if (selectedBackground is not SolidColorBrush actualSelection ||
            actualSelection.Color != expectedSelection.Color)
        {
            throw new InvalidOperationException(
                "Выбранная DataGridCell должна иметь отдельный видимый Excel-подобный фон.");
        }

    }

    private static void VerifyForegroundSetter(Style style, SolidColorBrush expected, string styleName)
    {
        var setter = style.Setters.OfType<Setter>()
            .FirstOrDefault(candidate => candidate.Property == TextBlock.ForegroundProperty);
        if (setter?.Value is not SolidColorBrush actual || actual.Color != expected.Color)
        {
            throw new InvalidOperationException(
                $"Стиль {styleName} обязан явно задавать контрастный TextBrush.");
        }
    }

    private static void VerifySpreadsheetGridDefinition(MainWindow window)
    {
        if (window.FindName("RecordsGrid") is not DataGrid grid)
        {
            throw new InvalidOperationException("Экран таблицы обязан содержать Excel-подобный RecordsGrid.");
        }

        if (grid.AutoGenerateColumns || grid.IsReadOnly ||
            !grid.EnableRowVirtualization || !grid.EnableColumnVirtualization ||
            !grid.CanUserResizeColumns ||
            grid.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader ||
            grid.SelectionMode != DataGridSelectionMode.Extended ||
            grid.ClipboardCopyMode != DataGridClipboardCopyMode.None ||
            !grid.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == ApplicationCommands.Copy) ||
            !grid.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == ApplicationCommands.Paste) ||
            grid.Columns.Count != 2)
        {
            throw new InvalidOperationException(
                "RecordsGrid должен иметь две фиксированные колонки, выбор клеток и обе виртуализации.");
        }
    }

    private static void VerifySpreadsheetClipboardText()
    {
        IReadOnlyList<IReadOnlyList<string>> source =
        [
            ["alpha", "beta"],
            ["строка 1\nстрока 2", "кавычка \" внутри"],
        ];
        string serialized = SpreadsheetClipboardText.Serialize(source);
        IReadOnlyList<IReadOnlyList<string>> parsed = SpreadsheetClipboardText.Parse(serialized + "\r\n");
        if (parsed.Count != source.Count ||
            parsed.Where((row, index) => !row.SequenceEqual(source[index], StringComparer.Ordinal)).Any())
        {
            throw new InvalidOperationException(
                "Excel-совместимый TSV должен сохранять строки, табуляции и кавычки без лишней последней строки.");
        }
    }

    private static void VerifyNavigationRequery(Application application)
    {
        using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
        var resourcesItem = viewModel.NavigationItems.Single(item => item.Section == WorkspaceSection.Resources);
        var raised = 0;
        viewModel.NavigateCommand.CanExecuteChanged += (_, _) => raised++;

        var radioButton = new RadioButton
        {
            Style = (Style)application.Resources["SidebarItem"],
            Command = viewModel.NavigateCommand,
            CommandParameter = resourcesItem,
        };
        BindingOperations.SetBinding(
            radioButton,
            UIElement.IsEnabledProperty,
            new Binding(nameof(NavigationItemViewModel.IsEnabled)) { Source = resourcesItem });

        viewModel.OpenPathAsync("C:\\staging\\database.res").GetAwaiter().GetResult();
        if (!viewModel.IsEditMode)
        {
            throw new InvalidOperationException("Редактирование должно включаться автоматически сразу после открытия базы.");
        }

        radioButton.Dispatcher.Invoke(() => { });
        if (raised == 0 || !radioButton.IsEnabled || radioButton.Opacity < 0.85)
        {
            throw new InvalidOperationException(
                "Навигация должна обновить CanExecute и стать контрастной после открытия базы.");
        }
    }

    private static void VerifyDatabaseTableNavigation()
    {
        using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
        viewModel.OpenPathAsync("C:\\staging\\database.res").GetAwaiter().GetResult();
        var group = viewModel.Databases.Single();
        var table = group.Tables.Single(candidate => candidate.IsEditable);
        if (!group.IsEnabled || !table.IsEnabled || !viewModel.SelectTableCommand.CanExecute(table))
        {
            throw new InvalidOperationException("Распознанная таблица должна быть доступна из иерархической навигации.");
        }

        viewModel.SelectTableCommand.Execute(table);
        if (viewModel.CurrentSection != WorkspaceSection.Records ||
            !ReferenceEquals(viewModel.SelectedTable, table) ||
            !table.IsSelected ||
            viewModel.PageTitle != table.Title ||
            !viewModel.BreadcrumbText.Contains(table.ResourceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Выбор дочерней таблицы должен открыть отдельную records-страницу и хлебные крошки.");
        }

        if (!viewModel.IsEditMode)
        {
            throw new InvalidOperationException("Выбор таблицы не должен выключать постоянный режим редактирования.");
        }

        var readOnlyTable = group.Tables.Single(candidate => candidate.IsReadOnly);
        viewModel.SelectTableCommand.Execute(readOnlyTable);
        if (!viewModel.IsCurrentTableReadOnly || !viewModel.IsEditMode)
        {
            throw new InvalidOperationException("Read-only таблица не должна выключать общий режим редактора.");
        }

        viewModel.NavigateCommand.Execute(viewModel.OverviewNavigationItem);
        if (readOnlyTable.IsSelected || !viewModel.IsEditMode)
        {
            throw new InvalidOperationException("Служебная навигация должна снять выбор таблицы, не выключая редактор.");
        }
    }

    private static void VerifyPreferencesStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "EI-Database-Studio-UiSmoke", Guid.NewGuid().ToString("N"));
        var databaseDirectory = Path.Combine(root, "last-opened");
        Directory.CreateDirectory(databaseDirectory);
        var settingsPath = Path.Combine(root, "settings.json");
        var databasePath = Path.Combine(databaseDirectory, "database.res");
        var store = new EditorPreferencesStore(settingsPath);
        store.RememberDatabasePath(databasePath);
        var reopenedStore = new EditorPreferencesStore(settingsPath);
        if (!string.Equals(
                reopenedStore.GetLastDatabaseDirectory(),
                Path.GetFullPath(databaseDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Диалог открытия должен запоминать последнюю папку database.res.");
        }

        Directory.Delete(root, recursive: true);
    }

    private static void VerifyRealWorkspace(MainWindow window, string sourcePath, string? screenshotPath)
    {
        using var viewModel = new MainWindowViewModel(new CoreDatabaseWorkspaceService());
        RunWithDispatcher(() => viewModel.OpenPathAsync(sourcePath));
        var tables = viewModel.Databases.SelectMany(database => database.Tables).ToArray();
        var records = tables.SelectMany(table => table.Records).ToArray();
        if (viewModel.DatabaseCount != 8 || viewModel.TableCount != 26 ||
            records.Length == 0 || viewModel.RecordCount != records.Length)
        {
            throw new InvalidOperationException(
                $"App-адаптер должен раскрыть реальные 8 БД, 26 таблиц и все найденные записи; " +
                $"получено {viewModel.DatabaseCount}/{viewModel.TableCount}/{records.Length}. " +
                $"Статус: {viewModel.StatusText}. Сообщение: {viewModel.NotificationMessage}");
        }

        if (tables.Any(table =>
                table.Columns.Count == 0 ||
                table.Columns.Select(column => column.Identity).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count ||
                table.Columns.Count < table.Records.Max(record => record.FieldCount)))
        {
            throw new InvalidOperationException(
                "Каждая из 26 таблиц должна получить полный набор уникальных физических и виртуальных Excel-столбцов.");
        }

        if (tables.Any(table => !table.IsEditable || !table.CanAppendRecords) ||
            tables.SelectMany(table => table.Columns)
                .Where(column => column.Projection is null)
                .Any(column => !column.IsEditable) ||
            tables.SelectMany(table => table.Columns)
                .Where(column => column.Projection is not null)
                .Any(column => column.IsEditable))
        {
            throw new InvalidOperationException(
                "Все 26 таблиц и их физические поля должны редактироваться; расшифрованные проекции остаются представлением исходного поля.");
        }

        if (records.Any(record => record.AreFieldsMaterialized))
        {
            throw new InvalidOperationException("Поля записей не должны материализоваться при открытии рабочей области.");
        }

        string companionTexts = Path.Combine(Path.GetDirectoryName(sourcePath)!, "texts.res");
        if (File.Exists(companionTexts))
        {
            if (!viewModel.HasTextsDocument || viewModel.TextResourceCount < 100)
            {
                throw new InvalidOperationException("Соседний texts.res должен автоматически подключаться и индексироваться.");
            }

            if (!viewModel.TextsNavigationItem.IsEnabled ||
                viewModel.TextResources.Count != viewModel.TextResourceCount)
            {
                throw new InvalidOperationException(
                    "Подключённый texts.res должен быть доступен отдельным полным разделом навигации.");
            }

            viewModel.NavigateCommand.Execute(viewModel.TextsNavigationItem);
            if (viewModel.CurrentSection != WorkspaceSection.Texts ||
                viewModel.FilteredTextResourceCount != viewModel.TextResourceCount)
            {
                throw new InvalidOperationException(
                    "Раздел русских текстов должен показывать все ресурсы подключённого texts.res.");
            }

            LocalizationEntryViewModel searchableText = viewModel.TextResources.First(text =>
                !string.IsNullOrWhiteSpace(text.Title));
            viewModel.TextsSearchText = searchableText.ResourceName;
            if (viewModel.FilteredTextResourceCount == 0 ||
                !viewModel.TextsView.Cast<object>().Contains(searchableText))
            {
                throw new InvalidOperationException(
                    "Полный раздел texts.res должен искать по ключу, названию и описанию.");
            }

            viewModel.TextsSearchText = string.Empty;

            DatabaseTableViewModel materialsTable = tables.Single(table => table.Key == "items.materials");
            viewModel.SelectTableCommand.Execute(materialsTable);
            RecordRowViewModel material = materialsTable.Records.First();
            viewModel.SelectedRecord = material;
            if (!material.RelatedTexts.Any(text => text.Prefix.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase)) ||
                !material.RelatedTexts.Any(text => text.Prefix.Equals("MATSHORT", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Материал должен связываться с MATERIAL и MATSHORT в texts.res.");
            }

            LocalizationEntryViewModel primaryMaterialText = material.RelatedTexts.Single(text =>
                text.Prefix.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase));
            if (!material.CanEditPrimaryLocalization ||
                material.LocalizationKeyText != primaryMaterialText.ResourceName ||
                material.LocalizationTitle != primaryMaterialText.Title ||
                material.LocalizationDescription != primaryMaterialText.Description)
            {
                throw new InvalidOperationException(
                    "Основная таблица материалов должна показывать редактируемые поля MATERIAL из texts.res в той же строке.");
            }

            LocalizationEntryViewModel textProbe = material.RelatedTexts.First(text => !text.IsNew);
            string originalTitle = textProbe.Title;
            textProbe.Title = originalTitle + " тест";
            if (!viewModel.HasChanges || !textProbe.IsModified || !viewModel.UndoCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Изменение русского названия должно попадать в общий журнал и Undo.");
            }

            viewModel.UndoCommand.Execute(null);
            if (textProbe.Title != originalTitle || textProbe.IsModified || viewModel.HasChanges)
            {
                throw new InvalidOperationException("Общий Undo должен полностью вернуть изменение texts.res.");
            }

            VerifyPairedSave(sourcePath, companionTexts);
        }

        var prototypes = tables.Single(table => table.Key == "units.prototypes");
        var races = tables.Single(table => table.Key == "units.races");
        SpreadsheetColumnViewModel raceReferenceColumn = prototypes.Columns.Single(column => column.FieldId == 1);
        HashSet<string> raceNames = races.Records
            .Select(record => record.FieldDefinitions.First(field => field.Id == 0).Value.ToString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!raceReferenceColumn.IsEditable || raceReferenceColumn.Kind != "String")
        {
            throw new InvalidOperationException(
                "Base Race должна оставаться обычным редактируемым строковым полем.");
        }

        foreach (string templateKey in new[]
                 {
                     "spells.templates",
                     "spells.armor_templates",
                     "spells.weapon_templates",
                 })
        {
            SpreadsheetColumnViewModel prototypeReference = tables.Single(table => table.Key == templateKey)
                .Columns.Single(column => column.FieldId == 1);
            if (!prototypeReference.IsEditable || prototypeReference.Kind != "String")
            {
                throw new InvalidOperationException(
                    $"{templateKey}/field-1 должен оставаться обычным редактируемым строковым полем.");
            }
        }

        viewModel.SelectTableCommand.Execute(prototypes);
        if (viewModel.HasTextsDocument)
        {
            RecordRowViewModel linkedUnit = prototypes.Records.FirstOrDefault(record =>
                record.CanEditPrimaryLocalization &&
                record.LocalizationKeyText.StartsWith("UNIT ", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "Хотя бы один прототип юнита должен связываться с UNIT из texts.res.");
            if (string.IsNullOrWhiteSpace(linkedUnit.LocalizationTitle))
            {
                throw new InvalidOperationException(
                    "Связанный UNIT обязан показывать русское название прямо в основной строке database.res.");
            }

            string linkedTitle = linkedUnit.LocalizationTitle;
            RecordFieldViewModel linkedName = linkedUnit.GetCell(0)
                ?? throw new InvalidOperationException("У прототипа юнита отсутствует поле имени.");
            linkedName.EditorText = linkedUnit.DisplayName + " Missing Link";
            if (linkedUnit.CanEditPrimaryLocalization ||
                !linkedUnit.LocalizationKeyText.Contains("нет ресурса", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "После изменения технического имени связь с UNIT должна немедленно пересчитываться.");
            }

            viewModel.UndoCommand.Execute(null);
            if (!linkedUnit.CanEditPrimaryLocalization ||
                linkedUnit.LocalizationTitle != linkedTitle ||
                viewModel.ChangeCount != 0)
            {
                throw new InvalidOperationException(
                    "Undo имени прототипа должен восстановить связанную русскую строку texts.res.");
            }

            RecordRowViewModel unlinkedHero = prototypes.Records.First(record =>
                record.DisplayName.Equals("Human Hero Hadagan5", StringComparison.OrdinalIgnoreCase));
            if (unlinkedHero.CanEditPrimaryLocalization ||
                !unlinkedHero.LocalizationKeyText.Contains("нет ресурса", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Для технического прототипа без UNIT основная строка должна явно показывать ожидаемый отсутствующий ключ.");
            }
        }

        if (prototypes.Columns.Count <= prototypes.Records[0].FieldCount ||
            records.Sum(record => record.MaterializedFieldCount) > 1)
        {
            throw new InvalidOperationException(
                "Построение столбцов должно использовать метаданные полей и не создавать редакторы клеток пачками.");
        }

        window.DataContext = viewModel;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        HashSet<int> prototypeFieldsWithSpecificProjections = prototypes.Columns
            .Where(column => column.Projection is not null &&
                             column.Projection.Kind != EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();
        HashSet<int> prototypeFieldsWithDecodedProjection = prototypes.Columns
            .Where(column => column.Projection?.Kind ==
                             EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue)
            .Select(column => column.FieldId)
            .ToHashSet();
        int readablePrototypeColumnCount = prototypes.Columns.Count(column => column.Projection switch
        {
            null => !prototypeFieldsWithSpecificProjections.Contains(column.FieldId) &&
                    !prototypeFieldsWithDecodedProjection.Contains(column.FieldId),
            { Kind: EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue } =>
                !prototypeFieldsWithSpecificProjections.Contains(column.FieldId),
            _ => true,
        });
        if (window.FindName("RecordsGrid") is not DataGrid recordsGrid ||
            recordsGrid.Columns.Count != readablePrototypeColumnCount + (viewModel.HasTextsDocument ? 5 : 2) ||
            recordsGrid.FrozenColumnCount != (viewModel.HasTextsDocument ? 5 : 3))
        {
            throw new InvalidOperationException(
                "DataGrid должен по умолчанию показать понятные значения без исходных raw-дубликатов и заморозить начало строки.");
        }

        if (viewModel.ShowAllColumns ||
            viewModel.ColumnModeButtonText != "Технические поля" ||
            viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex"))
        {
            throw new InvalidOperationException(
                "Таблица должна открываться в понятном режиме без RawHex и предлагать отдельный переход к техническим полям.");
        }

        DataGridColumn raceGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.FieldId == 1);
        if (raceGridColumn is not DataGridTextColumn)
        {
            throw new InvalidOperationException(
                "Ссылочное поле Base Race должно отрисовываться обычной текстовой колонкой без ComboBox.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (!viewModel.ShowAllColumns ||
            viewModel.ColumnModeButtonText != "Понятные поля" ||
            viewModel.VisibleSpreadsheetColumns.Count != prototypes.Columns.Count ||
            recordsGrid.Columns.Count != prototypes.Columns.Count + (viewModel.HasTextsDocument ? 5 : 2))
        {
            throw new InvalidOperationException(
                "Режим технических полей должен восстановить все физические столбцы и расшифровки.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.ShowAllColumns ||
            recordsGrid.Columns.Count != readablePrototypeColumnCount + (viewModel.HasTextsDocument ? 5 : 2) ||
            viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex"))
        {
            throw new InvalidOperationException(
                "Возврат в понятный режим должен снова скрыть raw-дубликаты без накопления колонок.");
        }

        var materializedAfterRender = records.Sum(record => record.MaterializedFieldCount);
        var totalFieldCount = records.Sum(record => record.FieldCount);
        if (materializedAfterRender <= 0 || materializedAfterRender >= totalFieldCount / 4)
        {
            throw new InvalidOperationException(
                $"Виртуализация должна создавать только видимые клетки: {materializedAfterRender} из {totalFieldCount}.");
        }

        viewModel.SelectedRecord = prototypes.Records[0];
        raceGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.FieldId == 1);
        RecordRowViewModel referenceRecord = prototypes.Records.First(record =>
            raceNames.Any(value => !value.Equals(record.GetCell(1)?.EditorText, StringComparison.OrdinalIgnoreCase)));
        RecordFieldViewModel referenceCell = referenceRecord.GetCell(1)
            ?? throw new InvalidOperationException("Не создана ссылочная клетка Base Race.");
        string originalReference = referenceCell.EditorText;
        string replacementReference = raceNames.First(value =>
            !value.Equals(originalReference, StringComparison.OrdinalIgnoreCase));
        recordsGrid.SelectedItem = referenceRecord;
        recordsGrid.CurrentCell = new DataGridCellInfo(referenceRecord, raceGridColumn);
        recordsGrid.ScrollIntoView(referenceRecord, raceGridColumn);
        recordsGrid.UpdateLayout();
        recordsGrid.Focus();
        if (!recordsGrid.BeginEdit())
        {
            throw new InvalidOperationException(
                $"Ссылочная Excel-клетка не перешла в режим редактирования: " +
                $"mode={viewModel.IsEditMode}, busy={viewModel.IsBusy}, table={viewModel.IsCurrentTableEditable}, " +
                $"record={referenceRecord.IsEditMode}, cell={referenceCell.CanEdit}, columnReadOnly={raceGridColumn.IsReadOnly}.");
        }

        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        TextBox referenceEditor = FindVisualChild<TextBox>(raceGridColumn.GetCellContent(referenceRecord))
            ?? throw new InvalidOperationException("DataGrid не создал TextBox для ссылочной клетки.");
        referenceEditor.Text = replacementReference;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        recordsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        recordsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (referenceCell.EditorText != replacementReference ||
            !referenceCell.IsModified ||
            viewModel.ChangeCount != 1 ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Ручной ввод Base Race должен изменить точное значение, журнал и Undo.");
        }

        viewModel.UndoCommand.Execute(null);
        if (referenceCell.EditorText != originalReference || referenceCell.IsModified || viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException("Undo должен полностью вернуть ссылочную клетку Base Race.");
        }

        var numericColumn = prototypes.Columns.First(column =>
            column.IsEditable && column.Kind is "Byte" or "Int32" or "UInt32" or "Float32");
        var numericCell = prototypes.Records[0].GetCell(numericColumn.FieldId)
            ?? throw new InvalidOperationException("Не создана числовая клетка для проверки валидации.");
        var originalNumericText = numericCell.EditorText;
        numericCell.EditorText = "не число";
        if (!numericCell.HasError || numericCell.IsModified)
        {
            throw new InvalidOperationException("Ошибочный ввод не должен менять payload и обязан показать ошибку клетки.");
        }

        numericCell.EditorText = originalNumericText;
        if (numericCell.HasError)
        {
            throw new InvalidOperationException("Возврат корректного значения должен очищать ошибку клетки.");
        }

        var editableColumn = prototypes.Columns.First(column => column.IsEditable && column.IsNameField);
        var editableCell = prototypes.Records[0].GetCell(editableColumn.FieldId)
            ?? throw new InvalidOperationException("Не создана редактируемая клетка имени.");
        var originalText = editableCell.EditorText;
        var nameGridColumn = recordsGrid.Columns.First(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.FieldId == editableColumn.FieldId);
        recordsGrid.SelectedItem = prototypes.Records[0];
        recordsGrid.CurrentCell = new DataGridCellInfo(prototypes.Records[0], nameGridColumn);
        recordsGrid.ScrollIntoView(prototypes.Records[0], nameGridColumn);
        recordsGrid.UpdateLayout();
        recordsGrid.Focus();
        if (!recordsGrid.BeginEdit())
        {
            throw new InvalidOperationException("Excel-клетка не перешла в режим редактирования.");
        }

        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var cellContent = nameGridColumn.GetCellContent(prototypes.Records[0]);
        var editor = FindVisualChild<TextBox>(cellContent)
            ?? throw new InvalidOperationException("DataGrid не создал TextBox для редактируемой клетки.");
        editor.Text = originalText + " grid-smoke";
        recordsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        recordsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!viewModel.IsEditMode || viewModel.ChangeCount != 1 || !prototypes.Records[0].IsModified ||
            editableCell.EditorText != originalText + " grid-smoke" ||
            !viewModel.UndoCommand.CanExecute(null) ||
            !viewModel.SaveCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Фиксация клетки должна сохранить постоянный режим, журнал, Undo и включить обычное сохранение.");
        }

        viewModel.UndoCommand.Execute(null);
        if (viewModel.ChangeCount != 0 || editableCell.EditorText != originalText || prototypes.Records[0].IsModified)
        {
            throw new InvalidOperationException("Undo должен полностью вернуть Excel-клетку к исходному значению.");
        }

        ValidationMessageViewModel navigableDiagnostic = viewModel.Diagnostics.FirstOrDefault(item =>
            item.CanNavigate && item.FieldId is not null)
            ?? throw new InvalidOperationException("Реальная база должна содержать сообщение с точной ячейкой.");
        RecordFocusRequestedEventArgs? navigationRequest = null;
        EventHandler<RecordFocusRequestedEventArgs> captureNavigation = (_, args) => navigationRequest = args;
        viewModel.RecordFocusRequested += captureNavigation;
        if (!viewModel.NavigateToDiagnosticCommand.CanExecute(navigableDiagnostic))
        {
            throw new InvalidOperationException("Точное сообщение проверки должно разрешать команду перехода.");
        }

        viewModel.NavigateToDiagnosticCommand.Execute(navigableDiagnostic);
        viewModel.RecordFocusRequested -= captureNavigation;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        RecordRowViewModel navigatedRecord = viewModel.SelectedRecord
            ?? throw new InvalidOperationException("Переход проверки не выбрал строку.");
        RecordFieldViewModel navigatedCell = navigatedRecord.GetCell(navigableDiagnostic.FieldId!.Value)
            ?? throw new InvalidOperationException("Переход проверки не нашёл клетку.");
        if (viewModel.CurrentSection != WorkspaceSection.Records ||
            viewModel.SelectedTable?.Key != navigableDiagnostic.TableKey ||
            navigatedRecord.SourceRecordIndex != navigableDiagnostic.RecordIndex ||
            navigationRequest is null ||
            navigationRequest.BeginEdit ||
            navigationRequest.FieldId != navigableDiagnostic.FieldId ||
            !navigatedRecord.HasDiagnostics ||
            !navigatedCell.HasDiagnostic ||
            !ReferenceEquals(recordsGrid.CurrentCell.Item, navigatedRecord))
        {
            throw new InvalidOperationException(
                "Сообщение проверки должно открыть точную таблицу, строку и подсвеченную клетку без входа в редактирование.");
        }

        var materials = tables.Single(table => table.Key == "items.materials");
        string[] editableMaterialHeaders =
        [
            "Имя", "Тип", "Код", "ID", "Цена", "Вес", "Мана", "Слоты", "Прочность", "Навык", "Урон",
            "Поглощение", "Маска травм", "Магазины",
        ];
        if (materials.Columns.Count != 26 ||
            !materials.IsEditable ||
            !materials.CanAppendRecords ||
            !materials.Columns.Where(column => column.Projection is null && column.IsEditable)
                .Select(column => column.Title)
                .SequenceEqual(editableMaterialHeaders, StringComparer.Ordinal) ||
            materials.Columns.Where(column => column.Projection is not null)
                .Any(column => column.IsEditable))
        {
            throw new InvalidOperationException(
                "Материалы должны иметь 14 редактируемых физических столбцов; " +
                "семь коэффициентов поглощения и пять shop-битов остаются синхронными проекциями исходных полей.");
        }

        var rawColumns = tables
            .SelectMany(table => table.Columns.Select(column => (Table: table, Column: column)))
            .Where(item => item.Column.Kind == "Hex")
            .ToArray();
        int physicalColumnCount = tables.Sum(table => table.Columns.Count(column => column.Projection is null));
        int rawEditorCount = tables.Sum(table => table.Columns.Count(column => column.Kind == "RawHex"));
        if (physicalColumnCount != 380 || rawColumns.Length != 0 || rawEditorCount == 0)
        {
            throw new InvalidOperationException(
                $"Полный каталог должен дать 380 редактируемых физических полей, расшифрованные проекции и raw-редакторы сложных структур; " +
                $"получено физических/Hex/raw {physicalColumnCount}/{rawColumns.Length}/{rawEditorCount}.");
        }

        var weapons = tables.Single(table => table.Key == "items.weapons");
        var armors = tables.Single(table => table.Key == "items.armors");
        int[] editableWeaponFields = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 21, 23, 24, 25, 29, 30];
        int[] protectedWeaponFields = [14, 22, 26, 27, 28];
        int[] editableArmorFields = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 23, 24];
        int[] protectedArmorFields = [14, 21, 22, 25, 26];
        if (!weapons.IsEditable || !weapons.CanAppendRecords ||
            !armors.IsEditable || !armors.CanAppendRecords ||
            weapons.Columns.Where(column => column.Projection is null && editableWeaponFields.Contains(column.FieldId))
                .Any(column => !column.IsEditable) ||
            weapons.Columns.Where(column => column.Projection is null && protectedWeaponFields.Contains(column.FieldId))
                .Any(column => !column.IsEditable) ||
            armors.Columns.Where(column => column.Projection is null && editableArmorFields.Contains(column.FieldId))
                .Any(column => !column.IsEditable) ||
            armors.Columns.Where(column => column.Projection is null && protectedArmorFields.Contains(column.FieldId))
                .Any(column => !column.IsEditable) ||
            !weapons.Columns.Any(column => column.Title == "Science" && column.Kind == "Byte") ||
            !weapons.Columns.Any(column => column.Title == "Awareness" && column.Kind == "Byte") ||
            armors.Columns.Count(column => column.Projection?.Kind ==
                EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.PackedField) != 18 ||
            armors.Columns.Count(column => column.Title.EndsWith("cripple mask", StringComparison.Ordinal)) != 2)
        {
            throw new InvalidOperationException(
                "Оружие и броня должны редактировать все физические поля, разрешать клонирование строк " +
                "и сохранять расшифрованные native-проекции рядом с исходным редактором.");
        }

        var acks = tables.Single(table => table.Key == "acks.answers");
        var nonEmptyAck = acks.Records
            .SelectMany(record => acks.Columns
                .Where(column => column.Projection?.Kind ==
                    EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue)
                .Select(column => GetSpreadsheetText(record, column)))
            .FirstOrDefault(value => value?.Contains("File:", StringComparison.Ordinal) == true);
        if (string.IsNullOrWhiteSpace(nonEmptyAck) || nonEmptyAck.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Вложенные реплики должны показывать имена файлов и параметры, а не raw Hex.");
        }

        viewModel.SelectTableCommand.Execute(acks);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.ShowAllColumns ||
            viewModel.VisibleSpreadsheetColumns.Count >= acks.Columns.Count ||
            viewModel.VisibleSpreadsheetColumns.Any(column =>
                column.Kind == "RawHex" || column.Title.EndsWith(" · raw", StringComparison.Ordinal)) ||
            !viewModel.VisibleSpreadsheetColumns.Any(column =>
                column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue) ||
            recordsGrid.Columns.Count != viewModel.VisibleSpreadsheetColumns.Count +
                (viewModel.HasTextsDocument ? 5 : 2))
        {
            throw new InvalidOperationException(
                "acks.db/Ответы и команды должен по умолчанию показывать File/параметры без соседних raw-hex столбцов.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (!viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex") ||
            viewModel.VisibleSpreadsheetColumns.Count != acks.Columns.Count)
        {
            throw new InvalidOperationException(
                "Технический режим acks.db должен по запросу вернуть исходные raw-редакторы без потери колонок.");
        }

        var prints = tables.Single(table => table.Key == "prints.blood");
        if (prints.Columns.Count(column => column.Projection is null) != prints.Records[0].FieldCount ||
            prints.Columns.Where(column => column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.NestedField)
                .Any(column => GetSpreadsheetText(prints.Records[0], column) == SpreadsheetProjectionValue.UnavailableText))
        {
            throw new InvalidOperationException("Вложенные параметры следов должны раскрываться в отдельные числовые колонки.");
        }

        string[] materialHeaders =
        [
            "Имя", "Тип", "Код", "ID", "Цена", "Вес", "Мана", "Слоты", "Прочность", "Навык", "Урон",
            "Поглощение", "Колющий", "Рубящий", "Дробящий", "Термический", "Химический", "Электрический", "Общий",
            "Маска травм", "Магазины", "Магазин 1", "Магазин 2", "Магазин 3", "Магазин 4", "Магазин 5",
        ];
        if (!materials.Columns.Select(column => column.Title).SequenceEqual(materialHeaders, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Заголовки Материалов должны совпадать с проверенной legacy-схемой и порядком полей.");
        }

        string[] thinValues =
        [
            "thin", "Leather", "le", "0", "4", "1.2", "11", "6", "32.94", "1", "0",
            "1.14, 1.2, 1.23, 1.14, 1.2, 1.35, 1.2",
            "1.14", "1.2", "1.23", "1.14", "1.2", "1.35", "1.2", "0", "1", "1", "0", "0", "0", "0",
        ];
        var thin = materials.Records[0];
        if (!materials.Columns.Select(column => GetSpreadsheetText(thin, column))
                .SequenceEqual(thinValues, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Строка thin должна показывать строки, числа, семь коэффициентов и пять shop-битов без Hex.");
        }

        var lastMaterial = materials.Records[^1];
        if (GetSpreadsheetText(lastMaterial, materials.Columns[0]) != "black panter hide" ||
            GetSpreadsheetText(lastMaterial, materials.Columns[2]) != "pt" ||
            GetSpreadsheetText(lastMaterial, materials.Columns[3]) != "49" ||
            materials.Columns.Where(column => column.Title.StartsWith("Магазин ", StringComparison.Ordinal))
                .Any(column => GetSpreadsheetText(lastMaterial, column) != "0"))
        {
            throw new InvalidOperationException(
                "Последняя строка Материалов должна полностью декодироваться, включая пять shop-битов.");
        }

        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        string[] readableMaterialHeaders =
        [
            "Имя", "Тип", "Код", "ID", "Цена", "Вес", "Мана", "Слоты", "Прочность", "Навык", "Урон",
            "Колющий", "Рубящий", "Дробящий", "Термический", "Химический", "Электрический", "Общий",
            "Маска травм", "Магазин 1", "Магазин 2", "Магазин 3", "Магазин 4", "Магазин 5",
        ];
        if (recordsGrid.Columns.Count != viewModel.VisibleSpreadsheetColumns.Count +
                (viewModel.HasTextsDocument ? 5 : 2) ||
            recordsGrid.FrozenColumnCount != (viewModel.HasTextsDocument ? 5 : 3) ||
            !viewModel.VisibleSpreadsheetColumns.Select(column => column.Title)
                .SequenceEqual(readableMaterialHeaders, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "DataGrid Материалов должен скрыть два агрегированных исходных поля и оставить их понятные коэффициенты и биты.");
        }

        var materialGridColumns = recordsGrid.Columns
            .Where(column => column.Header is SpreadsheetColumnViewModel)
            .ToArray();
        double preferredMaterialWidth = viewModel.VisibleSpreadsheetColumns.Sum(column => column.Layout.PreferredWidth);
        if (preferredMaterialWidth >= 2200 ||
            materialGridColumns.Sum(column => column.Width.Value) >= 2200 ||
            materialGridColumns.Any(column =>
            {
                var descriptor = (SpreadsheetColumnViewModel)column.Header;
                return column.MinWidth != descriptor.Layout.MinimumWidth ||
                       column.MaxWidth != descriptor.Layout.MaximumWidth ||
                       column.CanUserSort != descriptor.IsSortable;
            }))
        {
            throw new InvalidOperationException(
                "Материалы должны использовать компактные типозависимые ширины, сохраняя resize и сортировку.");
        }

        var nameLayout = materials.Columns.Single(column => column.IsNameField).Layout;
        var floatLayout = materials.Columns.Single(column => column.Title == "Цена").Layout;
        var bitLayout = materials.Columns.Single(column => column.Title == "Магазин 1").Layout;
        if (nameLayout.PreferredWidth != 150 ||
            floatLayout.PreferredWidth != 76 ||
            bitLayout.PreferredWidth != 60 ||
            nameLayout.MinimumWidth >= nameLayout.PreferredWidth ||
            bitLayout.MaximumWidth <= bitLayout.PreferredWidth)
        {
            throw new InvalidOperationException(
                "Профиль компактности должен различать имя, дробные значения и битовые колонки.");
        }

        // The localization columns intentionally follow the database name column. Use two
        // adjacent physical database fields to verify rectangular Excel copy/paste semantics.
        SpreadsheetColumnViewModel materialType = materials.Columns.Single(column => column.Title == "Тип");
        SpreadsheetColumnViewModel materialCode = materials.Columns.Single(column => column.Title == "Код");
        DataGridColumn materialTypeGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, materialType));
        DataGridColumn materialCodeGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, materialCode));
        RecordRowViewModel[] copyRows = [materials.Records[0], materials.Records[1]];
        recordsGrid.SelectedCells.Clear();
        foreach (RecordRowViewModel copyRow in copyRows)
        {
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(copyRow, materialTypeGridColumn));
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(copyRow, materialCodeGridColumn));
        }

        recordsGrid.CurrentCell = new DataGridCellInfo(copyRows[0], materialTypeGridColumn);
        if (!ApplicationCommands.Copy.CanExecute(null, recordsGrid))
        {
            throw new InvalidOperationException("Выбранный диапазон обязан разрешать Ctrl+C.");
        }

        if (!window.TryBuildClipboardMatrix(out IReadOnlyList<IReadOnlyList<string>> copiedMatrix))
        {
            throw new InvalidOperationException("Выбранный диапазон не преобразован в табличный буфер.");
        }

        string copiedText = SpreadsheetClipboardText.Serialize(copiedMatrix);
        string expectedCopiedText = SpreadsheetClipboardText.Serialize(
        [
            [copyRows[0].GetCell(materialType.FieldId)!.EditorText, copyRows[0].GetCell(materialCode.FieldId)!.EditorText],
            [copyRows[1].GetCell(materialType.FieldId)!.EditorText, copyRows[1].GetCell(materialCode.FieldId)!.EditorText],
        ]);
        if (!string.Equals(copiedText, expectedCopiedText, StringComparison.Ordinal) ||
            copiedText.StartsWith("Имя", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ctrl+C должен помещать в буфер прямоугольные значения без лишней строки заголовков.");
        }

        RecordRowViewModel[] pasteRows = [materials.Records[2], materials.Records[3]];
        string[] originalPasteValues =
        [
            pasteRows[0].GetCell(materialType.FieldId)!.EditorText,
            pasteRows[0].GetCell(materialCode.FieldId)!.EditorText,
            pasteRows[1].GetCell(materialType.FieldId)!.EditorText,
            pasteRows[1].GetCell(materialCode.FieldId)!.EditorText,
        ];
        IReadOnlyList<IReadOnlyList<string>> externalExcelValues =
        [
            ["clipboard material alpha", "ClipboardTypeA"],
            ["clipboard material beta", "ClipboardTypeB"],
        ];
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(pasteRows[0], materialTypeGridColumn));
        recordsGrid.CurrentCell = new DataGridCellInfo(pasteRows[0], materialTypeGridColumn);
        if (!ApplicationCommands.Paste.CanExecute(null, recordsGrid))
        {
            throw new InvalidOperationException("Редактируемая клетка и табличный буфер обязаны разрешать Ctrl+V.");
        }

        if (!window.TryBuildPasteCells(externalExcelValues, out IReadOnlyList<SpreadsheetPasteCell> pasteCells, out string? pasteError) ||
            !viewModel.TryPasteSpreadsheetCells(pasteCells, out int pastedCellCount, out pasteError) ||
            pastedCellCount != 4)
        {
            throw new InvalidOperationException($"Табличная вставка не подготовлена: {pasteError}");
        }

        if (viewModel.ChangeCount != 4 ||
            pasteRows[0].GetCell(materialType.FieldId)!.EditorText != externalExcelValues[0][0] ||
            pasteRows[0].GetCell(materialCode.FieldId)!.EditorText != externalExcelValues[0][1] ||
            pasteRows[1].GetCell(materialType.FieldId)!.EditorText != externalExcelValues[1][0] ||
            pasteRows[1].GetCell(materialCode.FieldId)!.EditorText != externalExcelValues[1][1] ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                $"Ctrl+V из Excel должен изменить точный прямоугольник и создать одну пакетную Undo-операцию. " +
                $"changes={viewModel.ChangeCount}; values=" +
                $"[{pasteRows[0].GetCell(materialType.FieldId)!.EditorText}] " +
                $"[{pasteRows[0].GetCell(materialCode.FieldId)!.EditorText}] " +
                $"[{pasteRows[1].GetCell(materialType.FieldId)!.EditorText}]; " +
                $"[{pasteRows[1].GetCell(materialCode.FieldId)!.EditorText}]; " +
                $"notification={viewModel.NotificationMessage}");
        }

        viewModel.UndoCommand.Execute(null);
        string[] restoredPasteValues =
        [
            pasteRows[0].GetCell(materialType.FieldId)!.EditorText,
            pasteRows[0].GetCell(materialCode.FieldId)!.EditorText,
            pasteRows[1].GetCell(materialType.FieldId)!.EditorText,
            pasteRows[1].GetCell(materialCode.FieldId)!.EditorText,
        ];
        if (!restoredPasteValues.SequenceEqual(originalPasteValues, StringComparer.Ordinal) ||
            viewModel.ChangeCount != 0 ||
            viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Один Ctrl+Z должен полностью отменять прямоугольную вставку из Excel.");
        }

        recordsGrid.SelectedCells.Clear();
        foreach (RecordRowViewModel pasteRow in pasteRows)
        {
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(pasteRow, materialTypeGridColumn));
        }

        recordsGrid.CurrentCell = new DataGridCellInfo(pasteRows[0], materialTypeGridColumn);
        IReadOnlyList<IReadOnlyList<string>> fillMatrix = [["FilledFromOneCell"]];
        if (!window.TryBuildPasteCells(fillMatrix, out IReadOnlyList<SpreadsheetPasteCell> fillCells, out string? fillError) ||
            fillCells.Count != 2 ||
            !viewModel.TryPasteSpreadsheetCells(fillCells, out int filledCellCount, out fillError) ||
            filledCellCount != 2 ||
            pasteRows.Any(row => row.GetCell(materialType.FieldId)!.EditorText != "FilledFromOneCell"))
        {
            throw new InvalidOperationException(
                $"Одна скопированная клетка должна заполнить весь выбранный диапазон: {fillError}");
        }

        viewModel.UndoCommand.Execute(null);
        if (pasteRows[0].GetCell(materialType.FieldId)!.EditorText != originalPasteValues[0] ||
            pasteRows[1].GetCell(materialType.FieldId)!.EditorText != originalPasteValues[2] ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Один Ctrl+Z должен отменять заполнение диапазона одной скопированной клеткой.");
        }

        SpreadsheetColumnViewModel materialId = materials.Columns.Single(column => column.Title == "ID");
        SpreadsheetColumnViewModel materialPrice = materials.Columns.Single(column => column.Title == "Цена");
        DataGridColumn materialIdGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, materialId));
        string originalId = pasteRows[0].GetCell(materialId.FieldId)!.EditorText;
        string originalPrice = pasteRows[0].GetCell(materialPrice.FieldId)!.EditorText;
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(pasteRows[0], materialIdGridColumn));
        recordsGrid.CurrentCell = new DataGridCellInfo(pasteRows[0], materialIdGridColumn);
        IReadOnlyList<IReadOnlyList<string>> invalidMatrix = SpreadsheetClipboardText.Parse("49\tне число");
        if (!window.TryBuildPasteCells(invalidMatrix, out IReadOnlyList<SpreadsheetPasteCell> invalidCells, out string? invalidError) ||
            viewModel.TryPasteSpreadsheetCells(invalidCells, out _, out invalidError))
        {
            throw new InvalidOperationException(
                $"Некорректный табличный диапазон должен быть принят маршрутизатором и отклонён моделью: {invalidError}");
        }

        if (pasteRows[0].GetCell(materialId.FieldId)!.EditorText != originalId ||
            pasteRows[0].GetCell(materialPrice.FieldId)!.EditorText != originalPrice ||
            viewModel.ChangeCount != 0 ||
            !viewModel.NotificationIsError)
        {
            throw new InvalidOperationException(
                "Ошибка одной клетки должна отменять всю вставку без частично изменённых значений.");
        }

        viewModel.DismissNotificationCommand.Execute(null);

        // Inspect the concrete cell template after the virtualization behavior itself was
        // already verified above. WPF can otherwise keep a far horizontal column unrealized
        // when five leading database/text columns are frozen.
        recordsGrid.EnableColumnVirtualization = false;
        recordsGrid.UpdateLayout();
        foreach (var expectedCell in new[] { (Title: "Колющий", Value: "1.14"), (Title: "Магазин 1", Value: "1") })
        {
            DataGridColumn projectedColumn = recordsGrid.Columns.Single(column =>
                column.Header is SpreadsheetColumnViewModel descriptor && descriptor.Title == expectedCell.Title);
            recordsGrid.SelectedItem = thin;
            recordsGrid.CurrentCell = new DataGridCellInfo(thin, projectedColumn);
            recordsGrid.ScrollIntoView(thin);
            recordsGrid.ScrollIntoView(thin, projectedColumn);
            recordsGrid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            recordsGrid.ScrollIntoView(thin, projectedColumn);
            recordsGrid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            FrameworkElement? content = projectedColumn.GetCellContent(thin);
            TextBlock? text = content as TextBlock ?? FindVisualChild<TextBlock>(content);
            if (!string.Equals(text?.Text, expectedCell.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Виртуальная клетка {expectedCell.Title} должна реально отрисовать {expectedCell.Value}; " +
                    $"получено «{text?.Text ?? "<нет TextBlock>"}».");
            }
            if (!string.Equals(text?.ToolTip?.ToString(), expectedCell.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Компактная клетка {expectedCell.Title} должна раскрывать полное значение в tooltip.");
            }
        }
        recordsGrid.EnableColumnVirtualization = true;

        DataGridColumn firstMaterialColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
        recordsGrid.ScrollIntoView(thin, firstMaterialColumn);
        FindVisualChild<ScrollViewer>(recordsGrid)?.ScrollToLeftEnd();
        recordsGrid.UpdateLayout();

        int originalMaterialCount = materials.RecordCount;
        viewModel.SelectedRecord = thin;
        if (!viewModel.AppendRecordCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "В режиме редактирования выбранный материал должен разрешать добавление строки по образцу.");
        }

        viewModel.AppendRecordCommand.Execute(null);
        RecordRowViewModel addedMaterial = materials.Records[^1];
        if (materials.RecordCount != originalMaterialCount + 1 ||
            viewModel.CurrentTableRecordCount != originalMaterialCount + 1 ||
            !addedMaterial.IsAdded ||
            !addedMaterial.IsModified ||
            !ReferenceEquals(viewModel.SelectedRecord, addedMaterial) ||
            !viewModel.HasChanges ||
            viewModel.ChangeCount != 1 ||
            viewModel.Changes.Count != 1 ||
            !viewModel.Changes[0].IsRecordAddition ||
            !ReferenceEquals(viewModel.Changes[0].Record, addedMaterial) ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Добавление материала должно создать новую последнюю строку, выбрать её и записать одну операцию в журнал/Undo.");
        }

        viewModel.UndoCommand.Execute(null);
        if (materials.RecordCount != originalMaterialCount ||
            viewModel.CurrentTableRecordCount != originalMaterialCount ||
            materials.Records.Any(record => ReferenceEquals(record, addedMaterial)) ||
            viewModel.SelectedRecord is not null ||
            viewModel.HasChanges ||
            viewModel.ChangeCount != 0 ||
            viewModel.Changes.Count != 0 ||
            viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Undo добавления материала должен удалить только новую строку и полностью очистить состояние изменений.");
        }

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            if (viewModel.HasTextsDocument)
            {
                viewModel.SelectTableCommand.Execute(prototypes);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                RecordRowViewModel linkedUnit = prototypes.Records.First(record =>
                    record.CanEditPrimaryLocalization &&
                    record.LocalizationKeyText.StartsWith("UNIT ", StringComparison.OrdinalIgnoreCase));
                viewModel.SelectedRecord = linkedUnit;
                recordsGrid.SelectedItem = linkedUnit;
                recordsGrid.ScrollIntoView(linkedUnit);
            }

            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(screenshotPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
        }

        VerifyUnsavedChangesClose(sourcePath);
    }

    private static void VerifyPairedSave(string databaseSource, string textsSource)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "database.res");
        string textsPath = Path.Combine(root, "texts.res");
        File.Copy(databaseSource, databasePath);
        File.Copy(textsSource, textsPath);
        try
        {
            var service = new CoreDatabaseWorkspaceService();
            WorkspaceSnapshot workspace = service.OpenAsync(databasePath, CancellationToken.None)
                .GetAwaiter().GetResult();
            DatabaseTableViewModel materials = workspace.Databases
                .SelectMany(group => group.Tables)
                .Single(table => table.Key == "items.materials");
            RecordRowViewModel material = materials.Records.First();
            material.IsEditMode = true;
            RecordFieldViewModel price = material.GetCell(4)
                ?? throw new InvalidOperationException("У материала нет поля цены.");
            string originalPrice = price.EditorText;
            price.EditorText = originalPrice == "4" ? "5" : "4";
            LocalizationEntryViewModel text = material.RelatedTexts
                .First(entry => entry.Prefix.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase));
            string editedTitle = text.Title + " тест";
            text.Title = editedTitle;

            SaveCurrentResult saved = service.SaveCurrentAsync(false, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!File.Exists(saved.BackupPath) || !File.Exists(saved.TextsBackupPath) ||
                saved.Workspace.TextsPath is null)
            {
                throw new InvalidOperationException("Парное сохранение должно создать проверенные бэкапы обоих RES.");
            }

            RecordRowViewModel reopenedMaterial = saved.Workspace.Databases
                .SelectMany(group => group.Tables)
                .Single(table => table.Key == "items.materials")
                .Records.First();
            if (reopenedMaterial.GetCell(4)?.EditorText == originalPrice ||
                !reopenedMaterial.RelatedTexts.Any(entry => entry.Title == editedTitle))
            {
                throw new InvalidOperationException("Парное сохранение не восстановило изменения database.res и texts.res.");
            }

            string copyPath = Path.Combine(root, "database.edited.res");
            SavePairCopyResult copy = service.SaveCopyAsync(copyPath, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!File.Exists(copy.Database.TargetPath) || copy.Texts is null || !File.Exists(copy.Texts.TargetPath) ||
                Path.GetFileName(copy.Texts.TargetPath) != "texts.edited.res")
            {
                throw new InvalidOperationException("«Сохранить копию» должно создавать связанную пару database/texts.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyUnsavedChangesClose(string sourcePath)
    {
        var closeWindow = new MainWindow();
        if (closeWindow.DataContext is not MainWindowViewModel closeViewModel)
        {
            throw new InvalidOperationException("MainWindow не создал рабочую модель для close-regression.");
        }

        RunWithDispatcher(() => closeViewModel.OpenPathAsync(sourcePath));
        DatabaseTableViewModel table = closeViewModel.Databases
            .SelectMany(database => database.Tables)
            .Single(candidate => candidate.Key == "units.prototypes");
        closeViewModel.SelectTableCommand.Execute(table);
        RecordFieldViewModel changedCell = table.Records[0].GetCell(0)
            ?? throw new InvalidOperationException("Close-regression не нашёл редактируемую клетку имени.");
        changedCell.EditorText += " close-regression";
        if (!closeViewModel.HasChanges || closeViewModel.ChangeCount != 1)
        {
            throw new InvalidOperationException("Close-regression не создал одно несохранённое изменение.");
        }

        closeWindow.Show();
        closeWindow.UpdateLayout();
        bool promptSeen = false;
        Exception? promptFailure = null;
        closeWindow.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                ConfirmDialog? dialog = null;
                try
                {
                    dialog = Application.Current.Windows.OfType<ConfirmDialog>().SingleOrDefault();
                    if (dialog is null)
                    {
                        throw new InvalidOperationException(
                            "Закрытие с изменениями не открыло отложенный ConfirmDialog.");
                    }

                    promptSeen = true;
                    if (dialog.FindName("ConfirmButton") is not Button confirmButton)
                    {
                        throw new InvalidOperationException("ConfirmDialog не содержит кнопку подтверждения.");
                    }

                    confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception)
                {
                    promptFailure = exception;
                    if (dialog?.IsVisible is true)
                    {
                        dialog.DialogResult = false;
                    }
                }
            }));

        closeWindow.Close();
        closeWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (promptFailure is not null)
        {
            throw new InvalidOperationException("Не пройдена проверка безопасного закрытия.", promptFailure);
        }

        if (!promptSeen || closeWindow.IsVisible)
        {
            throw new InvalidOperationException(
                "Подтверждение закрытия без сохранения должно закрыть окно без WPF InvalidOperationException.");
        }
    }

    private static string GetSpreadsheetText(
        RecordRowViewModel record,
        SpreadsheetColumnViewModel column)
    {
        RecordFieldViewModel field = record.GetCell(column.FieldId)
            ?? throw new InvalidOperationException($"Запись не содержит поле {column.FieldId}.");
        return column.Projection is null
            ? field.EditorText
            : field.GetProjectedText(column.Projection);
    }

    private static T? FindVisualChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        if (parent is T directMatch)
        {
            return directMatch;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RunWithDispatcher(Func<Task> operation)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = operation();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class FakeWorkspaceService : IDatabaseWorkspaceService
    {
        public Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(CreateWorkspace(path));

        public Task<WorkspaceSnapshot> AttachTextsAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static WorkspaceSnapshot CreateWorkspace(string path)
        {
            var table = new DatabaseTableViewModel(
                "units",
                "Существа",
                "units.prototypes",
                "Прототипы существ",
                "units.udb",
                3,
                EvilIslands.DatabaseEditor.Core.DatabaseSupportState.Editable,
                false,
                []);
            var database = new DatabaseGroupViewModel(
                "units",
                "Существа",
                "units.udb",
                EvilIslands.DatabaseEditor.Core.DatabaseSupportState.Editable,
                [
                    table,
                    new DatabaseTableViewModel(
                        "units",
                        "Существа",
                        "units.read_only_probe",
                        "Только просмотр",
                        "units.udb",
                        9,
                        EvilIslands.DatabaseEditor.Core.DatabaseSupportState.ReadOnly,
                        false,
                        []),
                ]);
            return new WorkspaceSnapshot
            {
                SourcePath = path,
                FileLength = 1,
                Sha256 = new string('0', 64),
                Resources = [],
                Databases = [database],
                Diagnostics = [],
            };
        }

        public Task<IReadOnlyList<ValidationMessageViewModel>> ValidateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ValidationMessageViewModel>>([]);
        }

        public RecordRowViewModel AppendRecordClone(DatabaseTableViewModel table, RecordRowViewModel template) =>
            throw new NotSupportedException();

        public void RefreshRelatedTexts(RecordRowViewModel record)
        {
        }

        public void RemoveAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public void RestoreAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public IReadOnlyList<LocalizationEntryViewModel> AddMissingTexts(RecordRowViewModel record) =>
            throw new NotSupportedException();

        public void RemoveAddedText(LocalizationEntryViewModel entry) => throw new NotSupportedException();

        public void RestoreAddedText(LocalizationEntryViewModel entry) => throw new NotSupportedException();

        public Task<SaveCurrentResult> SaveCurrentAsync(
            bool allowGameInstallationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
