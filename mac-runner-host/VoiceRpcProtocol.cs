using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.MacRunnerHost;

// Task #106 (Phase 2 VR companion). The Mac sidecar has no native STT/TTS of its
// own — the Swift parent owns the macOS Speech / AVSpeechSynthesizer frameworks
// (decision #166/#168). So /api/voice/query on the Mac host round-trips audio to
// Swift over the existing stdin/stdout channel rather than wiring a .NET engine.
//
// This class is the wire format for that round-trip, kept free of any process /
// pipe / framework dependency so it unit-tests cleanly from tests/ (which already
// references this project). It is the C# half of the protocol; the Swift half is
// mac-runner/Sources/VoiceRpcProtocol.swift and the two must stay in lockstep.
//
// Frames are single newline-delimited lines, "<prefix> <json>", correlated by an
// integer id. Requests flow sidecar -> Swift on the sidecar's stdout (alongside
// the existing "ready:"/"log:" lines); responses flow Swift -> sidecar on the
// sidecar's stdin (alongside the existing "config-update"/"shutdown" commands).

/// <summary>STT request payload: raw audio wrapped as a base64 WAV for the recognizer.</summary>
internal sealed record SttRequestFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("wavBase64")] string WavBase64);

/// <summary>TTS request payload: text plus the host's configured voice settings.</summary>
internal sealed record TtsRequestFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("voiceId")] string? VoiceId,
    [property: JsonPropertyName("rate")] int Rate,
    [property: JsonPropertyName("volume")] int Volume);

/// <summary>STT response payload: the transcript, or a non-null error.</summary>
internal sealed record SttResponseFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>TTS response payload: a base64 WAV, or a non-null error.</summary>
internal sealed record TtsResponseFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("wavBase64")] string? WavBase64,
    [property: JsonPropertyName("error")] string? Error);

internal static class VoiceRpcProtocol
{
    public const string SttRequestPrefix = "voice-stt-request";
    public const string TtsRequestPrefix = "voice-tts-request";
    public const string SttResponsePrefix = "voice-stt-response";
    public const string TtsResponsePrefix = "voice-tts-response";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ── Request serialization (sidecar -> Swift) ───────────────────────────

    public static string SerializeSttRequest(int id, byte[] wav)
        => $"{SttRequestPrefix} {JsonSerializer.Serialize(new SttRequestFrame(id, Convert.ToBase64String(wav)), JsonOptions)}";

    public static string SerializeTtsRequest(int id, string text, string? voiceId, int rate, int volume)
        => $"{TtsRequestPrefix} {JsonSerializer.Serialize(new TtsRequestFrame(id, text, voiceId, rate, volume), JsonOptions)}";

    // ── Response parsing (Swift -> sidecar) ────────────────────────────────

    public static bool IsVoiceResponse(string line)
        => line.StartsWith(SttResponsePrefix, StringComparison.Ordinal)
        || line.StartsWith(TtsResponsePrefix, StringComparison.Ordinal);

    public static bool TryParseSttResponse(string line, out SttResponseFrame? frame)
        => TryParse(line, SttResponsePrefix, out frame);

    public static bool TryParseTtsResponse(string line, out TtsResponseFrame? frame)
        => TryParse(line, TtsResponsePrefix, out frame);

    private static bool TryParse<T>(string line, string prefix, out T? frame) where T : class
    {
        frame = null;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var json = line.Length > prefix.Length ? line[prefix.Length..].TrimStart() : string.Empty;
        if (json.Length == 0)
        {
            return false;
        }

        try
        {
            frame = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return frame is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ── PCM -> WAV ─────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a raw little-endian PCM buffer in a canonical 44-byte RIFF/WAVE
    /// header so the Swift recognizer can load it from a temp file. The endpoint
    /// strips the header off the companion's upload and hands STT the bare PCM
    /// (validated 16-bit mono 16 kHz at RunnerLocalApiService); this restores it.
    /// </summary>
    public static byte[] WrapPcm16ToWav(byte[] pcm, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataLen = pcm.Length;

        var wav = new byte[44 + dataLen];
        var span = wav.AsSpan();

        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + dataLen);
        Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));

        Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);           // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), bitsPerSample);

        Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataLen);
        pcm.CopyTo(span.Slice(44));

        return wav;
    }
}
