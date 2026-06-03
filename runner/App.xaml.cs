using System.IO;
using System.Net.Http;
using System.Windows;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Client;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;
using FreeAiSsd.Shared.UI.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAiSsd.Runner;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Honor the OS "reduce animations" preference before any window is
        // shown so the LED pulse storyboard and button press translate can
        // pick up the identity resource from first paint.
        ReducedMotion.Apply(this);

        var ssdRoot = DetectSsdRoot();

        var collection = new ServiceCollection();

        // Infrastructure / shared
        collection.AddSingleton(new SsdLogger(ssdRoot, "runner"));
        // Chat + model-management client. Streaming chat uses ResponseHeadersRead, so this
        // timeout is effectively a first-token deadline; the default 100s aborted slow CPU
        // cold-loads (logged as "chat stream cancelled after 100012ms"). A generous ceiling
        // lets CPU first-token land; the chat path still honors the user's CancellationToken
        // (cancel / lock) so a genuinely wedged request is never stuck for the full window.
        collection.AddSingleton(sp => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        collection.AddSingleton(sp => new DocumentLibraryManager(ssdRoot));
        // Ingestion embeds get their own HttpClient with a bounded per-request timeout so a
        // wedged /api/embed fails fast instead of hanging the default 100s. The shared
        // singleton stays on chat/model-management, where streaming needs a long timeout.
        collection.AddSingleton(sp => new EmbeddingClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        collection.AddSingleton(sp => new DocumentIngestor(
            sp.GetRequiredService<DocumentLibraryManager>(),
            sp.GetRequiredService<EmbeddingClient>(),
            sp.GetRequiredService<SsdLogger>()));

        // Config store — single chokepoint for all config saves, encrypted or plain.
        collection.AddSingleton<IConfigStore>(sp =>
            new ConfigStore(sp.GetRequiredService<SsdLogger>()));

        // Runner services
        collection.AddSingleton<ISystemResourceProbe, WindowsSystemResourceProbe>();
        collection.AddSingleton<IOllamaLifecycleService, OllamaLifecycleService>();
        // MAC33: ModelManagementService needs the SSD root to enumerate the
        // on-disk model store via ModelOperations.DiscoverModelsOnDisk.
        collection.AddSingleton<IModelManagementService>(sp => new ModelManagementService(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ISystemResourceProbe>(),
            ssdRoot));
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
            ssdRoot,
            staticFilesRoot: null,
            docOps: sp.GetRequiredService<IDocumentOperationsService>(),
            libraryManager: sp.GetRequiredService<DocumentLibraryManager>(),
            modelService: sp.GetRequiredService<IModelManagementService>()));

        // ActivatorUtilities resolves registered services automatically; ssdRoot is
        // the only non-DI parameter and is passed positionally.
        collection.AddSingleton(sp => ActivatorUtilities.CreateInstance<MainWindow>(sp, ssdRoot));

        _services = collection.BuildServiceProvider();

        // Global safety net. Without this, an unhandled exception on the UI
        // thread — e.g. an `async void` handler throwing a type a local catch
        // missed — silently tears the whole Runner down (this is what made
        // "Create Library" appear to crash even though the library persisted).
        // Log the full stack to the SSD and keep the session alive; the
        // AppDomain / TaskScheduler hooks capture anything off the UI thread.
        var logger = _services.GetRequiredService<SsdLogger>();
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error($"Unhandled UI exception: {args.Exception}");
            System.Windows.MessageBox.Show(
                $"Something went wrong, but the app is still running.\n\n{args.Exception.Message}",
                "Free AI SSD — unexpected error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var detail = args.ExceptionObject as Exception;
            logger.Error(
                $"Unhandled non-UI exception (terminating={args.IsTerminating}): " +
                (detail?.ToString() ?? args.ExceptionObject?.ToString() ?? "unknown"));

            // A terminating crash skips OnExit, so the container is never disposed and
            // OllamaLifecycleService.Stop() never runs — the ollama (and its llama-server
            // children) are orphaned, holding the port and the GPU for the next launch.
            // Reap it here as a last act before the process dies.
            if (args.IsTerminating)
            {
                try { _services?.GetService<IOllamaLifecycleService>()?.Stop(); }
                catch (Exception stopEx) { logger.Warn($"Best-effort ollama stop on crash failed: {stopEx.Message}"); }
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose via DisposeAsync: the container holds a service that only implements
        // IAsyncDisposable (RunnerLocalApiService). Synchronous Dispose() throws
        // InvalidOperationException for it, which previously aborted the dispose chain
        // before OllamaLifecycleService.Stop() ran — orphaning ollama on every clean exit.
        try { _services?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Service disposal on exit failed: {ex}"); }
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
