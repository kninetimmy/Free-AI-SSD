using System.IO;
using System.Net.Http;
using System.Windows;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Client;
using FreeAiSsd.Shared.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAiSsd.Runner;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var ssdRoot = DetectSsdRoot();

        var collection = new ServiceCollection();

        // Infrastructure / shared
        collection.AddSingleton(new SsdLogger(ssdRoot, "runner"));
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton(sp => new DocumentLibraryManager(ssdRoot));
        collection.AddSingleton(sp => new EmbeddingClient(sp.GetRequiredService<HttpClient>()));
        collection.AddSingleton(sp => new DocumentIngestor(
            sp.GetRequiredService<DocumentLibraryManager>(),
            sp.GetRequiredService<EmbeddingClient>(),
            sp.GetRequiredService<SsdLogger>()));

        // Runner services
        collection.AddSingleton<IOllamaLifecycleService, OllamaLifecycleService>();
        collection.AddSingleton<IModelManagementService, ModelManagementService>();
        collection.AddSingleton<IDocumentOperationsService, DocumentOperationsService>();
        collection.AddSingleton<IChatService, ChatService>();
        collection.AddSingleton<IDcsBindingsImportService, DcsBindingsImportService>();
        collection.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
        collection.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        collection.AddSingleton<IHotasInputService, HotasInputService>();
        collection.AddSingleton<PttVoicePipelineService>();

        // RunnerLocalApiService needs a lazy factory for the TTS service because TTS is
        // (re-)created by MainWindow after config load / drive unlock.
        collection.AddSingleton<IRunnerLocalApiService>(sp =>
        {
            var mainWindowFactory = new Func<MainWindow?>(() => sp.GetService<MainWindow>());
            return new RunnerLocalApiService(
                sp.GetRequiredService<IChatService>(),
                sp.GetRequiredService<ISpeechToTextService>(),
                () => mainWindowFactory()?.CurrentTtsService,
                sp.GetRequiredService<SsdLogger>(),
                ssdRoot);
        });

        collection.AddSingleton(sp => new MainWindow(
            ssdRoot,
            sp.GetRequiredService<SsdLogger>(),
            sp.GetRequiredService<IOllamaLifecycleService>(),
            sp.GetRequiredService<IModelManagementService>(),
            sp.GetRequiredService<IDocumentOperationsService>(),
            sp.GetRequiredService<IChatService>(),
            sp.GetRequiredService<IDcsBindingsImportService>(),
            sp.GetRequiredService<ISpeechToTextService>(),
            sp.GetRequiredService<IAudioCaptureService>(),
            sp.GetRequiredService<IHotasInputService>(),
            sp.GetRequiredService<PttVoicePipelineService>(),
            sp.GetRequiredService<IRunnerLocalApiService>()));

        _services = collection.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _services = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Detects the SSD root from the runner executable's location. The runner may live
    /// either at &lt;ssdRoot&gt;/runner or &lt;ssdRoot&gt;/windows/runner depending on the
    /// layout, so walk up accordingly.
    /// </summary>
    private static string DetectSsdRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar);
        if (trimmed.EndsWith($"windows{Path.DirectorySeparatorChar}runner", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(Directory.GetParent(trimmed)!.FullName)!.FullName;
        }
        if (trimmed.EndsWith("runner", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(trimmed)!.FullName;
        }
        return baseDir;
    }
}
