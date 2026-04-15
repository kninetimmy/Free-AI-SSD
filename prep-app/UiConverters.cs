using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Visible when the bound collection (or int count) is empty / zero.
/// Bind to <c>{Binding Collection.Count}</c> so ObservableCollection's
/// "Count" PropertyChanged fires the re-evaluation.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            null => 0,
            int i => i,
            ICollection c => c.Count,
            IEnumerable e => CountEnumerable(e),
            _ => 1
        };
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int CountEnumerable(IEnumerable e)
    {
        var n = 0;
        foreach (var _ in e) n++;
        return n;
    }
}
