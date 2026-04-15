using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FreeAiSsd.Shared.UI.Theme;

namespace FreeAiSsd.PrepApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch unhandled exceptions on the UI thread so the app shows the error
        // instead of silently force-closing.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Catch unhandled exceptions from background threads / AppDomain.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Catch unobserved Task exceptions (fire-and-forget tasks) so they don't
        // silently swallow errors or tear down the process.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Call base after handlers are wired so startup failures (such as
        // MainWindow construction via StartupUri) are surfaced to the user
        // instead of failing silently.
        base.OnStartup(e);

        // Honor the OS "reduce animations" preference: disables LED pulse
        // and the 1px button press nudge. Must run after base.OnStartup so
        // Application.Resources is populated from the merged theme.
        ReducedMotion.Apply(this);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception}",
            "PrepApp Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true; // Keep the app alive so the user can see the error.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            // This handler fires on a background thread, so marshal to the UI
            // thread to safely show the dialog.
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                MessageBox.Show(
                    $"A fatal error occurred:\n\n{ex}",
                    "PrepApp Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // Prevent the runtime from tearing down the process.

        // This handler fires on a background thread, so marshal to the UI
        // thread to safely show the dialog.
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            MessageBox.Show(
                $"A background task failed:\n\n{e.Exception?.Flatten().InnerException ?? e.Exception}",
                "PrepApp Background Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }
}
