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

        // Shared holder for the active TTS engine. MainWindow writes this when the
        // engine is (re-)created on config load / drive unlock; RunnerLocalApiService
        // reads it to serve network TTS requests. Decouples the background HTTP
        // service from the WPF View layer and breaks the DI cycle that previously
        // required a lazy MainWindow factory here.
        collection.AddSingleton<ITtsProvider, TtsProvider>();

        collection.AddSingleton<IRunnerLocalApiService>(sp => new RunnerLocalApiService(
            sp.GetRequiredService<IChatService>(),
            sp.GetRequiredService<ISpeechToTextService>(),
            sp.GetRequiredService<ITtsProvider>(),
            sp.GetRequiredService<SsdLogger>(),
            ssdRoot));

        // ActivatorUtilities resolves registered services automatically; ssdRoot is
        // the only non-DI parameter and is passed positionally.
        collection.AddSingleton(sp => ActivatorUtilities.CreateInstance<MainWindow>(sp, ssdRoot));

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
            // <ssdRoot>/windows/runner -> walk up two levels. Fall back to baseDir if
            // the path is unexpectedly shallow (e.g. running from a drive root).
            return Directory.GetParent(trimmed)?.Parent?.FullName ?? baseDir;
        }
        if (trimmed.EndsWith("runner", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(trimmed)?.FullName ?? baseDir;
        }
        return baseDir;
    }
}
