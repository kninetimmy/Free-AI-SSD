using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAiSsd.Companion;

// Fully-qualify: `Application` is otherwise ambiguous between System.Windows.Application
// (WPF) and System.Windows.Forms.Application, which is auto-imported via the
// companion project's UseWindowsForms=true implicit usings.
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private CompanionRuntime? _runtime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddSingleton<CompanionLog>();
        collection.AddSingleton<FreeAiSsd.Shared.Client.IAudioCaptureService, FreeAiSsd.Shared.Client.AudioCaptureService>();
        collection.AddSingleton<FreeAiSsd.Shared.Client.IHotasInputService, FreeAiSsd.Shared.Client.HotasInputService>();
        collection.AddSingleton<CompanionRuntime>();
        _services = collection.BuildServiceProvider();

        _runtime = _services.GetRequiredService<CompanionRuntime>();
        _runtime.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runtime?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
