using System.Windows.Input;

namespace FreeAiSsd.Shared.Mvvm;

/// <summary>
/// An asynchronous ICommand implementation for long-running operations like
/// downloads, model pulls, and finalization. Prevents concurrent execution
/// by default (CanExecute returns false while a previous invocation is running).
/// Exceptions from the async delegate are captured and exposed via the
/// LastException property for ViewModel-level error handling.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// True while the async operation is executing. Prevents re-entrant calls.
    /// </summary>
    public bool IsExecuting => _isExecuting;

    /// <summary>
    /// The last exception thrown by the async delegate, or null if the last
    /// execution succeeded. ViewModels can check this for error handling.
    /// </summary>
    public Exception? LastException { get; private set; }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _isExecuting = true;
        LastException = null;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            LastException = ex;
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Call this when conditions affecting CanExecute have changed.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
