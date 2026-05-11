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

            // MAC31: tracks the most-recent detached pull-model task so
            // shutdown can drain it before tearing the lifetime down.
            // Without this drain, a fire-and-forget pull task may still
            // be writing its result line to stdout when the lifetime
            // disposes — which closes the writer mid-write.
            Task? activePullTask = null;

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

                // MAC31: detach pull-model so the command loop can read
                // the next stdin line — specifically `cancel-pull` —
                // while the pull is still in flight. HandleCommandAsync
                // emits its own result line for both success and
                // cancellation paths, so the parent's awaitCommandResult
                // continuation still resolves correctly. All other
                // commands stay sequential because the prep flow
                // depends on their ordering (ensure-structure must
                // finish before stage-runner runs, etc.) and Swift
                // never issues two of them in parallel anyway.
                if (IsPullModelCommand(line))
                {
                    var pullLine = line;
                    activePullTask = Task.Run(async () =>
                    {
                        try
                        {
                            await lifetime.HandleCommandAsync(pullLine);
                        }
                        catch (OperationCanceledException)
                        {
                            // PullModelAsync's catch already wrote
                            // the cancelled result line; nothing to
                            // surface to stderr.
                        }
                        catch (Exception ex)
                        {
                            // 2026-05-12 field test: PullModelAsync's
                            // inner catch already emits ok=false for
                            // exceptions inside the main pull path.
                            // This is the belt-and-suspenders layer for
                            // anything that throws BEFORE the inner try
                            // (e.g. ollamaExe missing, staging-precheck
                            // disk-full): emit a stdout result line so
                            // Swift's PrepHostController unblocks rather
                            // than waiting forever on a stderr message
                            // it never reads.
                            var modelTag = ExtractPullModelTag(pullLine);
                            try
                            {
                                await stdout.WriteLineAsync(
                                    "result: pull-model " +
                                    System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        ok = false,
                                        modelTag,
                                        reason = "pull-exception",
                                        message = ex.Message,
                                    }));
                            }
                            catch { /* parent likely gone */ }
                            try { await stderr.WriteLineAsync($"Command failed ('{pullLine}'): {ex.Message}"); }
                            catch { /* parent likely gone; loop will observe stdin EOF and exit */ }
                        }
                    });
                    continue;
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

            // MAC31: drain the in-flight pull task before disposing.
            // Cancel first so a real pull doesn't make shutdown wait for
            // the full download — cancel-pull is idempotent so it's safe
            // even if the task already completed. Then wait up to 5s for
            // the task's finally block to run (writes the cancelled
            // result line, releases the CTS slot). The lifetime's
            // _ollamaServer.Dispose() in DisposeAsync below kills the
            // process tree as a backstop if the cancel signal didn't
            // propagate in time.
            if (activePullTask is not null && !activePullTask.IsCompleted)
            {
                try { await lifetime.HandleCommandAsync("cancel-pull"); }
                catch { /* best-effort */ }
                try { await activePullTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* timeout or fault — DisposeAsync's server kill is the backstop */ }
            }
            else if (activePullTask is not null)
            {
                // Already complete — just await to surface any fault for visibility.
                try { await activePullTask; } catch { }
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

        /// <summary>
        /// MAC31: detects whether a stdin command line is a pull-model
        /// invocation. We dispatch those off the loop's await so a
        /// follow-up <c>cancel-pull</c> can be received while the pull
        /// is in flight. Case-insensitive to match
        /// <see cref="HostLifetime.HandleCommandAsync"/>'s own
        /// normalization.
        /// </summary>
        private static bool IsPullModelCommand(string line)
        {
            var trimmed = line.TrimStart();
            const string pullModel = "pull-model";
            if (!trimmed.StartsWith(pullModel, StringComparison.OrdinalIgnoreCase)) return false;
            // Must be either "pull-model" exactly or "pull-model <payload>"
            // — never something like "pull-modelfoo".
            if (trimmed.Length == pullModel.Length) return true;
            return trimmed[pullModel.Length] == ' ' || trimmed[pullModel.Length] == '\t';
        }

        /// <summary>
        /// 2026-05-12: surface the model tag from a `pull-model <tag>`
        /// command line so the fallback exception-result emitter can
        /// echo it back to Swift. Returns empty when the line has no
        /// payload — the result still unblocks the waiter.
        /// </summary>
        internal static string ExtractPullModelTag(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            var trimmed = line.TrimStart();
            const string pullModel = "pull-model";
            if (!trimmed.StartsWith(pullModel, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (trimmed.Length <= pullModel.Length) return string.Empty;
            return trimmed[pullModel.Length..].Trim();
        }
    }
}
