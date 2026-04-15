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
        var isEmpty = value switch
        {
            null => true,
            int i => i == 0,
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
