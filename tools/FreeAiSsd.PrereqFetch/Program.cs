using System.Net.Http;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;

// Phase-A hardening: CI-only helper that shares the PrereqResolver with PrepApp
// so the two never drift. Invoked from .github/workflows/build.yml via:
//   dotnet run --project tools/FreeAiSsd.PrereqFetch -- --kind <windows|mac-ollama> --out <dir>
//
// Fails closed on any resolve / download / hash-verify error. Writes a manifest
// file alongside the downloaded binaries that captures Url, vendor-published
// hash (when published), observed SHA-256, and the trust model used.

if (args.Length == 0) { PrintUsageAndExit(); }

string? kind = null;
string? outDir = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--kind":
            if (++i >= args.Length) PrintUsageAndExit();
            kind = args[i];
            break;
        case "--out":
            if (++i >= args.Length) PrintUsageAndExit();
            outDir = args[i];
            break;
        case "-h":
        case "--help":
            PrintUsageAndExit(0);
            break;
        default:
            Console.Error.WriteLine($"Unknown arg: {args[i]}");
            PrintUsageAndExit();
            break;
    }
}

if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(outDir))
{
    PrintUsageAndExit();
}

Directory.CreateDirectory(outDir!);

using var http = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10),
};
// GitHub API requires a User-Agent. Microsoft doesn't, but sending one is harmless.
http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeAiSsd-PrereqFetch/1.0 (+https://github.com/kninetimmy/free-ai-ssd)");
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

void Log(string msg) => Console.WriteLine($"[prereq-fetch] {msg}");

try
{
    switch (kind)
    {
        case "windows":
            await FetchWindowsAsync(http, outDir!, Log);
            break;
        case "mac-ollama":
            await FetchMacOllamaAsync(http, outDir!, Log);
            break;
        default:
            Console.Error.WriteLine($"Unknown --kind: {kind}");
            PrintUsageAndExit();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[prereq-fetch] FAILED: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(2);
}

return;

static void PrintUsageAndExit(int code = 1)
{
    Console.Error.WriteLine("Usage: FreeAiSsd.PrereqFetch --kind <windows|mac-ollama> --out <dir>");
    Environment.Exit(code);
}

static async Task FetchWindowsAsync(HttpClient http, string outDir, Action<string> log)
{
    log("Resolving Windows prerequisites (latest stable)...");

    var vc = PrereqResolver.ResolveVcRedistX64();
    log($"VC++ x64: version={vc.Version} url={vc.Url} trust={vc.TrustNote}");
    var vcPath = Path.Combine(outDir, "vc_redist.x64.exe");
    var vcSha = await PrereqResolver.DownloadAndVerifyAsync(http, vc, vcPath, log);

    var net = await PrereqResolver.ResolveDotnet8DesktopX64Async(http);
    log($".NET Desktop x64: version={net.Version} url={net.Url} trust={net.TrustNote}");
    var netPath = Path.Combine(outDir, "windowsdesktop-runtime-8-win-x64.exe");
    var netSha = await PrereqResolver.DownloadAndVerifyAsync(http, net, netPath, log);

    var manifest = new
    {
        prerequisites = new object[]
        {
            new
            {
                id = PrereqCatalog.VcRedistX64Id,
                displayName = "Microsoft Visual C++ Redistributable (x64)",
                filename = "vc_redist.x64.exe",
                version = vc.Version,
                sourceUrl = vc.Url,
                verifiedAtUtc = DateTime.UtcNow.ToString("o"),
                vendorHash = vc.Hash,
                vendorHashAlgorithm = vc.Hash is null ? null : vc.HashAlgorithm,
                sha256 = vcSha,
                sizeBytes = new FileInfo(vcPath).Length,
                silentArgs = "/install /quiet /norestart",
                requiresAdmin = true,
                isOptional = false,
                trustNote = vc.TrustNote,
            },
            new
            {
                id = PrereqCatalog.DotnetDesktop8X64Id,
                displayName = ".NET 8 Windows Desktop Runtime (x64)",
                filename = "windowsdesktop-runtime-8-win-x64.exe",
                version = net.Version,
                sourceUrl = net.Url,
                verifiedAtUtc = DateTime.UtcNow.ToString("o"),
                vendorHash = net.Hash,
                vendorHashAlgorithm = net.Hash is null ? null : net.HashAlgorithm,
                sha256 = netSha,
                sizeBytes = new FileInfo(netPath).Length,
                silentArgs = "/install /quiet /norestart",
                requiresAdmin = true,
                isOptional = true,
                trustNote = net.TrustNote,
            },
        },
    };

    var manifestPath = Path.Combine(outDir, PrereqCatalog.ManifestFileName);
    await File.WriteAllTextAsync(manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    log($"Wrote {manifestPath}");
}

static async Task FetchMacOllamaAsync(HttpClient http, string outDir, Action<string> log)
{
    // MAC38: resolve the Mac payload dynamically from the upstream release's
    // sha256sum.txt. PrepApp's staging path reads the bundled manifest below
    // and uses its hash + URL — there is no longer a static pin in
    // OllamaPackageTrustPolicy. Trust anchor is HTTPS to github.com plus the
    // vendor-published hash, identical to the .NET resolver path.
    var resolution = await PrereqResolver.ResolveLatestOllamaMacAsync(http);

    log($"Ollama (Mac): version={resolution.Version} url={resolution.Url} trust={resolution.TrustNote}");

    // Downstream staging code expects the on-disk filename to be lowercase
    // "ollama-darwin.zip" (see ArtifactStagingService + MacToolCatalog).
    var archivePath = Path.Combine(outDir, "ollama-darwin.zip");
    var observedSha = await PrereqResolver.DownloadAndVerifyAsync(http, resolution, archivePath, log);

    var manifest = new
    {
        id = MacToolCatalog.Ollama.Id,
        version = resolution.Version,
        sourceUrl = resolution.Url,
        archive = MacToolCatalog.Ollama.ArchiveFileName,
        verifiedAtUtc = DateTime.UtcNow.ToString("o"),
        vendorHash = resolution.Hash,
        vendorHashAlgorithm = resolution.HashAlgorithm,
        sha256 = observedSha,
        sizeBytes = new FileInfo(archivePath).Length,
        trustNote = resolution.TrustNote,
    };

    var manifestPath = Path.Combine(outDir, MacToolCatalog.ManifestFileName);
    await File.WriteAllTextAsync(manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    log($"Wrote {manifestPath}");
}
