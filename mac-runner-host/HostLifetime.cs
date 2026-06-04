using System.Net.Http;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAiSsd.MacRunnerHost;

/// <summary>
/// Owns the DI container and the running <see cref="RunnerLocalApiService"/>
/// instance. Mirrors the Windows runner's DI wiring (<c>runner/App.xaml.cs</c>)
/// but with platform-appropriate substitutes:
///
/// - <see cref="MacOllamaLifecycleService"/> in place of the Windows one.
///   The host does not start Ollama; it just consumes the trust gate's
///   contract via <c>IOllamaLifecycleService</c>.
/// - <see cref="NoOpSpeechToTextService"/> in place of WhisperSpeechToTextService.
/// - <see cref="NoOpTtsProvider"/> in place of TtsProvider.
///
/// The Swift parent owns Ollama lifecycle (it already does for direct chat),
/// and hands the resolved Ollama host URL to this process via the init
/// handshake — the host never starts a second Ollama.
/// </summary>
internal sealed class HostLifetime : IAsyncDisposable
{
    private readonly string _ssdRoot;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _testMode;
    private readonly object _stdoutLock = new();
    private readonly SsdLogger? _logger;
    private readonly HttpClient _httpClient;
    private ServiceProvider? _services;
    private IRunnerLocalApiService? _api;

    public HostLifetime(string ssdRoot, TextWriter stdout, TextWriter stderr, bool testMode = false)
    {
        _ssdRoot = ssdRoot;
        _stdout = stdout;
        _stderr = stderr;
        _testMode = testMode;

        try
        {
            _logger = new SsdLogger(_ssdRoot, "macos-runner-host");
        }
        catch (Exception ex)
        {
            // Logging on a non-existent ssdRoot during smoke tests should not fatal-fail
            // construction; the parent already gets stderr lines.
            stderr.WriteLine($"Failed to initialize SsdLogger: {ex.Message}");
            _logger = null;
        }

        _httpClient = new HttpClient();
    }

    public async Task StartAsync(PortableConfig config, string ollamaHost)
    {
        BuildContainer(config);
        var api = _services!.GetRequiredService<IRunnerLocalApiService>();
        api.LogMessage += OnLogMessage;
        await api.StartAsync(config, ollamaHost);

        var baseUrl = api.CurrentBaseUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            api.LogMessage -= OnLogMessage;
            throw new InvalidOperationException("RunnerLocalApiService did not start (no base URL). Check the host log for the underlying Kestrel/bind failure.");
        }

        _api = api;
        WriteLineSafe($"ready: {baseUrl}");
        _logger?.Info($"mac-runner-host ready at {baseUrl}");
    }

    public async Task RestartAsync(PortableConfig config, string ollamaHost)
    {
        await StopAsync();
        await StartAsync(config, ollamaHost);
    }

    public async Task StopAsync()
    {
        if (_api is not null)
        {
            try
            {
                await _api.StopAsync();
            }
            catch (Exception ex)
            {
                _stderr.WriteLine($"RunnerLocalApiService.StopAsync failed: {ex.Message}");
                _logger?.Error($"StopAsync failed: {ex.Message}");
            }
            finally
            {
                _api.LogMessage -= OnLogMessage;
                _api = null;
            }
        }

        if (_services is not null)
        {
            try
            {
                await _services.DisposeAsync();
            }
            catch (Exception ex)
            {
                _stderr.WriteLine($"ServiceProvider dispose failed: {ex.Message}");
            }
            _services = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
    }

    private void BuildContainer(PortableConfig config)
    {
        var collection = new ServiceCollection();

        if (_logger is not null)
        {
            collection.AddSingleton(_logger);
        }

        collection.AddSingleton(_httpClient);
        collection.AddSingleton(sp => new DocumentLibraryManager(_ssdRoot));
        // Parity with the Windows runner: ingestion embeds use a dedicated HttpClient with a
        // bounded per-request timeout so a wedged /api/embed fails fast. The shared _httpClient
        // stays on chat, which needs the long/streaming timeout.
        collection.AddSingleton(sp => new EmbeddingClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        collection.AddSingleton(sp => new DocumentIngestor(
            sp.GetRequiredService<DocumentLibraryManager>(),
            sp.GetRequiredService<EmbeddingClient>(),
            sp.GetService<SsdLogger>()));

        // The host never saves config — Swift owns saves. NoOpConfigStore
        // satisfies DocumentOperationsService's IConfigStore dependency while
        // preserving the MAC5/MAC6 plaintext-config invariant. MAC8 mutating
        // endpoints return the new activeLibraryId in the response so Swift
        // can persist via SsdEncryption.swift.
        collection.AddSingleton<IConfigStore, NoOpConfigStore>();

        collection.AddSingleton<IOllamaLifecycleService, MacOllamaLifecycleService>();
        // MAC33: ModelManagementService needs the SSD root to enumerate the
        // on-disk model store; the Mac sidecar can't write back to the
        // encrypted config after pulls, so config.Models is unreliable.
        // The 2-arg ctor defaults to UnknownSystemResourceProbe.Instance —
        // sizing warnings are unused on the Mac sidecar today.
        collection.AddSingleton<IModelManagementService>(sp => new ModelManagementService(
            sp.GetRequiredService<HttpClient>(),
            _ssdRoot));
        collection.AddSingleton<IDocumentOperationsService, DocumentOperationsService>();

        if (_testMode)
        {
            collection.AddSingleton<IChatService, TestModeChatService>();
        }
        else
        {
            collection.AddSingleton<IChatService, ChatService>();
        }

        collection.AddSingleton<ISpeechToTextService, NoOpSpeechToTextService>();
        collection.AddSingleton<ITtsProvider, NoOpTtsProvider>();

        collection.AddSingleton<IRunnerLocalApiService>(sp => new RunnerLocalApiService(
            sp.GetRequiredService<IChatService>(),
            sp.GetRequiredService<ISpeechToTextService>(),
            sp.GetRequiredService<ITtsProvider>(),
            sp.GetService<SsdLogger>(),
            _ssdRoot,
            staticFilesRoot: null,
            docOps: sp.GetRequiredService<IDocumentOperationsService>(),
            libraryManager: sp.GetRequiredService<DocumentLibraryManager>(),
            modelService: sp.GetRequiredService<IModelManagementService>()));

        _services = collection.BuildServiceProvider();
    }

    private void OnLogMessage(string message)
    {
        _logger?.Info(message);
        WriteLineSafe($"log: {message}");
    }

    private void WriteLineSafe(string line)
    {
        // Multiple callers may race on stdout (RunnerLocalApiService log events
        // fire from background threads). Serialize through a local lock so the
        // Swift parent always sees whole lines.
        lock (_stdoutLock)
        {
            try
            {
                _stdout.WriteLine(line);
                _stdout.Flush();
            }
            catch
            {
                // stdout closed (parent gone). Nothing useful to do — the
                // command loop will observe stdin EOF and exit.
            }
        }
    }
}
