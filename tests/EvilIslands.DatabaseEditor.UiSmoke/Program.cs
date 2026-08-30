using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        string uiSettingsRoot = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "window-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(uiSettingsRoot);
            var preferences = new EditorPreferencesStore(Path.Combine(uiSettingsRoot, "settings.json"));
            preferences.RememberColumnWidths(
                "units.prototypes",
                new Dictionary<string, double> { ["0"] = 222 });
            preferences.RememberInterfaceScale(1.0);
            preferences.RememberSoundPaneHeight(360);
            var application = new EvilIslands.DatabaseEditor.App.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            application.InitializeComponent();
            var window = new MainWindow(preferences);
            VerifyBranding(window);
            VerifyLongTextEditorDialog();
            VerifyContrastStyles(application, window);
            VerifySpreadsheetGridDefinition(window);
            VerifySpreadsheetClipboardText();
            VerifyNavigationRequery(application);
            VerifyDatabaseTableNavigation();
            VerifyPreferencesStore();
            if (args.Length > 0)
            {
                string? screenshotPath = args.Length > 1 ? args[1] : null;
                ValidateScreenshotPath(args[0], screenshotPath);
                VerifyRealWorkspace(window, args[0], screenshotPath);
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
        finally
        {
            if (Directory.Exists(uiSettingsRoot))
            {
                Directory.Delete(uiSettingsRoot, recursive: true);
            }
        }
    }

    private static void ValidateScreenshotPath(string sourcePath, string? screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            return;
        }

        string fullSourcePath = Path.GetFullPath(sourcePath);
        string fullScreenshotPath = Path.GetFullPath(screenshotPath);
        string companionTextsPath = Path.Combine(
            Path.GetDirectoryName(fullSourcePath)!,
            "texts.res");

        if (!Path.GetExtension(fullScreenshotPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            fullScreenshotPath.Equals(fullSourcePath, StringComparison.OrdinalIgnoreCase) ||
            fullScreenshotPath.Equals(companionTextsPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Второй аргумент UiSmoke — только путь нового снимка с расширением .png; " +
                "указывать database.res или texts.res запрещено.",
                nameof(screenshotPath));
        }
    }

    private static void VerifyContrastStyles(Application application, MainWindow window)
    {
        var expected = (SolidColorBrush)application.Resources["TextBrush"];
        VerifyForegroundSetter((Style)window.Resources["PageHeading"], expected, "PageHeading");
        VerifyForegroundSetter((Style)window.Resources["MetricValue"], expected, "MetricValue");
        VerifySemanticPalette(application, window);
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

        var gridStyle = (Style)application.Resources[typeof(DataGrid)];
        Setter? deferredScrollingSetter = gridStyle.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == ScrollViewer.IsDeferredScrollingEnabledProperty);
        if (!Equals(deferredScrollingSetter?.Value, false))
        {
            throw new InvalidOperationException(
                "Общий стиль таблиц обязан обновлять содержимое во время перетаскивания полосы прокрутки.");
        }

    }

    private static void VerifyBranding(MainWindow window)
    {
        if (window.Title != "Редактор БД от Атамана — Preview" ||
            window.Icon is null ||
            window.FindName("BrandIcon") is not Image { Source: not null })
        {
            throw new InvalidOperationException(
                "Окно должно использовать название и иконку «Редактор БД от Атамана».");
        }
    }

    private static void VerifySemanticPalette(Application application, MainWindow window)
    {
        var databaseAccent = (SolidColorBrush)application.Resources["DatabaseAccentBrush"];
        var localizationAccent = (SolidColorBrush)application.Resources["LocalizationAccentBrush"];
        var technicalAccent = (SolidColorBrush)application.Resources["TechnicalAccentBrush"];
        var modifiedAccent = (SolidColorBrush)application.Resources["AccentBrush"];
        var dangerAccent = (SolidColorBrush)application.Resources["DangerBrush"];
        if (new[]
            {
                databaseAccent.Color,
                localizationAccent.Color,
                technicalAccent.Color,
                modifiedAccent.Color,
                dangerAccent.Color,
            }.Distinct().Count() != 5)
        {
            throw new InvalidOperationException(
                "Database, texts, technical, modified и error состояния должны иметь разные семантические цвета.");
        }

        var databaseSurface = (SolidColorBrush)application.Resources["DatabaseSurfaceBrush"];
        var localizationSurface = (SolidColorBrush)application.Resources["LocalizationSurfaceBrush"];
        if (ColorSpread(databaseSurface.Color) > 14 || ColorSpread(localizationSurface.Color) > 14)
        {
            throw new InvalidOperationException(
                "Фоны database.res и texts.res должны оставаться приглушёнными, без яркой синей или фиолетовой заливки.");
        }

        VerifyBackgroundSetter(
            (Style)window.Resources["DatabaseIdentityColumnHeaderStyle"],
            (SolidColorBrush)application.Resources["DatabaseHeaderBrush"],
            "DatabaseIdentityColumnHeaderStyle",
            allowDerivedShade: true);
        VerifyBackgroundSetter(
            (Style)window.Resources["LocalizationColumnHeaderStyle"],
            (SolidColorBrush)application.Resources["LocalizationHeaderBrush"],
            "LocalizationColumnHeaderStyle");
        VerifyBackgroundSetter(
            (Style)window.Resources["TechnicalColumnHeaderStyle"],
            (SolidColorBrush)application.Resources["TechnicalHeaderBrush"],
            "TechnicalColumnHeaderStyle");

        static int ColorSpread(Color color) =>
            Math.Max(color.R, Math.Max(color.G, color.B)) -
            Math.Min(color.R, Math.Min(color.G, color.B));
    }

    private static void VerifyBackgroundSetter(
        Style style,
        SolidColorBrush expected,
        string styleName,
        bool allowDerivedShade = false)
    {
        var setter = style.Setters.OfType<Setter>()
            .FirstOrDefault(candidate => candidate.Property == Control.BackgroundProperty);
        if (setter?.Value is not SolidColorBrush actual ||
            (!allowDerivedShade && actual.Color != expected.Color))
        {
            throw new InvalidOperationException(
                $"Стиль {styleName} обязан явно задавать свой семантический фон.");
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

        if (window.FindName("FormulaAddressBox") is not TextBox { IsReadOnly: true } ||
            window.FindName("FormulaEditorBox") is not TextBox formulaEditor ||
            !formulaEditor.AcceptsReturn ||
            window.FindName("FormulaApplyButton") is not Button ||
            window.FindName("FormulaCancelButton") is not Button ||
            window.FindName("FormulaErrorText") is not TextBlock)
        {
            throw new InvalidOperationException(
                "Экран основной таблицы обязан содержать строку формул с адресом, полным значением, применением и отменой.");
        }

        if (window.FindName("TextsFormulaAddressBox") is not TextBox { IsReadOnly: true } ||
            window.FindName("TextsFormulaEditorBox") is not TextBox textsFormulaEditor ||
            !textsFormulaEditor.AcceptsReturn || textsFormulaEditor.MinLines < 3 ||
            window.FindName("TextsFormulaApplyButton") is not Button ||
            window.FindName("TextsFormulaCancelButton") is not Button ||
            window.FindName("TextsFormulaErrorText") is not TextBlock)
        {
            throw new InvalidOperationException(
                "Полный раздел texts.res обязан иметь крупную строку формул для названий и длинных описаний.");
        }

        if (window.FindName("NewRowButton") is not Button newRowButton ||
            !Equals(newRowButton.Content, "＋ Новая строка") ||
            newRowButton.ToolTip?.ToString()?.Contains("выберите наиболее похожую строку", StringComparison.OrdinalIgnoreCase) is not true)
        {
            throw new InvalidOperationException(
                "Команда добавления должна называться «Новая строка» и объяснять безопасное клонирование выбранной строки.");
        }

        if (window.FindName("InterfaceScaleText") is not TextBlock scaleText ||
            window.FindName("ScaleDownButton") is not Button ||
            window.FindName("ScaleUpButton") is not Button ||
            Math.Abs(window.CurrentInterfaceScale - 1.0) > 0.001)
        {
            throw new InvalidOperationException("Окно обязано иметь сохраняемый пользовательский масштаб интерфейса.");
        }

        window.SetInterfaceScale(1.25, persist: false);
        if (Math.Abs(window.CurrentInterfaceScale - 1.25) > 0.001 || scaleText.Text != "125%")
        {
            throw new InvalidOperationException("Переключатель масштаба должен применять 125% ко всему интерфейсу.");
        }

        window.SetInterfaceScale(1.0, persist: false);
        if (MainWindow.ChooseAutomaticInterfaceScale(1.0, 2560, 1400) != 1.25 ||
            MainWindow.ChooseAutomaticInterfaceScale(2.0, 3840, 2080) != 1.0 ||
            MainWindow.ChooseAutomaticInterfaceScale(1.0, 1920, 1040) != 1.0)
        {
            throw new InvalidOperationException(
                "Автомасштаб должен увеличивать интерфейс на 2K/100%, но не удваивать уже масштабированный 4K или Full HD.");
        }

        if (grid.AutoGenerateColumns || grid.IsReadOnly ||
            !grid.EnableRowVirtualization || !grid.EnableColumnVirtualization ||
            ScrollViewer.GetIsDeferredScrollingEnabled(grid) ||
            !grid.CanUserResizeColumns || grid.CanUserReorderColumns ||
            grid.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader ||
            grid.SelectionMode != DataGridSelectionMode.Extended ||
            grid.ClipboardCopyMode != DataGridClipboardCopyMode.None ||
            !grid.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == ApplicationCommands.Copy) ||
            !grid.CommandBindings.OfType<CommandBinding>().Any(binding => binding.Command == ApplicationCommands.Paste) ||
            grid.Columns.Count != 1 ||
            !Equals(grid.Columns[0].Header, "#"))
        {
            throw new InvalidOperationException(
                "RecordsGrid должен иметь одну центрированную колонку нумерации без пустого служебного столбца, выбор клеток и обе виртуализации.");
        }

        DataGridColumn indexColumn = grid.Columns[0];
        var expectedHeaderStyle = (Style)window.Resources["IndexColumnHeaderStyle"];
        var expectedCellStyle = (Style)window.Resources["IndexColumnCellStyle"];
        var expectedTextStyle = (Style)window.Resources["RecordIndexTextStyle"];
        if (!ReferenceEquals(indexColumn.HeaderStyle, expectedHeaderStyle) ||
            !ReferenceEquals(indexColumn.CellStyle, expectedCellStyle) ||
            indexColumn is not DataGridTemplateColumn templateColumn ||
            templateColumn.CellTemplate?.LoadContent() is not TextBlock numberText ||
            !ReferenceEquals(numberText.Style, expectedTextStyle) ||
            numberText.TextAlignment != TextAlignment.Center ||
            numberText.VerticalAlignment != VerticalAlignment.Center)
        {
            throw new InvalidOperationException(
                "Номер строки должен быть выровнен по центру по горизонтали и вертикали.");
        }
    }

    private static void VerifyFrozenIdentityColumns(DataGrid grid, bool hasTextsDocument)
    {
        DataGridColumn[] ordered = grid.Columns
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        int expectedFrozenCount = hasTextsDocument ? 2 : 1;
        if (grid.FrozenColumnCount != expectedFrozenCount ||
            ordered[0].Header is not SpreadsheetColumnViewModel { IsNameField: true } ||
            (hasTextsDocument && !Equals(ordered[1].Header, "Название RU")) ||
            ordered.Take(expectedFrozenCount).Any(column => Equals(column.Header, "#") ||
                                                           Equals(column.Header, "Ключ texts.res") ||
                                                           Equals(column.Header, "Описание RU")))
        {
            throw new InvalidOperationException(
                "При горизонтальной прокрутке закреплены только «Имя» и, при подключённом texts.res, «Название RU».");
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
        store.RememberColumnWidths(
            "items.materials",
            new Dictionary<string, double>
            {
                ["0"] = 123.5,
                ["texts:title"] = 211,
                ["invalid"] = double.NaN,
            });
        store.RememberInterfaceScale(1.25);
        store.RememberSoundPaneHeight(420);
        var reopenedStore = new EditorPreferencesStore(settingsPath);
        if (!string.Equals(
                reopenedStore.GetLastDatabaseDirectory(),
                Path.GetFullPath(databaseDirectory),
                StringComparison.OrdinalIgnoreCase) ||
            reopenedStore.GetColumnWidth("items.materials", "0") != 123.5 ||
            reopenedStore.GetColumnWidth("items.materials", "texts:title") != 211 ||
            reopenedStore.GetColumnWidth("items.materials", "invalid") is not null ||
            reopenedStore.GetInterfaceScale() != 1.25 ||
            reopenedStore.GetSoundPaneHeight() != 420)
        {
            throw new InvalidOperationException(
                "Настройки должны сохранять последнюю папку, ширины колонок, масштаб интерфейса и высоту панели реплик.");
        }

        reopenedStore.RememberDatabasePath(databasePath);
        if (new EditorPreferencesStore(settingsPath).GetColumnWidth("items.materials", "0") != 123.5)
        {
            throw new InvalidOperationException(
                "Обновление последней папки не должно стирать сохранённые ширины таблиц.");
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
                .Any(column => column.IsEditable !=
                    (column.Projection!.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit)))
        {
            throw new InvalidOperationException(
                "Все 26 таблиц и их физические поля должны редактироваться; битовые проекции редактируются как ячейки 0/1, остальные расшифровки остаются только для чтения.");
        }

        if (records.Any(record => record.AreFieldsMaterialized))
        {
            throw new InvalidOperationException("Поля записей не должны материализоваться при открытии рабочей области.");
        }

        string sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        string[] companionSounds = ["JmvSFX.res", "sfx.res", "speech.res", "atamannala.res"];
        int existingCompanionSounds = companionSounds.Count(name => File.Exists(Path.Combine(sourceDirectory, name)));
        if (existingCompanionSounds > 0 &&
            (!viewModel.HasSoundArchives ||
             viewModel.SoundArchiveCount != existingCompanionSounds ||
             viewModel.SoundResourceCount == 0))
        {
            throw new InvalidOperationException(
                "Соседние sfx/speech RES должны автоматически подключаться облегчённым каталогом звуков.");
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
            viewModel.SelectedRecord = null;
            if (!viewModel.AppendRecordCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Кнопка «Новая строка» должна оставаться доступной и объяснять необходимость выбора строки-образца.");
            }

            viewModel.AppendRecordCommand.Execute(null);
            if (!viewModel.NotificationIsError ||
                viewModel.NotificationMessage?.Contains("Сначала выберите", StringComparison.Ordinal) is not true)
            {
                throw new InvalidOperationException(
                    "Нажатие «Новая строка» без выбора должно показать понятную инструкцию, а не ничего не делать.");
            }

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
            var recordsViewNotifications = (INotifyCollectionChanged)viewModel.RecordsView;
            var textsViewNotifications = (INotifyCollectionChanged)viewModel.TextsView;
            var recordsViewResetCount = 0;
            var textsViewResetCount = 0;
            NotifyCollectionChangedEventHandler recordsViewChanged = (_, eventArgs) =>
            {
                if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    recordsViewResetCount++;
                }
            };
            NotifyCollectionChangedEventHandler textsViewChanged = (_, eventArgs) =>
            {
                if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    textsViewResetCount++;
                }
            };
            recordsViewNotifications.CollectionChanged += recordsViewChanged;
            textsViewNotifications.CollectionChanged += textsViewChanged;
            textProbe.Title = originalTitle + " тест";
            recordsViewNotifications.CollectionChanged -= recordsViewChanged;
            textsViewNotifications.CollectionChanged -= textsViewChanged;
            if (recordsViewResetCount != 0 || textsViewResetCount != 0)
            {
                throw new InvalidOperationException(
                    "Изменение одной клетки texts.res без активного поиска не должно синхронно " +
                    "перестраивать полные таблицы database.res и texts.res. " +
                    $"recordsReset={recordsViewResetCount}; textsReset={textsViewResetCount}");
            }

            if (!viewModel.HasChanges || !textProbe.IsModified || !viewModel.UndoCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Изменение русского названия должно попадать в общий журнал и Undo.");
            }

            viewModel.UndoCommand.Execute(null);
            if (textProbe.Title != originalTitle || textProbe.IsModified || viewModel.HasChanges)
            {
                throw new InvalidOperationException("Общий Undo должен полностью вернуть изменение texts.res.");
            }

            viewModel.TextsSearchText = textProbe.ResourceName;
            textsViewResetCount = 0;
            textsViewNotifications.CollectionChanged += textsViewChanged;
            textProbe.Title = originalTitle + " фильтр";
            if (textsViewResetCount != 0 || !viewModel.HasPendingTextsViewRefresh)
            {
                throw new InvalidOperationException(
                    "При активном поиске обновление каталога texts.res должно откладываться " +
                    "до завершения фиксации клетки, а не выполняться внутри setter. " +
                    $"textsReset={textsViewResetCount}; pending={viewModel.HasPendingTextsViewRefresh}");
            }

            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            textsViewNotifications.CollectionChanged -= textsViewChanged;
            if (textsViewResetCount != 1 || viewModel.HasPendingTextsViewRefresh)
            {
                throw new InvalidOperationException(
                    "Отложенное обновление отфильтрованного texts.res должно выполниться ровно один раз. " +
                    $"textsReset={textsViewResetCount}; pending={viewModel.HasPendingTextsViewRefresh}");
            }

            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            viewModel.TextsSearchText = string.Empty;
            if (textProbe.Title != originalTitle || textProbe.IsModified || viewModel.HasChanges)
            {
                throw new InvalidOperationException(
                    "Undo отложенно обновлённой клетки texts.res должен полностью восстановить исходное состояние.");
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
        VerifyMaximizedWindowFitsWorkArea(window);
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
            recordsGrid.Columns.Count != readablePrototypeColumnCount + (viewModel.HasTextsDocument ? 4 : 1) ||
            recordsGrid.FrozenColumnCount != (viewModel.HasTextsDocument ? 2 : 1))
        {
            throw new InvalidOperationException(
                "DataGrid должен по умолчанию показать понятные значения без исходных raw-дубликатов и закрепить только имя и русское название.");
        }

        VerifyFrozenIdentityColumns(recordsGrid, viewModel.HasTextsDocument);
        VerifySpreadsheetVisualRhythm(window, recordsGrid);

        DataGridColumn prototypeNameGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
        if (prototypeNameGridColumn.Width.UnitType != DataGridLengthUnitType.Pixel ||
            Math.Abs(prototypeNameGridColumn.Width.Value - 222) > 0.1)
        {
            throw new InvalidOperationException(
                "Сохранённая ширина колонки должна применяться при новом создании окна редактора.");
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
            recordsGrid.Columns.Count != prototypes.Columns.Count + (viewModel.HasTextsDocument ? 4 : 1))
        {
            throw new InvalidOperationException(
                "Режим технических полей должен восстановить все физические столбцы и расшифровки.");
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.ShowAllColumns ||
            recordsGrid.Columns.Count != readablePrototypeColumnCount + (viewModel.HasTextsDocument ? 4 : 1) ||
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
                $"Ручной ввод Base Race должен изменить точное значение, журнал и Undo. " +
                $"value={referenceCell.EditorText}; expected={replacementReference}; modified={referenceCell.IsModified}; changes={viewModel.ChangeCount}; busy={viewModel.IsBusy}; undo={viewModel.UndoCommand.CanExecute(null)}; status={viewModel.StatusText}; notification={viewModel.NotificationMessage}");
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
            materials.StatusText != "Схема распознана" ||
            !materials.StatusToolTip.Contains("можно редактировать", StringComparison.OrdinalIgnoreCase) ||
            !materials.Columns.Where(column => column.Projection is null && column.IsEditable)
                .Select(column => column.Title)
                .SequenceEqual(editableMaterialHeaders, StringComparer.Ordinal) ||
            materials.Columns.Count(column =>
                column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit &&
                column.IsEditable) != 5 ||
            materials.Columns.Where(column =>
                    column.Projection is not null &&
                    column.Projection.Kind != EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit)
                .Any(column => column.IsEditable))
        {
            throw new InvalidOperationException(
                "Материалы должны иметь 14 редактируемых физических столбцов, " +
                $"пять редактируемых shop-битов и нередактируемые расшифровки; статус таблицы — «Схема распознана». " +
                $"columns={materials.Columns.Count}; status={materials.StatusText}; physicalEditable={materials.Columns.Count(column => column.Projection is null && column.IsEditable)}; bitEditable={materials.Columns.Count(column => column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit && column.IsEditable)}; otherProjectionEditable={materials.Columns.Count(column => column.Projection is not null && column.Projection.Kind != EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit && column.IsEditable)}");
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

        if (viewModel.HasTextsDocument)
        {
            RecordRowViewModel weaponWithVariants = weapons.Records.First(record => record.RelatedTextCount > 1);
            viewModel.SelectTableCommand.Execute(weapons);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            DataGridColumn weaponNameColumn = recordsGrid.Columns.Single(column =>
                column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
            var databaseIdentityStyle = (Style)window.Resources["DatabaseIdentityColumnHeaderStyle"];
            var localizationHeaderStyle = (Style)window.Resources["LocalizationColumnHeaderStyle"];
            if (!ReferenceEquals(weaponNameColumn.HeaderStyle, databaseIdentityStyle) ||
                recordsGrid.Columns.Where(column => column.Header is string header &&
                        header is "Ключ texts.res" or "Название RU" or "Описание RU")
                    .Any(column => !ReferenceEquals(column.HeaderStyle, localizationHeaderStyle)))
            {
                throw new InvalidOperationException(
                    "Реальная таблица должна визуально разделять идентификатор database.res и три столбца texts.res.");
            }

            recordsGrid.CurrentCell = new DataGridCellInfo(weaponWithVariants, weaponNameColumn);
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(weaponWithVariants, weaponNameColumn));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Border? localizationPanel = window.FindName("LocalizationPanel") as Border;
            DataGrid? localizationGrid = window.FindName("LocalizationGrid") as DataGrid;
            if (!ReferenceEquals(viewModel.SelectedRecord, weaponWithVariants) ||
                localizationPanel?.Visibility != Visibility.Visible ||
                localizationGrid?.Items.Count != weaponWithVariants.RelatedTextCount ||
                !weaponWithVariants.HasMultipleLocalizations ||
                !weaponWithVariants.LocalizationTitle.Contains("смотрите ниже", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Выбор клетки оружия должен выбрать всю запись и немедленно показать все варианты texts.res снизу. " +
                    $"selected={ReferenceEquals(viewModel.SelectedRecord, weaponWithVariants)}; " +
                    $"panel={localizationPanel?.Visibility}; rows={localizationGrid?.Items.Count}; " +
                    $"expected={weaponWithVariants.RelatedTextCount}; title={weaponWithVariants.LocalizationTitle}");
            }

            DatabaseTableViewModel lootItems = tables.Single(table => table.Key == "items.loot_items");
            RecordRowViewModel missingLocalization = lootItems.Records.FirstOrDefault(record =>
                    record.DisplayName.Equals("whitetigerhead01", StringComparison.OrdinalIgnoreCase) &&
                    record.MissingLocalizationCount > 0)
                ?? lootItems.Records.First(record =>
                    record.ExpectedTextKeys.Count > 1 && record.MissingLocalizationCount > 0);
            viewModel.SelectTableCommand.Execute(lootItems);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            DataGridColumn localizationKeyColumn = recordsGrid.Columns.Single(column =>
                column.Header is string header && header == "Ключ texts.res");
            recordsGrid.CurrentCell = new DataGridCellInfo(missingLocalization, localizationKeyColumn);
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(missingLocalization, localizationKeyColumn));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            var missingKeysList = window.FindName("MissingLocalizationKeysList") as ItemsControl;
            var createMissingTextsButton = window.FindName("CreateMissingTextsButton") as Button;
            if (!ReferenceEquals(viewModel.SelectedRecord, missingLocalization) ||
                localizationPanel?.Visibility != Visibility.Visible ||
                localizationGrid?.Visibility != (missingLocalization.HasRelatedTexts
                    ? Visibility.Visible
                    : Visibility.Collapsed) ||
                missingKeysList?.Items.Count != missingLocalization.MissingLocalizationCount ||
                createMissingTextsButton?.IsEnabled != true ||
                (missingLocalization.RelatedTextCount == 0 &&
                 !missingLocalization.LocalizationKeyText.StartsWith("Нет ресурсов texts.res:", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Выбор строки с отсутствующими ресурсами texts.res должен раскрывать сами ожидаемые ключи " +
                    "и явное действие их создания. " +
                    $"selected={ReferenceEquals(viewModel.SelectedRecord, missingLocalization)}; " +
                    $"panel={localizationPanel?.Visibility}; grid={localizationGrid?.Visibility}; " +
                    $"missing={missingKeysList?.Items.Count}/{missingLocalization.MissingLocalizationCount}; " +
                    $"button={createMissingTextsButton?.IsEnabled}; key={missingLocalization.LocalizationKeyText}");
            }

            if (missingLocalization.DisplayName.Equals("whitetigerhead01", StringComparison.OrdinalIgnoreCase) &&
                !new[] { "LITEM whitetigerhead01", "INSTR whitetigerhead01" }
                    .All(missingLocalization.MissingLocalizationKeys.Contains))
            {
                throw new InvalidOperationException(
                    "Для whitetigerhead01 должны быть явно показаны отсутствующие LITEM и INSTR, а не безымянные варианты.");
            }
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
                (viewModel.HasTextsDocument ? 4 : 1) +
                (viewModel.HasSoundArchives ? 2 : 0))
        {
            throw new InvalidOperationException(
                "acks.db/Ответы и команды должен по умолчанию показывать File/параметры без соседних raw-hex столбцов.");
        }

        if (viewModel.HasSoundArchives)
        {
            RecordRowViewModel ackWithSound = acks.Records.FirstOrDefault(record => record.HasResolvedSounds)
                ?? throw new InvalidOperationException("Хотя бы одна запись acks.db должна разрешаться в WAV из sfx.res.");
            viewModel.SelectedRecord = ackWithSound;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Border? soundPanel = window.FindName("SoundPanel") as Border;
            DataGrid? soundLinksGrid = window.FindName("SoundLinksGrid") as DataGrid;
            GridSplitter? detailsPaneSplitter = window.FindName("DetailsPaneSplitter") as GridSplitter;
            RowDefinition? detailsPaneRow = window.FindName("DetailsPaneRow") as RowDefinition;
            if (soundPanel?.Visibility != Visibility.Visible ||
                soundLinksGrid is null ||
                soundLinksGrid.Items.Count != ackWithSound.RelatedSounds.Count ||
                detailsPaneSplitter?.Visibility != Visibility.Visible ||
                detailsPaneRow?.Height.IsAbsolute != true ||
                ScrollViewer.GetCanContentScroll(soundLinksGrid) ||
                ScrollViewer.GetIsDeferredScrollingEnabled(soundLinksGrid) ||
                !ackWithSound.RelatedSounds.Any(sound =>
                    sound.IsResolved &&
                    sound.ResourceName.StartsWith("Acks\\", StringComparison.OrdinalIgnoreCase)) ||
                !recordsGrid.Columns.Any(column => Equals(column.Header, "Аудио")) ||
                !recordsGrid.Columns.Any(column => Equals(column.Header, "Файл в звуковом RES")))
            {
                throw new InvalidOperationException(
                    "Таблица реплик должна показывать WAV в регулируемой нижней панели с плавной пиксельной прокруткой.");
            }

            DatabaseTableViewModel briefings = tables.Single(table => table.Key == "quests.briefings");
            RecordRowViewModel briefingWithSound = briefings.Records.FirstOrDefault(record =>
                record.RelatedSounds.Any(sound =>
                    sound.IsResolved &&
                    sound.Format.Equals("MP3", StringComparison.OrdinalIgnoreCase) &&
                    sound.HasTranscript))
                ?? throw new InvalidOperationException(
                    "Хотя бы один Брифинг должен связываться с MP3 и текстом #phrase из texts.res.");
            viewModel.SelectTableCommand.Execute(briefings);
            viewModel.SelectedRecord = briefingWithSound;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Grid? briefingWorkspace = window.FindName("BriefingWorkspace") as Grid;
            Grid? genericRecordsWorkspace = window.FindName("GenericRecordsWorkspace") as Grid;
            DataGrid? briefingPhrasesGrid = window.FindName("BriefingPhrasesGrid") as DataGrid;
            BriefingPhraseViewModel editablePhrase = briefingWithSound.BriefingPhrases.First(briefingEvent =>
                briefingEvent.IsPhrase && briefingEvent.HasText && briefingEvent.HasAudio);
            viewModel.SelectedBriefingPhrase = editablePhrase;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (!briefingWithSound.RelatedSounds.Any(sound =>
                    sound.IsResolved &&
                    sound.ResourceName.Contains("\\Briefing\\", StringComparison.OrdinalIgnoreCase) &&
                    sound.HasTranscript &&
                    sound.Source.StartsWith("Реплика ", StringComparison.Ordinal)) ||
                soundLinksGrid?.Items.Count != briefingWithSound.RelatedSounds.Count ||
                !soundLinksGrid.Columns.Any(column => Equals(column.Header, "Текст реплики из texts.res")) ||
                briefingWorkspace?.Visibility != Visibility.Visible ||
                genericRecordsWorkspace?.Visibility != Visibility.Collapsed ||
                briefingPhrasesGrid is null ||
                briefingPhrasesGrid.Items.Count != briefingWithSound.BriefingEventCount ||
                !briefingWithSound.BriefingPhrases.Any(briefingEvent => briefingEvent.Kind == BriefingEventKind.Camera) ||
                !briefingWithSound.BriefingPhrases.Any(briefingEvent => briefingEvent.Kind == BriefingEventKind.Animation) ||
                !briefingWithSound.BriefingPhrases.Any(briefingEvent => briefingEvent.Kind == BriefingEventKind.Thought) ||
                viewModel.SelectedBriefingPhrase is null ||
                !viewModel.IsPhraseBriefingEditor ||
                string.IsNullOrWhiteSpace(viewModel.BriefingDraftText) ||
                !viewModel.PlayBriefingAudioCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Режим Брифингов должен показывать полную сценарную ленту, мысли, камеру, анимацию, реплики и найденные MP3.");
            }

            LocalizationEntryViewModel briefingText = briefingWithSound.RelatedTexts.Single(text =>
                text.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase));
            string originalBriefingDescription = briefingText.Description;
            int editedPhraseNumber = viewModel.SelectedBriefingPhrase.Number;
            viewModel.BriefingDraftText = viewModel.SelectedBriefingPhrase.Text + " проверка";
            if (!viewModel.ApplyBriefingPhraseCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Выбранную реплику Брифинга нельзя применить из крупного редактора.");
            }

            viewModel.ApplyBriefingPhraseCommand.Execute(null);
            if (briefingText.Description == originalBriefingDescription ||
                viewModel.SelectedBriefingPhrase?.Number != editedPhraseNumber ||
                !viewModel.SelectedBriefingPhrase.Text.EndsWith("проверка", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Крупный редактор должен менять точный #phrase и сохранять выбор реплики после обновления texts.res.");
            }

            viewModel.UndoCommand.Execute(null);
            if (briefingText.Description != originalBriefingDescription)
            {
                throw new InvalidOperationException("Undo должен полностью вернуть исходное описание BRIEFING после правки реплики.");
            }

            BriefingPhraseViewModel cameraEvent = briefingWithSound.BriefingPhrases.First(briefingEvent =>
                briefingEvent.Kind == BriefingEventKind.Camera);
            viewModel.SelectedBriefingPhrase = cameraEvent;
            viewModel.BriefingDraftArguments = cameraEvent.Arguments == "99" ? "98" : "99";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (!viewModel.IsCommandBriefingEditor ||
                !viewModel.ApplyBriefingPhraseCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Параметры события камеры должны редактироваться в правой панели.");
            }

            viewModel.ApplyBriefingPhraseCommand.Execute(null);
            if (briefingText.Description == originalBriefingDescription ||
                viewModel.SelectedBriefingPhrase?.Kind != BriefingEventKind.Camera ||
                viewModel.SelectedBriefingPhrase.Arguments != viewModel.BriefingDraftArguments)
            {
                throw new InvalidOperationException(
                    "Редактор камеры должен менять точную директиву и сохранять выбранное событие после обновления texts.res.");
            }

            viewModel.UndoCommand.Execute(null);
            if (briefingText.Description != originalBriefingDescription)
            {
                throw new InvalidOperationException("Undo должен полностью вернуть BRIEFING после правки камеры.");
            }

            viewModel.SelectedBriefingPhrase = briefingWithSound.BriefingPhrases.First(briefingEvent =>
                briefingEvent.Kind == BriefingEventKind.Camera);
            int originalEventCount = briefingWithSound.BriefingEventCount;
            int originalCameraOrdinal = viewModel.SelectedBriefingPhrase.EventOrdinal;
            if (!viewModel.AddBriefingEventCommand.CanExecute("Camera"))
            {
                throw new InvalidOperationException("После выбранного шага должно быть можно добавить новое событие камеры.");
            }

            viewModel.AddBriefingEventCommand.Execute("Camera");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            string insertedEventDescription = briefingText.Description;
            if (briefingWithSound.BriefingEventCount != originalEventCount + 1 ||
                viewModel.SelectedBriefingPhrase?.Kind != BriefingEventKind.Camera ||
                viewModel.SelectedBriefingPhrase.EventOrdinal != originalCameraOrdinal + 1 ||
                viewModel.SelectedBriefingPhrase.Arguments != "1" ||
                !viewModel.MoveBriefingEventUpCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Новое сценарное событие должно появляться сразу после выбранного и оставаться выбранным для правки.");
            }

            viewModel.MoveBriefingEventUpCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            string movedEventDescription = briefingText.Description;
            if (movedEventDescription == insertedEventDescription ||
                viewModel.SelectedBriefingPhrase?.Kind != BriefingEventKind.Camera ||
                viewModel.SelectedBriefingPhrase.EventOrdinal != originalCameraOrdinal ||
                !viewModel.DeleteBriefingEventCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Кнопка «Выше» должна переставлять целый выбранный блок и сохранять выбор события.");
            }

            viewModel.DeleteBriefingEventCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingWithSound.BriefingEventCount != originalEventCount)
            {
                throw new InvalidOperationException("Удаление должно убирать ровно одно выбранное событие.");
            }

            viewModel.UndoCommand.Execute(null);
            viewModel.UndoCommand.Execute(null);
            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingText.Description != originalBriefingDescription ||
                briefingWithSound.BriefingEventCount != originalEventCount)
            {
                throw new InvalidOperationException(
                    "Три Undo должны точно вернуть исходный BRIEFING после удаления, перемещения и добавления события.");
            }

            viewModel.SelectedBriefingPhrase = briefingWithSound.BriefingPhrases.First(briefingEvent =>
                briefingEvent.Kind == BriefingEventKind.Camera && briefingEvent.IsTimelineEvent);
            int duplicateSourceOrdinal = viewModel.SelectedBriefingPhrase.EventOrdinal;
            if (!viewModel.DuplicateBriefingEventCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Выбранное событие сценария должно разрешать безопасное дублирование.");
            }

            viewModel.DuplicateBriefingEventCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingWithSound.BriefingEventCount != originalEventCount + 1 ||
                viewModel.SelectedBriefingPhrase?.EventOrdinal != duplicateSourceOrdinal + 1 ||
                viewModel.SelectedBriefingPhrase.Kind != BriefingEventKind.Camera)
            {
                throw new InvalidOperationException(
                    "Дублированное событие должно появляться сразу после исходного и оставаться выбранным.");
            }

            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingText.Description != originalBriefingDescription ||
                briefingWithSound.BriefingEventCount != originalEventCount)
            {
                throw new InvalidOperationException("Undo должен точно отменять дублирование события Брифинга.");
            }

            string invalidCameraDescription = originalBriefingDescription +
                                              (originalBriefingDescription.EndsWith('\n') || originalBriefingDescription.EndsWith('\r')
                                                  ? string.Empty
                                                  : "\r\n") +
                                              "#CAMERA bad";
            briefingText.Description = invalidCameraDescription;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            BriefingPhraseViewModel invalidCamera = briefingWithSound.BriefingPhrases.Single(briefingEvent =>
                briefingEvent.Kind == BriefingEventKind.Camera && briefingEvent.Arguments == "bad");
            viewModel.SelectedBriefingPhrase = invalidCamera;
            viewModel.ShowOnlyBriefingProblems = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (!invalidCamera.HasProblem ||
                !invalidCamera.ProblemSummary.Contains("Некорректная камера", StringComparison.CurrentCulture) ||
                briefingPhrasesGrid.Items.Count != briefingWithSound.BriefingProblemCount ||
                viewModel.ApplyBriefingPhraseCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Проверка Брифинга должна подсвечивать неверную камеру, объяснять проблему и показывать её фильтром.");
            }

            viewModel.ShowOnlyBriefingProblems = false;
            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingText.Description != originalBriefingDescription)
            {
                throw new InvalidOperationException("Undo должен убрать тестовую ошибку сценария без изменения исходного Брифинга.");
            }

            viewModel.SelectTableCommand.Execute(acks);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        }

        viewModel.ToggleColumnModeCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var technicalHeaderStyle = (Style)window.Resources["TechnicalColumnHeaderStyle"];
        if (!recordsGrid.Columns.Any(column =>
                column.Header is SpreadsheetColumnViewModel descriptor &&
                descriptor.Kind == "RawHex" &&
                ReferenceEquals(column.HeaderStyle, technicalHeaderStyle)))
        {
            throw new InvalidOperationException(
                "Технический режим должен выделять raw-поля отдельным янтарным стилем.");
        }
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
                (viewModel.HasTextsDocument ? 4 : 1) ||
            recordsGrid.FrozenColumnCount != (viewModel.HasTextsDocument ? 2 : 1) ||
            !viewModel.VisibleSpreadsheetColumns.Select(column => column.Title)
                .SequenceEqual(readableMaterialHeaders, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "DataGrid Материалов должен скрыть два агрегированных исходных поля и оставить их понятные коэффициенты и биты.");
        }
        VerifyFrozenIdentityColumns(recordsGrid, viewModel.HasTextsDocument);

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
                $"Материалы должны использовать компактные типозависимые ширины, сохраняя resize и сортировку. " +
                $"preferred={preferredMaterialWidth}; actual={materialGridColumns.Sum(column => column.Width.Value)}; mismatches={materialGridColumns.Count(column => { var descriptor = (SpreadsheetColumnViewModel)column.Header; return column.MinWidth != descriptor.Layout.MinimumWidth || column.MaxWidth != descriptor.Layout.MaximumWidth || column.CanUserSort != descriptor.IsSortable; })}");
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

        VerifyFormulaBar(window, viewModel, recordsGrid, materials, prototypes);
        VerifyTextsFormulaBar(window, viewModel, materials);

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
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
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

        int textCountBeforeAutomaticCreation = viewModel.TextResources.Count;
        if (viewModel.HasTextsDocument)
        {
            RecordFieldViewModel identityField = addedMaterial.GetCell(addedMaterial.IdentityFieldId)
                ?? throw new InvalidOperationException("Новый материал не содержит поле уникального ключа.");
            string clonedIdentity = identityField.EditorText;
            string uniqueIdentity = $"codex_ui_material_{Guid.NewGuid():N}";
            if (viewModel.RecordsView is not IEditableCollectionView editableView ||
                !editableView.IsEditingItem ||
                !ReferenceEquals(recordsGrid.CurrentCell.Item, addedMaterial))
            {
                throw new InvalidOperationException(
                    "После добавления DataGrid должен открыть уникальный ключ новой строки для редактирования.");
            }

            TextBox identityEditor = FindVisualChild<TextBox>(firstMaterialColumn.GetCellContent(addedMaterial))
                ?? throw new InvalidOperationException("DataGrid не создал редактор ключа новой строки.");
            identityEditor.Text = uniqueIdentity;
            if (!recordsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !editableView.IsEditingItem ||
                !viewModel.HasPendingRecordsViewRefresh)
            {
                throw new InvalidOperationException(
                    "Фиксация клетки новой строки должна отложить ровно обновление представления до завершения строки.");
            }

            if (!recordsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                throw new InvalidOperationException("DataGrid не завершил редактирование добавленной строки.");
            }
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (viewModel.HasPendingRecordsViewRefresh || editableView.IsEditingItem)
            {
                throw new InvalidOperationException(
                    "После завершения строки отложенное обновление должно выполниться один раз без зацикливания Dispatcher.");
            }

            if (addedMaterial.RelatedTextCount != 2 ||
                !addedMaterial.CanEditPrimaryLocalization ||
                addedMaterial.RelatedTexts.Any(entry => !entry.IsNew) ||
                viewModel.TextResources.Count != textCountBeforeAutomaticCreation + 2 ||
                viewModel.ChangeCount != 3 ||
                identityField.EditorText != uniqueIdentity)
            {
                throw new InvalidOperationException(
                    "После ввода уникального ключа нового материала должны автоматически появиться MATERIAL и MATSHORT в texts.res.");
            }

            viewModel.UndoCommand.Execute(null);
            if (identityField.EditorText != clonedIdentity ||
                addedMaterial.RelatedTextCount != 2 ||
                addedMaterial.RelatedTexts.Any(entry => entry.IsNew) ||
                viewModel.TextResources.Count != textCountBeforeAutomaticCreation ||
                viewModel.ChangeCount != 1 ||
                materials.RecordCount != originalMaterialCount + 1)
            {
                throw new InvalidOperationException(
                    $"Один Undo должен атомарно убрать автоматические texts.res и вернуть ключ клона, но оставить саму новую строку. " +
                    $"identity={identityField.EditorText}; expected={clonedIdentity}; related={addedMaterial.RelatedTextCount}; texts={viewModel.TextResources.Count}/{textCountBeforeAutomaticCreation}; changes={viewModel.ChangeCount}; rows={materials.RecordCount}/{originalMaterialCount + 1}; notification={viewModel.NotificationMessage}");
            }
        }

        viewModel.UndoCommand.Execute(null);
        if (materials.RecordCount != originalMaterialCount ||
            viewModel.CurrentTableRecordCount != originalMaterialCount ||
            materials.Records.Any(record => ReferenceEquals(record, addedMaterial)) ||
            ReferenceEquals(viewModel.SelectedRecord, addedMaterial) ||
            viewModel.HasChanges ||
            viewModel.ChangeCount != 0 ||
            viewModel.Changes.Count != 0 ||
            viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                $"Undo добавления материала должен удалить только новую строку и полностью очистить состояние изменений. " +
                $"rows={materials.RecordCount}/{originalMaterialCount}; current={viewModel.CurrentTableRecordCount}; " +
                $"present={materials.Records.Any(record => ReferenceEquals(record, addedMaterial))}; " +
                $"selected={viewModel.SelectedRecord?.DisplayName ?? "<null>"}; changes={viewModel.ChangeCount}/{viewModel.Changes.Count}; " +
                $"hasChanges={viewModel.HasChanges}; undo={viewModel.UndoCommand.CanExecute(null)}; " +
                $"pendingRefresh={viewModel.HasPendingRecordsViewRefresh}; status={viewModel.StatusText}; notification={viewModel.NotificationMessage}");
        }

        if (viewModel.HasTextsDocument)
        {
            DatabaseTableViewModel modifiers = tables.Single(table => table.Key == "spells.modifiers");
            int originalModifierCount = modifiers.RecordCount;
            int originalTextCount = viewModel.TextResources.Count;
            viewModel.SelectTableCommand.Execute(modifiers);
            viewModel.SelectedRecord = modifiers.Records[^1];
            viewModel.AppendRecordCommand.Execute(null);
            RecordRowViewModel addedModifier = modifiers.Records[^1];
            if (addedModifier.Fields.Any(field => !field.CanEdit))
            {
                throw new InvalidOperationException(
                    "У клона модификатора каждое физическое поле должно сразу редактироваться.");
            }

            RecordFieldViewModel modifierCode = addedModifier.GetCell(addedModifier.IdentityFieldId)
                ?? throw new InvalidOperationException("Новый модификатор не содержит поле технического кода.");
            string clonedModifierCode = modifierCode.EditorText;
            HashSet<string> existingModifierCodes = modifiers.Records
                .Where(record => !ReferenceEquals(record, addedModifier))
                .Select(record => record.IdentityValue)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string uniqueModifierCode = Enumerable.Range(0, 1000)
                .Select(index => $"z{index:000}")
                .First(code => !existingModifierCodes.Contains(code));
            modifierCode.EditorText = uniqueModifierCode;
            LocalizationEntryViewModel modifierText = addedModifier.RelatedTexts.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"После уникального кода новый модификатор должен автоматически получить запись texts.res. " +
                    $"identity={addedModifier.IdentityValue}; expected=[{string.Join(", ", addedModifier.ExpectedTextKeys)}]; " +
                    $"related={addedModifier.RelatedTextCount}; texts={viewModel.TextResources.Count}/{originalTextCount}; " +
                    $"field={modifierCode.EditorText}; canEdit={modifierCode.CanEdit}; isEditable={modifierCode.IsEditable}; " +
                    $"recordEdit={addedModifier.IsEditMode}; fieldError={modifierCode.ErrorMessage}; " +
                    $"changes={viewModel.ChangeCount}; notification={viewModel.NotificationMessage}; status={viewModel.StatusText}");
            if (!modifierText.ResourceName.Equals($"MODIFIER {uniqueModifierCode}", StringComparison.OrdinalIgnoreCase) ||
                !modifierText.IsNew ||
                !addedModifier.CanEditPrimaryLocalization ||
                viewModel.TextResources.Count != originalTextCount + 1 ||
                viewModel.ChangeCount != 2)
            {
                throw new InvalidOperationException(
                    $"Клон модификатора должен автоматически создать редактируемый MODIFIER в texts.res и одну общую операцию Undo. " +
                    $"resource={modifierText.ResourceName}; new={modifierText.IsNew}; editable={addedModifier.CanEditPrimaryLocalization}; " +
                    $"texts={viewModel.TextResources.Count}/{originalTextCount}; changes={viewModel.ChangeCount}; " +
                    $"expected=[{string.Join(", ", addedModifier.ExpectedTextKeys)}]; status={viewModel.StatusText}");
            }

            viewModel.UndoCommand.Execute(null);
            if (modifierCode.EditorText != clonedModifierCode ||
                viewModel.TextResources.Count != originalTextCount ||
                viewModel.ChangeCount != 1 ||
                modifiers.RecordCount != originalModifierCount + 1)
            {
                throw new InvalidOperationException(
                    "Undo кода нового модификатора должен одновременно убрать автоматически созданный MODIFIER texts.res.");
            }

            viewModel.UndoCommand.Execute(null);
            if (modifiers.RecordCount != originalModifierCount ||
                viewModel.HasChanges ||
                viewModel.ChangeCount != 0)
            {
                throw new InvalidOperationException(
                    "Второй Undo должен удалить клон модификатора и вернуть полностью чистое состояние.");
            }
        }

        if (viewModel.HasSoundArchives)
        {
            VerifyBriefingSoundStaging(window, viewModel, tables);
        }

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            DatabaseTableViewModel? previewMissingTextsTable = tables.FirstOrDefault(table =>
                table.Key.Equals("items.loot_items", StringComparison.OrdinalIgnoreCase));
            RecordRowViewModel? previewMissingTextsRecord = previewMissingTextsTable?.Records.FirstOrDefault(record =>
                record.DisplayName.Equals("whitetigerhead01", StringComparison.OrdinalIgnoreCase) &&
                record.MissingLocalizationCount > 0);
            DatabaseTableViewModel? previewBriefings = tables.FirstOrDefault(table =>
                table.Key.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase));
            RecordRowViewModel? previewSoundRecord = previewBriefings?.Records.FirstOrDefault(record =>
                record.RelatedSounds.Any(sound => sound.IsResolved && sound.HasTranscript));
            if (previewMissingTextsTable is not null && previewMissingTextsRecord is not null)
            {
                viewModel.SelectTableCommand.Execute(previewMissingTextsTable);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                DataGridColumn localizationKeyColumn = recordsGrid.Columns.Single(column =>
                    Equals(column.Header, "Ключ texts.res"));
                recordsGrid.CurrentCell = new DataGridCellInfo(previewMissingTextsRecord, localizationKeyColumn);
                recordsGrid.SelectedCells.Clear();
                recordsGrid.SelectedCells.Add(new DataGridCellInfo(previewMissingTextsRecord, localizationKeyColumn));
                recordsGrid.ScrollIntoView(previewMissingTextsRecord, localizationKeyColumn);
            }
            else if (previewBriefings is not null && previewSoundRecord is not null)
            {
                viewModel.SelectTableCommand.Execute(previewBriefings);
                viewModel.SelectedRecord = previewSoundRecord;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            }
            else
            {
                viewModel.SelectTableCommand.Execute(materials);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                RecordRowViewModel? multilineRecord = viewModel.HasTextsDocument
                    ? materials.Records.FirstOrDefault(record =>
                        record.DisplayName.Equals("lizard blue", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (multilineRecord is not null)
                {
                    DataGridColumn descriptionColumn = recordsGrid.Columns.Single(column =>
                        Equals(column.Header, "Описание RU"));
                    recordsGrid.CurrentCell = new DataGridCellInfo(multilineRecord, descriptionColumn);
                    recordsGrid.SelectedCells.Clear();
                    recordsGrid.SelectedCells.Add(new DataGridCellInfo(multilineRecord, descriptionColumn));
                    recordsGrid.ScrollIntoView(multilineRecord, descriptionColumn);
                }
                else
                {
                    viewModel.SelectTableCommand.Execute(prototypes);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    SpreadsheetColumnViewModel longValueColumn = prototypes.Columns.Single(column =>
                        column.FieldId == 29 && column.Projection is null);
                    RecordRowViewModel longValueRecord = prototypes.Records.First(record =>
                        (record.GetCell(longValueColumn.FieldId)?.EditorText.Length ?? 0) > 80);
                    DataGridColumn longValueGridColumn = recordsGrid.Columns.Single(column =>
                        ReferenceEquals(column.Header, longValueColumn));
                    recordsGrid.CurrentCell = new DataGridCellInfo(longValueRecord, longValueGridColumn);
                    recordsGrid.SelectedCells.Clear();
                    recordsGrid.SelectedCells.Add(new DataGridCellInfo(longValueRecord, longValueGridColumn));
                    recordsGrid.ScrollIntoView(longValueRecord, longValueGridColumn);
                }
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

    private static void VerifyBriefingSoundStaging(
        MainWindow window,
        MainWindowViewModel viewModel,
        IReadOnlyList<DatabaseTableViewModel> tables)
    {
        DatabaseTableViewModel briefings = tables.Single(table => table.Key == "quests.briefings");
        RecordRowViewModel record = briefings.Records.First(candidate => candidate.BriefingPhrases.Any(phrase =>
            phrase.IsPhrase && phrase.HasText && phrase.PrimarySound?.Format.Equals("MP3", StringComparison.OrdinalIgnoreCase) == true));
        viewModel.SelectTableCommand.Execute(briefings);
        viewModel.SelectedRecord = record;
        Button? importAudioButton = window.FindName("ImportBriefingAudioButton") as Button;
        BriefingPhraseViewModel nonPhrase = record.BriefingPhrases.First(candidate => !candidate.IsPhrase);
        viewModel.SelectedBriefingPhrase = nonPhrase;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (importAudioButton?.IsEnabled != false)
        {
            throw new InvalidOperationException(
                "Импорт MP3/WAV должен быть недоступен для служебного события Брифинга.");
        }

        BriefingPhraseViewModel phrase = record.BriefingPhrases.First(candidate =>
            candidate.IsPhrase && candidate.HasText && candidate.PrimarySound?.Format.Equals("MP3", StringComparison.OrdinalIgnoreCase) == true);
        viewModel.SelectedBriefingPhrase = phrase;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (importAudioButton?.IsEnabled != true)
        {
            throw new InvalidOperationException(
                "После выбора реплики подключённого Брифинга кнопка импорта MP3/WAV должна сразу разблокироваться.");
        }

        Border? pendingSoundPanel = window.FindName("PendingSoundEditPanel") as Border;
        Button? saveSoundCopyButton = window.FindName("SavePendingSoundCopyButton") as Button;
        string audioProbePath = Path.Combine(
            Path.GetTempPath(),
            $"EI-Database-Studio-UiSmoke-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(audioProbePath, [0x49, 0x44, 0x33, 0x04, 0, 0, 0, 0, 0, 0]);
            RunWithDispatcher(() => viewModel.ImportBriefingAudioPathAsync(audioProbePath));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (importAudioButton?.IsEnabled != true ||
                pendingSoundPanel?.Visibility != Visibility.Visible ||
                saveSoundCopyButton?.IsEnabled != true ||
                viewModel.PendingSoundEdit is null ||
                !viewModel.PendingSoundEdit.IsReplacement ||
                !viewModel.PendingSoundEdit.ResourceName.Equals(
                    phrase.PrimarySound?.ResourceName,
                    StringComparison.OrdinalIgnoreCase) ||
                !viewModel.HasUnsavedWork)
            {
                throw new InvalidOperationException(
                    "Импорт MP3 должен подготовить точную замену выбранной реплики, показать предпросмотр и не менять исходный RES.");
            }

            viewModel.UndoCommand.Execute(null);
            if (viewModel.PendingSoundEdit is not null)
            {
                throw new InvalidOperationException("Undo должен отменять подготовку звука до физического Save As.");
            }

            viewModel.RedoCommand.Execute(null);
            if (viewModel.PendingSoundEdit is null)
            {
                throw new InvalidOperationException("Redo должен возвращать подготовленную замену звука.");
            }

            viewModel.CancelPendingSoundEditCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (viewModel.PendingSoundEdit is not null ||
                pendingSoundPanel?.Visibility == Visibility.Visible)
            {
                throw new InvalidOperationException(
                    "Отмена должна убрать только подготовленный звук и скрыть панель без записи архива.");
            }
        }
        finally
        {
            if (File.Exists(audioProbePath))
            {
                File.Delete(audioProbePath);
            }
        }
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
            string sourceDirectory = Path.GetDirectoryName(databaseSource)!;
            string[] soundPaths = new[] { "JmvSFX.res", "sfx.res", "speech.res", "atamannala.res" }
                .Select(name => Path.Combine(sourceDirectory, name))
                .Where(File.Exists)
                .ToArray();
            if (soundPaths.Length > 0)
            {
                workspace = service.AttachSoundsAsync(soundPaths, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (workspace.SoundArchives.Count != soundPaths.Length || workspace.SoundResourceCount == 0)
                {
                    throw new InvalidOperationException(
                        "Ручное подключение звуковых RES к staging-паре должно построить непустой каталог.");
                }
            }

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
                saved.Workspace.TextsPath is null ||
                saved.Workspace.SoundArchives.Count != soundPaths.Length)
            {
                throw new InvalidOperationException(
                    "Парное сохранение должно создать проверенные бэкапы обоих RES и сохранить ручное подключение звука.");
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
        string settingsRoot = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var closeWindow = new MainWindow(new EditorPreferencesStore(Path.Combine(settingsRoot, "settings.json")));
        try
        {
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
        finally
        {
            if (closeWindow.IsVisible)
            {
                closeWindow.Hide();
            }

            Directory.Delete(settingsRoot, recursive: true);
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

    private static void VerifySpreadsheetVisualRhythm(MainWindow window, DataGrid recordsGrid)
    {
        var formulaBar = (Border)window.FindName("FormulaBarBorder");
        var formulaEditor = (TextBox)window.FindName("FormulaEditorBox");
        var recordsTable = (Border)window.FindName("RecordsTableBorder");
        var localizationGrid = (DataGrid)window.FindName("LocalizationGrid");
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        double tableGap = recordsTable.TranslatePoint(new Point(0, 0), window).Y -
                          formulaBar.TranslatePoint(new Point(0, formulaBar.ActualHeight), window).Y;
        if (recordsGrid.FontSize < 14 ||
            recordsGrid.RowHeight < 36 ||
            recordsGrid.ColumnHeaderHeight < 58 ||
            localizationGrid.FontSize < 14 ||
            localizationGrid.RowHeight < 38 ||
            localizationGrid.ColumnHeaderHeight < 40 ||
            Math.Abs(tableGap) > 1)
        {
            throw new InvalidOperationException(
                "Основная и связанная таблицы должны использовать единую читаемую типографику, " +
                "а строка формул должна непосредственно соединяться с заголовками таблицы. " +
                $"records={recordsGrid.FontSize:0.#}/{recordsGrid.RowHeight:0.#}/{recordsGrid.ColumnHeaderHeight:0.#}, " +
                $"texts={localizationGrid.FontSize:0.#}/{localizationGrid.RowHeight:0.#}/{localizationGrid.ColumnHeaderHeight:0.#}, " +
                $"gap={tableGap:0.#}.");
        }

        if (formulaEditor.FontSize < 14 ||
            formulaEditor.MinLines != 2 ||
            formulaEditor.MaxLines != 2 ||
            formulaEditor.TextWrapping != TextWrapping.Wrap ||
            formulaEditor.VerticalContentAlignment != VerticalAlignment.Top ||
            formulaEditor.ActualHeight < 54)
        {
            throw new InvalidOperationException(
                "Строка формул должна быть полноценным двухстрочным редактором с переносом текста. " +
                $"font={formulaEditor.FontSize:0.#}, lines={formulaEditor.MinLines}/{formulaEditor.MaxLines}, " +
                $"wrap={formulaEditor.TextWrapping}, height={formulaEditor.ActualHeight:0.#}.");
        }

        DataGridColumnHeader spreadsheetHeader = FindVisualChildren<DataGridColumnHeader>(recordsGrid)
            .FirstOrDefault(header => header.Content is SpreadsheetColumnViewModel)
            ?? throw new InvalidOperationException("Не найден отображаемый заголовок физической колонки.");
        TextBlock[] headerText = FindVisualChildren<TextBlock>(spreadsheetHeader).ToArray();
        if (spreadsheetHeader.VerticalContentAlignment != VerticalAlignment.Center ||
            headerText.Length < 2 ||
            headerText.Max(text => text.FontSize) < 12 ||
            headerText.Min(text => text.FontSize) < 10)
        {
            throw new InvalidOperationException(
                "Заголовок поля и его техническая подпись должны быть читаемыми и центрированными по высоте.");
        }

        DataGridColumn nameColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
        RecordRowViewModel firstRecord = (RecordRowViewModel)recordsGrid.Items[0];
        recordsGrid.ScrollIntoView(firstRecord, nameColumn);
        recordsGrid.UpdateLayout();
        TextBlock cellText = FindVisualChild<TextBlock>(nameColumn.GetCellContent(firstRecord))
            ?? throw new InvalidOperationException("Не найден текст отображаемой Excel-клетки.");
        if (cellText.FontSize < 14 || cellText.VerticalAlignment != VerticalAlignment.Center)
        {
            throw new InvalidOperationException(
                "Текст основной Excel-клетки должен быть читаемым и располагаться по вертикальному центру.");
        }

        DataGridColumn numericColumn = recordsGrid.Columns.First(column =>
            column.Header is SpreadsheetColumnViewModel descriptor &&
            descriptor.Kind is "Byte" or "Int32" or "UInt32" or "Float32");
        recordsGrid.ScrollIntoView(firstRecord, numericColumn);
        recordsGrid.UpdateLayout();
        TextBlock numericText = FindVisualChild<TextBlock>(numericColumn.GetCellContent(firstRecord))
            ?? throw new InvalidOperationException("Не найден текст числовой Excel-клетки.");
        if (cellText.TextAlignment != TextAlignment.Left ||
            numericText.TextAlignment != TextAlignment.Right)
        {
            throw new InvalidOperationException(
                "Excel-подобная сетка должна располагать текст слева, а числовые значения справа.");
        }
    }

    private static void VerifyFormulaBar(
        MainWindow window,
        MainWindowViewModel viewModel,
        DataGrid recordsGrid,
        DatabaseTableViewModel materials,
        DatabaseTableViewModel prototypes)
    {
        var addressBox = (TextBox)window.FindName("FormulaAddressBox");
        var editorBox = (TextBox)window.FindName("FormulaEditorBox");
        var applyButton = (Button)window.FindName("FormulaApplyButton");
        var openEditorButton = (Button)window.FindName("FormulaOpenEditorButton");
        RecordRowViewModel record = materials.Records[0];
        SpreadsheetColumnViewModel priceColumn = materials.Columns.Single(column => column.Title == "Цена");
        DataGridColumn priceGridColumn = recordsGrid.Columns.Single(column => ReferenceEquals(column.Header, priceColumn));
        RecordFieldViewModel priceField = record.GetCell(priceColumn.FieldId)
            ?? throw new InvalidOperationException("У материала отсутствует поле цены.");
        string originalPrice = priceField.EditorText;

        recordsGrid.CurrentCell = new DataGridCellInfo(record, priceGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(record, priceGridColumn));
         window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
         if (string.IsNullOrWhiteSpace(addressBox.Text) ||
            editorBox.Text != originalPrice ||
            editorBox.IsReadOnly ||
            !applyButton.IsEnabled ||
            openEditorButton.IsEnabled ||
            AutomationProperties.GetHelpText(editorBox).Contains("Цена", StringComparison.Ordinal) is not true)
        {
            throw new InvalidOperationException(
                "Строка формул должна показать адрес, полное значение и разрешить редактирование физической ячейки.");
        }

        string replacementPrice = originalPrice == "5" ? "6" : "5";
        if (!window.TryApplyFormulaValue(replacementPrice, out string? formulaError) ||
            formulaError is not null ||
            priceField.EditorText != replacementPrice ||
            viewModel.ChangeCount != 1 ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                $"Изменение через строку формул должно использовать общий валидатор и Undo: {formulaError}");
        }

        viewModel.UndoCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (priceField.EditorText != originalPrice || viewModel.ChangeCount != 0 || editorBox.Text != originalPrice)
        {
            throw new InvalidOperationException(
                "Undo должен полностью вернуть изменение строки формул и обновить показанное значение.");
        }

        if (window.TryApplyFormulaValue("не число", out string? invalidError) ||
            string.IsNullOrWhiteSpace(invalidError) ||
            priceField.EditorText != originalPrice ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Строка формул обязана отклонять неверный тип без изменения ячейки и журнала Undo.");
        }

        if (!window.TryApplyFormulaValue(originalPrice, out string? resetError) || resetError is not null)
        {
            throw new InvalidOperationException(
                $"После ошибочного ввода исходное значение должно снова приниматься: {resetError}");
        }

        if (viewModel.HasTextsDocument && record.CanEditPrimaryLocalization)
        {
            DataGridColumn titleColumn = recordsGrid.Columns.Single(column => Equals(column.Header, "Название RU"));
            string originalTitle = record.LocalizationTitle;
            recordsGrid.CurrentCell = new DataGridCellInfo(record, titleColumn);
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(record, titleColumn));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            string changedTitle = originalTitle + " fx";
            string? titleError = null;
            if (editorBox.Text != originalTitle || editorBox.IsReadOnly ||
                 !openEditorButton.IsEnabled ||
                 !window.TryApplyFormulaValue(changedTitle, out titleError) ||
                titleError is not null || record.LocalizationTitle != changedTitle || viewModel.ChangeCount != 1)
            {
                throw new InvalidOperationException(
                    $"Inline-Название RU должно редактироваться той же строкой формул и попадать в общий Undo: {titleError}");
            }

            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (record.LocalizationTitle != originalTitle || editorBox.Text != originalTitle || viewModel.ChangeCount != 0)
            {
                throw new InvalidOperationException(
                    "Undo должен вернуть изменение texts.res, сделанное через строку формул.");
            }

            RecordRowViewModel recordWithSeveralTexts = materials.Records.First(candidate =>
                candidate.RelatedTextCount > 1 && candidate.CanEditPrimaryLocalization);
            DataGridColumn descriptionColumn = recordsGrid.Columns.Single(column => Equals(column.Header, "Описание RU"));
            string originalDescription = recordWithSeveralTexts.LocalizationDescription;
            recordsGrid.CurrentCell = new DataGridCellInfo(recordWithSeveralTexts, descriptionColumn);
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(recordWithSeveralTexts, descriptionColumn));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            window.Activate();
            if (editorBox.IsReadOnly || !window.TryFocusFormulaEditor(selectAll: true) ||
                !editorBox.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException(
                    "Щелчок/F2 по строке формул должен передавать ей реальный клавиатурный фокус для редактируемого Описания RU.");
            }

            string changedDescription = originalDescription + " fx-focus";
            editorBox.SelectedText = changedDescription;
            if (editorBox.Text != changedDescription)
            {
                throw new InvalidOperationException(
                    "Текстовое поле строки формул должно принимать пользовательский ввод после получения фокуса.");
            }

            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (recordWithSeveralTexts.LocalizationDescription != changedDescription || viewModel.ChangeCount != 1)
            {
                var formulaErrorText = (TextBlock)window.FindName("FormulaErrorText");
                throw new InvalidOperationException(
                    "Кнопка применения строки формул должна записывать выбранное Описание RU через общий журнал изменений. " +
                    $"Поле=«{editorBox.Text}», модель=«{recordWithSeveralTexts.LocalizationDescription}», " +
                    $"изменений={viewModel.ChangeCount}, ошибка=«{formulaErrorText.Text}».");
            }

            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (recordWithSeveralTexts.LocalizationDescription != originalDescription || viewModel.ChangeCount != 0)
            {
                throw new InvalidOperationException(
                    "Undo должен вернуть ввод из сфокусированной строки формул для Описания RU.");
            }

            RecordRowViewModel? multilineDescriptionRecord = materials.Records.FirstOrDefault(candidate =>
                candidate.DisplayName.Equals("lizard blue", StringComparison.OrdinalIgnoreCase));
            if (multilineDescriptionRecord is not null)
            {
                recordsGrid.CurrentCell = new DataGridCellInfo(multilineDescriptionRecord, descriptionColumn);
                recordsGrid.SelectedCells.Clear();
                recordsGrid.SelectedCells.Add(new DataGridCellInfo(multilineDescriptionRecord, descriptionColumn));
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                ScrollViewer multilineContentHost = FindVisualChild<ScrollViewer>(editorBox)
                    ?? throw new InvalidOperationException("Не найдено содержимое двухстрочной строки формул.");
                if (editorBox.Text != multilineDescriptionRecord.LocalizationDescription ||
                    editorBox.LineCount < 2 ||
                    multilineContentHost.ViewportHeight < editorBox.FontSize * 2)
                {
                    throw new InvalidOperationException(
                        "Описание RU с явным переносом должно показывать обе строки в редакторе формул. " +
                        $"lines={editorBox.LineCount}, viewport={multilineContentHost.ViewportHeight:0.##}.");
                }
            }
        }

        SpreadsheetColumnViewModel projectedColumn = materials.Columns.Single(column => column.Title == "Магазин 1");
        DataGridColumn projectedGridColumn = recordsGrid.Columns.Single(column => ReferenceEquals(column.Header, projectedColumn));
        RecordFieldViewModel projectedField = record.GetCell(projectedColumn.FieldId)
            ?? throw new InvalidOperationException("Не найдено исходное поле shop-битов.");
        string originalProjectedValue = projectedField.GetProjectedText(projectedColumn.Projection!);
        string originalProjectedRaw = projectedField.EditorText;
        string changedProjectedValue = originalProjectedValue == "1" ? "0" : "1";
        recordsGrid.CurrentCell = new DataGridCellInfo(record, projectedGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(record, projectedGridColumn));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        string? projectedError = null;
        if (!projectedColumn.IsEditable || projectedGridColumn.IsReadOnly || editorBox.IsReadOnly || !applyButton.IsEnabled ||
            !window.TryApplyFormulaValue(changedProjectedValue, out projectedError) ||
            projectedError is not null ||
            projectedField.GetProjectedText(projectedColumn.Projection!) != changedProjectedValue ||
            projectedField.EditorText == originalProjectedRaw ||
            viewModel.ChangeCount != 1)
        {
            throw new InvalidOperationException(
                $"Shop-бит должен редактироваться как обычная Excel-ячейка и синхронизировать raw-маску: {projectedError}; " +
                $"descriptorEditable={projectedColumn.IsEditable}; gridReadOnly={projectedGridColumn.IsReadOnly}; mode={viewModel.IsEditMode}; busy={viewModel.IsBusy}; tableEditable={viewModel.IsCurrentTableEditable}; fieldEditable={projectedField.CanEditProjection(projectedColumn.Projection!)}; formulaReadOnly={editorBox.IsReadOnly}; apply={applyButton.IsEnabled}; projected={projectedField.GetProjectedText(projectedColumn.Projection!)}; expected={changedProjectedValue}; rawChanged={projectedField.EditorText != originalProjectedRaw}; changes={viewModel.ChangeCount}; status={viewModel.StatusText}");
        }

        viewModel.UndoCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (projectedField.GetProjectedText(projectedColumn.Projection!) != originalProjectedValue ||
            projectedField.EditorText != originalProjectedRaw ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Undo должен одновременно восстановить shop-бит и его исходную raw-маску.");
        }

        recordsGrid.CurrentCell = new DataGridCellInfo(record, projectedGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(record, projectedGridColumn));
        IReadOnlyList<IReadOnlyList<string>> projectedPasteMatrix =
            SpreadsheetClipboardText.Parse(changedProjectedValue);
        if (!window.TryBuildPasteCells(
                projectedPasteMatrix,
                out IReadOnlyList<SpreadsheetPasteCell> projectedPasteCells,
                out projectedError) ||
            !viewModel.TryPasteSpreadsheetCells(projectedPasteCells, out int projectedPasteCount, out projectedError) ||
            projectedPasteCount != 1 ||
            projectedField.GetProjectedText(projectedColumn.Projection!) != changedProjectedValue)
        {
            throw new InvalidOperationException(
                $"Ctrl+V должен вставлять 0/1 непосредственно в выбранную shop-ячейку: {projectedError}");
        }

        viewModel.UndoCommand.Execute(null);
        if (projectedField.GetProjectedText(projectedColumn.Projection!) != originalProjectedValue ||
            projectedField.EditorText != originalProjectedRaw ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Один Undo должен полностью отменять вставку в shop-ячейку.");
        }

        viewModel.SelectTableCommand.Execute(prototypes);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        SpreadsheetColumnViewModel longValueColumn = prototypes.Columns.Single(column =>
            column.FieldId == 29 && column.Projection is null);
        RecordRowViewModel longValueRecord = prototypes.Records.First(candidate =>
            (candidate.GetCell(longValueColumn.FieldId)?.EditorText.Length ?? 0) > 80);
        RecordFieldViewModel longValueField = longValueRecord.GetCell(longValueColumn.FieldId)
            ?? throw new InvalidOperationException("У прототипа отсутствует длинное поле StringList.");
        DataGridColumn longValueGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, longValueColumn));
        recordsGrid.CurrentCell = new DataGridCellInfo(longValueRecord, longValueGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(longValueRecord, longValueGridColumn));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        ScrollViewer formulaContentHost = FindVisualChild<ScrollViewer>(editorBox)
            ?? throw new InvalidOperationException("Строка формул должна содержать ScrollViewer PART_ContentHost.");
         if (editorBox.Text != longValueField.EditorText ||
             !openEditorButton.IsEnabled ||
             editorBox.TextWrapping != TextWrapping.Wrap ||
            editorBox.LineCount < 2 ||
            formulaContentHost.ComputedHorizontalScrollBarVisibility != Visibility.Collapsed ||
            formulaContentHost.ComputedVerticalScrollBarVisibility != Visibility.Collapsed ||
            formulaContentHost.ViewportHeight < editorBox.FontSize * 2 ||
            formulaContentHost.ScrollableWidth > 0)
        {
            throw new InvalidOperationException(
                "Длинный StringList должен переноситься минимум на две видимые строки без горизонтальной полосы. " +
                $"lines={editorBox.LineCount}, wrap={editorBox.TextWrapping}, " +
                $"H={formulaContentHost.ComputedHorizontalScrollBarVisibility}, " +
                $"V={formulaContentHost.ComputedVerticalScrollBarVisibility}, " +
                $"viewport={formulaContentHost.ViewportHeight:0.##}, font={editorBox.FontSize:0.##}, " +
                $"horizontal={formulaContentHost.ScrollableWidth:0.##}.");
        }

        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static void VerifyTextsFormulaBar(
        MainWindow window,
        MainWindowViewModel viewModel,
        DatabaseTableViewModel returnTable)
    {
        if (!viewModel.HasTextsDocument)
        {
            return;
        }

        viewModel.NavigateCommand.Execute(viewModel.TextsNavigationItem);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var textsGrid = (DataGrid)window.FindName("TextsGrid");
        var addressBox = (TextBox)window.FindName("TextsFormulaAddressBox");
        var editorBox = (TextBox)window.FindName("TextsFormulaEditorBox");
        var applyButton = (Button)window.FindName("TextsFormulaApplyButton");
        var openEditorButton = (Button)window.FindName("TextsFormulaOpenEditorButton");
        DataGridColumn descriptionColumn = textsGrid.Columns.Single(column => Equals(column.Header, "Описание RU"));
        LocalizationEntryViewModel entry = viewModel.TextResources.First(candidate =>
            !string.IsNullOrWhiteSpace(candidate.Description) && !candidate.IsNew);
        string originalDescription = entry.Description;

        textsGrid.CurrentCell = new DataGridCellInfo(entry, descriptionColumn);
        textsGrid.SelectedCells.Clear();
        textsGrid.SelectedCells.Add(new DataGridCellInfo(entry, descriptionColumn));
         window.UpdateLayout();
         window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
         if (string.IsNullOrWhiteSpace(addressBox.Text) ||
            editorBox.Text != originalDescription ||
            editorBox.IsReadOnly ||
            !applyButton.IsEnabled ||
            !openEditorButton.IsEnabled ||
            editorBox.MinLines < 3 ||
             editorBox.ActualHeight < 80)
        {
            throw new InvalidOperationException(
                "Раздел texts.res должен показывать выбранное полное описание в крупной редактируемой строке формул.");
        }

        string changedDescription = originalDescription + " fx-texts";
        if (!window.TryApplyTextsFormulaValue(changedDescription, out string? error) ||
            error is not null ||
            entry.Description != changedDescription ||
            viewModel.ChangeCount != 1)
        {
            throw new InvalidOperationException(
                $"Строка формул texts.res должна записывать описание через общий Undo: {error}");
        }

        viewModel.UndoCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (entry.Description != originalDescription ||
            editorBox.Text != originalDescription ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Undo должен полностью вернуть описание, изменённое через строку формул texts.res.");
        }

        viewModel.SelectTableCommand.Execute(returnTable);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static void VerifyLongTextEditorDialog()
    {
        string? appliedText = null;
        var dialog = new LongTextEditorDialog(
            "E4",
            "Описание RU",
            "Первая строка\nВторая строка",
            value =>
            {
                appliedText = value;
                return (true, null);
            });

        if (dialog.Width < 900 ||
            dialog.Height < 620 ||
            !dialog.Statistics.Contains("2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Встроенный редактор большого текста должен открываться крупным, многострочным и показывать статистику.");
        }

        dialog.EditorText = "Строка 1\nСтрока 2\nСтрока 3";
        if (!dialog.TryCommit() ||
            !dialog.WasApplied ||
            appliedText != dialog.EditorText ||
            !dialog.Statistics.Contains("3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Встроенный редактор большого текста должен передавать полное значение общему обработчику применения.");
        }

        var rejectingDialog = new LongTextEditorDialog(
            "E4",
            "Описание RU",
            "Исходное значение",
            _ => (false, "Проверочная ошибка"));
        if (rejectingDialog.TryCommit() || rejectingDialog.WasApplied)
        {
            throw new InvalidOperationException(
                "Встроенный редактор не должен закрывать применение при ошибке общего валидатора.");
        }
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            yield break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void VerifyMaximizedWindowFitsWorkArea(MainWindow window)
    {
        window.WindowState = WindowState.Maximized;
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr monitor = MonitorFromWindow(handle, 0x00000002);
        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
        };
        if (monitor == IntPtr.Zero ||
            !GetMonitorInfo(monitor, ref monitorInfo) ||
            !GetWindowRect(handle, out NativeRect windowRect) ||
            windowRect.Left < monitorInfo.WorkArea.Left - 1 ||
            windowRect.Top < monitorInfo.WorkArea.Top - 1 ||
            windowRect.Right > monitorInfo.WorkArea.Right + 1 ||
            windowRect.Bottom > monitorInfo.WorkArea.Bottom + 1)
        {
            throw new InvalidOperationException(
                "Развёрнутое окно должно оставаться внутри рабочей области и не уходить под панель задач Windows.");
        }

        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private sealed class FakeWorkspaceService : IDatabaseWorkspaceService
    {
        public Task<WorkspaceSnapshot> OpenAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(CreateWorkspace(path));

        public Task<WorkspaceSnapshot> AttachTextsAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceSnapshot> AttachSoundsAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken) =>
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

        public void RefreshRelatedSounds(RecordRowViewModel record)
        {
        }

        public void RemoveAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public void RestoreAddedRecord(RecordRowViewModel record) => throw new NotSupportedException();

        public IReadOnlyList<LocalizationEntryViewModel> AddMissingTexts(RecordRowViewModel record) =>
            throw new NotSupportedException();

        public void RemoveAddedText(LocalizationEntryViewModel entry) => throw new NotSupportedException();

        public void RestoreAddedText(LocalizationEntryViewModel entry) => throw new NotSupportedException();

        public Task<PendingSoundEditViewModel> PrepareBriefingSoundImportAsync(
            RecordRowViewModel record,
            BriefingPhraseViewModel phrase,
            string sourcePath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SoundArchiveCopyResult> SaveSoundCopyAsync(
            PendingSoundEditViewModel edit,
            string targetPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SaveCurrentResult> SaveCurrentAsync(
            bool allowGameInstallationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
