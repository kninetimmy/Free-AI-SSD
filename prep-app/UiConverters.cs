using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Visible when the bound collection (or int count) is empty / zero.
/// Bind to <c>{Binding Collection.Count}</c> so ObservableCollection's
/// "Count" PropertyChanged fires the re-evaluation.
///
/// MAC31: also recognizes strings (visible when null/whitespace) and
/// supports a <c>ConverterParameter=Inverted</c> flip so the same
/// converter can drive "show when non-empty" bindings without adding a
/// sibling class. Existing callers don't pass a parameter and never
/// bound to strings, so their semantics are unchanged.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            ICollection c => c.Count == 0,
            IEnumerable e => IsEmpty(e),
            _ => false
        };
        var inverted = parameter is string p &&
                       string.Equals(p, "Inverted", StringComparison.OrdinalIgnoreCase);
        var visible = inverted ? !isEmpty : isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
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
