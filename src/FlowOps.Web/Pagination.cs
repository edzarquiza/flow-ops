using FlowOps.Application.Tickets;
using Microsoft.AspNetCore.Routing;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only pagination for the <c>_Pagination</c> partial — the same pattern as
/// <see cref="WorkflowRail"/> and <see cref="TimelinePresentation"/>. It reads an already-computed
/// <see cref="PagedResult{T}"/> (current page, total pages, total count, page size — all decided by
/// the query service) and decides only how to lay those numbers out and which extra route values
/// (e.g. a Work Queue filter) each page link should carry. It introduces no new query, no new page
/// size, and no new ordering.
/// </summary>
public sealed record PaginationViewModel(
    string Page,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages,
    RouteValueDictionary? RouteValues = null,
    string AriaLabel = "Pagination",
    PaginationPlacement Placement = PaginationPlacement.Bottom)
{
    public static PaginationViewModel For<T>(
        string page,
        PagedResult<T> result,
        RouteValueDictionary? routeValues = null,
        string ariaLabel = "Pagination",
        PaginationPlacement placement = PaginationPlacement.Bottom) =>
        new(page, result.PageNumber, result.PageSize, result.TotalCount, result.TotalPages, routeValues, ariaLabel, placement);
}

/// <summary>Where one <c>_Pagination</c> render sits relative to the table/list it paginates — a
/// table with more than one page renders it both above and below (the page-number links reachable
/// without scrolling past a long page first), but the "Showing X–Y of Z" count only ever appears
/// once, at the bottom, where it always has.</summary>
public enum PaginationPlacement
{
    Top,
    Bottom,
}

/// <summary>
/// The small, predictable page-number-with-ellipsis algorithm CLAUDE.md §16 expects for anything
/// beyond trivial pagination: always show the first and last page; show a window of up to three
/// pages around the current one; collapse anything else into a single ellipsis marker (<c>null</c>)
/// per gap. For seven or fewer pages, every page is shown and no ellipsis appears.
/// </summary>
public static class PaginationDisplay
{
    public static IReadOnlyList<int?> PageNumbers(int currentPage, int totalPages)
    {
        if (totalPages <= 7)
        {
            return Enumerable.Range(1, Math.Max(totalPages, 1)).Select(i => (int?)i).ToList();
        }

        var pages = new List<int?> { 1 };

        var windowStart = Math.Max(2, currentPage - 1);
        var windowEnd = Math.Min(totalPages - 1, currentPage + 1);

        if (windowStart > 2)
        {
            pages.Add(null);
        }

        for (var i = windowStart; i <= windowEnd; i++)
        {
            pages.Add(i);
        }

        if (windowEnd < totalPages - 1)
        {
            pages.Add(null);
        }

        pages.Add(totalPages);
        return pages;
    }
}
