using NAudio.Wave;

namespace FreeAiSsd.Shared.Client;

/// <summary>
/// Generates simple PCM beep tones for PTT activation/deactivation feedback.
/// No embedded WAV files needed — tones are synthesized on the fly.
/// </summary>
internal static class PttSounds
{
    private const int SampleRate = 44100;
    private const short Amplitude = 8000; // ~25% volume — subtle, not jarring

    /// <summary>
    /// Short rising tone (~100ms, 880 Hz) indicating PTT activation.
    /// </summary>
    public static byte[] GetActivationBeep() => GenerateTone(880, 0.10);

    /// <summary>
    /// Short falling tone (~80ms, 440 Hz) indicating PTT deactivation / end of recording.
    /// </summary>
    public static byte[] GetDeactivationBeep() => GenerateTone(440, 0.08);

    /// <summary>
    /// Short error tone (~200ms, two quick 330 Hz blips) for error feedback.
    /// </summary>
    public static byte[] GetErrorBeep()
    {
        var blip = GenerateTone(330, 0.08);
        var silence = new byte[(int)(SampleRate * 0.04) * 2]; // 40ms silence (16-bit mono)
        var result = new byte[blip.Length + silence.Length + blip.Length];
        Buffer.BlockCopy(blip, 0, result, 0, blip.Length);
        Buffer.BlockCopy(silence, 0, result, blip.Length, silence.Length);
        Buffer.BlockCopy(blip, 0, result, blip.Length + silence.Length, blip.Length);
        return result;
    }

    /// <summary>
    /// Plays a PCM beep asynchronously on the default output device.
    /// Returns immediately — playback happens on a background thread.
    /// </summary>
    /// <remarks>
    /// The <paramref name="outputDevice"/> argument is accepted for API symmetry
    /// with <c>AudioCaptureService</c> but is currently ignored: NAudio's legacy
    /// <c>WaveOut</c> enumeration APIs live in a Windows-only assembly that
    /// isn't reachable from this cross-platform (<c>net8.0</c>) shared project,
    /// and <c>WaveOutEvent</c> exposes no static device listing. Beeps play on
    /// the system default output, which is fine for non-critical PTT feedback.
    /// </remarks>
    public static void PlayAsync(byte[] pcmData, string? outputDevice = null)
    {
        _ = outputDevice; // intentionally unused — see remarks
        Task.Run(() =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream(pcmData);
                using var raw = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
                using var wo = new WaveOutEvent();

                wo.Init(raw);
                wo.Play();

                // Wait for playback to finish (short tones, so this is fast)
                while (wo.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(10);
            }
            catch
            {
                // Beep playback is non-critical — swallow errors silently.
            }
        });
    }

    private static byte[] GenerateTone(double frequencyHz, double durationSeconds)
    {
        int sampleCount = (int)(SampleRate * durationSeconds);
        var buffer = new byte[sampleCount * 2]; // 16-bit mono

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / SampleRate;

            // Apply a short fade-in/fade-out envelope to avoid clicks
            double envelope = 1.0;
            int fadeSamples = Math.Min(sampleCount / 5, (int)(SampleRate * 0.01));
            if (i < fadeSamples)
                envelope = (double)i / fadeSamples;
            else if (i > sampleCount - fadeSamples)
                envelope = (double)(sampleCount - i) / fadeSamples;

            double sample = Math.Sin(2.0 * Math.PI * frequencyHz * t) * Amplitude * envelope;
            short pcm = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

            buffer[i * 2] = (byte)(pcm & 0xFF);
            buffer[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return buffer;
    }
}
