using System.Net;
using System.Net.Http;
using System.Text;
using FreeAiSsd.RunnerCli;

namespace FreeAiSsd.Tests;

public sealed class RunnerCliReplTests
{
    [Fact]
    public async Task HelpCommand_PrintsCommandList()
    {
        var (repl, output) = BuildRepl(new StubHandler((_, _) => Empty()));

        var outcome = await repl.HandleLineAsync("/help", CancellationToken.None);

        Assert.Equal(Repl.LineOutcome.Continue, outcome);
        Assert.Contains("/models", output.ToString());
        Assert.Contains("/model <name>", output.ToString());
        Assert.Contains("/quit", output.ToString());
    }

    [Fact]
    public async Task ExitAliases_ReturnExitOutcome()
    {
        foreach (var alias in new[] { "quit", "exit", "/quit", "/exit", "QUIT", "Exit" })
        {
            var (repl, _) = BuildRepl(new StubHandler((_, _) => Empty()));
            var outcome = await repl.HandleLineAsync(alias, CancellationToken.None);
            Assert.Equal(Repl.LineOutcome.Exit, outcome);
        }
    }

    [Fact]
    public async Task PromptWithoutModel_EmitsHint()
    {
        var (repl, output) = BuildRepl(new StubHandler((_, _) => Empty()), model: null);

        var outcome = await repl.HandleLineAsync("what is the capital of france?", CancellationToken.None);

        Assert.Equal(Repl.LineOutcome.Continue, outcome);
        Assert.Contains("/models", output.ToString());
    }

    [Fact]
    public async Task UnknownSlashCommand_IsReported()
    {
        var (repl, output) = BuildRepl(new StubHandler((_, _) => Empty()));

        await repl.HandleLineAsync("/bogus", CancellationToken.None);

        Assert.Contains("Unknown command", output.ToString());
    }

    [Fact]
    public async Task ModelCommand_UpdatesCurrentModel()
    {
        var (repl, output) = BuildRepl(new StubHandler((_, _) => Empty()), model: null);

        await repl.HandleLineAsync("/model phi3", CancellationToken.None);

        Assert.Contains("Model set to: phi3", output.ToString());
    }

    [Fact]
    public async Task StreamingPrompt_RendersTokensAndSources()
    {
        var ndjson = string.Join('\n',
            """{"type":"start","model":"phi3"}""",
            """{"type":"token","token":"Paris"}""",
            """{"type":"token","token":"."}""",
            """{"type":"complete","usedRagContext":true,"sources":["atlas.pdf"],"responseText":"Paris."}""");

        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("/api/chat/stream", req.RequestUri!.AbsolutePath);
            return NdjsonResponse(ndjson);
        });

        var (repl, output) = BuildRepl(handler, model: "phi3");
        await repl.HandleLineAsync("capital of france?", CancellationToken.None);

        var rendered = output.ToString();
        Assert.Contains("Paris.", rendered);
        Assert.Contains("sources: atlas.pdf", rendered);
    }

    [Fact]
    public async Task NonStreamingPrompt_RendersResponseBodyAndSources()
    {
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("/api/chat", req.RequestUri!.AbsolutePath);
            return JsonResponse("""{"responseText":"42.","sources":["hhgttg.txt"],"usedRagContext":true}""");
        });

        var (repl, output) = BuildRepl(handler, model: "phi3", stream: false);
        await repl.HandleLineAsync("meaning of life?", CancellationToken.None);

        var rendered = output.ToString();
        Assert.Contains("42.", rendered);
        Assert.Contains("sources: hhgttg.txt", rendered);
    }

    [Fact]
    public async Task NonStreamingPrompt_NoRagContext_EmitsNegativeFooter()
    {
        var handler = new StubHandler((_, _) =>
            JsonResponse("""{"responseText":"I don't know.","sources":[],"usedRagContext":false}"""));

        var (repl, output) = BuildRepl(handler, model: "phi3", stream: false);
        await repl.HandleLineAsync("anything?", CancellationToken.None);

        Assert.Contains("(no RAG context used)", output.ToString());
    }

    [Fact]
    public async Task RequestFailure_IsReportedWithoutExitingRepl()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (repl, output) = BuildRepl(handler, model: "phi3");

        var outcome = await repl.HandleLineAsync("hi", CancellationToken.None);

        Assert.Equal(Repl.LineOutcome.Continue, outcome);
        Assert.Contains("Request failed", output.ToString());
    }

    private static (Repl repl, StringWriter output) BuildRepl(
        StubHandler handler,
        string? model = "phi3",
        bool stream = true)
    {
        var http = new HttpClient(handler);
        var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), null, http);
        var output = new StringWriter();
        var repl = new Repl(client, model, stream, new StringReader(""), output);
        return (repl, output);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NdjsonResponse(string ndjson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
    };

    private static HttpResponseMessage Empty() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
