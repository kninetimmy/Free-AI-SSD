using System.Windows.Input;

namespace FreeAiSsd.Shared.Mvvm;

/// <summary>
/// A synchronous ICommand implementation that delegates Execute and CanExecute
/// to provided callbacks. Supports manual CanExecuteChanged notifications
/// for enabling/disabling buttons based on ViewModel state.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Call this when conditions affecting CanExecute have changed,
    /// so the UI re-evaluates button enabled/disabled state.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
