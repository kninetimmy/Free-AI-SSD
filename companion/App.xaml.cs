using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAiSsd.Companion;

public partial class App : Application
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
