using System.Text.RegularExpressions;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.PrepApp.Services;

/// <summary>
/// C27 Stage 4: collapse a Hugging Face repo's <c>siblings[]</c> array
/// into one entry per distinct GGUF quant label. Multi-part series
/// (<c>...-Q4_K_M-00001-of-00003.gguf</c>) sum to a single entry whose
/// size + part count surface on the picker child row.
///
/// Lives in <c>prep-core</c> so the WPF view-host and the
/// <c>mac-prep-host</c> sidecar both consume one implementation —
/// keeps the per-quant projection byte-identical across OSes (the
/// same lesson C24 cashed in for the catalog projection helper).
/// </summary>
public static class HuggingFaceQuantProjector
{
    /// <summary>
    /// Project siblings into per-quant <see cref="StarterCatalogEntry"/>
    /// rows. <see cref="StarterCatalogEntry.Tag"/> uses the
    /// <c>hf.co/owner/repo:quant</c> form Ollama natively understands.
    /// </summary>
    public static IReadOnlyList<StarterCatalogEntry> Project(
        string repoId, IReadOnlyList<HuggingFaceSiblingFile> siblings)
    {
        var byQuant = AggregateByQuant(siblings);
        return byQuant
            .OrderBy(kv => QuantSortOrder(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new StarterCatalogEntry(
                Tag: $"hf.co/{repoId}:{kv.Key}",
                SizeTier: "Custom",
                BestAt: kv.Value.parts > 1
                    ? $"{kv.Key} ({kv.Value.parts}-part split)"
                    : kv.Key,
                PullCount: null,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: null,
                LastUpdated: null,
                Source: ModelSource.HuggingFace,
                IsExpandable: false,
                ParentRepoId: repoId,
                QuantLabel: kv.Key,
                QuantSizeBytes: kv.Value.size > 0 ? kv.Value.size : null))
            .ToList();
    }

    /// <summary>
    /// Project siblings into the JSON-friendly anonymous shape the Mac
    /// sidecar's <c>hf-siblings</c> arm emits. Same ordering + size
    /// summing as <see cref="Project"/>; SwiftUI host decodes via
    /// <c>StarterModelDisplayEntry</c>.
    /// </summary>
    public static object[] ProjectAsWirePayload(
        string repoId, IReadOnlyList<HuggingFaceSiblingFile> siblings)
    {
        var byQuant = AggregateByQuant(siblings);
        return byQuant
            .OrderBy(kv => QuantSortOrder(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (object)new
            {
                tag = $"hf.co/{repoId}:{kv.Key}",
                quantLabel = kv.Key,
                quantSizeBytes = kv.Value.size > 0 ? (long?)kv.Value.size : null,
                partCount = kv.Value.parts,
                parentRepoId = repoId,
            })
            .ToArray();
    }

    /// <summary>
    /// Pull a quant label out of a GGUF filename. Returns null when no
    /// recognizable label is present — those siblings drop out of the
    /// projection (typically incomplete uploads or non-quant variants).
    /// </summary>
    public static string? ExtractQuantLabel(string filename)
    {
        var m = QuantLabelRegex.Match(filename);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Order quants from smallest to largest so the child rows render
    /// bottom-of-the-curve first. Coarse ordinal:
    /// IQ &lt; Q2 &lt; Q3 &lt; Q4 &lt; Q5 &lt; Q6 &lt; Q8 &lt;
    /// F16/BF16 &lt; F32. Inexact (Q4_K_M vs Q4_0 ties on the digit)
    /// but stable enough that "smallest quant first" is the visual default.
    /// </summary>
    public static string QuantSortOrder(string label)
    {
        if (label.StartsWith("IQ", StringComparison.OrdinalIgnoreCase)) return "0_" + label;
        if (label.StartsWith("Q", StringComparison.OrdinalIgnoreCase) && label.Length >= 2 && char.IsDigit(label[1]))
            return "1_" + label[1] + "_" + label;
        if (label.StartsWith("BF16", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("F16", StringComparison.OrdinalIgnoreCase)) return "2_" + label;
        if (label.StartsWith("F32", StringComparison.OrdinalIgnoreCase)) return "3_" + label;
        return "9_" + label;
    }

    private static Dictionary<string, (long size, int parts)> AggregateByQuant(
        IReadOnlyList<HuggingFaceSiblingFile> siblings)
    {
        var byQuant = new Dictionary<string, (long size, int parts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sib in siblings)
        {
            if (sib.Filename is null) continue;
            if (!sib.Filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            var label = ExtractQuantLabel(sib.Filename);
            if (label is null) continue;
            var size = sib.SizeBytes ?? 0L;
            if (byQuant.TryGetValue(label, out var existing))
            {
                byQuant[label] = (existing.size + size, existing.parts + 1);
            }
            else
            {
                byQuant[label] = (size, 1);
            }
        }
        return byQuant;
    }

    private static readonly Regex QuantLabelRegex =
        new(@"(?:[-_\.])(Q[0-9]+_[A-Z0-9_]+|F16|F32|BF16|IQ[0-9]+_[A-Z0-9_]+)(?:[-_\.]|\.gguf)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
