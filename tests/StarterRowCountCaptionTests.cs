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
}
