using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.ViewModels;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class EquipmentEditorRegression
{
    internal static int Run(string source, string output)
    {
        var app = new EvilIslands.DatabaseEditor.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        typeof(Application).GetField("_startupUri", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, null); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // No MainWindow / preference store / map catalog is created in this harness.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Check(source, output); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Require(app.Windows.Cast<Window>().All(w => !w.IsVisible), "Headless test opened visible window"); app.Shutdown(); Console.WriteLine("PASS equipment draft, live material change, names, seven slot filter, Cancel, Apply/Undo, rendering, input unchanged"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Check(string source, string output)
    {
        PrototypeCombatRegression.Check();
        Directory.CreateDirectory(output); var hash = SHA256.HashData(File.ReadAllBytes(source));
        using var vm = new MainWindowViewModel(); await vm.OpenPathAsync(source);
        vm.SelectTableCommand.Execute(vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes"));
        var row = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes").Records.First(r => r.CoreRecord.IdentityValue == "Human Hero");
        vm.SelectedRecord = row;
        var field = row.GetCell(32)!; field.TryGetStringListValues(out var values); string before = field.EditorText;
        var records = vm.Databases.SelectMany(g => g.Tables).ToDictionary(t => t.Key, t => (IReadOnlyList<DatabaseRecord>)t.Records.Select(r => r.CoreRecord).ToArray());
        var titles = vm.TextResources.GroupBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var context = new EmbeddedMagicEditorContext(EmbeddedMagicCatalog.Create(records), titles);
        EquipmentShapeSearchRegression.CheckChoosing(context);
        var luaFolder = LuaDataWorkspace.FindNearby(source);
        Require(luaFolder is not null, "Calculation regression requires nearby Lua");
        var lua = LuaDataWorkspace.Open(luaFolder!);
        Require(PrototypeCombatCalculation.SupportedFormulas(File.ReadAllText(lua.SchemaSource)), "Installed formulas no longer match the calculation profile");
        Require(!PrototypeCombatCalculation.SupportedFormulas(File.ReadAllText(lua.SchemaSource).Replace("skill * 1 / 800", "skill * 1 / 700", StringComparison.Ordinal)), "Changed formula still accepted");
        var captured = records.SelectMany(t => t.Value.Select(r => ModelRecord.Capture(t.Key, r))).ToArray();
        var liveReport = EquipmentInspection.Inspect(captured.Single(r => r.Table == "units.prototypes" && r.Name == "Human Hero"), captured, lua);
        Require(liveReport.General.Any(r => r.Name == "Сила" && r.Value == "39"), "Live initial-prototype calculation unavailable");
        File.WriteAllText(Path.Combine(output, "calculation-live.txt"), string.Join(Environment.NewLine, liveReport.General.Select(r => r.Name + ": " + r.Value).Concat(liveReport.Notes)));
        StructuredStringListEditorDialog New() => new("BB1", "Варианты доспехов", values, context.Weapons.Select(c => c.Value).ToArray(),
            updated => { bool ok = field.TrySetStringListValues(updated, out string? error); return (ok, error); }, magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Armor]);
        var dialog = New(); dialog.ConfigureEquipment(vm, row, 32);
        dialog.Measure(new Size(1420, 900)); dialog.Arrange(new Rect(0, 0, 1420, 900)); dialog.UpdateLayout();
        await dialog.EquipmentLoadTask;
        var references = (ListBox)dialog.FindName("ReferenceResultsList");
        Require(references.Items.Cast<EmbeddedMagicChoice>().Any(c => c.DisplayText != c.Value), "References lack Russian names");
        var preview = (EvilIslands.DatabaseEditor.App.UnitModelPreviewView)dialog.FindName("EquipmentPreview");
        Require(preview.HasVisibleScene && preview.EditorAnimationName == "cidle09" && preview.AnimationPlaying && preview.EditorLoop,
            "Equipment must play cidle09 in a loop");
        double firstFrame = preview.AnimationFrame;
        await Task.Delay(120);
        Require(preview.AnimationFrame != firstFrame, "Idle animation did not advance");
        EquipmentShapeSearchRegression.CheckFiltering(dialog, armor: true);
        await dialog.EquipmentLoadTask;
        var slots = (ComboBox)dialog.FindName("EquipmentSlotCombo");
        Require(slots.Items.Count == 8, "Seven slots and All missing");
        for (int i = 1; i <= 7; i++)
        {
            slots.SelectedIndex = i;
            var choices = ((ComboBox)dialog.FindName("MagicShapeComboBox")).Items.Cast<EmbeddedMagicChoice>().ToArray();
            Require(choices.Length > 0 && choices.All(c => records["items.armors"].Any(r => r.IdentityValue == c.Value && Convert.ToInt32(r.Fields.Single(f => f.Id == 2).Value) == i - 1)), "Slot filter mismatch " + i);
        }
        // Selecting a duplicate slot must not replace the saved draft for statistics/model.
        dialog.SelectedItem = dialog.Items[0];
        var alternate = ((ComboBox)dialog.FindName("MagicMaterialComboBox")).Items.Cast<EmbeddedMagicChoice>().First(c => !dialog.Items[0].Value.EndsWith("." + c.Value)).Value;
        var duplicate = new StructuredStringListItemViewModel(dialog.Items[0].Value[..(dialog.Items[0].Value.LastIndexOf('.') + 1)] + alternate);
        dialog.Items.Add(duplicate);
        dialog.SelectedItem = dialog.Items[0];
        string StatText() => string.Join("|", ((StackPanel)dialog.FindName("EquipmentStatisticsPanel")).Children.OfType<Grid>()
            .SelectMany(g => g.Children.OfType<Border>()).Select(b => ((TextBlock)b.Child).Text));
        string stableStats = StatText();
        dialog.SelectedItem = duplicate;
        Require(StatText() == stableStats && dialog.EquipmentDraft!.Lists[32].SequenceEqual(dialog.Items.Select(i => i.Value)),
            "Selecting a row changed saved equipment statistics/order");
        dialog.Items.Remove(duplicate);
        dialog.SelectedItem = dialog.Items[1]; dialog.SelectedItem = dialog.Items[0];
        var material = (ComboBox)dialog.FindName("MagicMaterialComboBox");
        Require(dialog.Items.Any(i => i.RussianName.Length > 0), "Russian names absent");
        string old = dialog.Items[0].Value;
        int poseSamples = 0;
        bool sawBindPose = false;
        var poseWatch = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1) };
        poseWatch.Tick += (_, _) => { poseSamples++; sawBindPose |= !preview.HasVisibleScene || preview.ActiveAnimationName != "cidle09"; };
        poseWatch.Start();
        material.SelectedIndex = (material.SelectedIndex + 1) % material.Items.Count;
        await dialog.EquipmentLoadTask;
        poseWatch.Stop();
        Require(poseSamples > 0 && !sawBindPose && preview.ActiveAnimationName == "cidle09",
            "Equipment replacement exposed bind pose while preparing animation");
        Require(dialog.Items[0].Value != old, "Material choice did not update draft");
        Require(field.EditorText == before, "Preview modified document before Apply");
        Require(dialog.EquipmentDraft!.Lists[32][0] == dialog.Items[0].Value, "Stale preview");
        string changed = dialog.Items[0].Value;
        var statistics = (StackPanel)dialog.FindName("EquipmentStatisticsPanel");
        Require(statistics.Children.OfType<Grid>().Any() && statistics.Children.OfType<Expander>().All(e => !e.IsExpanded),
            "General statistics must be a visible table with collapsed details");
        var detail = statistics.Children.OfType<Expander>().First();
        string detailKey = (string)detail.Tag;
        detail.IsExpanded = true;
        material.SelectedIndex = (material.SelectedIndex + 1) % material.Items.Count;
        await dialog.EquipmentLoadTask;
        Require(statistics.Children.OfType<Expander>().Single(e => (string)e.Tag == detailKey).IsExpanded,
            "Dressing reset expanded statistics section");
        statistics.Children.OfType<Expander>().Single(e => (string)e.Tag == detailKey).IsExpanded = false;
        changed = dialog.Items[0].Value;
        var shapeCombo = (ComboBox)dialog.FindName("MagicShapeComboBox");
        var shapeChoice = (EmbeddedMagicChoice)shapeCombo.SelectedItem;
        Require(shapeChoice.DisplayText == shapeChoice.Value + " — " + dialog.Items[0].RussianName,
            "Selected armor dropdown label lacks localized material-specific name");
        Require(shapeCombo.Items.Cast<EmbeddedMagicChoice>().All(c => c.DisplayText.StartsWith(c.Value + " — ")),
            "Armor dropdown still contains unlabelled codes");
        var content = (FrameworkElement)dialog.Content; content.Measure(new Size(1420, 900)); content.Arrange(new Rect(0, 0, 1420, 900)); content.UpdateLayout();
        Require(statistics.TranslatePoint(new Point(), content).X > 1000 && statistics.ActualWidth >= 260,
            "Statistics must occupy a readable column right of model");
        var image = new RenderTargetBitmap(1420, 900, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(output, "equipment.png"))) png.Save(file);
        var rootContent = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        using var presentation = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Equipment headless lifecycle")
            { Width = 1420, Height = 900, WindowStyle = unchecked((int)0x80000000) });
        presentation.RootVisual = rootContent;
        preview.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        ((System.Windows.Controls.Primitives.ToggleButton)dialog.FindName("RawModeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Require(!preview.AnimationPlaying, "Hidden equipment still animates");
        ((System.Windows.Controls.Primitives.ToggleButton)dialog.FindName("StructuredModeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await dialog.EquipmentLoadTask;
        Require(preview.HasVisibleScene && preview.AnimationPlaying && preview.EditorAnimationName == "cidle09", "Raw/structured return lost model or idle");
        presentation.RootVisual = null;
        dialog.Content = rootContent;
        dialog.Close(); Require(field.EditorText == before, "Cancel modified document");
        dialog = New(); dialog.ConfigureEquipment(vm, row, 32);
        dialog.Items[0].Value = changed;
        Require(dialog.TryCommit(), "Apply rejected: " + ((TextBlock)dialog.FindName("ErrorText")).Text); Require(field.EditorText != before, "Apply did not edit");
        vm.UndoCommand.Execute(null); Require(field.EditorText == before, "Undo did not restore whole list"); dialog.Close();
        foreach (int weaponField in new[] { 33, 42 })
        {
            var weaponRow = vm.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "units.prototypes").Records
                .Single(r => r.CoreRecord.IdentityValue == "ZombieFemaleF14");
            vm.SelectedRecord = weaponRow;
            var cell = weaponRow.GetCell(weaponField)!;
            string originalWeapon = cell.EditorText;
            var weaponDialog = new StructuredStringListEditorDialog("weapon", "Оружие", [originalWeapon], [],
                _ => (true, (string?)null), singleValueMode: true, magicContext: context,
                allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Weapon]);
            weaponDialog.ConfigureEquipment(vm, weaponRow, weaponField);
            weaponDialog.Measure(new Size(1600, 900)); weaponDialog.Arrange(new Rect(0, 0, 1600, 900)); weaponDialog.UpdateLayout();
            await Task.Delay(50);
            for (int pass = 0; pass < 12; pass++)
            {
                var pending = weaponDialog.EquipmentLoadTask;
                await pending;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (ReferenceEquals(pending, weaponDialog.EquipmentLoadTask)) break;
            }
            EquipmentShapeSearchRegression.CheckFiltering(weaponDialog, armor: false);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var view = (EvilIslands.DatabaseEditor.App.UnitModelPreviewView)typeof(StructuredStringListEditorDialog).GetField("_equipmentPreview", flags)!.GetValue(weaponDialog)!;
            UnitModelScene Scene() => (UnitModelScene)typeof(EvilIslands.DatabaseEditor.App.UnitModelPreviewView).GetField("_editingScene", flags)!.GetValue(view)!;
            Require(Scene() is not null, "Weapon preview failed: " + ((TextBlock)typeof(StructuredStringListEditorDialog).GetField("_equipmentStatus", flags)!.GetValue(weaponDialog)!).Text);
            Require(Scene().Meshes.Any(m => m.Name == "rh3.daxe01") && Scene().Meshes.Any(m => m.Name == "lh3.daxe01"), "Equipment dialog omitted identical axe instance: " + weaponField);
            int adjacent = weaponField == 33 ? 42 : 33;
            string adjacentOriginal = weaponRow.GetCell(adjacent)!.EditorText;
            var choices = (ComboBox)weaponDialog.FindName("MagicMaterialComboBox");
            Require(choices.Items.Count > 1, "No weapon materials");
            choices.SelectedIndex = (choices.SelectedIndex + 1) % choices.Items.Count;
            for (int pass = 0; pass < 12; pass++)
            {
                var pending = weaponDialog.EquipmentLoadTask;
                await pending;
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (ReferenceEquals(pending, weaponDialog.EquipmentLoadTask)) break;
            }
            Require(weaponDialog.EquipmentDraft!.Text(weaponField) == weaponDialog.Items[0].Value &&
                weaponDialog.Items[0].Value != originalWeapon, "Weapon draft did not follow material: " + weaponField);
            Require(cell.EditorText == originalWeapon && weaponRow.GetCell(adjacent)!.EditorText == adjacentOriginal && weaponDialog.EquipmentDraft!.Text(adjacent) == adjacentOriginal, "Weapon draft changed adjacent/source slot");
            Require(Scene().Meshes.Any(m => m.Name == "rh3.daxe01") && Scene().Meshes.Any(m => m.Name == "lh3.daxe01"), "Material change lost hand instance");
            Require(view.ActiveAnimationName is not null, "Equipment animation lost");
            Require(Scene().Textures[Scene().Meshes.Single(m => m.Name == "rh3.daxe01").TextureSlot].Bgra.Length > 0, "Axe material lost after animation");
            var selectedWeapon = (EmbeddedMagicChoice)((ComboBox)weaponDialog.FindName("MagicShapeComboBox")).SelectedItem;
            Require(selectedWeapon.DisplayText.StartsWith(selectedWeapon.Value + " — ") &&
                weaponDialog.Items[0].RussianName.Length > 0 && selectedWeapon.DisplayText.Contains(weaponDialog.Items[0].RussianName),
                "Weapon dropdown missing Russian name");
            weaponDialog.Close();
            Require(cell.EditorText == originalWeapon && weaponRow.GetCell(adjacent)!.EditorText == adjacentOriginal, "Cancel changed weapon data");
            var applyDialog = new StructuredStringListEditorDialog("weapon", "Оружие", [originalWeapon], [],
                updated => { cell.EditorText = updated.Single(); return (true, (string?)null); }, singleValueMode: true, magicContext: context,
                allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Weapon]);
            applyDialog.ConfigureEquipment(vm, weaponRow, weaponField);
            applyDialog.Items[0].Value = weaponDialog.Items[0].Value;
            await applyDialog.EquipmentLoadTask;
            Require(applyDialog.TryCommit() && cell.EditorText == applyDialog.Items[0].Value && cell.EditorText != originalWeapon, "Weapon Apply failed");
            Require(weaponRow.GetCell(adjacent)!.EditorText == adjacentOriginal, "Weapon Apply changed adjacent slot");
            vm.UndoCommand.Execute(null);
            Require(cell.EditorText == originalWeapon && weaponRow.GetCell(adjacent)!.EditorText == adjacentOriginal, "Weapon Apply Undo did not restore exact slots");
            applyDialog.Close();
            Console.WriteLine("PASS Zombie equipment slot " + weaponField + ": two axes, material+animation, adjacent preserved, Cancel and real Apply/Undo");
        }
        Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Input RES modified");
        var unit = new ModelRecord("units.prototypes", "test", new Dictionary<int, string> { [1] = "race", [11] = "2", [33] = "sword.iron" }, new Dictionary<int, string[]> { [32] = ["helm.iron"] });
        ModelRecord[] numeric = [unit,
            new("units.races", "race", new Dictionary<int, string> { [39] = "1, 2, 3, 4, 5, 6, 7" }, new Dictionary<int, string[]>()),
            new("items.armors", "helm", new Dictionary<int, string> { [2] = "0", [8] = "2", [21] = "2, 1, 1, 1, 1, 1, 1, 1, 0" }, new Dictionary<int, string[]>()),
            new("items.materials", "iron", new Dictionary<int, string> { [5] = "3", [10] = "4", [11] = "2, 2, 2, 2, 2, 2, 2" }, new Dictionary<int, string[]>()),
            new("items.weapons", "sword", new Dictionary<int, string> { [8] = "1", [24] = "5", [25] = "2" }, new Dictionary<int, string[]>())];
        string inspection = EquipmentInspection.Describe(unit, numeric, null);
        Require(inspection.Contains("Голова: 4 / 4 / 4") && inspection.Contains("урон 20–28") && inspection.Contains("экипировки: 9") && inspection.Contains("2 / 4 / 6 / 8 / 10 / 12 / 14"), "Item/race calculation regression");
    }
}
