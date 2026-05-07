using FreeAiSsd.MacPrepHost;

// MAC17 entry point. Mirrors the MAC6 mac-runner-host shape but with a
// command surface focused on prep operations (artifact staging, model
// pull/verify, prereqs, readiness) instead of a long-running HTTP host.
//
// The Swift mac-prep-app spawns this binary with stdin piped. The init
// handshake is a single newline-terminated JSON line:
//
//   { "ssdRoot": "/Volumes/FREEAI", "ollamaHost": "http://127.0.0.1:11434" }
//
// After the init line, the parent communicates via newline-delimited
// commands on stdin. See HostLifetime.HandleCommandAsync for the supported
// command set. Stdin closure is treated as shutdown so the host cannot
// outlive the parent.
//
// Stdout protocol matches mac-runner-host:
//   ready                          — emitted once after handshake parses
//   log: <line>                    — progress / informational forwarding
//   progress: <pct> <message>      — structured progress for SwiftUI
//   result: <command> <json>       — command completion payload
// Failures land on stderr.
//
// Plaintext-config invariant from MAC5: this sidecar never receives the
// plaintext PortableConfig. Encrypted-config IO stays Swift-authoritative
// via SsdEncryption.swift on the parent side. Prep-core's EncryptionService
// is intentionally NOT registered here — Swift owns that surface.

return await HostRunner.RunAsync(Console.In, Console.Out, Console.Error, args);

namespace FreeAiSsd.MacPrepHost
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

            await using var lifetime = new HostLifetime(handshake.SsdRoot, handshake.OllamaHost, stdout, stderr, testMode: testMode);
            try
            {
                lifetime.Start();
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Sidecar startup failed: {ex.Message}");
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

                if (line.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                try
                {
                    await lifetime.HandleCommandAsync(line);
                }
                catch (Exception ex)
                {
                    await stderr.WriteLineAsync($"Command failed ('{line}'): {ex.Message}");
                }
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
