using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.App.Infrastructure;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not WorkspaceSection section || parameter is not string requested)
        {
            return Visibility.Collapsed;
        }

        if (requested.Equals("Records", StringComparison.OrdinalIgnoreCase))
        {
            return section == WorkspaceSection.Records
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        return Enum.TryParse<WorkspaceSection>(requested, true, out var parsed) && section == parsed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            return "—";
        }

        string[] suffixes = ["Б", "КБ", "МБ", "ГБ"];
        double size = bytes;
        var suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return suffix == 0
            ? $"{bytes:N0} {suffixes[suffix]}"
            : $"{size:N2} {suffixes[suffix]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SeverityBrushConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(233, 196, 106));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(115, 190, 255));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString()?.ToLowerInvariant() switch
        {
            "error" or "ошибка" => ErrorBrush,
            "warning" or "предупреждение" => WarningBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SpreadsheetProjectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        parameter is DatabaseFieldProjection projection
            ? value is RecordFieldViewModel field
                ? field.GetProjectedText(projection)
                : SpreadsheetProjectionValue.Format(value?.ToString(), projection)
            : SpreadsheetProjectionValue.UnavailableText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
