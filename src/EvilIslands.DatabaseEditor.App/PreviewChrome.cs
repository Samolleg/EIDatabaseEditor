using System.Windows;
using System.Windows.Controls;

namespace EvilIslands.DatabaseEditor.App;

// Apply only to the controls hosted in the bottom details pane, not editor dialogs.
internal static class PreviewChrome
{
    internal static void Compact(FrameworkElement element)
    {
        if (element is Button or ComboBox || element is TextBox { IsReadOnly: false })
        {
            var control = (Control)element;
            control.MinHeight = 0;
            control.Height = 28;
            control.Padding = new Thickness(6, 2, 6, 2);
            control.FontSize = 13;
        }
        if (element is Control or TextBlock)
        {
            var margin = element.Margin;
            element.Margin = new Thickness(margin.Left, Math.Min(margin.Top, 2), margin.Right, Math.Min(margin.Bottom, 2));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>().ToArray()) Compact(child);
    }
}
