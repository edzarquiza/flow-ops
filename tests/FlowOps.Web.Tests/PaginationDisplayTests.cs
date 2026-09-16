using FlowOps.Web;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// The pagination polish pass (whole-application UI refinement): a pure presentation function, so
/// it is tested directly rather than through a full HTTP round trip — no database, no
/// WebApplicationFactory, nothing brittle about markup. These assert the algorithm's actual
/// behavior (which pages appear, where ellipses land, that first/last always appear), not
/// incidental HTML.
/// </summary>
public sealed class PaginationDisplayTests
{
    [Fact]
    public void PageNumbers_SevenOrFewerPages_ShowsEveryPageWithNoEllipsis()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 3, totalPages: 7);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], result);
    }

    [Fact]
    public void PageNumbers_SinglePage_ShowsJustThatPage()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 1, totalPages: 1);

        Assert.Equal([1], result);
    }

    [Fact] // The Work Queue/At-Risk scenario this was written for: 302 tickets, page size 25.
    public void PageNumbers_ManyPages_MiddlePage_ShowsFirstLastAndWindowWithBothEllipses()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 10, totalPages: 13);

        Assert.Equal([1, null, 9, 10, 11, null, 13], result);
    }

    [Fact]
    public void PageNumbers_ManyPages_FirstPage_OmitsLeadingEllipsis()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 1, totalPages: 13);

        Assert.Equal([1, 2, null, 13], result);
    }

    [Fact]
    public void PageNumbers_ManyPages_LastPage_OmitsTrailingEllipsis()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 13, totalPages: 13);

        Assert.Equal([1, null, 12, 13], result);
    }

    [Fact] // First and last page are never collapsed into an ellipsis, whatever the current page.
    public void PageNumbers_AlwaysIncludesFirstAndLastPage()
    {
        var result = PaginationDisplay.PageNumbers(currentPage: 50, totalPages: 100);

        Assert.Equal(1, result[0]);
        Assert.Equal(100, result[^1]);
    }
}
