using System.Net.Http;
using System.Text.Json;
using FreeAiSsd.MacRunnerHost;

// MAC6 entry point. The sidecar is spawned by the Swift mac-runner with stdin
// piped. The init handshake is a single newline-terminated JSON line:
//
//   { "ssdRoot": "...", "ollamaHost": "...", "config": { ...PortableConfig as JSON dictionary... } }
//
// After the init line, the parent communicates via newline-delimited
// commands on stdin:
//
//   config-update <json-config>   — restart RunnerLocalApiService with a new config
//   shutdown                       — graceful stop, exit 0
//
// Stdin closure is treated as shutdown so the host cannot outlive the parent.
// The host writes "ready: <baseUrl>" and "log: <line>" lines to stdout so the
// Swift app can surface status; failures land on stderr.
//
// Plaintext-config invariant from MAC5: the Swift parent never writes a
// plaintext PortableConfig to disk to pass it here. The handshake travels via
// stdin only, the host holds the parsed config in memory, and on shutdown the
// process simply exits — there is no on-disk artifact to scrub.

return await HostRunner.RunAsync(Console.In, Console.Out, Console.Error, args);

namespace FreeAiSsd.MacRunnerHost
{
    internal static class HostRunner
    {
        public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, TextWriter stderr, string[] args)
        {
            var testMode = args.Any(a => string.Equals(a, "--test-mode", StringComparison.OrdinalIgnoreCase));

            string? initLine;
            try
            {
                initLine = await stdin.ReadLineAsync();
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Failed to read init handshake from stdin: {ex.Message}");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(initLine))
            {
                await stderr.WriteLineAsync("Init handshake missing on stdin (empty line). Refusing to start.");
                return 2;
            }

            HostHandshake handshake;
            try
            {
                handshake = HostHandshake.Parse(initLine);
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Init handshake parse failed: {ex.Message}");
                return 2;
            }

            await using var lifetime = new HostLifetime(handshake.SsdRoot, stdout, stderr, testMode: testMode);
            try
            {
                await lifetime.StartAsync(handshake.Config, handshake.OllamaHost);
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Initial RunnerLocalApiService.StartAsync failed: {ex.Message}");
                return 3;
            }

            // Command loop. Treat stdin EOF as shutdown so an orphaned host always exits.
            while (true)
            {
                string? line;
                try
                {
                    line = await stdin.ReadLineAsync();
                }
                catch (Exception ex)
                {
                    await stderr.WriteLineAsync($"stdin read failed: {ex.Message}");
                    break;
                }

                if (line is null)
                {
                    // EOF — parent closed stdin (or crashed). Shut down.
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                // Task #106: voice-*-response frames from the Swift parent complete a
                // pending /api/voice/query STT/TTS round-trip. Dispatch them first so
                // they're never mistaken for an unknown command.
                if (lifetime.TryDispatchVoiceResponse(line))
                {
                    continue;
                }

                if (line.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (line.StartsWith("config-update", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = line.Length > "config-update".Length
                        ? line["config-update".Length..].TrimStart()
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        await stderr.WriteLineAsync("config-update command missing JSON payload.");
                        continue;
                    }

                    try
                    {
                        var newConfig = HostHandshake.ParseConfig(payload);
                        await lifetime.RestartAsync(newConfig, handshake.OllamaHost);
                    }
                    catch (Exception ex)
                    {
                        await stderr.WriteLineAsync($"config-update failed: {ex.Message}");
                    }
                    continue;
                }

                await stderr.WriteLineAsync($"Unknown command: {line}");
            }

            try
            {
                await lifetime.StopAsync();
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Shutdown error: {ex.Message}");
                return 4;
            }

            return 0;
        }
    }
}
