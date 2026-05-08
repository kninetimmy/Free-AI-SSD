using System.Text;
using FreeAiSsd.PrepApp;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC28 drift-pin for <see cref="OllamaServerHandle.ConsumeAsync"/>.
///
/// Pre-MAC28, the inner ollama server's stdout/stderr was silently
/// discarded by a method literally named <c>DrainAsync</c>. v1.3.8 mac
/// field test reproduced the
///   "Ollama server on 127.0.0.1:&lt;port&gt; did not become healthy
///    within 15 seconds"
/// failure with no further diagnostic context — the only thing the user
/// saw was the timeout, while the Gatekeeper "Verifying 'Ollama'" modal
/// was still on screen.
///
/// MAC28 routes ollama's own startup output through <c>onLog</c> so
/// future first-launch failures surface their cause in the PrepApp UI.
/// These tests pin the routing contract so a future refactor cannot
/// silently re-discard.
/// </summary>
public sealed class OllamaServerHandleConsumeTests
{
    [Fact]
    public async Task ConsumeAsync_RoutesEachNonEmptyLineThroughOnLogWithStreamLabel()
    {
        var input = string.Join('\n', new[]
        {
            "time=2026-05-08T09:09:30Z level=INFO msg=\"server config env=...\"",
            "time=2026-05-08T09:09:30Z level=INFO msg=\"Listening on 127.0.0.1:53376\"",
            "time=2026-05-08T09:09:30Z level=INFO msg=\"Dynamic LLM libraries: [metal cpu_avx cpu_avx2]\"",
        }) + "\n";

        var captured = new List<string>();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(input)));

        await OllamaServerHandle.ConsumeAsync(reader, captured.Add, "stderr");

        // Each non-empty line must reach onLog, prefixed so the user can
        // distinguish ollama-server output from sidecar log lines, and
        // labelled so stdout vs stderr is preserved on the wire.
        Assert.Equal(3, captured.Count);
        Assert.All(captured, line => Assert.StartsWith("[ollama serve stderr] ", line));
        Assert.Contains(captured, l => l.Contains("Listening on 127.0.0.1:53376"));
        Assert.Contains(captured, l => l.Contains("Dynamic LLM libraries: [metal cpu_avx cpu_avx2]"));
    }

    [Fact]
    public async Task ConsumeAsync_SkipsEmptyAndWhitespaceLines()
    {
        // Real ollama startup output includes blank lines between sections.
        // Logging them at INFO level would just clutter the PrepApp scroll
        // without adding diagnostic value — pin that they're filtered.
        var input = "first\n\n   \nsecond\n\n";
        var captured = new List<string>();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(input)));

        await OllamaServerHandle.ConsumeAsync(reader, captured.Add, "stdout");

        Assert.Equal(2, captured.Count);
        Assert.Equal("[ollama serve stdout] first", captured[0]);
        Assert.Equal("[ollama serve stdout] second", captured[1]);
    }

    [Fact]
    public async Task ConsumeAsync_OnLogThrowingDoesNotAbortTheDrain()
    {
        // The primary reason this loop exists is to keep the child process
        // from blocking on a full stdout/stderr pipe. A buggy onLog
        // implementation (e.g. UI dispatcher already shut down) must not
        // leave bytes unread on the pipe. The catch around onLog enforces
        // that — pin it so a future cleanup can't trade resilience for
        // "cleaner code".
        var input = "alpha\nbravo\ncharlie\n";
        var attempts = 0;
        var captured = new List<string>();
        Action<string> onLog = line =>
        {
            attempts++;
            if (line.Contains("bravo"))
            {
                throw new InvalidOperationException("simulated UI dispatcher failure");
            }
            captured.Add(line);
        };

        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(input)));

        await OllamaServerHandle.ConsumeAsync(reader, onLog, "stdout");

        // All three lines must have been read off the pipe (attempts == 3),
        // even though the middle one threw on its way to onLog.
        Assert.Equal(3, attempts);
        Assert.Equal(2, captured.Count);
        Assert.Equal("[ollama serve stdout] alpha", captured[0]);
        Assert.Equal("[ollama serve stdout] charlie", captured[1]);
    }

    [Fact]
    public async Task ConsumeAsync_EmptyStream_ReturnsImmediately()
    {
        var captured = new List<string>();
        using var reader = new StreamReader(new MemoryStream(Array.Empty<byte>()));

        await OllamaServerHandle.ConsumeAsync(reader, captured.Add, "stderr");

        Assert.Empty(captured);
    }
}
