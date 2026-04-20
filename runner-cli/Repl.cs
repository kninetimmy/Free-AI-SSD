namespace FreeAiSsd.RunnerCli;

public sealed class Repl
{
    private readonly RunnerApiClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _stream;
    private string? _model;

    public Repl(
        RunnerApiClient client,
        string? initialModel,
        bool stream,
        TextReader input,
        TextWriter output)
    {
        _client = client;
        _model = string.IsNullOrWhiteSpace(initialModel) ? null : initialModel.Trim();
        _stream = stream;
        _input = input;
        _output = output;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        _output.WriteLine($"Target: {_client.BaseUri}");

        // Probe once before entering the loop so a wrong URL / missing API key /
        // firewall block fails loud instead of showing a misleading "connected".
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            var health = await _client.GetHealthAsync(probeCts.Token);
            _output.WriteLine($"Host reachable (ollamaRunning={health.OllamaRunning}). Type /help for commands. Ctrl-C or 'exit' to quit.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Warning: initial health check failed ({ex.Message}). Entering REPL anyway — first request will retry.");
        }

        while (!ct.IsCancellationRequested)
        {
            await _output.WriteAsync(Prompt());
            await _output.FlushAsync();

            string? line;
            try
            {
                line = await _input.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                // EOF (Ctrl-D or pipe closed) → clean exit.
                _output.WriteLine();
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var outcome = await HandleLineAsync(trimmed, ct);
            if (outcome == LineOutcome.Exit)
            {
                break;
            }
        }

        return 0;
    }

    internal enum LineOutcome { Continue, Exit }

    internal async Task<LineOutcome> HandleLineAsync(string line, CancellationToken ct)
    {
        if (IsExitCommand(line))
        {
            return LineOutcome.Exit;
        }

        if (line.StartsWith('/'))
        {
            await HandleSlashCommandAsync(line, ct);
            return LineOutcome.Continue;
        }

        if (_model is null)
        {
            _output.WriteLine("No model selected. Use '/models' to list installed models, then '/model <name>'.");
            return LineOutcome.Continue;
        }

        await SendPromptAsync(line, ct);
        return LineOutcome.Continue;
    }

    private static bool IsExitCommand(string line) =>
        line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("/exit", StringComparison.OrdinalIgnoreCase);

    private async Task HandleSlashCommandAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;

        switch (command)
        {
            case "/help":
                PrintHelp();
                break;

            case "/models":
                await ShowModelsAsync(ct);
                break;

            case "/model":
                SetModel(arg);
                break;

            case "/health":
                await ShowHealthAsync(ct);
                break;

            case "/clear":
                try { Console.Clear(); } catch { /* not a real console (e.g. pipe) */ }
                break;

            default:
                _output.WriteLine($"Unknown command: {command}. Type /help.");
                break;
        }
    }

    private void PrintHelp()
    {
        _output.WriteLine("Commands:");
        _output.WriteLine("  <prompt>          Send prompt to the current model (RAG context applied server-side).");
        _output.WriteLine("  /models           List installed models on the host.");
        _output.WriteLine("  /model <name>     Set the model to use for subsequent prompts.");
        _output.WriteLine("  /health           Show host API health.");
        _output.WriteLine("  /clear            Clear the screen.");
        _output.WriteLine("  /help             Show this help.");
        _output.WriteLine("  /quit, /exit      Exit. 'quit' and 'exit' also work without the slash.");
        _output.WriteLine($"  Current model:   {(_model ?? "(none)")}");
    }

    private async Task ShowModelsAsync(CancellationToken ct)
    {
        try
        {
            var models = await _client.GetModelsAsync(ct);
            if (models.Count == 0)
            {
                _output.WriteLine("(no models installed on host)");
                return;
            }

            foreach (var m in models)
            {
                var marker = string.Equals(m, _model, StringComparison.OrdinalIgnoreCase) ? " (current)" : string.Empty;
                _output.WriteLine($"  {m}{marker}");
            }
        }
        catch (Exception ex)
        {
            WriteError("Failed to list models", ex);
        }
    }

    private void SetModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _output.WriteLine($"Current model: {(_model ?? "(none)")}");
            _output.WriteLine("Usage: /model <name>");
            return;
        }

        _model = name.Trim();
        _output.WriteLine($"Model set to: {_model}");
    }

    private async Task ShowHealthAsync(CancellationToken ct)
    {
        try
        {
            var health = await _client.GetHealthAsync(ct);
            _output.WriteLine(
                $"status={health.Status} network={health.NetworkModeEnabled} " +
                $"requireApiKey={health.RequireApiKey} ollamaRunning={health.OllamaRunning} " +
                $"serverTimeUtc={health.TimestampUtc:O}");
        }
        catch (Exception ex)
        {
            WriteError("Health check failed", ex);
        }
    }

    private async Task SendPromptAsync(string prompt, CancellationToken ct)
    {
        try
        {
            if (_stream)
            {
                await StreamPromptAsync(prompt, ct);
            }
            else
            {
                var response = await _client.ChatAsync(_model!, prompt, ct);
                _output.WriteLine(response.ResponseText);
                WriteFooter(response.UsedRagContext, response.Sources);
            }
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine();
            _output.WriteLine("(cancelled)");
        }
        catch (Exception ex)
        {
            WriteError("Request failed", ex);
        }
    }

    private async Task StreamPromptAsync(string prompt, CancellationToken ct)
    {
        var sources = (IReadOnlyList<string>?)null;
        var usedRag = false;
        var sawAnyToken = false;

        await foreach (var evt in _client.ChatStreamAsync(_model!, prompt, ct))
        {
            switch (evt.Type)
            {
                case "token" when evt.Token is { Length: > 0 }:
                    await _output.WriteAsync(evt.Token);
                    await _output.FlushAsync();
                    sawAnyToken = true;
                    break;
                case "complete":
                    usedRag = evt.UsedRagContext;
                    sources = evt.Sources;
                    if (!sawAnyToken && !string.IsNullOrEmpty(evt.ResponseText))
                    {
                        // Server sent no incremental tokens (e.g. whole response in 'complete').
                        _output.Write(evt.ResponseText);
                    }
                    break;
                case "rag-warning":
                    _output.WriteLine();
                    _output.WriteLine($"[Warning: answered without document context — {evt.Message}]");
                    break;
                case "error":
                    _output.WriteLine();
                    _output.WriteLine($"[Error: {evt.Message}]");
                    break;
            }
        }

        _output.WriteLine();
        WriteFooter(usedRag, sources);
    }

    private void WriteFooter(bool usedRag, IReadOnlyList<string>? sources)
    {
        if (usedRag && sources is { Count: > 0 })
        {
            _output.WriteLine($"— sources: {string.Join(", ", sources)}");
        }
        else
        {
            _output.WriteLine("— (no RAG context used)");
        }
    }

    private void WriteError(string label, Exception ex)
    {
        _output.WriteLine($"{label}: {ex.Message}");
    }

    private string Prompt() => _model is null ? "> " : $"{_model}> ";
}
