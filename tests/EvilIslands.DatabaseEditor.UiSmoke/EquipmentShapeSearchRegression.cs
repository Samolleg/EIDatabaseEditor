using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EvilIslands.DatabaseEditor.App.Controls;
using EvilIslands.DatabaseEditor.App.Dialogs;
using EvilIslands.DatabaseEditor.App.Models;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class EquipmentShapeSearchRegression
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Shape search: " + message);
    }

    internal static void CheckFiltering(StructuredStringListEditorDialog dialog, bool armor)
    {
        var shapes = (SearchableComboBox)dialog.FindName("MagicShapeComboBox");
        shapes.ApplyTemplate();
        var search = shapes.SearchBox!;
        var status = shapes.SearchStatusText!;
        var clear = shapes.ClearSearchButton!;
        Require(shapes.SearchPopup!.Child.IsAncestorOf(search) && dialog.FindName("MagicShapeSearchBox") is null,
            "search is outside the popup capture subtree");
        var materials = (ComboBox)dialog.FindName("MagicMaterialComboBox");
        var slots = armor ? (ComboBox)dialog.FindName("EquipmentSlotCombo") : null;
        if (slots is not null) slots.SelectedIndex = 0;
        var selected = (EmbeddedMagicChoice)shapes.SelectedItem;
        string? material = (materials.SelectedItem as EmbeddedMagicChoice)?.Value;
        string[] values = dialog.Items.Select(i => i.Value).ToArray();
        int allCount = shapes.Items.Count;
        var pendingPreview = dialog.EquipmentLoadTask;
        Require(search.Height == 28 && search.MinHeight == 0, "search is not compact");

        void Query(string text, string expected)
        {
            search.Text = text;
            var visible = shapes.Items.Cast<EmbeddedMagicChoice>().ToArray();
            Require(visible.Any(c => c.Value == expected && c.MatchesSearch(text)), "no expected match for " + text + ": " + expected);
            Require(visible.All(c => c.Value == selected.Value || c.MatchesSearch(text)), "unfiltered results for " + text);
            Require(visible.Length < allCount, "query did not narrow the list: " + text);
            Require((shapes.SelectedItem as EmbeddedMagicChoice)?.Value == selected.Value &&
                (materials.SelectedItem as EmbeddedMagicChoice)?.Value == material, "typing changed shape/material");
            Require(dialog.Items.Select(i => i.Value).SequenceEqual(values) && ReferenceEquals(pendingPreview, dialog.EquipmentLoadTask), "typing mutated draft or reloaded preview");
        }

        if (armor)
        {
            Query("БЕЗРУКАВКА", "vlcsh");
            Query(" vLcSh ", "vlcsh");
            Query("initarsh", "vlcsh");
        }
        else
        {
            Query("дубина КОСТЕЙ", "2h bone club");
            Query(" 2h_bone_CLUB ", "2h bone club");
            Query("СТАЛЬНОЙ ТЯЖЕЛЫЙ ДВУРУЧНЫЙ ТОПОР", "2h metal battle axe");
            Query("DaXe01", "2h metal battle axe");
            Query("INITWETA1.MOD", "2h metal battle axe");
            Require(shapes.Items.Cast<EmbeddedMagicChoice>().Single(c => c.Value == "2h metal battle axe").Description.Contains("rh3.daxe01"), "model tooltip missing");
        }
        search.Text = "__not_a_shape_9dc1__";
        Require(shapes.Items.Count == 1 && (shapes.SelectedItem as EmbeddedMagicChoice)?.Value == selected.Value &&
            status.Text.Contains("Совпадений нет") && status.Text.Contains("сохранена"), "empty search lost chosen form or explanation");
        Require(dialog.Items.Select(i => i.Value).SequenceEqual(values) && (materials.SelectedItem as EmbeddedMagicChoice)?.Value == material, "empty search changed item");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(search.Text.Length == 0 && shapes.Items.Count == allCount && status.Visibility == Visibility.Collapsed && !clear.IsEnabled, "clear did not restore list");

        if (slots is not null)
        {
            search.Text = "безрукавка";
            slots.SelectedIndex = 4; // Shirt, DB armor type 3.
            Require(shapes.Items.Cast<EmbeddedMagicChoice>().Any(c => c.Value == "vlcsh") &&
                shapes.Items.Cast<EmbeddedMagicChoice>().All(c => c.MatchesSearch(search.Text) && c.ModelText.Contains("initarsh")), "RU search and armor slot filter disagree");
            slots.SelectedIndex = 1; // Helmet: the vest must not leak into this slot.
            Require(shapes.Items.Count == 0 && status.Text.Contains("Совпадений нет"), "search ignored helmet filter");
            search.Clear();
            var row = dialog.SelectedItem;
            dialog.SelectedItem = null;
            dialog.SelectedItem = row;
            Require(dialog.Items.Select(i => i.Value).SequenceEqual(values), "slot filtering changed armor list");
        }
        Console.WriteLine("PASS " + (armor ? "armor" : "weapon") + " shape search: RU/ID/model, substring and words, no match/clear, selection/material/draft preserved" + (armor ? ", seven-slot composition" : ", all material titles and yo/e"));
    }

    internal static void CheckChoosing(EmbeddedMagicEditorContext context)
    {
        var dialog = new StructuredStringListEditorDialog("search", "Оружие", ["2h metal battle axe.steel"], [],
            _ => (true, (string?)null), singleValueMode: true, magicContext: context,
            allowedElementKinds: [StructuredElementKind.RawValue, StructuredElementKind.Weapon]);
        try
        {
            var shapes = (SearchableComboBox)dialog.FindName("MagicShapeComboBox");
            shapes.ApplyTemplate();
            var search = shapes.SearchBox!;
            int count = shapes.Items.Count;
            search.Text = "дубина костей";
            shapes.SelectedItem = shapes.Items.Cast<EmbeddedMagicChoice>().Single(c => c.Value == "2h bone club");
            Require(shapes.Items.Cast<EmbeddedMagicChoice>().All(c => c.MatchesSearch(search.Text)), "previous pinned selection remained after choosing a match");
            ((Button)dialog.FindName("UpdateElementButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(dialog.Items[0].Value.StartsWith("2h bone club.", StringComparison.Ordinal), "choosing filtered form did not build item");
            Require(search.Text == "дубина костей" && (shapes.SelectedItem as EmbeddedMagicChoice)?.Value == "2h bone club", "updating item reset query/selection");
            var materials = (ComboBox)dialog.FindName("MagicMaterialComboBox");
            Require(materials.Items.Count > 1, "fixture has no material alternatives");
            materials.SelectedIndex = (materials.SelectedIndex + 1) % materials.Items.Count;
            Require(shapes.Items.Cast<EmbeddedMagicChoice>().All(c => c.MatchesSearch(search.Text)), "material relocalization removed search");
            search.Clear();
            Require(shapes.Items.Count == count && shapes.Items.Cast<EmbeddedMagicChoice>().Any(c => c.Value == "2h metal battle axe"), "clearing after material change did not restore all choices");
            CheckPopupInput(dialog, shapes);
            Console.WriteLine("PASS filtered shape selection, item build, persistent query, material relocalization, full list restore");
        }
        finally { dialog.Close(); }
    }

    private static void CheckPopupInput(StructuredStringListEditorDialog dialog, SearchableComboBox shapes)
    {
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        using var owner = new HwndSource(new HwndSourceParameters("Shape search hidden owner")
            { Width = 1280, Height = 780, WindowStyle = unchecked((int)0x80000000) });
        using var popupHost = new HwndSource(new HwndSourceParameters("Shape search hidden popup subtree")
            { Width = 440, Height = 300, WindowStyle = unchecked((int)0x80000000) });
        var popup = shapes.SearchPopup!;
        // Materialize the real template subtree in a hidden presentation source.
        // The native Popup must not become a visible interactive window in this harness.
        popup.SetValue(Popup.IsOpenProperty, false);
        try
        {
            owner.RootVisual = content;
            content.Measure(new Size(1280, 780)); content.Arrange(new Rect(0, 0, 1280, 780));
            popupHost.RootVisual = popup.Child;
            popup.Child.Measure(new Size(440, 300)); popup.Child.Arrange(new Rect(0, 0, 440, 300));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            shapes.IsDropDownOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Require(shapes.IsDropDownOpen && !popup.IsOpen, "hidden popup state not opened");
            var search = shapes.SearchBox!;
            Require(search.Focusable && search.IsEnabled && !search.IsReadOnly && !shapes.IsTextSearchEnabled,
                "popup text box cannot accept input");
            Require(search.IsKeyboardFocused, "opening popup did not focus its search box");
            var selected = shapes.SelectedItem;
            string[] before = dialog.Items.Select(i => i.Value).ToArray();

            void Text(string value)
            {
                var composition = new TextComposition(InputManager.Current, search, value);
                search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.TextInputEvent });
            }
            KeyEventArgs KeyDown(Key key)
            {
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, popupHost, 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                search.RaiseEvent(e);
                if (!e.Handled) { e.RoutedEvent = Keyboard.KeyDownEvent; search.RaiseEvent(e); }
                return e;
            }

            search.Clear();
            Text("дубина"); Text(" "); Text("костей");
            Require(search.Text == "дубина костей" && shapes.SearchText == search.Text,
                "routed text/space did not reach popup search: " + search.Text);
            Require(shapes.IsDropDownOpen && ReferenceEquals(selected, shapes.SelectedItem) &&
                dialog.Items.Select(i => i.Value).SequenceEqual(before), "typing in open popup changed selection/draft or closed list");
            int caret = search.CaretIndex;
            KeyDown(Key.Home);
            Require(search.CaretIndex == 0 && shapes.IsDropDownOpen, "Home was intercepted by ComboBox");
            KeyDown(Key.End);
            Require(search.CaretIndex == search.Text.Length && caret == search.Text.Length, "End/caret was intercepted");
            Require(KeyDown(Key.Down).Handled && shapes.IsDropDownOpen, "Down closed the search popup");
            Require(shapes.ItemContainerGenerator.ContainerFromItem(shapes.FirstSearchResult) is ComboBoxItem { IsKeyboardFocusWithin: true },
                "Down did not focus the first matching result");
            search.Focus();
            Require(KeyDown(Key.Enter).Handled && !shapes.IsDropDownOpen &&
                ((EmbeddedMagicChoice)shapes.SelectedItem).Value == "2h bone club", "Enter did not choose filtered result");
            Require(search.Text == "дубина костей", "closing list lost query");
            shapes.IsDropDownOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Require(KeyDown(Key.Escape).Handled && !shapes.IsDropDownOpen &&
                dialog.Items.Select(i => i.Value).SequenceEqual(before), "Escape closed editor or modified draft");
            shapes.IsDropDownOpen = true;
            search.Text = "__not_a_shape__";
            Require(KeyDown(Key.Enter).Handled && shapes.IsDropDownOpen, "no-match Enter selected retained item or closed popup");
            shapes.ClearSearchButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(search.Text.Length == 0 && shapes.IsDropDownOpen && shapes.Items.Count > 1, "clear closed popup or lost choices");
            Require(search.IsKeyboardFocused, "clear did not return focus to popup search");
            Console.WriteLine("PASS real popup subtree in hidden HWND: routed RU text/space, Home/End, Down/Enter/Escape, reopen, no-match/clear, no draft mutation; native popup intentionally not shown");
        }
        finally
        {
            shapes.IsDropDownOpen = false;
            popupHost.RootVisual = null;
            owner.RootVisual = null;
            popup.ClearValue(Popup.IsOpenProperty);
            dialog.Content = content;
        }
    }
}
