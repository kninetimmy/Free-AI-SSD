using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FreeAiSsd.Shared.Mvvm;

/// <summary>
/// Base class for all ViewModels. Provides INotifyPropertyChanged implementation
/// with a convenient SetProperty helper that only raises change notifications
/// when the value actually changes, preventing redundant UI updates.
/// </summary>
public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the PropertyChanged event for the specified property.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Sets the backing field to the new value and raises PropertyChanged if the
    /// value actually changed. Returns true if the value was updated.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
