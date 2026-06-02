using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FreeAiSsd.Runner;

/// <summary>
/// Visible when the bound collection (or int count) is empty / zero.
/// Bind to <c>{Binding Collection.Count}</c> so ObservableCollection's
/// "Count" PropertyChanged fires the re-evaluation.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            int i => i == 0,
            string s => string.IsNullOrEmpty(s),
            ICollection c => c.Count == 0,
            IEnumerable e => IsEmpty(e),
            _ => false
        };
        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsEmpty(IEnumerable e)
    {
        var enumerator = e.GetEnumerator();
        try
        {
            return !enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }
}

/// <summary>
/// Formats a byte count (<see cref="DocumentFileEntry.SizeBytes"/>) as a
/// human-readable size, e.g. <c>1.2 MB</c>. One-way; the file list is read-only.
/// </summary>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };

        if (bytes < 0)
        {
            bytes = 0;
        }

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Whole bytes show no decimal; KB and up show one.
        var format = unit == 0 ? "0" : "0.0";
        return $"{size.ToString(format, culture)} {Units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
