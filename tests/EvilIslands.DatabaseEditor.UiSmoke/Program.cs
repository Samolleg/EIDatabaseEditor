using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
        if (args.Length == 2 && args[0] == "--lua-row-format") return LuaRowFormatRegression.Run(args[1]);
        if (args.Length == 3 && args[0] == "--module-header") return ModuleHeaderRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--viewer") return ViewerRegression.Run(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--workspace-maps") return WorkspaceMapsRegression.Run();
        if (args.Length == 2 && args[0] == "--audit-fixes") return AuditFixRegression.Run(args[1]);
        if (args.Length == 3 && args[0] == "--equipment-editor") return EquipmentEditorRegression.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--module-crash") return ModuleCrashRegression.Run(args[1]);
        if (args.Length == 3 && args[0] == "--module-crash-child") return ModuleCrashRegression.Child(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--module-blob-restore") return ModuleBlobRegression.Restart(args[1]);
        if (args.Length == 1 && args[0] == "--module-blobs") return ModuleBlobRegression.Run();
        if (args.Length == 1 && args[0] == "--module-save") return ModuleSaveRegression.Run();
        if (args.Length == 3 && args[0] == "--module-integration") return ModuleIntegrationRegression.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--module-transactions") return ModuleTransactionRegression.Run(args[1]);
        if (args.Length == 3 && args[0] == "--animation-edit") return AnimationEditRegression.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--mob-equipment") return MobEquipmentRegression.Run(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--res-tiles") return ResTilesRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--lua-data") return LuaDataRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--lua-columns") return LuaColumnsRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--details-splitter") return DetailsSplitterRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--spell-effects") return SpellVisualEffectsRegression.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--animation-audit") return AnimationPreviewRegression.Audit(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--animation-models") return AnimationPreviewRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--item-audit") return ItemModelPreviewAudit.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--item-models") return ItemModelPreviewRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--models") return ModelPreviewRegression.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--model-audit") return ModelPreviewAudit.Run(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "--model-gallery") return ModelPreviewAudit.Gallery(args[1], args[2], args[3], args.Length > 4 ? args[4..] : null);
        if (args.Length >= 3 && args[0] == "--texture-open")
            return TextureOpenRegression.Run(args[1..]);
        if (args.Length >= 3 && args[0] == "--nearby-textures")
            return NearbyTextureRegression.Run(args[1..]);
        if (args.Length >= 3 && args[0] == "--mmp-preview")
            return MmpPreviewRegression.Run(args[1..]);
        if (args.Length >= 3 && args[0] == "--texture-layout")
        {
            return TextureLayoutRegression.Run(args[1..]);
        }
        if (args.Length >= 3 && args[0] == "--textures")
        {
            return TextureRegression.Run(args[1..]);
        }
        if (args.Length >= 3 && args[0] == "--save-safety")
        {
            return SaveSafetyRegression.Run(args[1..]);
        }

        if (args.Length == 1 && args[0] == "--res-manager")
        {
            var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            VerifyResFileManagerDialog();
            Console.WriteLine("RES manager WPF smoke PASS");
            app.Shutdown();
            return 0;
        }

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
            VerifyUnifiedControlStyles(application);
            var recoveryStore = new RecoverySnapshotStore(Path.Combine(uiSettingsRoot, "recovery"));
            var window = new MainWindow(preferences, recoveryStore);
            VerifyBranding(window);
            VerifyLongTextEditorDialog();
            VerifyStructuredStringListEditorDialog();
            VerifyStructuredRecordListEditorDialog();
            VerifyDatabaseComparisonDialog();
            VerifyReferenceUsagesDialog();
            VerifyResFileManagerDialog();
            VerifyContrastStyles(application, window);
            VerifySpreadsheetGridDefinition(window);
            VerifySpreadsheetClipboardText();
            VerifyNavigationRequery(application);
            VerifyDatabaseTableNavigation();
            VerifyPreferencesStore();
            VerifyLayoutResetConfirmation();
            VerifyRecoverySnapshotStore();
            VerifyStartupRecoveryPrompt();
            if (args.Length > 0)
            {
                string? screenshotPath = args.Length > 1 ? args[1] : null;
                ValidateScreenshotPath(args[0], screenshotPath);
                VerifyRealWorkspace(window, args[0], screenshotPath);
            }
            VerifyDetachedDetailsWindow();
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

    private static void VerifyUnifiedControlStyles(Application application)
    {
        VerifyTemplatedStyle(application.Resources["RoundedButton"] as Style, Button.TemplateProperty, "RoundedButton");
        VerifyTemplatedStyle(application.Resources["DarkTextBox"] as Style, TextBox.TemplateProperty, "DarkTextBox");
        VerifyTemplatedStyle(application.Resources["DarkComboBox"] as Style, ComboBox.TemplateProperty, "DarkComboBox");
        VerifyTemplatedStyle(application.Resources["DarkComboBoxItem"] as Style, ComboBoxItem.TemplateProperty, "DarkComboBoxItem");
        VerifyTemplatedStyle(application.Resources["DarkListBoxItem"] as Style, ListBoxItem.TemplateProperty, "DarkListBoxItem");

        var implicitComboBoxStyle = application.Resources[typeof(ComboBox)] as Style;
        var implicitComboBoxItemStyle = application.Resources[typeof(ComboBoxItem)] as Style;
        var implicitTextBoxStyle = application.Resources[typeof(TextBox)] as Style;
        var implicitListBoxStyle = application.Resources[typeof(ListBox)] as Style;
        var implicitCheckBoxStyle = application.Resources[typeof(CheckBox)] as Style;
        var implicitExpanderStyle = application.Resources[typeof(Expander)] as Style;
        var implicitSplitterStyle = application.Resources[typeof(GridSplitter)] as Style;
        var implicitProgressStyle = application.Resources[typeof(ProgressBar)] as Style;
        if (implicitComboBoxStyle?.BasedOn != application.Resources["DarkComboBox"] ||
            implicitComboBoxItemStyle?.BasedOn != application.Resources["DarkComboBoxItem"] ||
            implicitTextBoxStyle?.BasedOn != application.Resources["DarkTextBox"] ||
            implicitListBoxStyle is null ||
            implicitCheckBoxStyle is null ||
            implicitExpanderStyle is null ||
            implicitSplitterStyle is null ||
            implicitProgressStyle is null)
        {
            throw new InvalidOperationException(
                "Кнопки, поля, выпадающие и обычные списки, флажки, раскрывающиеся панели и разделители должны использовать общую тему приложения.");
        }

        VerifyTemplatedStyle(implicitCheckBoxStyle, CheckBox.TemplateProperty, "CheckBox");
        VerifyTemplatedStyle(implicitExpanderStyle, Expander.TemplateProperty, "Expander");
        VerifyTemplatedStyle(implicitProgressStyle, ProgressBar.TemplateProperty, "ProgressBar");

        var comboBoxBackground = FindSetterValue(
            (Style)application.Resources["DarkComboBox"],
            Control.BackgroundProperty);
        var expectedInputBackground = application.Resources["InputBrush"] as SolidColorBrush;
        if (comboBoxBackground is not SolidColorBrush actualInputBackground ||
            expectedInputBackground is null ||
            actualInputBackground.Color != expectedInputBackground.Color)
        {
            throw new InvalidOperationException(
                "Выпадающие списки должны использовать общий тёмный фон поля, а не системный светлый шаблон Windows.");
        }

        var dataGridStyle = application.Resources[typeof(DataGrid)] as Style;
        var dataGridCellStyle = application.Resources[typeof(DataGridCell)] as Style;
        var dataGridHeaderStyle = application.Resources[typeof(DataGridColumnHeader)] as Style;
        if (!Equals(FindSetterValue(dataGridStyle, DataGrid.RowHeightProperty), 38d) ||
            !Equals(FindSetterValue(dataGridStyle, DataGrid.ColumnHeaderHeightProperty), 42d) ||
            !Equals(FindSetterValue(dataGridStyle, ScrollViewer.IsDeferredScrollingEnabledProperty), false))
        {
            throw new InvalidOperationException(
                "Все таблицы должны наследовать единые размеры строк и заголовков и живую прокрутку.");
        }

        if (!Equals(FindSetterValue(dataGridCellStyle, TextBlock.TextAlignmentProperty), TextAlignment.Center) ||
            !Equals(FindSetterValue(dataGridCellStyle, Control.VerticalContentAlignmentProperty), VerticalAlignment.Center) ||
            !Equals(FindSetterValue(dataGridHeaderStyle, Control.HorizontalContentAlignmentProperty), HorizontalAlignment.Center) ||
            !Equals(FindSetterValue(dataGridHeaderStyle, Control.VerticalContentAlignmentProperty), VerticalAlignment.Center))
        {
            throw new InvalidOperationException(
                "Все таблицы должны центрировать содержимое ячеек и заголовков по горизонтали и вертикали.");
        }

        static void VerifyTemplatedStyle(Style? style, DependencyProperty property, string name)
        {
            if (FindSetterValue(style, property) is not ControlTemplate)
            {
                throw new InvalidOperationException(
                    $"Стиль {name} обязан иметь собственный шаблон и не зависеть от системной темы Windows.");
            }
        }

        static object? FindSetterValue(Style? style, DependencyProperty property)
        {
            for (Style? current = style; current is not null; current = current.BasedOn)
            {
                Setter? setter = current.Setters.OfType<Setter>()
                    .LastOrDefault(candidate => candidate.Property == property);
                if (setter is not null)
                {
                    return setter.Value;
                }
            }

            return null;
        }
    }

    private static void VerifyBranding(MainWindow window)
    {
        if (window.Title != "Редактор БД от Атамана" ||
            window.Icon is null ||
            window.FindName("BrandIcon") is not Image { Source: not null } ||
            window.FindName("OpenResFileManagerButton") is not Button ||
            window.FindName("ManagedBackupsList") is not ItemsControl ||
            window.FindName("CompareDatabaseButton") is not Button)
        {
            throw new InvalidOperationException(
                "Окно должно использовать бренд редактора и содержать управление резервными копиями и сравнением RES.");
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

    private static void VerifyDarkComboBoxTemplate(ComboBox comboBox, string controlName)
    {
        comboBox.ApplyTemplate();
        if (comboBox.Template is null ||
            comboBox.Template.FindName("InputBorder", comboBox) is not Border inputBorder ||
            comboBox.Template.FindName("PART_Popup", comboBox) is not Popup ||
            inputBorder.Background is not SolidColorBrush inputBackground ||
            RelativeLuminance(inputBackground.Color) > 0.12)
        {
            throw new InvalidOperationException(
                $"{controlName} обязан создавать собственные тёмные поле и popup, а не системный светлый ComboBox.");
        }

        if (comboBox.Foreground is not SolidColorBrush foreground ||
            ContrastRatio(foreground.Color, inputBackground.Color) < 4.5)
        {
            throw new InvalidOperationException(
                $"{controlName} обязан сохранять читаемый контраст текста на тёмном фоне.");
        }

        static double ContrastRatio(Color first, Color second)
        {
            double light = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            double dark = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (light + 0.05) / (dark + 0.05);
        }

        static double RelativeLuminance(Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255d;
                return value <= 0.04045
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) +
                   0.7152 * Linear(color.G) +
                   0.0722 * Linear(color.B);
        }
    }

    private static void VerifySpreadsheetGridDefinition(MainWindow window)
    {
        if (window.Resources["SpreadsheetCellEditor"] is not Style spreadsheetCellEditor ||
            spreadsheetCellEditor.BasedOn != Application.Current.Resources["DarkTextBox"] ||
            FindStyleSetter(spreadsheetCellEditor, TextBox.TextAlignmentProperty) is not TextAlignment.Center ||
            window.Resources["SpreadsheetCellText"] is not Style spreadsheetCellText ||
            FindStyleSetter(spreadsheetCellText, TextBlock.TextAlignmentProperty) is not TextAlignment.Center)
        {
            throw new InvalidOperationException(
                "Показ и редактирование значения внутри таблицы должны использовать общий тёмный стиль и центрирование.");
        }

        if (MainWindow.RecoveryAutosaveInterval != TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                "Аварийное автосохранение должно выполняться раз в пять минут, как в табличных редакторах.");
        }

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

        if (window.FindName("ResetLayoutButton") is not Button resetLayoutButton ||
            !Equals(resetLayoutButton.Content, "Сбросить макет") ||
            resetLayoutButton.ToolTip?.ToString()?.Contains("После подтверждения", StringComparison.OrdinalIgnoreCase) is not true)
        {
            throw new InvalidOperationException(
                "Сброс раскладки должен быть явно назван и предупреждать об обязательном подтверждении.");
        }

        if (window.FindName("LocalizationDetachButton") is not Button { Content: "↗ Открепить" } ||
            window.FindName("SoundDetachButton") is not Button { Content: "↗ Открепить" })
        {
            throw new InvalidOperationException(
                "Нижние связанные тексты и звук должны явно открепляться в отдельное окно.");
        }

        static object? FindStyleSetter(Style style, DependencyProperty property) =>
            style.Setters.OfType<Setter>()
                .LastOrDefault(setter => setter.Property == property)?.Value;

        string[] splitterNames =
        [
            "SidebarSplitter",
            "FormulaSplitter",
            "TextsFormulaSplitter",
            "DetailsPaneSplitter",
            "LocalizationSoundSplitter",
            "BriefingScenesSplitter",
            "BriefingTimelineSplitter",
        ];
        if (splitterNames.Any(name =>
                window.FindName(name) is not GridSplitter { ShowsPreview: false }))
        {
            throw new InvalidOperationException(
                "Навигация, строки формул, вспомогательные панели и редактор брифингов должны менять размер в реальном времени.");
        }

        if (window.FindName("SidebarColumn") is not ColumnDefinition { MinWidth: 220, MaxWidth: 520 } ||
            window.FindName("FormulaPaneRow") is not RowDefinition { MinHeight: 60, MaxHeight: 240 } ||
            window.FindName("TextsFormulaPaneRow") is not RowDefinition { MinHeight: 70, MaxHeight: 300 } ||
            window.FindName("BriefingScenesColumn") is not ColumnDefinition { MinWidth: 170, MaxWidth: 520 } ||
            window.FindName("BriefingTimelineColumn") is not ColumnDefinition { MinWidth: 300, MaxWidth: 900 })
        {
            throw new InvalidOperationException(
                "Изменяемые панели должны иметь безопасные минимальные и максимальные размеры.");
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

        string[] spreadsheetGridNames =
        [
            "ResourcesGrid",
            "TextsGrid",
            "RecordsGrid",
            "LocalizationGrid",
            "SoundLinksGrid",
            "BriefingPhrasesGrid",
            "ChangesGrid",
            "ValidationGrid",
        ];
        foreach (string gridName in spreadsheetGridNames)
        {
            if (window.FindName(gridName) is not DataGrid spreadsheetGrid ||
                spreadsheetGrid.ClipboardCopyMode != DataGridClipboardCopyMode.None ||
                !spreadsheetGrid.CommandBindings.OfType<CommandBinding>()
                    .Any(binding => binding.Command == ApplicationCommands.Copy) ||
                !spreadsheetGrid.CommandBindings.OfType<CommandBinding>()
                    .Any(binding => binding.Command == ApplicationCommands.Paste))
            {
                throw new InvalidOperationException(
                    $"Таблица {gridName} обязана использовать единый Excel-совместимый Ctrl+C/Ctrl+V.");
            }
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

        DataObject data = MainWindow.CreateSpreadsheetClipboardDataObject(source);
        string[] requiredFormats =
        [
            DataFormats.UnicodeText,
            DataFormats.Text,
            DataFormats.CommaSeparatedValue,
            DataFormats.Html,
        ];
        if (requiredFormats.Any(format => !data.GetDataPresent(format, autoConvert: false)) ||
            data.GetData(DataFormats.Html, autoConvert: false) is not string html)
        {
            throw new InvalidOperationException(
                "Копирование диапазона должно публиковать текстовый, CSV и HTML-форматы Microsoft Excel.");
        }

        byte[] htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
        int startFragment = ReadClipboardHtmlOffset(html, "StartFragment:");
        int endFragment = ReadClipboardHtmlOffset(html, "EndFragment:");
        if (startFragment < 0 || endFragment <= startFragment || endFragment > htmlBytes.Length)
        {
            throw new InvalidOperationException("HTML-буфер содержит неверные границы табличного фрагмента.");
        }

        string fragment = System.Text.Encoding.UTF8.GetString(
            htmlBytes,
            startFragment,
            endFragment - startFragment);
        if (!fragment.StartsWith("<table>", StringComparison.Ordinal) ||
            !fragment.Contains("строка 1<br>строка 2", StringComparison.Ordinal) ||
            !fragment.Contains("кавычка &quot; внутри", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "HTML-буфер должен передавать Excel настоящий прямоугольник ячеек с Unicode и переносами строк.");
        }

        static int ReadClipboardHtmlOffset(string value, string label)
        {
            int start = value.IndexOf(label, StringComparison.Ordinal);
            if (start < 0)
            {
                return -1;
            }

            start += label.Length;
            int end = value.IndexOf("\r\n", start, StringComparison.Ordinal);
            return end > start && int.TryParse(value[start..end], out int offset) ? offset : -1;
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

        ApplyFakeNavigationWorkspace(viewModel);
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

    private static void ApplyFakeNavigationWorkspace(MainWindowViewModel viewModel)
    {
        var workspace = new FakeWorkspaceService().OpenAsync("C:\\staging\\database.res", CancellationToken.None).GetAwaiter().GetResult();
        typeof(MainWindowViewModel).GetMethod("ApplyWorkspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, [workspace, false]);
    }

    private static void VerifyDatabaseTableNavigation()
    {
        using var viewModel = new MainWindowViewModel(new FakeWorkspaceService());
        ApplyFakeNavigationWorkspace(viewModel);
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
        store.RememberLayout(new EditorLayoutState
        {
            SidebarWidth = 344,
            DetailsPaneHeight = 438,
            LocalizationPaneRatio = 0.62,
            FormulaBarHeight = 118,
            TextsFormulaBarHeight = 164,
            BriefingScenesWidth = 288,
            BriefingTimelineWidth = 612,
            ValidationCategoriesWidth = 294,
            ValidationDetailsWidth = 518,
            MainWindowWidth = 1730,
            MainWindowHeight = 1030,
            MainWindowMaximized = true,
            LongTextEditorWidth = 1110,
            LongTextEditorHeight = 790,
            LongTextEditorMaximized = true,
            DetailsPaneDetached = true,
            DetailsWindowWidth = 1240,
            DetailsWindowHeight = 740,
            DetailsWindowMaximized = true,
            ResManagerWindowWidth = 1580,
            ResManagerWindowHeight = 920,
            ResManagerWindowMaximized = true,
            ResManagerFoldersWidth = 286,
            ResManagerDetailsWidth = 356,
            ResManagerDetailsVisible = false,
            ResManagerChangesExpanded = true,
        });
        var reopenedStore = new EditorPreferencesStore(settingsPath);
        EditorLayoutState reopenedLayout = reopenedStore.GetLayout();
        if (!string.Equals(
                reopenedStore.GetLastDatabaseDirectory(),
                Path.GetFullPath(databaseDirectory),
                StringComparison.OrdinalIgnoreCase) ||
            reopenedStore.GetColumnWidth("items.materials", "0") != 123.5 ||
            reopenedStore.GetColumnWidth("items.materials", "texts:title") != 211 ||
            reopenedStore.GetColumnWidth("items.materials", "invalid") is not null ||
            reopenedStore.GetInterfaceScale() != 1.25 ||
            reopenedStore.GetSoundPaneHeight() != 438 ||
            reopenedLayout.SidebarWidth != 344 ||
            reopenedLayout.DetailsPaneHeight != 438 ||
            reopenedLayout.LocalizationPaneRatio != 0.62 ||
            reopenedLayout.FormulaBarHeight != 118 ||
            reopenedLayout.TextsFormulaBarHeight != 164 ||
            reopenedLayout.BriefingScenesWidth != 288 ||
            reopenedLayout.BriefingTimelineWidth != 612 ||
            reopenedLayout.ValidationCategoriesWidth != 294 ||
            reopenedLayout.ValidationDetailsWidth != 518 ||
            reopenedLayout.MainWindowWidth != 1730 ||
            reopenedLayout.MainWindowHeight != 1030 ||
            !reopenedLayout.MainWindowMaximized ||
            reopenedLayout.LongTextEditorWidth != 1110 ||
            reopenedLayout.LongTextEditorHeight != 790 ||
            !reopenedLayout.LongTextEditorMaximized ||
            !reopenedLayout.DetailsPaneDetached ||
            reopenedLayout.DetailsWindowWidth != 1240 ||
            reopenedLayout.DetailsWindowHeight != 740 ||
            !reopenedLayout.DetailsWindowMaximized ||
            reopenedLayout.ResManagerWindowWidth != 1580 ||
            reopenedLayout.ResManagerWindowHeight != 920 ||
            !reopenedLayout.ResManagerWindowMaximized ||
            reopenedLayout.ResManagerFoldersWidth != 286 ||
            reopenedLayout.ResManagerDetailsWidth != 356 ||
            reopenedLayout.ResManagerDetailsVisible ||
            !reopenedLayout.ResManagerChangesExpanded)
        {
            throw new InvalidOperationException(
                "Настройки должны сохранять последнюю папку, ширины колонок, масштаб и всю модульную раскладку редактора.");
        }

        reopenedStore.ResetLayout();
        EditorLayoutState resetLayout = new EditorPreferencesStore(settingsPath).GetLayout();
        if (resetLayout.SidebarWidth != 300 || resetLayout.DetailsPaneHeight != 360 ||
            resetLayout.FormulaBarHeight != 60 || resetLayout.BriefingTimelineWidth != 460 ||
            resetLayout.ValidationCategoriesWidth != 260 ||
            resetLayout.ValidationDetailsWidth != 430 ||
            resetLayout.MainWindowMaximized || resetLayout.LongTextEditorMaximized ||
            resetLayout.DetailsPaneDetached || resetLayout.DetailsWindowWidth != 1080 ||
            resetLayout.DetailsWindowHeight != 620 || resetLayout.DetailsWindowMaximized ||
            resetLayout.ResManagerWindowWidth != 1460 || resetLayout.ResManagerWindowHeight != 860 ||
            resetLayout.ResManagerWindowMaximized || resetLayout.ResManagerFoldersWidth != 250 ||
            resetLayout.ResManagerDetailsWidth != 310 || !resetLayout.ResManagerDetailsVisible ||
            resetLayout.ResManagerChangesExpanded)
        {
            throw new InvalidOperationException("Сброс макета должен возвращать проверенные размеры по умолчанию.");
        }

        reopenedStore.RememberDatabasePath(databasePath);
        if (new EditorPreferencesStore(settingsPath).GetColumnWidth("items.materials", "0") != 123.5)
        {
            throw new InvalidOperationException(
                "Обновление последней папки не должно стирать сохранённые ширины таблиц.");
        }

        Directory.Delete(root, recursive: true);
    }

    private static void VerifyLayoutResetConfirmation()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "layout-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        var preferences = new EditorPreferencesStore(settingsPath);
        preferences.RememberLayout(new EditorLayoutState { SidebarWidth = 344 });
        var resetWindow = new MainWindow(
            preferences,
            new RecoverySnapshotStore(Path.Combine(root, "recovery")));

        try
        {
            resetWindow.Show();
            resetWindow.UpdateLayout();
            if (resetWindow.FindName("SidebarColumn") is not ColumnDefinition sidebarColumn ||
                resetWindow.FindName("ResetLayoutButton") is not Button resetButton ||
                resetWindow.DataContext is not MainWindowViewModel viewModel ||
                Math.Abs(sidebarColumn.Width.Value - 344) > 0.01)
            {
                throw new InvalidOperationException(
                    "Layout-reset regression не смогла подготовить сохранённую пользовательскую раскладку.");
            }

            bool cancelPromptSeen = false;
            Exception? promptFailure = null;
            resetWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    ConfirmDialog? dialog = null;
                    try
                    {
                        dialog = Application.Current.Windows.OfType<ConfirmDialog>().SingleOrDefault();
                        if (dialog?.FindName("HeadingText") is not TextBlock { Text: "Сбросить раскладку?" })
                        {
                            throw new InvalidOperationException(
                                "Кнопка сброса не открыла понятное подтверждение раскладки.");
                        }

                        cancelPromptSeen = true;
                        dialog.DialogResult = false;
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

            resetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (promptFailure is not null)
            {
                throw new InvalidOperationException("Не пройдена отмена сброса раскладки.", promptFailure);
            }

            if (!cancelPromptSeen || Math.Abs(sidebarColumn.Width.Value - 344) > 0.01 ||
                new EditorPreferencesStore(settingsPath).GetLayout().SidebarWidth != 344 ||
                viewModel.NotificationMessage == "Стандартная раскладка восстановлена.")
            {
                throw new InvalidOperationException(
                    "Отмена подтверждения не должна менять или забывать пользовательскую раскладку.");
            }

            bool confirmPromptSeen = false;
            resetWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    ConfirmDialog? dialog = null;
                    try
                    {
                        dialog = Application.Current.Windows.OfType<ConfirmDialog>().SingleOrDefault();
                        if (dialog?.FindName("ConfirmButton") is not Button confirmButton)
                        {
                            throw new InvalidOperationException(
                                "Подтверждение сброса не содержит основной кнопки.");
                        }

                        confirmPromptSeen = true;
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

            resetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (promptFailure is not null)
            {
                throw new InvalidOperationException("Не пройден подтверждённый сброс раскладки.", promptFailure);
            }

            if (!confirmPromptSeen || Math.Abs(sidebarColumn.Width.Value - 300) > 0.01 ||
                new EditorPreferencesStore(settingsPath).GetLayout().SidebarWidth != 300 ||
                viewModel.NotificationMessage != "Стандартная раскладка восстановлена.")
            {
                throw new InvalidOperationException(
                    "Раскладка должна сбрасываться только после явного подтверждения.");
            }
        }
        finally
        {
            resetWindow.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyDetachedDetailsWindow()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "detached-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        var window = new MainWindow(
            new EditorPreferencesStore(settingsPath),
            new RecoverySnapshotStore(Path.Combine(root, "recovery")));
        DetachedDetailsWindow? detached = null;

        try
        {
            window.Show();
            window.UpdateLayout();
            if (window.FindName("LocalizationDetachButton") is not Button detachButton ||
                window.FindName("DetailsPane") is not TabControl detailsPane ||
                window.FindName("GenericRecordsWorkspace") is not Grid workspace ||
                !workspace.Children.Contains(detailsPane))
            {
                throw new InvalidOperationException(
                    "Не удалось подготовить нижнюю вспомогательную панель к откреплению.");
            }

            detachButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            detached = Application.Current.Windows
                .OfType<DetachedDetailsWindow>()
                .SingleOrDefault()
                ?? throw new InvalidOperationException("Кнопка не открыла отдельное окно связанных данных.");
            if (!window.IsDetailsPaneDetached || workspace.Children.Contains(detailsPane) ||
                !ReferenceEquals(detached.PaneContent, detailsPane) ||
                window.FindName("DetailsPaneRow") is not RowDefinition detailsRow ||
                detailsRow.Height.Value != 0)
            {
                throw new InvalidOperationException(
                    "Откреплённая панель должна физически покинуть основную таблицу и освободить ей место.");
            }

            detached.Width = 1260;
            detached.Height = 760;
            detached.Close();
            EditorLayoutState saved = new EditorPreferencesStore(settingsPath).GetLayout();
            if (window.IsDetailsPaneDetached || !workspace.Children.Contains(detailsPane) ||
                saved.DetailsPaneDetached ||
                Math.Abs(saved.DetailsWindowWidth - 1260) > 1 ||
                Math.Abs(saved.DetailsWindowHeight - 760) > 1)
            {
                throw new InvalidOperationException(
                    "Закрепление должно вернуть панель и запомнить размер отдельного окна.");
            }
        }
        finally
        {
            if (detached?.IsVisible is true)
            {
                detached.Close();
            }

            window.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyRecoverySnapshotStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "EI-Database-Studio-UiSmoke", "recovery-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string recoveryDirectory = Path.Combine(root, "recovery");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "database.res");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        var store = new RecoverySnapshotStore(recoveryDirectory);
        RecoverySnapshot? latest = null;
        for (var index = 0; index < 3; index++)
        {
            RecoverySnapshotDraft draft = store.Prepare(sourcePath);
            File.WriteAllBytes(draft.DatabasePath, [(byte)index, 7, 8]);
            string textsPath = Path.Combine(draft.DirectoryPath, "texts.res");
            File.WriteAllBytes(textsPath, [(byte)index, 9]);
            latest = store.Complete(
                sourcePath,
                draft,
                new SavePairCopyResult(
                    new SaveCopyResult(draft.DatabasePath, 3, $"DB{index}", true, 0),
                    new SaveCopyResult(textsPath, 2, $"TX{index}", true, 0)),
                pendingSound: null);
        }

        RecoverySnapshot? reopened = store.FindLatest(sourcePath);
        IReadOnlyList<RecoverySnapshot> available = store.FindAvailable();
        int completeSnapshotCount = Directory.EnumerateFiles(recoveryDirectory, "recovery.json", SearchOption.AllDirectories).Count();
        if (latest is null || reopened is null ||
            !string.Equals(latest.DatabasePath, reopened.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            available.Count != 1 ||
            !string.Equals(available[0].SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
            completeSnapshotCount != 2)
        {
            throw new InvalidOperationException(
                "Аварийное автосохранение должно открывать последнюю полную пару и хранить не более двух снимков.");
        }

        using (var stream = new FileStream(sourcePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(5);
        }
        if (store.FindLatest(sourcePath) is not null || store.FindAvailable().Count != 0)
        {
            throw new InvalidOperationException(
                "Аварийная копия не должна предлагаться после внешнего изменения исходного database.res.");
        }

        store.Discard(sourcePath);
        if (Directory.Exists(recoveryDirectory) &&
            Directory.EnumerateFiles(recoveryDirectory, "recovery.json", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException("Штатный отказ от восстановления должен удалять снимки выбранного источника.");
        }

        Directory.Delete(root, recursive: true);
    }

    private static void VerifyStartupRecoveryPrompt()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "EI-Database-Studio-UiSmoke",
            "startup-recovery-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string recoveryDirectory = Path.Combine(root, "recovery");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "database.res");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        var recoveryStore = new RecoverySnapshotStore(recoveryDirectory);
        RecoverySnapshotDraft draft = recoveryStore.Prepare(sourcePath);
        File.WriteAllBytes(draft.DatabasePath, [4, 3, 2, 1]);
        recoveryStore.Complete(
            sourcePath,
            draft,
            new SavePairCopyResult(
                new SaveCopyResult(draft.DatabasePath, 4, "DB", true, 0),
                null),
            pendingSound: null);

        var startupWindow = new MainWindow(
            new EditorPreferencesStore(Path.Combine(root, "settings.json")),
            recoveryStore);
        bool promptSeen = false;
        Exception? promptFailure = null;
        var promptCloser = new DispatcherTimer(DispatcherPriority.Normal, startupWindow.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        promptCloser.Tick += (_, _) =>
        {
            RecoverySessionDialog? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<RecoverySessionDialog>().FirstOrDefault();
                if (dialog is null)
                {
                    return;
                }

                if (ReferenceEquals(dialog.Owner, startupWindow))
                {
                    promptSeen = true;
                }
                dialog.DialogResult = false;
            }
            catch (Exception exception)
            {
                promptFailure = exception;
                promptCloser.Stop();
                if (dialog?.IsVisible is true)
                {
                    dialog.DialogResult = false;
                }
            }
        };
        promptCloser.Start();

        try
        {
            startupWindow.Show();
            startupWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (promptFailure is not null)
            {
                throw new InvalidOperationException(
                    "Не пройдена проверка предложения аварийной копии при запуске.",
                    promptFailure);
            }

            if (!promptSeen ||
                startupWindow.DataContext is not MainWindowViewModel startupViewModel ||
                startupViewModel.DatabaseCount != 0)
            {
                throw new InvalidOperationException(
                    "Аварийная копия должна предлагаться до ручного выбора database.res и не открываться после отказа.");
            }
        }
        finally
        {
            promptCloser.Stop();
            startupWindow.Close();
            foreach (MainWindow unexpectedWindow in Application.Current.Windows
                         .OfType<MainWindow>()
                         .Where(candidate => candidate.IsVisible)
                         .ToArray())
            {
                unexpectedWindow.Close();
            }
            Directory.Delete(root, recursive: true);
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

        if (tables.Any(table => !table.IsEditable || !table.CanAppendRecords) ||
            tables.SelectMany(table => table.Columns)
                .Where(column => column.Projection is null)
                .Any(column => !column.IsEditable) ||
            tables.SelectMany(table => table.Columns)
                .Where(column => column.Projection is not null)
                .Any(column => column.IsEditable !=
                    (column.Projection!.Kind is
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit or
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement or
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.NestedField or
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.PackedField or
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue)))
        {
            string projectionMismatches = string.Join(
                "; ",
                tables.SelectMany(table => table.Columns.Select(column => (table.Key, Column: column)))
                    .Where(item => item.Column.Projection is not null &&
                                   item.Column.IsEditable !=
                                   (item.Column.Projection.Kind is
                                       EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit or
                                       EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement or
                                       EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.NestedField or
                                       EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.PackedField or
                                       EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue))
                    .Take(12)
                    .Select(item => $"{item.Key}/{item.Column.Title}/{item.Column.Projection!.Kind}={item.Column.IsEditable}"));
            throw new InvalidOperationException(
                "Все 26 таблиц и их физические поля должны редактироваться; обратимые битовые, списочные, вложенные, упакованные и enum-проекции редактируются по ячейкам. " +
                projectionMismatches);
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
        VerifyEmbeddedMagicConstructor(viewModel);
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
        var weaponTable = tables.Single(table => table.Key == "items.weapons");
        var armorTable = tables.Single(table => table.Key == "items.armors");
        var quickItems = tables.Single(table => table.Key == "items.quick_items");
        var questItems = tables.Single(table => table.Key == "items.quest_items");
        var lootItems = tables.Single(table => table.Key == "items.loot_items");
        var levers = tables.Single(table => table.Key == "levers.prototypes");
        var perks = tables.Single(table => table.Key == "perks.perks");
        SpreadsheetColumnViewModel weaponClassColumn = weaponTable.Columns.Single(column =>
            column.FieldId == 2 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        SpreadsheetColumnViewModel armorClassColumn = armorTable.Columns.Single(column =>
            column.FieldId == 2 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        SpreadsheetColumnViewModel quickItemTypeColumn = quickItems.Columns.Single(column =>
            column.FieldId == 21 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        SpreadsheetColumnViewModel lootTypeColumn = lootItems.Columns.Single(column =>
            column.FieldId == 21 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        SpreadsheetColumnViewModel instructionTypeColumn = lootItems.Columns.Single(column =>
            column.FieldId == 22 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        if (weaponTable.Columns.Where(column => column.FieldId == 4).Any(column => column.ShowInReadableView) ||
            armorTable.Columns.Where(column => column.FieldId == 4).Any(column => column.ShowInReadableView) ||
            quickItems.Columns.Where(column => new[] { 2, 4 }.Contains(column.FieldId))
                .Any(column => column.ShowInReadableView) ||
            questItems.Columns.Where(column => new[] { 2, 3, 4 }.Contains(column.FieldId))
                .Any(column => column.ShowInReadableView) ||
            lootItems.Columns.Where(column => new[] { 2, 4 }.Contains(column.FieldId))
                .Any(column => column.ShowInReadableView) ||
            questItems.Columns.Where(column => new[] { 2, 3, 4 }.Contains(column.FieldId) && column.Projection is null)
                .Any(column => !column.IsEditable) ||
            lootItems.Columns.Single(column => column.FieldId == 2 && column.Projection is null).IsEditable is false ||
            weaponClassColumn.Title != "Класс оружия" ||
            weaponClassColumn.Projection?.ValueLabels?.GetValueOrDefault(13) != "Двуручный топор" ||
            armorClassColumn.Title != "Тип части брони" ||
            armorClassColumn.Projection?.ValueLabels?.GetValueOrDefault(6) != "Перчатки" ||
            quickItemTypeColumn.Title != "Тип быстрого предмета" ||
            quickItemTypeColumn.Projection?.ValueLabels?.GetValueOrDefault(5) != "Жезл" ||
            lootTypeColumn.Title != "Тип лута" ||
            instructionTypeColumn.Title != "Тип инструкции" ||
            lootTypeColumn.Projection?.ValueLabels?.Count != 6 ||
            instructionTypeColumn.Projection?.ValueLabels?.Count != 8)
        {
            throw new InvalidOperationException(
                "Понятный режим должен скрывать неиспользуемые наследованные поля всех предметных таблиц, " +
                "а рабочие типы — показывать полными русскими перечислениями.");
        }

        string[] expectedLeverPointColumns =
            ["Точка 1 — X", "Точка 1 — Y", "Точка 2 — X", "Точка 2 — Y"];
        if (!levers.Columns
                .Where(column => column.FieldId == 1 &&
                                 column.Projection?.Kind ==
                                 EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement)
                .OrderBy(column => column.Projection!.Index)
                .Select(column => column.Title)
                .SequenceEqual(expectedLeverPointColumns, StringComparer.Ordinal) ||
            levers.Columns.Single(column => column.FieldId == 2 && column.Projection is null).Title !=
                "Количество активных точек (0–2)")
        {
            throw new InvalidOperationException(
                "Две native-точки взаимодействия рычага должны показываться как две понятные пары X/Y.");
        }
        DatabaseFieldProjection leverPointProjection = levers.Columns.First(column =>
                column.FieldId == 1 &&
                column.Projection?.Kind ==
                EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement)
            .Projection!;
        if (SpreadsheetProjectionValue.Format("NaN", leverPointProjection) !=
                SpreadsheetProjectionValue.UnavailableText ||
            SpreadsheetProjectionValue.Format("1.7445577E-39", leverPointProjection) !=
                SpreadsheetProjectionValue.UnavailableText)
        {
            throw new InvalidOperationException(
                "NaN и денормальные значения неиспользуемых точек рычага должны отображаться как «—», а не как числа.");
        }

        SpreadsheetColumnViewModel perkAutoSkill = perks.Columns.Single(column =>
            column.FieldId == 12 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        SpreadsheetColumnViewModel perkManualSkill = perks.Columns.Single(column =>
            column.FieldId == 13 &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.EnumValue);
        if (perkAutoSkill.Projection?.ValueLabels?.GetValueOrDefault(3) != "Стихийная магия" ||
            perkAutoSkill.Projection?.ValueLabels?.GetValueOrDefault(int.MaxValue) != "Не применяется" ||
            perkManualSkill.Projection?.ValueLabels?.GetValueOrDefault(0) != "Наука" ||
            perkManualSkill.Projection?.ValueLabels?.GetValueOrDefault(int.MaxValue) != "Не применяется")
        {
            throw new InvalidOperationException(
                "Перки должны показывать автоматические и ручные навыки из разных native-перечислений.");
        }
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
        VerifyValidationDashboard(window, viewModel);
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
            viewModel.SelectedColumnMode != "Понятные поля" ||
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

        viewModel.SelectedColumnMode = "Все поля";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (!viewModel.ShowAllColumns ||
            viewModel.SelectedColumnMode != "Все поля" ||
            viewModel.VisibleSpreadsheetColumns.Count != prototypes.Columns.Count ||
            recordsGrid.Columns.Count != prototypes.Columns.Count + (viewModel.HasTextsDocument ? 4 : 1))
        {
            throw new InvalidOperationException(
                "Режим технических полей должен восстановить все физические столбцы и расшифровки.");
        }

        viewModel.SelectedColumnMode = "Понятные поля";
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

        viewModel.SelectTableCommand.Execute(questItems);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.VisibleSpreadsheetColumns.Any(column => new[] { 2, 3, 4 }.Contains(column.FieldId)))
        {
            throw new InvalidOperationException(
                "Понятный режим квестовых предметов не должен показывать неиспользуемые ID и материал.");
        }

        viewModel.SelectedColumnMode = "Все поля";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (!new[] { 2, 3, 4 }.All(fieldId => viewModel.VisibleSpreadsheetColumns.Any(column =>
                column.FieldId == fieldId && column.Projection is null && column.IsEditable)))
        {
            throw new InvalidOperationException(
                "Технический режим обязан вернуть три скрытых физических поля квестовых предметов для точного доступа.");
        }

        viewModel.SelectedColumnMode = "Понятные поля";
        viewModel.SelectTableCommand.Execute(lootItems);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.VisibleSpreadsheetColumns.Any(column => column.FieldId == 2) ||
            !viewModel.VisibleSpreadsheetColumns.Contains(lootTypeColumn) ||
            !viewModel.VisibleSpreadsheetColumns.Contains(instructionTypeColumn))
        {
            throw new InvalidOperationException(
                "Понятный режим лута должен скрывать служебный ID, но оставлять понятные типы лута и инструкции.");
        }

        viewModel.SelectTableCommand.Execute(prototypes);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

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
            materials.Columns.Count(column =>
                column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement &&
                column.IsEditable) != 7 ||
            materials.Columns.Where(column =>
                    column.Projection is not null &&
                    column.Projection.Kind is not (
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit or
                        EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement))
                .Any(column => column.IsEditable))
        {
            throw new InvalidOperationException(
                "Материалы должны иметь 14 редактируемых физических столбцов, " +
                $"пять редактируемых shop-битов, семь элементов поглощения и нередактируемые сложные расшифровки; статус таблицы — «Схема распознана». " +
                $"columns={materials.Columns.Count}; status={materials.StatusText}; physicalEditable={materials.Columns.Count(column => column.Projection is null && column.IsEditable)}; bitEditable={materials.Columns.Count(column => column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit && column.IsEditable)}; listEditable={materials.Columns.Count(column => column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement && column.IsEditable)}; otherProjectionEditable={materials.Columns.Count(column => column.Projection is not null && column.Projection.Kind is not (EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.Bit or EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.ListElement) && column.IsEditable)}");
        }

        var rawColumns = tables
            .SelectMany(table => table.Columns.Select(column => (Table: table, Column: column)))
            .Where(item => item.Column.Kind == "Hex")
            .ToArray();
        int physicalColumnCount = tables.Sum(table => table.Columns.Count(column => column.Projection is null));
        int displayedColumnCount = tables.Sum(table => table.Columns.Count);
        int rawEditorCount = tables.Sum(table => table.Columns.Count(column => column.Kind == "RawHex"));
        if (physicalColumnCount != 380 || displayedColumnCount != 702 || rawColumns.Length != 0 || rawEditorCount == 0)
        {
            throw new InvalidOperationException(
                $"Полный каталог должен дать 380 редактируемых физических полей, 702 понятные колонки, расшифрованные проекции и raw-редакторы сложных структур; " +
                $"получено физических/всего/Hex/raw {physicalColumnCount}/{displayedColumnCount}/{rawColumns.Length}/{rawEditorCount}.");
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
            !weapons.Columns.Any(column => column.Title == "Наука" && column.Kind == "Byte") ||
            !weapons.Columns.Any(column => column.Title == "Внимательность" && column.Kind == "Byte") ||
            armors.Columns.Count(column => column.Projection?.Kind ==
                EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.PackedField) != 18 ||
            armors.Columns.Count(column => column.Title.EndsWith("маска травм", StringComparison.Ordinal)) != 2)
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
            .FirstOrDefault(value => value?.Contains("Файл:", StringComparison.Ordinal) == true);
        if (string.IsNullOrWhiteSpace(nonEmptyAck) || nonEmptyAck.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Вложенные реплики должны показывать имена файлов и параметры, а не raw Hex.");
        }

        viewModel.SelectTableCommand.Execute(acks);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.ShowAllColumns ||
            viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex") ||
            !viewModel.VisibleSpreadsheetColumns.Any(column =>
                column.Kind == "StructuredListDecoded" &&
                column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue) ||
            viewModel.VisibleSpreadsheetColumns.Count >= acks.Columns.Count ||
            recordsGrid.Columns.Count != viewModel.VisibleSpreadsheetColumns.Count +
                (viewModel.HasTextsDocument ? 4 : 1) +
                (viewModel.HasSoundArchives ? 2 : 0))
        {
            throw new InvalidOperationException(
                "acks.db/Ответы и команды должен показывать только понятные составные списки; raw-код доступен отдельно в техническом режиме.");
        }

        var cries = tables.Single(table => table.Key == "acks.cries");
        viewModel.SelectTableCommand.Execute(cries);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (window.FindName("ColumnModeComboBox") is not ComboBox columnModeComboBox)
        {
            throw new InvalidOperationException("Над таблицей должен находиться явный выбор режима полей.");
        }

        if (cries.Columns.Count >= 12 ||
            !columnModeComboBox.IsEnabled ||
            columnModeComboBox.Items.Count != 3 ||
            !Equals(columnModeComboBox.SelectedItem, "Понятные поля") ||
            viewModel.VisibleSpreadsheetColumns.Count >= cries.Columns.Count ||
            !viewModel.HasHiddenTechnicalColumns ||
            !viewModel.SpreadsheetColumnCountText.StartsWith(
                $"{viewModel.VisibleSpreadsheetColumns.Count:N0} из {cries.Columns.Count:N0} полей",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Малая таблица acks.db/Боевые крики должна разрешать открыть скрытые технические поля и ясно показывать счётчик.");
        }

        viewModel.SelectedColumnMode = "Все поля";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (!viewModel.ShowAllColumns ||
            viewModel.VisibleSpreadsheetColumns.Count != cries.Columns.Count ||
            !viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex") ||
            !viewModel.SpreadsheetColumnCountText.StartsWith(
                $"Все {cries.Columns.Count:N0} полей",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Технический режим малой таблицы должен показать все исходные поля, включая RawHex.");
        }

        viewModel.SelectedColumnMode = "Исходные байты";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (viewModel.VisibleSpreadsheetColumns.Count != cries.Columns.Count(column => column.Projection is null) ||
            viewModel.VisibleSpreadsheetColumns.Any(column => column.Projection is not null) ||
            !viewModel.VisibleSpreadsheetColumns.Any(column => column.Kind == "RawHex"))
        {
            throw new InvalidOperationException(
                "Режим исходных байтов должен убрать расшифровки и оставить только физические поля database.res.");
        }

        viewModel.SelectedColumnMode = "Понятные поля";
        viewModel.SelectTableCommand.Execute(acks);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

        SpreadsheetColumnViewModel structuredAckColumn = viewModel.VisibleSpreadsheetColumns.First(column =>
            column.Kind == "StructuredListDecoded");
        RecordRowViewModel structuredAckRecord = acks.Records.First(record =>
            record.GetCell(structuredAckColumn.FieldId)?.CanEditStructuredList is true);
        DataGridColumn structuredAckGridColumn = recordsGrid.Columns.First(column =>
            ReferenceEquals(column.Header, structuredAckColumn));
        viewModel.SelectedRecord = structuredAckRecord;
        recordsGrid.SelectedItem = structuredAckRecord;
        recordsGrid.CurrentCell = new DataGridCellInfo(structuredAckRecord, structuredAckGridColumn);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (window.FindName("FormulaOpenEditorButton") is not Button
            {
                IsEnabled: true,
                ToolTip: "Открыть редактор вариантов реплики",
            } ||
            window.FindName("FormulaEditorBox") is not TextBox { IsReadOnly: true })
        {
            throw new InvalidOperationException(
                "Понятная ячейка acks.db должна открывать отдельный редактор вариантов, оставляя сводный текст защищённым от случайной перезаписи.");
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
            TextBox? briefingTitleTextBox = window.FindName("BriefingTitleTextBox") as TextBox;
            Button? briefingTitleSaveButton = window.FindName("BriefingTitleSaveButton") as Button;
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
                briefingTitleTextBox is null ||
                briefingTitleSaveButton is null ||
                !briefingTitleTextBox.IsEnabled ||
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
            string originalBriefingTitle = briefingText.Title;
            string originalBriefingDescription = briefingText.Description;
            string originalBriefingIdentity = briefingWithSound.DisplayName;
            int titleChangeCount = viewModel.ChangeCount;
            viewModel.BriefingTitleDraft = originalBriefingTitle + " — проверка названия";
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (!viewModel.ApplyBriefingTitleCommand.CanExecute(null) || !briefingTitleSaveButton.IsEnabled)
            {
                throw new InvalidOperationException(
                    "Изменённое название Брифинга должно разрешать явное сохранение в связанном BRIEFING-ресурсе.");
            }

            viewModel.ApplyBriefingTitleCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (!briefingText.Title.EndsWith("проверка названия", StringComparison.Ordinal) ||
                briefingWithSound.LocalizationTitle != briefingText.Title ||
                briefingWithSound.DisplayName != originalBriefingIdentity ||
                briefingText.Description != originalBriefingDescription ||
                viewModel.ChangeCount != titleChangeCount + 1 ||
                viewModel.ApplyBriefingTitleCommand.CanExecute(null))
            {
                throw new InvalidOperationException(
                    "Редактор названия должен менять только заголовок BRIEFING в texts.res, не затрагивая технический ключ и сценарий.");
            }

            viewModel.UndoCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (briefingText.Title != originalBriefingTitle ||
                briefingWithSound.LocalizationTitle != originalBriefingTitle ||
                viewModel.BriefingTitleDraft != originalBriefingTitle ||
                briefingText.Description != originalBriefingDescription ||
                viewModel.ChangeCount != titleChangeCount)
            {
                throw new InvalidOperationException(
                    "Undo должен полностью вернуть название Брифинга и синхронизировать поле редактора.");
            }

            var soundForExcel = briefingWithSound.RelatedSounds.First(sound => sound.HasTranscript);
            DataGridColumn[] soundColumns = soundLinksGrid!.Columns.Take(4).ToArray();
            soundLinksGrid.SelectedCells.Clear();
            foreach (DataGridColumn column in soundColumns)
            {
                soundLinksGrid.SelectedCells.Add(new DataGridCellInfo(soundForExcel, column));
            }
            soundLinksGrid.CurrentCell = new DataGridCellInfo(soundForExcel, soundColumns[0]);
            if (!ApplicationCommands.Copy.CanExecute(null, soundLinksGrid) ||
                !window.TryBuildClipboardMatrix(
                    soundLinksGrid,
                    out IReadOnlyList<IReadOnlyList<string>> soundMatrix) ||
                soundMatrix.Count != 1 || soundMatrix[0].Count != soundColumns.Length ||
                !soundMatrix[0].Contains(soundForExcel.TranscriptText, StringComparer.Ordinal) ||
                !soundMatrix[0].Contains(soundForExcel.DisplayResourceName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Таблица «Связанные звуковые файлы» должна копировать текст реплики и путь RES в Excel.");
            }

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
                    "Крупный редактор должен менять точный #phrase и сохранять выбор реплики после обновления texts.res. " +
                    $"descriptionChanged={briefingText.Description != originalBriefingDescription}; " +
                    $"expectedNumber={editedPhraseNumber}; selectedNumber={viewModel.SelectedBriefingPhrase?.Number}; " +
                    $"selectedText={viewModel.SelectedBriefingPhrase?.Text}");
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

        viewModel.SelectedColumnMode = "Все поля";
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
        VerifyAuxiliarySpreadsheetClipboard(window, viewModel, materials);
        // The helper checks temporarily navigate through texts.res and auxiliary grids.
        // Re-select the table and let the column rebuild finish before exercising the
        // mixed DB + localization clipboard range below.
        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

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

        DataGridColumn materialNameGridColumn = recordsGrid.Columns.Single(column =>
            column.Header is SpreadsheetColumnViewModel descriptor && descriptor.IsNameField);
        DataGridColumn rowNumberGridColumn = recordsGrid.Columns.Single(column => Equals(column.Header, "#"));
        RecordRowViewModel servicePasteRow = materials.Records[0];
        DataGridColumn? localizationTitleGridColumn = recordsGrid.Columns.SingleOrDefault(column =>
            Equals(column.Header, "Название RU"));
        DataGridColumn? localizationKeyGridColumn = recordsGrid.Columns.SingleOrDefault(column =>
            Equals(column.Header, "Ключ texts.res"));
        DataGridColumn? localizationDescriptionGridColumn = recordsGrid.Columns.SingleOrDefault(column =>
            Equals(column.Header, "Описание RU"));
        RecordRowViewModel? mixedPasteRow = materials.Records.FirstOrDefault(record => record.CanEditPrimaryLocalization);
        if (localizationTitleGridColumn is not null &&
            localizationKeyGridColumn is not null &&
            localizationDescriptionGridColumn is not null &&
            mixedPasteRow is not null)
        {
            string originalMixedTitle = mixedPasteRow.LocalizationTitle;
            string originalMixedDescription = mixedPasteRow.LocalizationDescription;
            string originalMixedType = mixedPasteRow.GetCell(materialType.FieldId)!.EditorText;
            string changedMixedTitle = originalMixedTitle + " Excel";
            string changedMixedDescription = originalMixedDescription + " Вставлено из Excel.";
            string changedMixedType = originalMixedType + "Excel";
            IReadOnlyList<IReadOnlyList<string>> mixedExcelRow =
            [[
                mixedPasteRow.DisplayName,
                changedMixedTitle,
                mixedPasteRow.Index.ToString(CultureInfo.InvariantCulture),
                mixedPasteRow.LocalizationKeyText,
                changedMixedDescription,
                changedMixedType,
            ]];
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(mixedPasteRow, materialNameGridColumn));
            recordsGrid.CurrentCell = new DataGridCellInfo(mixedPasteRow, materialNameGridColumn);
            if (!window.TryBuildPasteCells(mixedExcelRow, out IReadOnlyList<SpreadsheetPasteCell> mixedCells, out string? mixedError) ||
                mixedCells.Count != 4 ||
                !viewModel.TryPasteSpreadsheetCells(mixedCells, out int mixedChangedCount, out mixedError) ||
                mixedChangedCount != 3 ||
                mixedPasteRow.LocalizationTitle != changedMixedTitle ||
                mixedPasteRow.LocalizationDescription != changedMixedDescription ||
                mixedPasteRow.GetCell(materialType.FieldId)!.EditorText != changedMixedType)
            {
                throw new InvalidOperationException(
                    $"Excel-диапазон с RU и неизменными служебными столбцами должен вставляться атомарно: {mixedError}");
            }

            string recoveryIntegrationRoot = Path.Combine(
                Path.GetTempPath(),
                "EI-Database-Studio-UiSmoke",
                "recovery-integration-" + Guid.NewGuid().ToString("N"));
            var recoveryStore = new RecoverySnapshotStore(recoveryIntegrationRoot);
            RecoverySnapshotDraft recoveryDraft = recoveryStore.Prepare(sourcePath);
            SavePairCopyResult? recoverySave = null;
            RunWithDispatcher(async () =>
                recoverySave = await viewModel.SaveRecoveryCopyPathAsync(recoveryDraft.DatabasePath));
            if (recoverySave is null)
            {
                throw new InvalidOperationException(
                    $"Аварийная копия изменённых database.res/texts.res не создана: {viewModel.NotificationMessage}");
            }

            RecoverySnapshot recoverySnapshot = recoveryStore.Complete(
                sourcePath,
                recoveryDraft,
                recoverySave,
                pendingSound: null);
            if (!File.Exists(recoverySnapshot.DatabasePath) ||
                recoverySnapshot.TextsPath is null || !File.Exists(recoverySnapshot.TextsPath) ||
                recoveryStore.FindLatest(sourcePath) is null)
            {
                throw new InvalidOperationException(
                    "Полная аварийная пара database.res + texts.res должна повторно обнаруживаться после записи manifest.");
            }
            recoveryStore.Discard(sourcePath);
            Directory.Delete(recoveryIntegrationRoot, recursive: true);

            viewModel.UndoCommand.Execute(null);
            if (mixedPasteRow.LocalizationTitle != originalMixedTitle ||
                mixedPasteRow.LocalizationDescription != originalMixedDescription ||
                mixedPasteRow.GetCell(materialType.FieldId)!.EditorText != originalMixedType ||
                viewModel.ChangeCount != 0)
            {
                throw new InvalidOperationException(
                    "Один Undo должен одновременно отменять DB- и texts.res-ячейки смешанной Excel-вставки.");
            }
        }

        recordsGrid.CurrentCell = new DataGridCellInfo(servicePasteRow, rowNumberGridColumn);
        if (window.TryBuildPasteCells(
                [[(servicePasteRow.Index + 1).ToString(CultureInfo.InvariantCulture)]],
                out _,
                out string? serviceColumnError) ||
            string.IsNullOrWhiteSpace(serviceColumnError))
        {
            throw new InvalidOperationException(
                "Изменение вычисляемого номера строки должно отклоняться до модификации модели.");
        }

        viewModel.SelectTableCommand.Execute(acks);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        SpreadsheetColumnViewModel computedFloat = acks.Columns.First(column =>
            !column.IsEditable &&
            column.Projection?.Kind == EvilIslands.DatabaseEditor.Core.DatabaseFieldProjectionKind.DecodedValue &&
            acks.Records.Any(record => !string.IsNullOrWhiteSpace(GetSpreadsheetText(record, column))));
        RecordRowViewModel computedRow = acks.Records.First(record =>
            !string.IsNullOrWhiteSpace(GetSpreadsheetText(record, computedFloat)));
        DataGridColumn computedGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, computedFloat));
        string computedValue = GetSpreadsheetText(computedRow, computedFloat);
        recordsGrid.CurrentCell = new DataGridCellInfo(computedRow, computedGridColumn);
        if (!window.TryBuildPasteCells(
                [[computedValue]],
                out IReadOnlyList<SpreadsheetPasteCell> computedCells,
                out string? computedError) ||
            computedCells.Count != 0)
        {
            throw new InvalidOperationException(
                $"Неизменная read-only расшифровка из Excel должна пропускаться автоматически: {computedError}");
        }

        if (window.TryBuildPasteCells([["999999"]], out _, out computedError) ||
            string.IsNullOrWhiteSpace(computedError) ||
            !computedError.Contains(computedFloat.Title, StringComparison.Ordinal) ||
            computedError.Contains(nameof(SpreadsheetColumnViewModel), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Ошибка вставки в вычисляемую колонку должна показывать короткое понятное название, а не внутреннее описание объекта.");
        }

        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        materialTypeGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, materialType));

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

    private static void VerifyValidationDashboard(MainWindow window, MainWindowViewModel viewModel)
    {
        if (window.FindName("ValidationGrid") is not DataGrid validationGrid ||
            window.FindName("ValidationCategoriesList") is not ListBox validationCategoriesList ||
            window.FindName("ValidationSearchBox") is not TextBox ||
            window.FindName("ValidationSeverityFilter") is not ComboBox ||
            window.FindName("ValidationDomainFilter") is not ComboBox ||
            window.FindName("ValidationBlockingFilter") is not CheckBox ||
            window.FindName("ValidationClearFiltersButton") is not Button ||
            validationGrid.Columns.Count != 3 ||
            !ReferenceEquals(validationGrid.ItemsSource, viewModel.DiagnosticsView) ||
            !ReferenceEquals(validationCategoriesList.ItemsSource, viewModel.DiagnosticCategories))
        {
            throw new InvalidOperationException(
                "Проверка должна иметь отдельный навигатор причин, чистый список записей, фильтры и панель подробностей.");
        }

        if (viewModel.Diagnostics.Count == 0)
        {
            throw new InvalidOperationException("Реальная база должна содержать диагностические сообщения для проверки интерфейса.");
        }

        ValidationMessageViewModel probe = viewModel.Diagnostics.First();
        if (string.IsNullOrWhiteSpace(probe.CategoryText) ||
            probe.CategoryText.StartsWith("SEM_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(probe.TableTitle) ||
            viewModel.DiagnosticsView.GroupDescriptions.Count != 0 ||
            viewModel.DiagnosticCategories.Count < 2 ||
            viewModel.DiagnosticCategories[0] is not { IsAll: true } allCategory ||
            allCategory.Count != viewModel.Diagnostics.Count)
        {
            throw new InvalidOperationException(
                "Диагностика должна отделять понятные виды проблем от записей и не смешивать группы со строками таблицы.");
        }

        DiagnosticCategoryViewModel probeCategory = viewModel.DiagnosticCategories.First(item => item.Key == probe.GroupTitle);
        viewModel.SelectedDiagnosticCategory = probeCategory;
        int expectedByCategory = viewModel.Diagnostics.Count(item => item.GroupTitle == probe.GroupTitle);
        if (viewModel.FilteredDiagnosticCount != expectedByCategory || expectedByCategory == 0)
        {
            throw new InvalidOperationException("Выбор вида проблемы должен показывать только относящиеся к нему записи.");
        }

        viewModel.SelectedDiagnosticCategory = viewModel.DiagnosticCategories[0];

        viewModel.SelectedDiagnostic = probe;
        if (!viewModel.HasSelectedDiagnostic)
        {
            throw new InvalidOperationException("Выбранная проблема должна открывать отдельную панель подробностей.");
        }

        viewModel.DiagnosticSearchText = probe.Code;
        int expectedByCode = viewModel.Diagnostics.Count(item =>
            item.SearchText.Contains(probe.Code, StringComparison.CurrentCultureIgnoreCase));
        if (viewModel.FilteredDiagnosticCount != expectedByCode || expectedByCode == 0)
        {
            throw new InvalidOperationException("Поиск проверки должен находить и понятный текст, и скрытый технический код.");
        }

        viewModel.DiagnosticSearchText = string.Empty;
        viewModel.SelectedDiagnosticDomainFilter = probe.DomainText;
        int expectedByDomain = viewModel.Diagnostics.Count(item => item.DomainText == probe.DomainText);
        if (viewModel.FilteredDiagnosticCount != expectedByDomain)
        {
            throw new InvalidOperationException("Фильтр области проверки должен отделять структуру от семантики.");
        }

        viewModel.SelectedDiagnosticDomainFilter = "Все области";
        viewModel.ShowOnlyBlockingDiagnostics = true;
        if (viewModel.FilteredDiagnosticCount != viewModel.Diagnostics.Count(item => item.IsBlocking))
        {
            throw new InvalidOperationException("Фильтр блокирующих проблем должен оставлять только ошибки, мешающие сохранению.");
        }

        viewModel.ShowOnlyBlockingDiagnostics = false;
        viewModel.ClearDiagnosticFiltersCommand.Execute(null);
        if (viewModel.FilteredDiagnosticCount != viewModel.DiagnosticCount ||
            viewModel.HasActiveDiagnosticFilters ||
            viewModel.DiagnosticsView.GroupDescriptions.Count != 0 ||
            viewModel.SelectedDiagnosticCategory is not { IsAll: true })
        {
            throw new InvalidOperationException("Сброс фильтров должен вернуть все виды проблем и полный список записей.");
        }
    }

    private static void VerifyResFileManagerDialog()
    {
        string unopenedArchivePath = Path.Combine(Path.GetTempPath(), $"ui-smoke-{Guid.NewGuid():N}.res");
        string settingsPath = Path.Combine(Path.GetTempPath(), $"res-manager-layout-{Guid.NewGuid():N}.json");
        var preferences = new EditorPreferencesStore(settingsPath);
        preferences.RememberLayout(new EditorLayoutState
        {
            ResManagerWindowWidth = 1510,
            ResManagerWindowHeight = 890,
            ResManagerFoldersWidth = 278,
            ResManagerDetailsWidth = 334,
            ResManagerDetailsVisible = true,
            ResManagerChangesExpanded = true,
        });
        var dialog = new ResFileManagerDialog(unopenedArchivePath, null, preferences);
        if (dialog.ResizeMode != ResizeMode.CanResize ||
            dialog.Width != 1510 || dialog.Height != 890 ||
            dialog.FindName("FoldersTree") is not TreeView ||
            dialog.FindName("FilesGrid") is not DataGrid
            {
                IsReadOnly: true,
                SelectionMode: DataGridSelectionMode.Extended,
                ClipboardCopyMode: DataGridClipboardCopyMode.IncludeHeader,
            } filesGrid ||
            filesGrid.Columns.Count != 5 ||
            dialog.FindName("FolderPathColumn") is not DataGridTextColumn { Visibility: Visibility.Collapsed } ||
            dialog.FindName("FoldersColumn") is not ColumnDefinition { Width.Value: 278 } ||
            dialog.FindName("DetailsColumn") is not ColumnDefinition { Width.Value: 334 } ||
            dialog.FindName("DetailsPanel") is not Border { Visibility: Visibility.Visible } ||
            dialog.FindName("ChangesGrid") is not DataGrid { IsReadOnly: true } ||
            dialog.FindName("BreadcrumbPanel") is not StackPanel ||
            dialog.FindName("OperationStatusText") is not TextBlock ||
            dialog.FindName("AddFilesButton") is not Button ||
            dialog.FindName("AddFolderButton") is not Button ||
            dialog.FindName("ExtractButton") is not Button ||
            dialog.FindName("SaveCopyButton") is not Button ||
            dialog.FindName("SaveCurrentButton") is not Button)
        {
            throw new InvalidOperationException(
                "RES-менеджер обязан содержать запоминаемую трёхпанельную раскладку, адресную строку, журнал изменений и безопасные команды.");
        }

        dialog.Close();
        File.Delete(settingsPath);
    }

    private static void VerifyDatabaseComparisonDialog()
    {
        var result = new DatabaseComparisonResult(
            @"C:\current\database.res",
            new string('1', 64),
            @"C:\compared\database.res",
            new string('2', 64),
            [
                new DatabaseDifference(
                    DatabaseDifferenceKind.FieldChanged,
                    "items.materials",
                    "Материалы",
                    "items.idb",
                    "astral rock",
                    "astral rock",
                    45,
                    45,
                    1,
                    0,
                    "Тип",
                    "Stone",
                    "Metal",
                    [1],
                    [2]),
                new DatabaseDifference(
                    DatabaseDifferenceKind.OnlyInCompared,
                    "items.materials",
                    "Материалы",
                    "items.idb",
                    "comparison item",
                    "comparison item",
                    null,
                    51,
                    null,
                    0,
                    "Вся запись",
                    string.Empty,
                    "24 поля",
                    null,
                    null),
            ]);
        var dialog = new DatabaseComparisonDialog(result);
        if (dialog.FindName("DifferencesGrid") is not DataGrid
            {
                IsReadOnly: true,
                AutoGenerateColumns: false,
                ClipboardCopyMode: DataGridClipboardCopyMode.ExcludeHeader,
            } grid ||
            grid.Columns.Count < 8 ||
            dialog.DataContext is not DatabaseComparisonDialogViewModel viewModel ||
            viewModel.Differences.Count != 2 ||
            viewModel.ModifiedRecordCount != 1 ||
            viewModel.OnlyInComparedCount != 1 ||
            dialog.FindName("KindFilter") is not ComboBox { Template: not null } kindFilter ||
            kindFilter.ItemContainerStyle is null)
        {
            throw new InvalidOperationException(
                "Диалог сравнения обязан показывать read-only Excel-совместимый отчёт и корректную сводку.");
        }

        VerifyDarkComboBoxTemplate(kindFilter, "Фильтр сравнения баз");

        viewModel.SearchText = "Metal";
        if (viewModel.DifferencesView.Cast<object>().Count() != 1)
        {
            throw new InvalidOperationException(
                "Поиск отчёта сравнения должен фильтровать по значениям полей.");
        }

        viewModel.SelectVisibleCompatible();
        if (viewModel.SelectedCount != 1 || !viewModel.HasSelection ||
            !viewModel.Differences[0].CanApply || viewModel.Differences[1].CanApply)
        {
            throw new InvalidOperationException(
                "Сравнение должно разрешать выбор только существующих совместимых полей, но не новых записей.");
        }

        dialog.Close();
    }

    private static void VerifyReferenceUsagesDialog()
    {
        var result = new DatabaseReferenceSearchResult(
            new DatabaseReferenceTarget("items.materials", "Материалы", 4, "Metal", "Металл"),
            [
                new DatabaseReferenceUsage(
                    DatabaseReferenceMatchKind.Confirmed,
                    "items.weapons",
                    "Оружие",
                    "items.idb",
                    7,
                    "metal battle axe",
                    "Боевой топор",
                    3,
                    "Материал",
                    "Metal"),
                new DatabaseReferenceUsage(
                    DatabaseReferenceMatchKind.StructuredValue,
                    "units.prototypes",
                    "Прототипы монстров",
                    "units.udb",
                    12,
                    "Human Hero",
                    "Герой",
                    29,
                    "Оружие 1",
                    "metal battle axe.Metal[arrow{it;im0}]")
            ]);
        var dialog = new ReferenceUsagesDialog(result);
        if (dialog.FindName("UsagesGrid") is not DataGrid
            {
                IsReadOnly: true,
                AutoGenerateColumns: false,
            } grid ||
            grid.Columns.Count < 6 ||
            dialog.FindName("KindFilter") is not ComboBox { Template: not null } kindFilter ||
            kindFilter.ItemContainerStyle is null ||
            dialog.DataContext is not ReferenceUsagesDialogViewModel viewModel ||
            viewModel.Usages.Count != 2 ||
            viewModel.ConfirmedCount != 1 ||
            viewModel.StructuredCount != 1)
        {
            throw new InvalidOperationException(
                "Диалог «Где используется» обязан различать подтверждённые ссылки и составные совпадения и открывать точное поле.");
        }

        dialog.Close();
    }

    private static void VerifyEmbeddedMagicConstructor(MainWindowViewModel viewModel)
    {
        Dictionary<string, IReadOnlyList<DatabaseRecord>> recordsByTable = viewModel.Databases
            .SelectMany(database => database.Tables)
            .ToDictionary(
                table => table.Key,
                table => (IReadOnlyList<DatabaseRecord>)table.Records
                    .Select(record => record.CoreRecord)
                    .ToArray(),
                StringComparer.Ordinal);
        var titles = viewModel.TextResources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.Title))
            .GroupBy(resource => resource.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
        var context = new EmbeddedMagicEditorContext(EmbeddedMagicCatalog.Create(recordsByTable), titles);
        EmbeddedMagicModifierOption modifier = context.Catalog.Modifiers.First(option =>
            option.Value.Equals("it", StringComparison.OrdinalIgnoreCase));
        (EmbeddedMagicShapeOption Shape, EmbeddedMagicMaterialOption Material, EmbeddedMagicSpellOption Spell) valid =
            context.Catalog.Shapes
                .SelectMany(shape => context.Catalog.GetCompatibleMaterials(shape.Value)
                    .SelectMany(material => context.Catalog.Spells
                        .Select(spell => (Shape: shape, Material: material, Spell: spell))))
                .First(candidate => context.Catalog.Evaluate(
                    $"{candidate.Shape.Value}.{candidate.Material.Value}",
                    candidate.Spell.Value,
                    [modifier.Value]).IsAllowedForManualInsertion);
        EmbeddedMagicShapeOption shape = valid.Shape;
        EmbeddedMagicMaterialOption material = valid.Material;
        EmbeddedMagicSpellOption spell = valid.Spell;
        string value = $"{shape.Value}.{material.Value}[{spell.Value}{{{modifier.Value}}}]";
        var dialog = new StructuredStringListEditorDialog(
            "AX1",
            "Гарантированный лут",
            [value],
            [],
            _ => (true, null),
            magicContext: context,
            allowedElementKinds:
            [
                StructuredElementKind.RawValue,
                StructuredElementKind.Weapon,
                StructuredElementKind.Armor,
            ]);

        StructuredElementKind expectedItemKind = shape.TableKey == "items.weapons"
            ? StructuredElementKind.Weapon
            : StructuredElementKind.Armor;
        if (dialog.FindName("ElementTypeComboBox") is not ComboBox
                { SelectedItem: StructuredElementKindChoice selectedType } ||
            selectedType.Kind != expectedItemKind ||
            dialog.FindName("MagicConstructorPanel") is not StackPanel magicPanel ||
            magicPanel.Visibility != Visibility.Visible ||
            dialog.FindName("GenericElementEditorPanel") is not StackPanel genericPanel ||
            genericPanel.Visibility != Visibility.Collapsed ||
            dialog.FindName("ItemConstructorPanel") is not StackPanel { Visibility: Visibility.Visible } ||
            dialog.FindName("EmbedSpellCheckBox") is not CheckBox { IsChecked: true } ||
            dialog.FindName("SpellConstructorPanel") is not StackPanel { Visibility: Visibility.Visible } ||
            dialog.FindName("MagicShapeComboBox") is not ComboBox { SelectedItem: EmbeddedMagicChoice } ||
            dialog.FindName("MagicMaterialComboBox") is not ComboBox { SelectedItem: EmbeddedMagicChoice } ||
            dialog.FindName("MagicSpellComboBox") is not ComboBox { SelectedItem: EmbeddedMagicChoice } ||
            dialog.FindName("MagicModifiersList") is not ItemsControl { Items.Count: 1 } ||
            dialog.FindName("MagicCapacityText") is not TextBlock capacity ||
            !capacity.Text.Contains("Сложность:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Вложенная магия должна открываться типизированным конструктором формы, материала, заклинания и отдельных рун.");
        }

        dialog.Items[0].Value = $"{shape.Value}.{material.Value}[{spell.Value}{{__missing_modifier__}}]";
        if (dialog.TryCommit() || dialog.WasApplied)
        {
            throw new InvalidOperationException(
                "Изменённая комбинация с неизвестной руной должна блокироваться до записи в RES.");
        }

        var itemBuilderDialog = new StructuredStringListEditorDialog(
            "AX1B",
            "Гарантированный лут",
            [string.Empty],
            [],
            _ => (true, null),
            magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, expectedItemKind]);
        var itemTypeComboBox = (ComboBox)itemBuilderDialog.FindName("ElementTypeComboBox")!;
        itemTypeComboBox.SelectedItem = itemTypeComboBox.Items.Cast<StructuredElementKindChoice>()
            .First(choice => choice.Kind == expectedItemKind);
        var itemShapeComboBox = (ComboBox)itemBuilderDialog.FindName("MagicShapeComboBox")!;
        itemShapeComboBox.SelectedItem = itemShapeComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(shape.Value, StringComparison.OrdinalIgnoreCase));
        var itemMaterialComboBox = (ComboBox)itemBuilderDialog.FindName("MagicMaterialComboBox")!;
        itemMaterialComboBox.SelectedItem = itemMaterialComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(material.Value, StringComparison.OrdinalIgnoreCase));
        var itemEmbedSpellCheckBox = (CheckBox)itemBuilderDialog.FindName("EmbedSpellCheckBox")!;
        itemEmbedSpellCheckBox.IsChecked = true;
        var itemSpellComboBox = (ComboBox)itemBuilderDialog.FindName("MagicSpellComboBox")!;
        itemSpellComboBox.SelectedItem = itemSpellComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(spell.Value, StringComparison.OrdinalIgnoreCase));
        ((Button)itemBuilderDialog.FindName("UpdateElementButton")!).RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        if (itemBuilderDialog.Items[0].Value != value ||
            itemBuilderDialog.Items[0].Kind != (expectedItemKind == StructuredElementKind.Weapon
                ? "Оружие с магией"
                : "Броня с магией"))
        {
            throw new InvalidOperationException(
                "Новый предмет должен собираться из формы, совместимого материала, заклинания и руны без ручного ввода RES-строки.");
        }

        var spellDialog = new StructuredStringListEditorDialog(
            "AX2",
            "Заклинания",
            [string.Empty],
            [],
            _ => (true, null),
            magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Spell]);
        if (spellDialog.FindName("ElementTypeComboBox") is not ComboBox
                { SelectedItem: StructuredElementKindChoice spellType } spellTypeComboBox ||
            spellType.Kind != StructuredElementKind.Spell ||
            spellTypeComboBox.Items.Count != 2 ||
            spellDialog.FindName("ItemConstructorPanel") is not StackPanel { Visibility: Visibility.Collapsed } ||
            spellDialog.FindName("SpellConstructorPanel") is not StackPanel { Visibility: Visibility.Visible } ||
            spellDialog.FindName("MagicSpellComboBox") is not ComboBox spellComboBox ||
            spellDialog.FindName("MagicModifierComboBox") is not ComboBox modifierComboBox ||
            spellDialog.FindName("UpdateElementButton") is not Button spellUpdateButton)
        {
            throw new InvalidOperationException(
                "Поле заклинаний должно сразу открывать выбор заклинания/боевого навыка и рун.");
        }

        spellComboBox.SelectedItem = spellComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(spell.Value, StringComparison.OrdinalIgnoreCase));
        modifierComboBox.SelectedItem = modifierComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(modifier.Value, StringComparison.OrdinalIgnoreCase));
        ((Button)spellDialog.FindName("AddMagicModifierButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        spellUpdateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (spellDialog.Items[0].Value != $"{spell.Value}{{{modifier.Value}}}" ||
            spellDialog.Items[0].Kind != "Заклинание")
        {
            throw new InvalidOperationException(
                "Выбранные заклинание и руна должны собираться в один физический элемент RES.");
        }

        var armorDialog = new StructuredStringListEditorDialog(
            "AX3",
            "Варианты доспехов",
            [string.Empty],
            [],
            _ => (true, null),
            magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Armor]);
        if (armorDialog.FindName("ElementTypeComboBox") is not ComboBox
                { SelectedItem: StructuredElementKindChoice armorType } ||
            armorType.Kind != StructuredElementKind.Armor ||
            ((ComboBox)armorDialog.FindName("ElementTypeComboBox")).Items.Count != 2 ||
            armorDialog.FindName("MagicShapeComboBox") is not ComboBox armorShapeComboBox ||
            armorShapeComboBox.Items.Cast<EmbeddedMagicChoice>().Any(choice =>
                context.Catalog.Shapes.First(option => option.Value == choice.Value).TableKey != "items.armors") ||
            armorDialog.FindName("EmbedSpellCheckBox") is not CheckBox { IsChecked: false })
        {
            throw new InvalidOperationException(
                "Варианты доспехов должны сразу открывать конструктор брони без форм оружия.");
        }

        EmbeddedMagicShapeOption armorShape = context.Catalog.Shapes.First(option =>
            option.TableKey == "items.armors" &&
            context.Catalog.GetCompatibleMaterials(option.Value).Count > 0);
        EmbeddedMagicMaterialOption armorMaterial = context.Catalog.GetCompatibleMaterials(armorShape.Value)[0];
        armorShapeComboBox.SelectedItem = armorShapeComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(armorShape.Value, StringComparison.OrdinalIgnoreCase));
        var armorMaterialComboBox = (ComboBox)armorDialog.FindName("MagicMaterialComboBox")!;
        armorMaterialComboBox.SelectedItem = armorMaterialComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(armorMaterial.Value, StringComparison.OrdinalIgnoreCase));
        ((Button)armorDialog.FindName("UpdateElementButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (armorDialog.Items[0].Value != $"{armorShape.Value}.{armorMaterial.Value}" ||
            armorDialog.Items[0].Kind != "Броня")
        {
            throw new InvalidOperationException(
                "Броня без магии должна собираться из формы и совместимого материала.");
        }

        var weaponDialog = new StructuredStringListEditorDialog(
            "AX4",
            "Оружие 1",
            [string.Empty],
            [],
            _ => (true, null),
            magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Weapon]);
        EmbeddedMagicShapeOption weaponShape = context.Catalog.Shapes.First(option =>
            option.TableKey == "items.weapons" &&
            context.Catalog.GetCompatibleMaterials(option.Value).Count > 0);
        EmbeddedMagicMaterialOption weaponMaterial = context.Catalog.GetCompatibleMaterials(weaponShape.Value)[0];
        var weaponShapeComboBox = (ComboBox)weaponDialog.FindName("MagicShapeComboBox")!;
        weaponShapeComboBox.SelectedItem = weaponShapeComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(weaponShape.Value, StringComparison.OrdinalIgnoreCase));
        var weaponMaterialComboBox = (ComboBox)weaponDialog.FindName("MagicMaterialComboBox")!;
        weaponMaterialComboBox.SelectedItem = weaponMaterialComboBox.Items.Cast<EmbeddedMagicChoice>()
            .First(choice => choice.Value.Equals(weaponMaterial.Value, StringComparison.OrdinalIgnoreCase));
        ((Button)weaponDialog.FindName("UpdateElementButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (weaponDialog.Items[0].Value != $"{weaponShape.Value}.{weaponMaterial.Value}" ||
            weaponDialog.Items[0].Kind != "Оружие")
        {
            throw new InvalidOperationException(
                "Оружие без магии должно собираться из формы и совместимого материала.");
        }
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
        Button? saveSoundCurrentButton = window.FindName("SavePendingSoundCurrentButton") as Button;
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
                saveSoundCurrentButton?.IsEnabled != true ||
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
            string originalTitle = text.Title;
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

            ManagedBackupViewModel databaseBackup = saved.Workspace.ManagedBackups.Single(backup =>
                backup.Kind == ManagedResKind.Database);
            ManagedBackupViewModel textsBackup = saved.Workspace.ManagedBackups.Single(backup =>
                backup.Kind == ManagedResKind.Texts);
            if (!databaseBackup.HasBackup || !textsBackup.HasBackup)
            {
                throw new InvalidOperationException(
                    "После парного сохранения обзор должен показать отдельные backup database.res и texts.res.");
            }

            RestoreManagedBackupResult restoredDatabase = service.RestoreManagedBackupAsync(
                    databaseBackup,
                    allowGameInstallationPath: false,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            RecordRowViewModel databaseRestoredMaterial = restoredDatabase.Workspace.Databases
                .SelectMany(group => group.Tables)
                .Single(table => table.Key == "items.materials")
                .Records.First();
            if (databaseRestoredMaterial.GetCell(4)?.EditorText != originalPrice ||
                !databaseRestoredMaterial.RelatedTexts.Any(entry => entry.Title == editedTitle))
            {
                throw new InvalidOperationException(
                    "Отдельный откат database.res должен восстановить базу, не откатывая texts.res.");
            }

            ManagedBackupViewModel currentTextsBackup = restoredDatabase.Workspace.ManagedBackups.Single(backup =>
                backup.Kind == ManagedResKind.Texts);
            RestoreManagedBackupResult restoredTexts = service.RestoreManagedBackupAsync(
                    currentTextsBackup,
                    allowGameInstallationPath: false,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            RecordRowViewModel fullyRestoredMaterial = restoredTexts.Workspace.Databases
                .SelectMany(group => group.Tables)
                .Single(table => table.Key == "items.materials")
                .Records.First();
            if (fullyRestoredMaterial.GetCell(4)?.EditorText != originalPrice ||
                !fullyRestoredMaterial.RelatedTexts.Any(entry => entry.Title == originalTitle) ||
                restoredTexts.Workspace.ManagedBackups
                    .Where(backup => backup.Kind is ManagedResKind.Database or ManagedResKind.Texts)
                    .Any(backup => !backup.HasBackup || backup.Backup?.BackupCount != 2))
            {
                throw new InvalidOperationException(
                    "Раздельный откат texts.res должен восстановить текст и сохранить по две обратимые backup-копии.");
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
        var closeWindow = new MainWindow(
            new EditorPreferencesStore(Path.Combine(settingsRoot, "settings.json")),
            new RecoverySnapshotStore(Path.Combine(settingsRoot, "recovery")));
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
            tableGap < 0 || tableGap > 8)
        {
            throw new InvalidOperationException(
                "Основная и связанная таблицы должны использовать единую читаемую типографику, " +
                "а строку формул должен отделять от таблицы только узкий рабочий разделитель. " +
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
        if (cellText.TextAlignment != TextAlignment.Center ||
            numericText.TextAlignment != TextAlignment.Center)
        {
            throw new InvalidOperationException(
                "Excel-подобная сетка должна центрировать текстовые и числовые значения.");
        }

        recordsGrid.CurrentCell = new DataGridCellInfo(firstRecord, nameColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(firstRecord, nameColumn));
        if (!recordsGrid.BeginEdit())
        {
            throw new InvalidOperationException(
                "Проверочная редактируемая ячейка не перешла в режим ввода.");
        }

        recordsGrid.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        TextBox editingText = FindVisualChild<TextBox>(nameColumn.GetCellContent(firstRecord))
            ?? throw new InvalidOperationException("Не найден редактор Excel-клетки после F2/двойного щелчка.");
        if (editingText.TextAlignment != TextAlignment.Center ||
            editingText.VerticalContentAlignment != VerticalAlignment.Center)
        {
            throw new InvalidOperationException(
                "Текст должен оставаться по центру после перехода ячейки в режим редактирования.");
        }

        recordsGrid.CancelEdit(DataGridEditingUnit.Cell);
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

        DatabaseTableViewModel npcs = viewModel.Databases
            .SelectMany(group => group.Tables)
            .Single(table => table.Key == "units.npcs");
        viewModel.SelectTableCommand.Execute(npcs);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        RecordRowViewModel npc = npcs.Records[0];
        SpreadsheetColumnViewModel tameColumn = npcs.Columns.Single(column => column.Title == "Приручение");
        DataGridColumn tameGridColumn = recordsGrid.Columns.Single(column => ReferenceEquals(column.Header, tameColumn));
        RecordFieldViewModel manualSkills = npc.GetCell(tameColumn.FieldId)
            ?? throw new InvalidOperationException("Не найден исходный список ручных навыков NPC.");
        string originalTame = manualSkills.GetProjectedText(tameColumn.Projection!);
        string originalManualSkillsRaw = manualSkills.EditorText;
        byte originalTameValue = byte.Parse(originalTame, CultureInfo.InvariantCulture);
        string changedTame = (originalTameValue == byte.MaxValue ? originalTameValue - 1 : originalTameValue + 1)
            .ToString(CultureInfo.InvariantCulture);
        recordsGrid.CurrentCell = new DataGridCellInfo(npc, tameGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(npc, tameGridColumn));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        string? skillError = null;
        if (!tameColumn.IsEditable ||
            tameGridColumn.IsReadOnly ||
            editorBox.IsReadOnly ||
            !applyButton.IsEnabled ||
            !window.TryApplyFormulaValue(changedTame, out skillError) ||
            skillError is not null ||
            manualSkills.GetProjectedText(tameColumn.Projection!) != changedTame ||
            manualSkills.EditorText == originalManualSkillsRaw ||
            viewModel.ChangeCount != 1)
        {
            throw new InvalidOperationException(
                $"Приручение должно редактироваться как обычная ячейка и синхронизировать ByteList: {skillError}; " +
                $"descriptorEditable={tameColumn.IsEditable}; gridReadOnly={tameGridColumn.IsReadOnly}; " +
                $"formulaReadOnly={editorBox.IsReadOnly}; apply={applyButton.IsEnabled}; " +
                $"projected={manualSkills.GetProjectedText(tameColumn.Projection!)}; expected={changedTame}; " +
                $"rawChanged={manualSkills.EditorText != originalManualSkillsRaw}; changes={viewModel.ChangeCount}");
        }

        if (window.TryApplyFormulaValue("256", out skillError) ||
            string.IsNullOrWhiteSpace(skillError) ||
            manualSkills.GetProjectedText(tameColumn.Projection!) != changedTame)
        {
            throw new InvalidOperationException(
                "Приручение должно отклонять значение 256 без изменения уже записанного элемента.");
        }

        viewModel.UndoCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (manualSkills.GetProjectedText(tameColumn.Projection!) != originalTame ||
            manualSkills.EditorText != originalManualSkillsRaw ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Undo должен одновременно восстановить Приручение и исходный список ручных навыков.");
        }

        SpreadsheetColumnViewModel[] manualSkillColumns = new[] { "Наука", "Кража", "Приручение" }
            .Select(title => npcs.Columns.Single(column => column.Title == title))
            .ToArray();
        string[] originalManualSkillValues = manualSkillColumns
            .Select(column => manualSkills.GetProjectedText(column.Projection!))
            .ToArray();
        string[] changedManualSkillValues = originalManualSkillValues
            .Select(value =>
            {
                byte parsed = byte.Parse(value, CultureInfo.InvariantCulture);
                return (parsed == byte.MaxValue ? parsed - 1 : parsed + 1).ToString(CultureInfo.InvariantCulture);
            })
            .ToArray();
        DataGridColumn scienceGridColumn = recordsGrid.Columns.Single(column =>
            ReferenceEquals(column.Header, manualSkillColumns[0]));
        recordsGrid.CurrentCell = new DataGridCellInfo(npc, scienceGridColumn);
        recordsGrid.SelectedCells.Clear();
        recordsGrid.SelectedCells.Add(new DataGridCellInfo(npc, scienceGridColumn));
        IReadOnlyList<IReadOnlyList<string>> skillsPasteMatrix =
            SpreadsheetClipboardText.Parse(string.Join('\t', changedManualSkillValues));
        if (!window.TryBuildPasteCells(
                skillsPasteMatrix,
                out IReadOnlyList<SpreadsheetPasteCell> skillsPasteCells,
                out skillError) ||
            !viewModel.TryPasteSpreadsheetCells(skillsPasteCells, out int skillsPasteCount, out skillError) ||
            skillsPasteCount != 3 ||
            !manualSkillColumns
                .Select(column => manualSkills.GetProjectedText(column.Projection!))
                .SequenceEqual(changedManualSkillValues, StringComparer.Ordinal) ||
            viewModel.ChangeCount != 1)
        {
            throw new InvalidOperationException(
                $"Ctrl+V должен атомарно вставлять три соседних навыка NPC в один ByteList: {skillError}");
        }

        viewModel.UndoCommand.Execute(null);
        if (!manualSkillColumns
                .Select(column => manualSkills.GetProjectedText(column.Projection!))
                .SequenceEqual(originalManualSkillValues, StringComparer.Ordinal) ||
            manualSkills.EditorText != originalManualSkillsRaw ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Один Undo должен полностью отменять прямоугольную Excel-вставку в навыки NPC.");
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
             !Equals(openEditorButton.ToolTip, "Открыть структурный редактор списка") ||
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

        string originalListText = longValueField.EditorText;
        if (window.TryApplyFormulaValue(originalListText + "[", out string? malformedListError) ||
            string.IsNullOrWhiteSpace(malformedListError) ||
            longValueField.EditorText != originalListText ||
            viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Сырая строка StringList с незакрытой скобкой должна отклоняться без изменения поля и Undo-журнала.");
        }

        if (!longValueField.TryGetStringListValues(out IReadOnlyList<string> originalValues) || originalValues.Count == 0)
        {
            throw new InvalidOperationException("Для проверки структурного редактора не удалось прочитать элементы StringList.");
        }

        string[] changedValues = originalValues.ToArray();
        changedValues[0] += " codex-probe";
        if (!longValueField.TrySetStringListValues(changedValues, out string? listError) ||
            listError is not null ||
            viewModel.ChangeCount != 1 ||
            !viewModel.UndoCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                $"Структурное применение StringList должно создавать одну общую Undo-операцию: {listError}");
        }

        viewModel.UndoCommand.Execute(null);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (longValueField.EditorText != originalListText || viewModel.ChangeCount != 0)
        {
            throw new InvalidOperationException(
                "Один Undo должен полностью восстановить StringList после структурного применения.");
        }

        foreach (int weaponFieldId in new[] { 33, 42 })
        {
            SpreadsheetColumnViewModel weaponColumn = prototypes.Columns.Single(column =>
                column.FieldId == weaponFieldId && column.Projection is null);
            RecordFieldViewModel weaponField = longValueRecord.GetCell(weaponFieldId)
                ?? throw new InvalidOperationException($"У прототипа отсутствует поле оружия {weaponFieldId}.");
            DataGridColumn weaponGridColumn = recordsGrid.Columns.Single(column =>
                ReferenceEquals(column.Header, weaponColumn));
            recordsGrid.CurrentCell = new DataGridCellInfo(longValueRecord, weaponGridColumn);
            recordsGrid.SelectedCells.Clear();
            recordsGrid.SelectedCells.Add(new DataGridCellInfo(longValueRecord, weaponGridColumn));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            if (weaponField.Kind != "String" ||
                !openEditorButton.IsEnabled ||
                !Equals(openEditorButton.ToolTip, "Открыть структурный редактор значения"))
            {
                throw new InvalidOperationException(
                    $"{weaponColumn.Title} должно открываться как один составной объект оружия с вложенной магией. " +
                    $"kind={weaponField.Kind}, enabled={openEditorButton.IsEnabled}, tooltip={openEditorButton.ToolTip}.");
            }
        }

        viewModel.SelectTableCommand.Execute(materials);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static void VerifyAuxiliarySpreadsheetClipboard(
        MainWindow window,
        MainWindowViewModel viewModel,
        DatabaseTableViewModel returnTable)
    {
        if (!viewModel.HasTextsDocument)
        {
            return;
        }

        viewModel.SelectTableCommand.Execute(returnTable);
        RecordRowViewModel record = returnTable.Records.First(candidate => candidate.RelatedTextCount >= 2);
        viewModel.SelectedRecord = record;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var localizationGrid = (DataGrid)window.FindName("LocalizationGrid");
        LocalizationEntryViewModel[] relatedRows = localizationGrid.Items.Cast<object>()
            .OfType<LocalizationEntryViewModel>()
            .Take(2)
            .ToArray();
        DataGridColumn localizationTitle = localizationGrid.Columns.Single(column => Equals(column.Header, "Название RU"));
        DataGridColumn localizationDescription = localizationGrid.Columns.Single(column => Equals(column.Header, "Описание RU"));
        if (relatedRows.Length != 2)
        {
            throw new InvalidOperationException("Для проверки связанных текстов нужны две строки texts.res.");
        }

        localizationGrid.SelectedCells.Clear();
        foreach (LocalizationEntryViewModel row in relatedRows)
        {
            localizationGrid.SelectedCells.Add(new DataGridCellInfo(row, localizationTitle));
            localizationGrid.SelectedCells.Add(new DataGridCellInfo(row, localizationDescription));
        }
        localizationGrid.CurrentCell = new DataGridCellInfo(relatedRows[0], localizationTitle);
        if (!ApplicationCommands.Copy.CanExecute(null, localizationGrid) ||
            !window.TryBuildClipboardMatrix(localizationGrid, out IReadOnlyList<IReadOnlyList<string>> relatedMatrix) ||
            relatedMatrix.Count != 2 ||
            !relatedMatrix[0].SequenceEqual([relatedRows[0].Title, relatedRows[0].Description], StringComparer.Ordinal) ||
            !relatedMatrix[1].SequenceEqual([relatedRows[1].Title, relatedRows[1].Description], StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Нижняя таблица «Связанные тексты» должна копировать прямоугольный диапазон названий и описаний в Excel.");
        }

        string[][] originalRelated = relatedRows
            .Select(row => new[] { row.Title, row.Description })
            .ToArray();
        IReadOnlyList<IReadOnlyList<string>> relatedPaste =
        [
            [originalRelated[0][0] + " Excel", originalRelated[0][1] + " Excel"],
            [originalRelated[1][0] + " Excel", originalRelated[1][1] + " Excel"],
        ];
        localizationGrid.SelectedCells.Clear();
        foreach (LocalizationEntryViewModel row in relatedRows)
        {
            localizationGrid.SelectedCells.Add(new DataGridCellInfo(row, localizationTitle));
            localizationGrid.SelectedCells.Add(new DataGridCellInfo(row, localizationDescription));
        }
        // Reproduce a backwards/drag selection: WPF may keep the active cell at the
        // bottom-right even though Excel semantics require paste to start at top-left.
        localizationGrid.CurrentCell = new DataGridCellInfo(relatedRows[1], localizationDescription);
        if (!window.TryBuildLocalizationPasteCells(
                localizationGrid,
                relatedPaste,
                out IReadOnlyList<LocalizationSpreadsheetPasteCell> relatedCells,
                out string? relatedError) ||
            !viewModel.TryPasteLocalizationSpreadsheetCells(relatedCells, out int relatedChanged, out relatedError) ||
            relatedChanged != 4)
        {
            throw new InvalidOperationException(
                $"Нижняя таблица «Связанные тексты» не приняла диапазон из Excel: {relatedError}");
        }

        viewModel.UndoCommand.Execute(null);
        if (relatedRows.Where((row, index) =>
                row.Title != originalRelated[index][0] || row.Description != originalRelated[index][1]).Any())
        {
            throw new InvalidOperationException(
                "Undo должен целиком вернуть пакетную вставку в нижнюю таблицу связанных текстов.");
        }

        viewModel.NavigateCommand.Execute(viewModel.TextsNavigationItem);
        viewModel.TextsSearchText = string.Empty;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var textsGrid = (DataGrid)window.FindName("TextsGrid");
        DataGridColumn textsTitle = textsGrid.Columns.Single(column => Equals(column.Header, "Название RU"));
        LocalizationEntryViewModel[] bulkRows = textsGrid.Items.Cast<object>()
            .OfType<LocalizationEntryViewModel>()
            .Where(entry => !entry.IsNew)
            .Take(32)
            .ToArray();
        if (bulkRows.Length < 16)
        {
            throw new InvalidOperationException("Недостаточно строк texts.res для проверки пакетной вставки.");
        }

        string[] originalTitles = bulkRows.Select(entry => entry.Title).ToArray();
        IReadOnlyList<IReadOnlyList<string>> bulkPaste = bulkRows
            .Select((entry, index) => (IReadOnlyList<string>)[entry.Title + $" Excel {index + 1}"])
            .ToArray();
        textsGrid.SelectedCells.Clear();
        textsGrid.SelectedCells.Add(new DataGridCellInfo(bulkRows[0], textsTitle));
        textsGrid.CurrentCell = new DataGridCellInfo(bulkRows[0], textsTitle);

        var recordsViewNotifications = (INotifyCollectionChanged)viewModel.RecordsView;
        var textsViewNotifications = (INotifyCollectionChanged)viewModel.TextsView;
        var recordsResets = 0;
        var textsResets = 0;
        NotifyCollectionChangedEventHandler recordsChanged = (_, eventArgs) =>
        {
            if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
            {
                recordsResets++;
            }
        };
        NotifyCollectionChangedEventHandler textsChanged = (_, eventArgs) =>
        {
            if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
            {
                textsResets++;
            }
        };
        recordsViewNotifications.CollectionChanged += recordsChanged;
        textsViewNotifications.CollectionChanged += textsChanged;
        var stopwatch = Stopwatch.StartNew();
        bool built = window.TryBuildLocalizationPasteCells(
            textsGrid,
            bulkPaste,
            out IReadOnlyList<LocalizationSpreadsheetPasteCell> bulkCells,
            out string? bulkError);
        var bulkChanged = 0;
        bool pasted = built && viewModel.TryPasteLocalizationSpreadsheetCells(
            bulkCells,
            out bulkChanged,
            out bulkError);
        stopwatch.Stop();
        recordsViewNotifications.CollectionChanged -= recordsChanged;
        textsViewNotifications.CollectionChanged -= textsChanged;
        if (!pasted || bulkChanged != bulkRows.Length || recordsResets != 0 || textsResets != 0 ||
            stopwatch.Elapsed > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException(
                "Пакетная вставка в texts.res должна выполняться одной операцией без зависания и полных перестроений. " +
                $"built={built}; pasted={pasted}; changed={bulkChanged}/{bulkRows.Length}; " +
                $"recordsReset={recordsResets}; textsReset={textsResets}; elapsed={stopwatch.Elapsed}; error={bulkError}");
        }

        viewModel.UndoCommand.Execute(null);
        if (bulkRows.Where((entry, index) => entry.Title != originalTitles[index]).Any() || viewModel.HasChanges)
        {
            throw new InvalidOperationException(
                "Undo должен одной командой вернуть большую пакетную вставку из Excel в texts.res.");
        }

        viewModel.SelectTableCommand.Execute(returnTable);
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

    private static void VerifyStructuredStringListEditorDialog()
    {
        const string embeddedMagic = "2h metal battle axe.astral metall[lightning{it;e3;e2;m3;im3}]";
        if (!StringListEditorCodec.TryParseEditorText(
                $"first value; {embeddedMagic}; last value",
                out IReadOnlyList<string> parsed,
                out string? parseError) ||
            parseError is not null ||
            parsed.Count != 3 ||
            parsed[1] != embeddedMagic)
        {
            throw new InvalidOperationException(
                $"Строковый список должен сохранять разделители вложенной магии внутри одного элемента: {parseError}");
        }

        if (StringListEditorCodec.TryParseEditorText(
                "first; weapon.material[lightning{it;e3}; last",
                out _,
                out string? bracketError) ||
            string.IsNullOrWhiteSpace(bracketError))
        {
            throw new InvalidOperationException(
                "Несбалансированные квадратные/фигурные скобки должны блокировать применение до изменения TLV-границ.");
        }

        StringListElementAnalysis stepSound = StringListEditorCodec.AnalyzeElement(
            @"steps\Human\Grass",
            "Звуки шагов");
        if (stepSound.Kind != "Звук шага" ||
            stepSound.SpellCode is not null ||
            !stepSound.Summary.Contains("модель: Human", StringComparison.Ordinal) ||
            !stepSound.Summary.Contains("поверхность: Grass", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Пути steps\\… должны разбираться как блоки звука шага, а не как предметы или заклинания.");
        }

        StringListElementAnalysis nonMagicBlock = StringListEditorCodec.AnalyzeElement(
            "animation{walk;fast}",
            "Блоки анимации");
        if (nonMagicBlock.Kind == "Заклинание" || nonMagicBlock.SpellCode is not null)
        {
            throw new InvalidOperationException(
                "Фигурные параметры неизвестного немагического списка не должны автоматически называться заклинанием.");
        }

        StringListElementAnalysis bareSpell = StringListEditorCodec.AnalyzeElement(
            "powerhit",
            "Заклинания");
        if (bareSpell.Kind != "Заклинание" ||
            bareSpell.SpellCode != "powerhit" ||
            bareSpell.Modifiers.Count != 0)
        {
            throw new InvalidOperationException(
                "Заклинание или боевой навык без рун должны оставаться типизированным элементом.");
        }

        IReadOnlyList<string>? appliedValues = null;
        var dialog = new StructuredStringListEditorDialog(
            "AX1",
            "Гарантированный лут",
            ["first value", embeddedMagic],
            ["long sword.mithrill", "light metal axe.astral metall"],
            values =>
            {
                appliedValues = values.ToArray();
                return (true, null);
            });

        var itemsGrid = dialog.FindName("ItemsGrid") as DataGrid;
        if (dialog.Width < 1000 ||
            dialog.Height < 680 ||
            itemsGrid is null ||
            dialog.FindName("ElementDetailsScrollViewer") is not ScrollViewer detailsScrollViewer ||
            detailsScrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Auto ||
            detailsScrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ||
            dialog.FindName("RawEditorBox") is not TextBox ||
            dialog.FindName("StructuredPanel") is not Grid ||
            dialog.FindName("RawPanel") is not Grid ||
            dialog.FindName("StructuredModeButton") is not System.Windows.Controls.Primitives.ToggleButton ||
            dialog.FindName("MagicConstructorPanel") is not StackPanel ||
            dialog.FindName("MagicShapeComboBox") is not ComboBox magicShapeComboBox ||
            dialog.FindName("MagicMaterialComboBox") is not ComboBox ||
            dialog.FindName("MagicSpellComboBox") is not ComboBox ||
            dialog.FindName("MagicModifiersList") is not ItemsControl ||
            dialog.FindName("MagicCapacityText") is not TextBlock ||
            dialog.Items.Count != 2 ||
            itemsGrid.Items.Count != 2)
        {
            throw new InvalidOperationException(
                "Структурный редактор списка должен открываться крупным и содержать табличный и сырой режимы. " +
                $"width={dialog.Width}, height={dialog.Height}, grid={itemsGrid is not null}, " +
                $"raw={dialog.FindName("RawEditorBox") is TextBox}, model={dialog.Items.Count}, visual={itemsGrid?.Items.Count ?? -1}.");
        }

        if (magicShapeComboBox.Template is null ||
            magicShapeComboBox.ItemContainerStyle is null ||
            magicShapeComboBox.MaxDropDownHeight > 300.1 ||
            !magicShapeComboBox.ItemContainerStyle.Setters.OfType<Setter>()
                .Any(setter => setter.Property == Control.BackgroundProperty) ||
            !magicShapeComboBox.ItemContainerStyle.Setters.OfType<Setter>()
                .Any(setter => setter.Property == Control.ForegroundProperty))
        {
            throw new InvalidOperationException(
                "Выпадающие списки конструктора должны явно задавать тёмный фон, читаемый цвет текста и ограниченную высоту.");
        }


        VerifyDarkComboBoxTemplate(magicShapeComboBox, "Форма оружия или доспеха");
        VerifyDarkComboBoxTemplate(
            (ComboBox)dialog.FindName("MagicMaterialComboBox"),
            "Материал предмета");
        VerifyDarkComboBoxTemplate(
            (ComboBox)dialog.FindName("MagicSpellComboBox"),
            "Заклинание предмета");
        VerifyDarkComboBoxTemplate(
            (ComboBox)dialog.FindName("MagicModifierComboBox"),
            "Руны и модификаторы");

        IReadOnlyList<string>? appliedSingleValue = null;
        var singleValueDialog = new StructuredStringListEditorDialog(
            "AG1",
            "Оружие 1",
            [embeddedMagic],
            ["long sword.mithrill"],
            values =>
            {
                appliedSingleValue = values.ToArray();
                return (true, null);
            },
            singleValueMode: true);
        if (singleValueDialog.FindName("ListActionsBorder") is not Border singleActions ||
            singleActions.Visibility != Visibility.Collapsed ||
            singleValueDialog.FindName("DialogTitleText") is not TextBlock singleTitle ||
            singleTitle.Text != "Структурный редактор значения" ||
            singleValueDialog.Items.Count != 1)
        {
            throw new InvalidOperationException(
                "Скалярные поля Оружие 1/2 должны использовать структурный режим одного значения без команд списка.");
        }

        singleValueDialog.Items[0].Value = "bone sword.scrab bones[lightning{it;m0;im0}]";
        if (!singleValueDialog.TryCommit() ||
            !singleValueDialog.WasApplied ||
            appliedSingleValue is null ||
            appliedSingleValue.Count != 1 ||
            appliedSingleValue[0] != "bone sword.scrab bones[lightning{it;m0;im0}]")
        {
            throw new InvalidOperationException(
                "Структурный редактор скалярного оружия должен вернуть ровно один проверенный составной объект.");
        }


        var stepSoundDialog = new StructuredStringListEditorDialog(
            "BC1",
            "Звуки шагов",
            [@"steps\Human\Grass"],
            [],
            _ => (true, null));
        if (stepSoundDialog.Items[0].Kind != "Звук шага" ||
            stepSoundDialog.FindName("SpellCodePanel") is not StackPanel spellPanel ||
            spellPanel.Visibility != Visibility.Collapsed ||
            stepSoundDialog.FindName("PrimaryValueLabel") is not TextBlock primaryLabel ||
            primaryLabel.Text != "Путь блока звука шага")
        {
            throw new InvalidOperationException(
                "Контекстная форма звука шага не должна показывать поля заклинания и модификаторов.");
        }

        dialog.Items[0].Value = "long sword.mithrill";
        if (!dialog.TryCommit() ||
            !dialog.WasApplied ||
            appliedValues is null ||
            appliedValues.Count != 2 ||
            appliedValues[0] != "long sword.mithrill" ||
            appliedValues[1] != embeddedMagic)
        {
            throw new InvalidOperationException(
                "Структурный редактор обязан передавать исходные элементы напрямую, без повторного деления вложенной магии.");
        }

        var rejectingDialog = new StructuredStringListEditorDialog(
            "AX1",
            "Гарантированный лут",
            ["weapon.material[lightning{it;e3}]"],
            [],
            _ => (true, null));
        rejectingDialog.Items[0].Value = "weapon.material[lightning{it;e3}";
        if (rejectingDialog.TryCommit() || rejectingDialog.WasApplied)
        {
            throw new InvalidOperationException(
                "Структурный редактор не должен применять элемент с незакрытой квадратной скобкой.");
        }
    }

    private static void VerifyStructuredRecordListEditorDialog()
    {
        IReadOnlyList<StructuredListFieldDescriptor> fields =
        [
            new(1, "Файл", "String", true),
            new(2, "Вероятность", "Int32", true),
            new(3, "Описание", "String", true),
            new(4, "Ключ", "String", true),
            new(10, "Флаги условий", "UInt32", true),
        ];
        IReadOnlyList<StructuredListEntryValue> source =
        [
            new(0, new Dictionary<int, string>
            {
                [1] = @"Acks\HH\Attack\9.wav",
                [2] = "25",
                [3] = "Атака",
                [4] = "Attack09",
                [10] = "641",
            }),
        ];
        IReadOnlyList<StructuredListEntryValue>? applied = null;
        var dialog = new StructuredRecordListEditorDialog(
            "F18",
            "Атака",
            fields,
            source,
            values =>
            {
                applied = values.ToArray();
                return (true, null);
            });

        if (dialog.Width < 1100 ||
            dialog.Height < 650 ||
            dialog.FindName("EntriesGrid") is not DataGrid entriesGrid ||
            entriesGrid.Items.Count != 1 ||
            dialog.FindName("AddButton") is not Button { IsEnabled: true } ||
            dialog.FindName("DuplicateButton") is not Button { IsEnabled: true } ||
            dialog.FindName("DeleteButton") is not Button { IsEnabled: true } ||
            dialog.FindName("MoveUpButton") is not Button { IsEnabled: false } ||
            dialog.FindName("ConditionsColumn") is not DataGridColumn { Visibility: Visibility.Visible } ||
            dialog.Entries[0].ConditionsText != "бег, день, снаружи")
        {
            throw new InvalidOperationException(
                "Редактор вариантов реплики должен показывать таблицу, расшифровку условий и действия над вариантами.");
        }

        dialog.Entries[0].FileName = @"Acks\HH\Attack\10.wav";
        dialog.Entries[0].Probability = "30";
        dialog.AddEntry();
        dialog.Entries[1].FileName = @"Acks\HH\Attack\11.wav";
        dialog.Entries[1].Probability = "20";
        dialog.Entries[1].Description = "Новый вариант";
        dialog.Entries[1].Key = "Attack11";
        dialog.Entries[1].Conditions = "0";
        dialog.DuplicateSelectedEntry();
        dialog.MoveSelectedEntry(-1);
        dialog.DeleteSelectedEntry();
        if (!dialog.TryCommit() ||
            !dialog.WasApplied ||
            applied is null ||
            applied.Count != 2 ||
            applied[0].Fields[1] != @"Acks\HH\Attack\10.wav" ||
            applied[0].Fields[2] != "30" ||
            applied[0].Fields[3] != "Атака" ||
            applied[0].Fields[10] != "641" ||
            applied[1].Fields[1] != @"Acks\HH\Attack\11.wav" ||
            applied[1].Fields[3] != "Новый вариант" ||
            applied[1].SourceIndex is not null)
        {
            throw new InvalidOperationException(
                "Редактор вариантов реплики должен добавлять, дублировать, перемещать и удалять строки без потери значений.");
        }

        IReadOnlyList<StructuredListEntryValue>? addedToEmpty = null;
        var emptyDialog = new StructuredRecordListEditorDialog(
            "G26",
            "Выбор",
            fields,
            [],
            values =>
            {
                addedToEmpty = values.ToArray();
                return (true, null);
            });
        if (emptyDialog.FindName("DuplicateButton") is not Button { IsEnabled: false } ||
            emptyDialog.FindName("DeleteButton") is not Button { IsEnabled: false })
        {
            throw new InvalidOperationException(
                "В пустом редакторе действия, требующие выбранного варианта, должны быть отключены.");
        }

        emptyDialog.AddEntry();
        emptyDialog.Entries[0].FileName = @"Acks\HH\Select\1.wav";
        emptyDialog.Entries[0].Probability = "100";
        emptyDialog.Entries[0].Description = "Выбор";
        emptyDialog.Entries[0].Key = "Select01";
        if (!emptyDialog.TryCommit() ||
            addedToEmpty is not { Count: 1 } ||
            addedToEmpty[0].Index != 0 ||
            addedToEmpty[0].Fields[1] != @"Acks\HH\Select\1.wav" ||
            addedToEmpty[0].Fields[2] != "100")
        {
            throw new InvalidOperationException(
                "Пустой список вариантов должен позволять создать и применить первую реплику.");
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

        public Task<DatabaseComparisonResult> CompareAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DatabaseReferenceSearchResult> FindReferencesAsync(
            string tableKey,
            int recordIndex,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<SaveSoundCurrentResult> SaveSoundCurrentAsync(
            PendingSoundEditViewModel edit,
            bool allowGameInstallationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RestoreManagedBackupResult> RestoreManagedBackupAsync(
            ManagedBackupViewModel backup,
            bool allowGameInstallationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SaveCurrentResult> SaveCurrentAsync(
            bool allowGameInstallationPath,
            CancellationToken cancellationToken, bool createBackup = true) =>
            throw new NotSupportedException();

        public Task<SavePairCopyResult> SaveCopyAsync(string targetPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
