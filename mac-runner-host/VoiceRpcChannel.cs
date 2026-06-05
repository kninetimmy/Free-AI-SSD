using System.Collections.Concurrent;
using FreeAiSsd.Shared;

namespace FreeAiSsd.MacRunnerHost;

// Task #106. Correlated request/response over the existing sidecar<->Swift stdio
// channel. The sidecar issues STT/TTS requests on stdout (via the same locked
// writer the "ready:"/"log:" lines use) and the Swift parent replies on stdin,
// where Program.cs's command loop hands each "voice-*-response" line here.
//
// Each request carries a monotonically-increasing id; a pending
// TaskCompletionSource per id is completed when the matching response arrives.
// The HTTP request's CancellationToken (from /api/voice/query) bounds the wait,
// so a dropped/hung Swift side surfaces as a cancellation rather than a leak.

internal sealed class VoiceRpcChannel : IDisposable
{
    private readonly Action<string> _writeLine;
    private readonly SsdLogger? _logger;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<SttResponseFrame>> _pendingStt = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<TtsResponseFrame>> _pendingTts = new();
    private volatile bool _disposed;

    /// <param name="writeLine">
    /// Writes a whole line to the sidecar's stdout. Must be the same serialized
    /// writer used for "ready:"/"log:" so request frames never interleave.
    /// </param>
    public VoiceRpcChannel(Action<string> writeLine, SsdLogger? logger)
    {
        _writeLine = writeLine;
        _logger = logger;
    }

    public async Task<SttResponseFrame> SendSttRequestAsync(byte[] wav, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancelledOrDisposed(_disposed);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<SttResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStt[id] = tcs;

        using var reg = cancellationToken.Register(static state =>
        {
            var (dict, key) = ((ConcurrentDictionary<int, TaskCompletionSource<SttResponseFrame>>, int))state!;
            if (dict.TryRemove(key, out var pending))
            {
                pending.TrySetCanceled();
            }
        }, (_pendingStt, id));

        try
        {
            _writeLine(VoiceRpcProtocol.SerializeSttRequest(id, wav));
        }
        catch (Exception ex)
        {
            _pendingStt.TryRemove(id, out _);
            throw new InvalidOperationException($"Failed to send STT request to Swift host: {ex.Message}", ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<TtsResponseFrame> SendTtsRequestAsync(string text, string? voiceId, int rate, int volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancelledOrDisposed(_disposed);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<TtsResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTts[id] = tcs;

        using var reg = cancellationToken.Register(static state =>
        {
            var (dict, key) = ((ConcurrentDictionary<int, TaskCompletionSource<TtsResponseFrame>>, int))state!;
            if (dict.TryRemove(key, out var pending))
            {
                pending.TrySetCanceled();
            }
        }, (_pendingTts, id));

        try
        {
            _writeLine(VoiceRpcProtocol.SerializeTtsRequest(id, text, voiceId, rate, volume));
        }
        catch (Exception ex)
        {
            _pendingTts.TryRemove(id, out _);
            throw new InvalidOperationException($"Failed to send TTS request to Swift host: {ex.Message}", ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Called from the Program.cs stdin loop for every command line. Returns true
    /// (and completes the matching pending request) if the line is a voice
    /// response frame; false if it's some other command the loop should handle.
    /// </summary>
    public bool TryCompleteFromStdinLine(string line)
    {
        if (!VoiceRpcProtocol.IsVoiceResponse(line))
        {
            return false;
        }

        if (VoiceRpcProtocol.TryParseSttResponse(line, out var stt) && stt is not null)
        {
            if (_pendingStt.TryRemove(stt.Id, out var pending))
            {
                pending.TrySetResult(stt);
            }
            else
            {
                _logger?.Warn($"STT response for unknown/expired id {stt.Id} ignored.");
            }
            return true;
        }

        if (VoiceRpcProtocol.TryParseTtsResponse(line, out var tts) && tts is not null)
        {
            if (_pendingTts.TryRemove(tts.Id, out var pending))
            {
                pending.TrySetResult(tts);
            }
            else
            {
                _logger?.Warn($"TTS response for unknown/expired id {tts.Id} ignored.");
            }
            return true;
        }

        // Recognizable prefix but unparseable payload — swallow it so it isn't
        // mistaken for an unknown command, and log for diagnosis.
        _logger?.Warn("Dropped malformed voice-*-response frame.");
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        FaultAll(_pendingStt);
        FaultAll(_pendingTts);
    }

    private static void FaultAll<T>(ConcurrentDictionary<int, TaskCompletionSource<T>> pending)
    {
        foreach (var key in pending.Keys.ToList())
        {
            if (pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(VoiceRpcChannel),
                    "Voice RPC channel was torn down (host restart/shutdown) before the response arrived."));
            }
        }
    }
}

internal static class VoiceRpcChannelExtensions
{
    public static void ThrowIfCancelledOrDisposed(this CancellationToken token, bool disposed)
    {
        token.ThrowIfCancellationRequested();
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VoiceRpcChannel));
        }
    }
}
