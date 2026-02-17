using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace FreeAiSsd.PrepApp;

public sealed class ModelOperations
{
    public async Task<PullModelResult> PullModelAsync(string ollamaExe, string modelRoot, string modelTag, Action<string> onLog, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelRoot,
            ["OLLAMA_HOST"] = "127.0.0.1:11500"
        };

        var exitCode = await RunProcessStreamingAsync(ollamaExe, $"pull {modelTag}", Path.GetDirectoryName(ollamaExe)!, env, onLog, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to pull model {modelTag}. Exit code: {exitCode}");
        }

        var modelFile = FindModelBlobForModel(modelRoot, modelTag)
                        ?? throw new FileNotFoundException($"Unable to locate model blob for {modelTag} in {modelRoot}.");

        var sha256 = await ComputeSha256Async(modelFile, ct);
        var size = new FileInfo(modelFile).Length;
        return new PullModelResult(sha256, size);
    }

    public async Task<bool> VerifyModelAsync(string modelRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct)
    {
        var modelBlob = FindModelBlobForModel(modelRoot, modelTag);
        if (modelBlob is null)
        {
            onLog($"Verify failed for {modelTag}: model blob not found.");
            return false;
        }

        var actualHash = await ComputeSha256Async(modelBlob, ct);
        var matches = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        onLog(matches
            ? $"Verify passed for {modelTag}."
            : $"Verify failed for {modelTag}: expected {expectedHash}, got {actualHash}.");
        return matches;
    }

    public static string? FindModelBlobForModel(string modelRoot, string model)
    {
        var manifestTag = model.Replace(':', '-');
        var manifestsPath = Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library");
        if (!Directory.Exists(manifestsPath))
        {
            return null;
        }

        var manifest = Directory.EnumerateFiles(manifestsPath, manifestTag, SearchOption.AllDirectories).FirstOrDefault();
        if (manifest is null)
        {
            return null;
        }

        var content = File.ReadAllText(manifest);
        var digestLine = content.Split('\n').FirstOrDefault(l => l.Contains("\"digest\"", StringComparison.OrdinalIgnoreCase));
        if (digestLine is null)
        {
            return null;
        }

        var marker = "sha256:";
        var idx = digestLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var hashStart = idx + marker.Length;
        var hashChars = new string(digestLine.Skip(hashStart).TakeWhile(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(hashChars))
        {
            return null;
        }

        var blob = Path.Combine(modelRoot, "blobs", $"sha256-{hashChars}");
        return File.Exists(blob) ? blob : null;
    }

    private static async Task<string> ComputeSha256Async(string modelPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(modelPath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> RunProcessStreamingAsync(string fileName, string arguments, string workingDirectory, IDictionary<string, string> env, Action<string> onOutput, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // no-op best effort
            }
        });

        var outputTask = Consume(process.StandardOutput, onOutput, ct);
        var errorTask = Consume(process.StandardError, onOutput, ct);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task Consume(StreamReader reader, Action<string> onOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }
}

public sealed record PullModelResult(string Sha256, long SizeBytes);
