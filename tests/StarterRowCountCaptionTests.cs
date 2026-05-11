using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Tests;

// M11: pin the picker's "Showing X of Y" caption strings. Wording must
// stay byte-identical to the Swift mirror in
// mac-prep-app/Sources/StarterCatalogTypes.swift so cross-OS smoke tests
// read the same line on both platforms — see the matching test runner
// in mac-prep-app/Tests/PrepAppTests.swift.
public class StarterRowCountCaptionTests
{
    [Fact]
    public void Empty_WhenNoFilterAndNoSearch()
    {
        Assert.Equal(string.Empty,
            StarterRowCountCaption.Format(visible: 399, total: 399,
                showOnlyMostPopular: false, hasSearch: false));
    }

    [Fact]
    public void Empty_WhenTotalIsZero()
    {
        Assert.Equal(string.Empty,
            StarterRowCountCaption.Format(visible: 0, total: 0,
                showOnlyMostPopular: true, hasSearch: false));
    }

    [Fact]
    public void AnnouncesTopNCap_WhenPopularOnly()
    {
        Assert.Equal("Showing top 15 of 399 by pulls.",
            StarterRowCountCaption.Format(visible: 15, total: 399,
                showOnlyMostPopular: true, hasSearch: false));
    }

    [Fact]
    public void NotesSearchFilter_WhenSearchOnly()
    {
        Assert.Equal("Showing 12 of 399 matching search.",
            StarterRowCountCaption.Format(visible: 12, total: 399,
                showOnlyMostPopular: false, hasSearch: true));
    }

    [Fact]
    public void CombinesPopularAndSearch()
    {
        Assert.Equal("Showing top 4 of 399 by pulls (filtered by search).",
            StarterRowCountCaption.Format(visible: 4, total: 399,
                showOnlyMostPopular: true, hasSearch: true));
    }

    // C3 / C4 / C5 — new branches for parameter cap, capabilities, sort

    [Fact]
    public void ParameterCapOnly_EmitsMatchingFilterLine()
    {
        Assert.Equal("Showing 8 of 12 matching filter (≤14B).",
            StarterRowCountCaption.Format(
                visible: 8, total: 12,
                showOnlyMostPopular: false, hasSearch: false,
                maxParametersBillion: 14));
    }

    [Fact]
    public void CapabilitiesAndCap_ComposeAlphabetically()
    {
        // Capabilities listed in the caption are joined alphabetically
        // (case-insensitive) so wording is stable across selection orders.
        Assert.Equal("Showing 3 of 12 matching filter (≤7B, tools+vision).",
            StarterRowCountCaption.Format(
                visible: 3, total: 12,
                showOnlyMostPopular: false, hasSearch: false,
                maxParametersBillion: 7,
                requiredCapabilities: new[] { "vision", "tools" }));
    }

    [Fact]
    public void NewestSort_AppendsSortedSentence()
    {
        Assert.Equal("Sorted by newest.",
            StarterRowCountCaption.Format(
                visible: 12, total: 12,
                showOnlyMostPopular: false, hasSearch: false,
                sortMode: ModelSortMode.Newest));
    }

    [Fact]
    public void AlphabeticalSort_AppendsSortedSentence()
    {
        Assert.Equal("Sorted A–Z.",
            StarterRowCountCaption.Format(
                visible: 12, total: 12,
                showOnlyMostPopular: false, hasSearch: false,
                sortMode: ModelSortMode.Alphabetical));
    }

    [Fact]
    public void PopularSearchFilterAndNewest_Combine()
    {
        Assert.Equal(
            "Showing top 5 of 200 by pulls (filtered by search; ≤14B, tools). Sorted by newest.",
            StarterRowCountCaption.Format(
                visible: 5, total: 200,
                showOnlyMostPopular: true, hasSearch: true,
                maxParametersBillion: 14,
                requiredCapabilities: new[] { "tools" },
                sortMode: ModelSortMode.Newest));
    }

    [Fact]
    public void SubBillionCap_RendersInMegabytes()
    {
        Assert.Equal("Showing 4 of 12 matching filter (≤500M).",
            StarterRowCountCaption.Format(
                visible: 4, total: 12,
                showOnlyMostPopular: false, hasSearch: false,
                maxParametersBillion: 0.5));
    }
}
