using System.Security.Claims;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;

namespace FlowOps.Web.Api;

/// <summary>
/// ADR-0037: a small, read-only JSON API over the same Application services the Razor Pages already
/// call. Nothing here decides anything — every visibility, role and tenant rule is the existing
/// service's own (<see cref="CurrentUser"/> in, authorized result out), so this surface can never be
/// more permissive than the pages are. Authenticated by the same login cookie as the rest of the
/// app (ADR-0008); an unauthenticated call is a 401, not a redirect to the login page.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapFlowOpsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization()
            .WithTags("FlowOps API v1 (read-only)");

        api.MapGet("/tickets", ListTicketsAsync)
            .WithName("ListTickets")
            .WithSummary("The work queue: tickets the caller may see, one page at a time.")
            .Produces<PagedResult<TicketListItem>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/tickets/{id:int}", GetTicketAsync)
            .WithName("GetTicket")
            .WithSummary("One ticket. 404 both when it does not exist and when the caller may not see it.")
            .Produces<TicketDetail>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/attention", ListAtRiskAsync)
            .WithName("ListAtRisk")
            .WithSummary("Tickets that need attention, most urgent first, each with the signals that put it there.")
            .Produces<PagedResult<AttentionListItem>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/workload", GetWorkloadAsync)
            .WithName("GetTeamWorkload")
            .WithSummary("Where open work is concentrated: a summary plus one row per team the caller may see.")
            .Produces<WorkloadResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    // 403, not the cookie handler's Forbid(): an authenticated caller with no usable organization
    // membership (or a deactivated account) has nothing this API can show them.
    private static IResult NoAccess() => Results.StatusCode(StatusCodes.Status403Forbidden);

    private static async Task<IResult> ListTicketsAsync(
        ClaimsPrincipal principal,
        CurrentUserAccessor currentUserAccessor,
        TicketQueryService tickets,
        CancellationToken cancellationToken,
        int page = 1,
        string? search = null,
        Status? status = null,
        Priority? priority = null,
        bool assignedToMe = false,
        bool includeFinished = false)
    {
        var user = await currentUserAccessor.GetCurrentUserAsync(principal, cancellationToken);
        if (user is null)
        {
            return NoAccess();
        }

        // Same rule the queue page applies: finished work is hidden unless asked for, or unless the
        // caller filters directly on a finished status.
        var includesFinished = includeFinished || status is Status.Resolved or Status.Closed;

        var result = await tickets.GetQueueAsync(
            user, page, TicketQueueFilter.None, search, QueueDateField.Created, null, includesFinished,
            status, priority, assignedToMe, null, null, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetTicketAsync(
        int id,
        ClaimsPrincipal principal,
        CurrentUserAccessor currentUserAccessor,
        TicketQueryService tickets,
        CancellationToken cancellationToken)
    {
        var user = await currentUserAccessor.GetCurrentUserAsync(principal, cancellationToken);
        if (user is null)
        {
            return NoAccess();
        }

        var ticket = await tickets.GetDetailAsync(id, user, cancellationToken);
        return ticket is null ? Results.NotFound() : Results.Ok(ticket);
    }

    private static async Task<IResult> ListAtRiskAsync(
        ClaimsPrincipal principal,
        CurrentUserAccessor currentUserAccessor,
        AttentionQueryService attention,
        CancellationToken cancellationToken,
        int page = 1,
        string? search = null)
    {
        var user = await currentUserAccessor.GetCurrentUserAsync(principal, cancellationToken);
        if (user is null)
        {
            return NoAccess();
        }

        return Results.Ok(await attention.GetAtRiskAsync(user, page, search, null, cancellationToken));
    }

    private static async Task<IResult> GetWorkloadAsync(
        ClaimsPrincipal principal,
        CurrentUserAccessor currentUserAccessor,
        AnalyticsQueryService analytics,
        AttentionQueryService attention,
        CancellationToken cancellationToken,
        WorkType? workType = null)
    {
        var user = await currentUserAccessor.GetCurrentUserAsync(principal, cancellationToken);
        if (user is null)
        {
            return NoAccess();
        }

        // Mirrors the Team Workload page exactly (ADR-0036): a present-tense snapshot whose only
        // honoured filter is work type, with the at-risk counts taken from the Attention policy —
        // never recomputed here. An Agent sees their own workload only, so no per-team table.
        var filter = new DashboardFilter(AnalyticsQueryService.ReportingWindowDays, null, workType);
        var summaryCounts = await analytics.GetTeamWorkloadSummaryCountsAsync(user, filter, cancellationToken);
        var (atRiskTotal, atRiskByTeam) = await attention.GetAtRiskSummaryAsync(user, cancellationToken);
        var summary = summaryCounts with { AtRiskCount = atRiskTotal };

        if (user.Role == UserRole.Agent)
        {
            return Results.Ok(new WorkloadResponse(summary, OwnWorkloadOnly: true, []));
        }

        var teamRows = await analytics.GetTeamWorkloadTableAsync(user, filter, cancellationToken);
        var teams = teamRows
            .Select(row => row with { AtRiskCount = atRiskByTeam.GetValueOrDefault(row.TeamId) })
            .ToList();

        return Results.Ok(new WorkloadResponse(summary, OwnWorkloadOnly: false, teams));
    }
}

/// <summary>The Team Workload page's data: counts for everything the caller may see, and (for
/// anyone but an Agent) one row per team.</summary>
public sealed record WorkloadResponse(
    TeamWorkloadSummary Summary,
    bool OwnWorkloadOnly,
    IReadOnlyList<TeamWorkloadRow> Teams);
