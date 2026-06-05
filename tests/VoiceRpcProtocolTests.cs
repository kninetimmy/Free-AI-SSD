using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeAiSsd.MacRunnerHost;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

// Task #106: the C# half of the sidecar<->Swift voice RPC wire format, plus the
// VoiceRpcChannel correlation logic and the RPC-backed STT/TTS services. The real
// SFSpeech/AVSpeech round-trip is on-hardware only (no mic/Speech in CI); these
// pin the serialize/parse and id-correlation that the wire contract depends on.
public class VoiceRpcProtocolTests
{
    [Fact]
    public void WrapPcm16ToWav_WritesCanonical16kMonoHeader()
    {
        var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var wav = VoiceRpcProtocol.WrapPcm16ToWav(pcm);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));        // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));        // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));    // sample rate
        Assert.Equal(16000 * 2, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));        // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));       // bits per sample
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void SerializeSttRequest_HasPrefix_AndBase64Audio()
    {
        var line = VoiceRpcProtocol.SerializeSttRequest(7, new byte[] { 0x41, 0x42, 0x43 });
        Assert.StartsWith("voice-stt-request ", line);
        var body = JsonDocument.Parse(line["voice-stt-request ".Length..]).RootElement;
        Assert.Equal(7, body.GetProperty("id").GetInt32());
        Assert.Equal("QUJD", body.GetProperty("wavBase64").GetString());
    }

    [Fact]
    public void SerializeTtsRequest_CarriesTextAndVoiceSettings()
    {
        var line = VoiceRpcProtocol.SerializeTtsRequest(3, "hi", "voice.x", 5, 80);
        Assert.StartsWith("voice-tts-request ", line);
        var body = JsonDocument.Parse(line["voice-tts-request ".Length..]).RootElement;
        Assert.Equal(3, body.GetProperty("id").GetInt32());
        Assert.Equal("hi", body.GetProperty("text").GetString());
        Assert.Equal("voice.x", body.GetProperty("voiceId").GetString());
        Assert.Equal(5, body.GetProperty("rate").GetInt32());
        Assert.Equal(80, body.GetProperty("volume").GetInt32());
    }

    [Fact]
    public void TryParseSttResponse_ParsesSwiftShapedFrame()
    {
        // Shape produced by VoiceRpcProtocol.swift sttResponse(...).
        var line = "voice-stt-response {\"error\":null,\"id\":9,\"text\":\"engine start\"}";
        Assert.True(VoiceRpcProtocol.TryParseSttResponse(line, out var frame));
        Assert.NotNull(frame);
        Assert.Equal(9, frame!.Id);
        Assert.Equal("engine start", frame.Text);
        Assert.Null(frame.Error);
    }

    [Fact]
    public void TryParseTtsResponse_ParsesErrorFrame()
    {
        var line = "voice-tts-response {\"error\":\"denied\",\"id\":2,\"wavBase64\":null}";
        Assert.True(VoiceRpcProtocol.TryParseTtsResponse(line, out var frame));
        Assert.Equal(2, frame!.Id);
        Assert.Null(frame.WavBase64);
        Assert.Equal("denied", frame.Error);
    }

    [Fact]
    public void IsVoiceResponse_DistinguishesFromOtherCommands()
    {
        Assert.True(VoiceRpcProtocol.IsVoiceResponse("voice-stt-response {}"));
        Assert.True(VoiceRpcProtocol.IsVoiceResponse("voice-tts-response {}"));
        Assert.False(VoiceRpcProtocol.IsVoiceResponse("shutdown"));
        Assert.False(VoiceRpcProtocol.IsVoiceResponse("config-update {}"));
    }
}

public class VoiceRpcChannelTests
{
    [Fact]
    public async Task SendSttRequest_CorrelatesResponseById()
    {
        string? sent = null;
        var channel = new VoiceRpcChannel(line => sent = line, logger: null);

        var pending = channel.SendSttRequestAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert.NotNull(sent);
        Assert.StartsWith("voice-stt-request ", sent);

        // Echo back a response with the same id the request carried.
        var id = JsonDocument.Parse(sent!["voice-stt-request ".Length..]).RootElement.GetProperty("id").GetInt32();
        Assert.True(channel.TryCompleteFromStdinLine(
            $"voice-stt-response {{\"id\":{id},\"text\":\"hello world\",\"error\":null}}"));

        var result = await pending;
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task SendTtsRequest_PropagatesErrorFrame()
    {
        string? sent = null;
        var channel = new VoiceRpcChannel(line => sent = line, logger: null);

        var pending = channel.SendTtsRequestAsync("hi", null, 0, 100, CancellationToken.None);
        var id = JsonDocument.Parse(sent!["voice-tts-request ".Length..]).RootElement.GetProperty("id").GetInt32();
        channel.TryCompleteFromStdinLine($"voice-tts-response {{\"id\":{id},\"wavBase64\":null,\"error\":\"boom\"}}");

        var result = await pending;
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public void TryCompleteFromStdinLine_ReturnsFalseForNonVoiceLines()
    {
        var channel = new VoiceRpcChannel(_ => { }, logger: null);
        Assert.False(channel.TryCompleteFromStdinLine("shutdown"));
        Assert.False(channel.TryCompleteFromStdinLine("config-update {}"));
    }

    [Fact]
    public async Task SendStt_CancelledToken_Cancels()
    {
        var channel = new VoiceRpcChannel(_ => { }, logger: null);
        using var cts = new CancellationTokenSource();
        var pending = channel.SendSttRequestAsync(new byte[] { 1 }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}

public class RpcSpeechToTextServiceTests
{
    [Fact]
    public async Task TranscribeAudio_WrapsPcmAndReturnsTranscript()
    {
        string? sent = null;
        var channel = new VoiceRpcChannel(line => sent = line, logger: null);
        var stt = new RpcSpeechToTextService(channel);
        Assert.True(stt.IsModelLoaded); // short-circuits EnsureSttInitializedAsync

        var pending = stt.TranscribeAudioAsync(new byte[] { 0x10, 0x20 }, CancellationToken.None);

        // The request must carry a base64 WAV (header + the 2 PCM bytes => 46 bytes).
        var body = JsonDocument.Parse(sent!["voice-stt-request ".Length..]).RootElement;
        var wav = Convert.FromBase64String(body.GetProperty("wavBase64").GetString()!);
        Assert.Equal(46, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));

        var id = body.GetProperty("id").GetInt32();
        channel.TryCompleteFromStdinLine($"voice-stt-response {{\"id\":{id},\"text\":\"flaps up\",\"error\":null}}");

        var result = await pending;
        var success = Assert.IsType<TranscriptionResult.Success>(result);
        Assert.Equal("flaps up", success.Text);
    }

    [Fact]
    public async Task TranscribeAudio_ErrorFrame_BecomesFailure()
    {
        string? sent = null;
        var channel = new VoiceRpcChannel(line => sent = line, logger: null);
        var stt = new RpcSpeechToTextService(channel);

        var pending = stt.TranscribeAudioAsync(new byte[] { 0x10 }, CancellationToken.None);
        var id = JsonDocument.Parse(sent!["voice-stt-request ".Length..]).RootElement.GetProperty("id").GetInt32();
        channel.TryCompleteFromStdinLine($"voice-stt-response {{\"id\":{id},\"text\":null,\"error\":\"denied\"}}");

        var result = await pending;
        var failure = Assert.IsType<TranscriptionResult.Failure>(result);
        Assert.Equal("denied", failure.ErrorMessage);
    }
}

public class RpcTextToSpeechServiceTests
{
    [Fact]
    public async Task SynthesizeToWav_ForwardsConfigVoiceSettings_AndReturnsBytes()
    {
        string? sent = null;
        var channel = new VoiceRpcChannel(line => sent = line, logger: null);
        var config = new PortableConfig { TtsVoiceName = "voice.z", TtsRate = -3, TtsVolume = 55 };
        var tts = new RpcTextToSpeechService(channel, config);

        var pending = tts.SynthesizeToWavAsync("spoken reply", CancellationToken.None);
        var body = JsonDocument.Parse(sent!["voice-tts-request ".Length..]).RootElement;
        Assert.Equal("voice.z", body.GetProperty("voiceId").GetString());
        Assert.Equal(-3, body.GetProperty("rate").GetInt32());
        Assert.Equal(55, body.GetProperty("volume").GetInt32());

        var id = body.GetProperty("id").GetInt32();
        var wavBase64 = Convert.ToBase64String(new byte[] { 0xAA, 0xBB });
        channel.TryCompleteFromStdinLine($"voice-tts-response {{\"id\":{id},\"wavBase64\":\"{wavBase64}\",\"error\":null}}");

        var bytes = await pending;
        Assert.Equal(new byte[] { 0xAA, 0xBB }, bytes);
    }

    [Fact]
    public async Task SpeakAsync_HostPlayback_IsUnsupportedOnMacSidecar()
    {
        var channel = new VoiceRpcChannel(_ => { }, logger: null);
        var tts = new RpcTextToSpeechService(channel, new PortableConfig());
        await Assert.ThrowsAsync<NotSupportedException>(() => tts.SpeakAsync("hi"));
    }
}
