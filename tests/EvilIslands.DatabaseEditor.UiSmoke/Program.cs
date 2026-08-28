using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            VerifyNavigationRequery(application);
            VerifyDatabaseTableNavigation();
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
        if (!Equals(selectedBackground, Brushes.Transparent))
        {
            throw new InvalidOperationException("Выбранная DataGridCell должна оставлять видимым тёмно-зелёный фон строки.");
        }

        var referenceItemStyle = (Style)window.Resources["SpreadsheetReferenceItem"];
        Trigger? selectedReferenceTrigger = referenceItemStyle.Triggers.OfType<Trigger>()
            .FirstOrDefault(trigger => trigger.Property == ComboBoxItem.IsSelectedProperty && Equals(trigger.Value, true));
        var selectedReferenceBackground = selectedReferenceTrigger?.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.BackgroundProperty)?.Value;
        var selectedReferenceForeground = selectedReferenceTrigger?.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.ForegroundProperty)?.Value;
        if (selectedReferenceBackground is not SolidColorBrush selectedBrush ||
            selectedBrush.Color != Color.FromRgb(0x16, 0x4C, 0x40) ||
            !Equals(selectedReferenceForeground, Brushes.White))
        {
            throw new InvalidOperationException(
                "Выбранный пункт ссылочного ComboBox обязан использовать контрастный тёмно-зелёный фон и белый текст.");
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
            grid.Columns.Count != 2)
        {
            throw new InvalidOperationException(
                "RecordsGrid должен иметь две фиксированные колонки, выбор клеток и обе виртуализации.");
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

        viewModel.ConfirmEditMode();
        if (!viewModel.IsEditMode)
        {
            throw new InvalidOperationException("Подтверждённый режим редактирования должен включаться в editable-таблице.");
        }

        EventHandler<CancelEventArgs> cancelEnding = (_, args) => args.Cancel = true;
        viewModel.EditModeEndingRequested += cancelEnding;
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.EditModeEndingRequested -= cancelEnding;
        if (!viewModel.IsEditMode)
        {
            throw new InvalidOperationException(
                "Отмена фиксации активной Excel-клетки должна сохранить режим редактирования.");
        }

        var readOnlyTable = group.Tables.Single(candidate => candidate.IsReadOnly);
        viewModel.SelectTableCommand.Execute(readOnlyTable);
        if (!viewModel.IsCurrentTableReadOnly || viewModel.IsEditMode || viewModel.ToggleEditModeCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Read-only таблица должна приостановить режим и отключить команду редактирования.");
        }

        viewModel.NavigateCommand.Execute(viewModel.OverviewNavigationItem);
        if (readOnlyTable.IsSelected || viewModel.ToggleEditModeCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Служебная навигация должна снять выбор таблицы и не включать редактирование.");
        }
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

        if (tables.Where(table => table.IsReadOnly).SelectMany(table => table.Columns).Any(column => column.IsEditable))
        {
            throw new InvalidOperationException("Read-only каталог не должен содержать редактируемые столбцы.");
        }

        if (records.Any(record => record.AreFieldsMaterialized))
        {
            throw new InvalidOperationException("Поля записей не должны материализоваться при открытии рабочей области.");
        }

        var prototypes = tables.Single(table => table.Key == "units.prototypes");
        var races = tables.Single(table => table.Key == "units.races");
        SpreadsheetColumnViewModel raceReferenceColumn = prototypes.Columns.Single(column => column.FieldId == 1);
        HashSet<string> raceNames = races.Records
            .Select(record => record.FieldDefinitions.First(field => field.Id == 0).Value.ToString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownRaceChoices = (raceReferenceColumn.ReferenceChoices ?? [])
            .Where(choice => choice.IsKnown)
            .Select(choice => choice.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missingRaceValues = prototypes.Records
            .Select(record => record.FieldDefinitions.First(field => field.Id == 1).Value.ToString() ?? string.Empty)
            .Where(value => value.Length > 0 && !raceNames.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!raceReferenceColumn.HasReferenceChoices ||
            !raceNames.IsSubsetOf(knownRaceChoices) ||
            missingRaceValues.Any(value =>
                raceReferenceColumn.ReferenceChoices?.Any(choice =>
                    !choice.IsKnown && choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) is not true))
        {
            throw new InvalidOperationException(
                "Base Race должна использовать общий список рас и сохранять видимыми отсутствующие legacy-ссылки.");
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
            if (!prototypeReference.HasReferenceChoices ||
                prototypeReference.ReferenceChoices?.FirstOrDefault()?.Value != string.Empty)
            {
                throw new InvalidOperationException(
                    $"{templateKey}/field-1 должен предлагать необязательную ссылку на CodeName заклинания.");
            }
        }

        viewModel.SelectTableCommand.Execute(prototypes);
        if (prototypes.Columns.Count != 62 ||
            prototypes.Columns.Count <= prototypes.Records[0].FieldCount ||
            records.Sum(record => record.MaterializedFieldCount) != 0)
        {
            throw new InvalidOperationException(
                "Построение столбцов должно использовать метаданные полей и не создавать редакторы клеток.");
        }

        window.DataContext = viewModel;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.FindName("RecordsGrid") is not DataGrid recordsGrid ||
            recordsGrid.Columns.Count != prototypes.Columns.Count + 2 ||
            recordsGrid.FrozenColumnCount != 3)
        {
            throw new InvalidOperationException(
                "DataGrid обязан создать отдельный столбец для каждого поля и заморозить начало строки.");
        }

        DataGridColumn raceGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.FieldId == 1);
        if (raceGridColumn is not DataGridComboBoxColumn)
        {
            throw new InvalidOperationException(
                "Проверенное ссылочное поле Base Race должно отрисовываться выпадающим списком.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.VisibleSpreadsheetColumns.Count != 12 || recordsGrid.Columns.Count != 14)
        {
            throw new InvalidOperationException(
                "Режим основных полей должен оставить 12 содержательных и две служебные колонки.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (recordsGrid.Columns.Count != prototypes.Columns.Count + 2)
        {
            throw new InvalidOperationException(
                "Повторное включение всех полей должно восстановить колонки без накопления дубликатов.");
        }

        var materializedAfterRender = records.Sum(record => record.MaterializedFieldCount);
        var totalFieldCount = records.Sum(record => record.FieldCount);
        if (materializedAfterRender <= 0 || materializedAfterRender >= totalFieldCount / 4)
        {
            throw new InvalidOperationException(
                $"Виртуализация должна создавать только видимые клетки: {materializedAfterRender} из {totalFieldCount}.");
        }

        viewModel.SelectedRecord = prototypes.Records[0];
        viewModel.ConfirmEditMode();
        raceGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.FieldId == 1);
        RecordRowViewModel referenceRecord = prototypes.Records.First(record =>
            raceReferenceColumn.ReferenceChoices!.Any(choice =>
                choice.IsKnown &&
                choice.Value.Length > 0 &&
                !choice.Value.Equals(record.GetCell(1)?.EditorText, StringComparison.OrdinalIgnoreCase)));
        RecordFieldViewModel referenceCell = referenceRecord.GetCell(1)
            ?? throw new InvalidOperationException("Не создана ссылочная клетка Base Race.");
        string originalReference = referenceCell.EditorText;
        ReferenceChoiceViewModel replacementReference = raceReferenceColumn.ReferenceChoices!
            .First(choice => choice.IsKnown && choice.Value.Length > 0 &&
                !choice.Value.Equals(originalReference, StringComparison.OrdinalIgnoreCase));
        recordsGrid.SelectedItem = referenceRecord;
        recordsGrid.CurrentCell = new DataGridCellInfo(referenceRecord, raceGridColumn);
        recordsGrid.ScrollIntoView(referenceRecord, raceGridColumn);
        recordsGrid.UpdateLayout();
        recordsGrid.Focus();
        if (!recordsGrid.BeginEdit())
        {
            throw new InvalidOperationException("Ссылочная Excel-клетка не перешла в режим редактирования.");
        }

        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        ComboBox referenceEditor = FindVisualChild<ComboBox>(raceGridColumn.GetCellContent(referenceRecord))
            ?? throw new InvalidOperationException("DataGrid не создал ComboBox для ссылочной клетки.");
        referenceEditor.IsDropDownOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        object? selectedReferenceItem = referenceEditor.SelectedItem;
        ComboBoxItem? selectedItem = selectedReferenceItem is null
            ? null
            : referenceEditor.ItemContainerGenerator.ContainerFromItem(selectedReferenceItem) as ComboBoxItem;
        Color? selectedBackgroundColor = (selectedItem?.Background as SolidColorBrush)?.Color;
        Color? selectedForegroundColor = (selectedItem?.Foreground as SolidColorBrush)?.Color;
        if (selectedReferenceItem is null ||
            selectedItem is null ||
            selectedBackgroundColor != Color.FromRgb(0x16, 0x4C, 0x40) ||
            selectedForegroundColor != Colors.White)
        {
            throw new InvalidOperationException(
                "Реально открытый список Base Race должен сохранять читаемый выбранный пункт тёмно-зелёным с белым текстом; " +
                $"item={selectedReferenceItem ?? "<null>"}, container={selectedItem is not null}, " +
                $"background={selectedBackgroundColor?.ToString() ?? "<null>"}, foreground={selectedForegroundColor?.ToString() ?? "<null>"}.");
        }

        referenceEditor.IsDropDownOpen = false;
        referenceEditor.SelectedValue = replacementReference.Value;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        recordsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        recordsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (referenceCell.EditorText != replacementReference.Value ||
            !referenceCell.IsModified ||
            viewModel.ChangeCount != 1 ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Выбор Base Race из списка должен изменить точное значение, журнал и Undo.");
        }

        viewModel.UndoCommand.Execute(null);
        if (referenceCell.EditorText != originalReference || referenceCell.IsModified || viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException("Undo должен полностью вернуть ссылочную клетку Base Race.");
        }

        viewModel.SelectTableCommand.Execute(races);
        RecordRowViewModel renamedRace = races.Records[0];
        RecordFieldViewModel renamedRaceCell = renamedRace.GetCell(0)
            ?? throw new InvalidOperationException("Не создано ключевое поле расы.");
        string originalRaceName = renamedRaceCell.EditorText;
        string temporaryRaceName = originalRaceName + " grid-reference-smoke";
        renamedRaceCell.EditorText = temporaryRaceName;
        if (!raceReferenceColumn.ReferenceChoices!.Any(choice =>
                choice.IsKnown && choice.Value.Equals(temporaryRaceName, StringComparison.Ordinal)) ||
            raceReferenceColumn.ReferenceChoices!.Any(choice =>
                choice.IsKnown && choice.Value.Equals(originalRaceName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Переименование целевой расы должно немедленно обновить общий список ссылок.");
        }

        viewModel.UndoCommand.Execute(null);
        if (!raceReferenceColumn.ReferenceChoices!.Any(choice =>
                choice.IsKnown && choice.Value.Equals(originalRaceName, StringComparison.Ordinal)) ||
            raceReferenceColumn.ReferenceChoices!.Any(choice =>
                choice.IsKnown && choice.Value.Equals(temporaryRaceName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Undo переименования расы должен восстановить исходный список ссылок.");
        }

        viewModel.SelectTableCommand.Execute(prototypes);

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
        viewModel.ToggleEditModeCommand.Execute(null);
        if (viewModel.IsEditMode || viewModel.ChangeCount != 1 || !prototypes.Records[0].IsModified ||
            editableCell.EditorText != originalText + " grid-smoke" ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "Выход из режима должен сначала зафиксировать активную Excel-клетку, журнал и Undo.");
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
            "Маска травм",
        ];
        if (materials.Columns.Count != 24 ||
            !materials.IsEditable ||
            !materials.CanAppendRecords ||
            !materials.Columns.Where(column => column.IsEditable)
                .Select(column => column.Title)
                .SequenceEqual(editableMaterialHeaders, StringComparer.Ordinal) ||
            materials.Columns.Where(column => !column.IsEditable)
                .Any(column => column.Projection is null))
        {
            throw new InvalidOperationException(
                "Материалы должны иметь 12 подтверждённых редактируемых примитивных столбцов; " +
                "семь коэффициентов поглощения и пять shop-битов остаются проекциями только для чтения.");
        }

        var rawColumns = tables
            .SelectMany(table => table.Columns.Select(column => (Table: table, Column: column)))
            .Where(item => item.Column.Kind == "Hex")
            .ToArray();
        if (tables.Sum(table => table.Columns.Count) != 578 || rawColumns.Length != 0)
        {
            throw new InvalidOperationException(
                $"Полный каталог должен дать 578 понятных колонок без Hex: legacy-поля плюс подтверждённые native-массивы навыков; " +
                $"получено колонок/Hex {tables.Sum(table => table.Columns.Count)}/{rawColumns.Length}.");
        }

        var weapons = tables.Single(table => table.Key == "items.weapons");
        var armors = tables.Single(table => table.Key == "items.armors");
        if (!weapons.Columns.Any(column => column.Title == "Science" && column.Kind == "Byte") ||
            !weapons.Columns.Any(column => column.Title == "Awareness" && column.Kind == "Byte") ||
            armors.Columns.Count(column => column.Projection?.Kind ==
                EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.PackedField) != 18 ||
            armors.Columns.Count(column => column.Title.EndsWith("cripple mask", StringComparison.Ordinal)) != 2)
        {
            throw new InvalidOperationException(
                "Известные raw-массивы оружия и смешанные структуры защиты доспехов должны раскрываться по native-layout.");
        }

        var acks = tables.Single(table => table.Key == "acks.answers");
        var nonEmptyAck = acks.Records
            .SelectMany(record => acks.Columns
                .Where(column => column.Kind == "StructuredList")
                .Select(column => record.GetCell(column.FieldId)?.EditorText))
            .FirstOrDefault(value => value?.Contains("File:", StringComparison.Ordinal) == true);
        if (string.IsNullOrWhiteSpace(nonEmptyAck) || nonEmptyAck.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Вложенные реплики должны показывать имена файлов и параметры, а не raw Hex.");
        }

        var prints = tables.Single(table => table.Key == "prints.blood");
        if (prints.Columns.Count != 7 ||
            prints.Columns.Where(column => column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.NestedField)
                .Any(column => GetSpreadsheetText(prints.Records[0], column) == SpreadsheetProjectionValue.UnavailableText))
        {
            throw new InvalidOperationException("Вложенные параметры следов должны раскрываться в отдельные числовые колонки.");
        }

        string[] materialHeaders =
        [
            "Имя", "Тип", "Код", "ID", "Цена", "Вес", "Мана", "Слоты", "Прочность", "Навык", "Урон",
            "Колющий", "Рубящий", "Дробящий", "Термический", "Химический", "Электрический", "Общий",
            "Маска травм", "Магазин 1", "Магазин 2", "Магазин 3", "Магазин 4", "Магазин 5",
        ];
        if (!materials.Columns.Select(column => column.Title).SequenceEqual(materialHeaders, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Заголовки Материалов должны совпадать с проверенной legacy-схемой и порядком полей.");
        }

        string[] thinValues =
        [
            "thin", "Leather", "le", "0", "4", "1.2", "11", "6", "32.94", "1", "0",
            "1.14", "1.2", "1.23", "1.14", "1.2", "1.35", "1.2", "0", "1", "0", "0", "0", "0",
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
            materials.Columns.Skip(19).Any(column => GetSpreadsheetText(lastMaterial, column) != "0"))
        {
            throw new InvalidOperationException(
                "Последняя строка Материалов должна полностью декодироваться, включая пять shop-битов.");
        }

        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (recordsGrid.Columns.Count != materials.Columns.Count + 2 || recordsGrid.FrozenColumnCount != 3)
        {
            throw new InvalidOperationException(
                "DataGrid Материалов обязан создать все 24 содержательных столбца без дубликатов.");
        }

        var materialGridColumns = recordsGrid.Columns
            .Where(column => column.Header is SpreadsheetColumnViewModel)
            .ToArray();
        double preferredMaterialWidth = materials.Columns.Sum(column => column.Layout.PreferredWidth);
        if (preferredMaterialWidth >= 1900 ||
            materialGridColumns.Sum(column => column.Width.Value) >= 1900 ||
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

        foreach (var expectedCell in new[] { (Title: "Колющий", Value: "1.14"), (Title: "Магазин 1", Value: "1") })
        {
            DataGridColumn projectedColumn = recordsGrid.Columns.Single(column =>
                column.Header is SpreadsheetColumnViewModel descriptor && descriptor.Title == expectedCell.Title);
            recordsGrid.SelectedItem = thin;
            recordsGrid.CurrentCell = new DataGridCellInfo(thin, projectedColumn);
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

        DataGridColumn firstMaterialColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
        recordsGrid.ScrollIntoView(thin, firstMaterialColumn);
        FindVisualChild<ScrollViewer>(recordsGrid)?.ScrollToLeftEnd();
        recordsGrid.UpdateLayout();

        int originalMaterialCount = materials.RecordCount;
        viewModel.SelectedRecord = thin;
        viewModel.ConfirmEditMode();
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
        closeViewModel.ConfirmEditMode();
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

        public void RemoveAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public void RestoreAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public Task<SaveCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
