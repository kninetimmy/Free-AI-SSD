using FreeAiSsd.RunnerCli;

// Default matches PortableConfig.NetworkPort in shared/. If that default changes,
// update here too — see backlog note about factoring 41555 into a shared constant.
const string DefaultUrl = "http://127.0.0.1:41555";

try
{
    var parsed = CliArgs.Parse(args);

    if (parsed.ShowHelp)
    {
        PrintUsage(Console.Out);
        return 0;
    }

    var urlString = parsed.Url
        ?? Environment.GetEnvironmentVariable("FREEAI_URL")
        ?? DefaultUrl;

    if (!Uri.TryCreate(urlString, UriKind.Absolute, out var baseUri) ||
        baseUri.Scheme is not ("http" or "https"))
    {
        Console.Error.WriteLine($"Invalid URL: {urlString}. Expected http(s)://host:port.");
        return 2;
    }

    var apiKey = parsed.ApiKey ?? Environment.GetEnvironmentVariable("FREEAI_API_KEY");

    using var client = new RunnerApiClient(baseUri, apiKey);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // Ctrl-C exits. On Windows the idle prompt's Console.In.ReadLineAsync does not
        // observe the token synchronously, so the user may need to press Enter after
        // Ctrl-C to actually return — acceptable tradeoff to avoid P/Invoke.
        e.Cancel = true;
        cts.Cancel();
    };

    var repl = new Repl(client, parsed.Model, !parsed.NoStream, Console.In, Console.Out);
    return await repl.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    return 1;
}

static void PrintUsage(TextWriter w)
{
    w.WriteLine("FreeAiSsd.RunnerCli — headless REPL client for a running Runner's LAN API.");
    w.WriteLine();
    w.WriteLine("Usage: FreeAiSsd.RunnerCli [options]");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  --url <url>         Runner base URL. Default: $FREEAI_URL or http://127.0.0.1:41555.");
    w.WriteLine("  --api-key <key>     API key. Default: $FREEAI_API_KEY. Only required if host enforces it.");
    w.WriteLine("  --model <name>      Model to use at startup (can be changed with /model in the REPL).");
    w.WriteLine("  --no-stream         Use /api/chat instead of /api/chat/stream (useful on flaky links).");
    w.WriteLine("  --help, -h          Show this help.");
    w.WriteLine();
    w.WriteLine("Precedence for --url / --api-key: flag > environment variable > default.");
}

internal sealed record CliArgs(string? Url, string? ApiKey, string? Model, bool NoStream, bool ShowHelp)
{
    public static CliArgs Parse(IReadOnlyList<string> argv)
    {
        string? url = null, apiKey = null, model = null;
        var noStream = false;
        var help = false;

        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            switch (a)
            {
                case "--url":
                    url = RequireValue(argv, ref i, a);
                    break;
                case "--api-key":
                    apiKey = RequireValue(argv, ref i, a);
                    break;
                case "--model":
                    model = RequireValue(argv, ref i, a);
                    break;
                case "--no-stream":
                    noStream = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}. Use --help.");
            }
        }

        return new CliArgs(url, apiKey, model, noStream, help);
    }

    private static string RequireValue(IReadOnlyList<string> argv, ref int i, string flag)
    {
        if (i + 1 >= argv.Count)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        return argv[++i];
    }
}
