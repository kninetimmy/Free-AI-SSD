using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// xUnit [Fact] that auto-skips when no Tesseract binary can be located on this
/// host. Used by the real-command OCR integration test so CI agents without
/// Tesseract installed report it as skipped, not failed — while a dev box with
/// the UB-Mannheim install (or tesseract on PATH) actually exercises the
/// shell-out. Mirrors the OllamaIntegrationFactAttribute / AdminFactAttribute
/// pattern. The skip-or-run decision is made at discovery time via the same
/// locator the test body uses (<see cref="TesseractTestBinary.Find"/>).
/// </summary>
public sealed class TesseractIntegrationFactAttribute : FactAttribute
{
    public TesseractIntegrationFactAttribute()
    {
        if (TesseractTestBinary.Find() is null)
        {
            Skip = "Requires a Tesseract binary on PATH or under a standard Tesseract-OCR install dir.";
        }
    }
}

/// <summary>
/// Locates a Tesseract binary for the integration test: standard Windows
/// install dirs first, then PATH. Returns null when none is found, which the
/// integration attribute turns into a skip.
/// </summary>
internal static class TesseractTestBinary
{
    public static string? Find()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var exeName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // A malformed PATH segment must not abort the search.
            }
        }
        return null;
    }

    /// <summary>Returns the adjacent tessdata dir for a binary, or null to let Tesseract self-locate.</summary>
    public static string? TessdataDirFor(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null) return null;
        var tessdata = Path.Combine(dir, "tessdata");
        return Directory.Exists(tessdata) ? tessdata : null;
    }
}
