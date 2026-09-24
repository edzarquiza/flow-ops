using System.Security.Claims;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using FlowOps.Web.Pages.Tickets;
using FlowOps.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 12: an end-to-end optimistic-concurrency conflict through the real
/// <see cref="DetailsModel"/> — not merely at the <c>TicketService</c>/EF layer — proving the Web
/// layer's existing catch block (Details.cshtml.cs, ADR-0011) surfaces the "reload and retry"
/// message and that no silent overwrite occurs.
/// </summary>
/// <remarks>
/// The race is made deterministic rather than timing-dependent (the opposite of the Phase 12
/// test-reliability lesson): two separate, DI-resolved <see cref="FlowOpsDbContext"/> scopes both
/// load the same ticket while it is still Assigned; one context commits a real transition first,
/// genuinely bumping the row's <c>xmin</c>. The second scope's <see cref="Ticket"/> instance stays
/// tracked with the now-stale original <c>xmin</c> — EF Core's identity map does not overwrite an
/// already-tracked entity's original values from a later query on the same context — so driving a
/// second, independently-legal transition through that same scope's <see cref="DetailsModel"/>
/// deterministically reproduces a genuine <see cref="DbUpdateConcurrencyException"/> at save time,
/// every run, with no reliance on real overlapping request timing.
/// </remarks>
public sealed class TicketConcurrencyWebTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketConcurrencyWebTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ConcurrentEdit_SecondSaveConflicts_WebLayerShowsReloadMessage_NoSilentOverwrite()
    {
        var ticketId = await _factory.CreateTicketAsync(TestUsers.AgentEmail, "Concurrency conflict scenario");

        using var setupScope = _factory.Services.CreateScope();
        var managerId = await TestReferenceData.UserIdAsync(setupScope.ServiceProvider, TestUsers.ManagerEmail);
        var managerUser = new CurrentUser(managerId, _factory.OrganizationId, UserRole.Manager, new HashSet<int> { _factory.TeamId }, new HashSet<int> { _factory.TeamId });

        // Get the ticket to Assigned — PutOnHold (below) is legal from Assigned or InProgress, but
        // not from the ticket's initial Open status.
        var setupService = setupScope.ServiceProvider.GetRequiredService<TicketService>();
        await setupService.AssignAsync(ticketId, managerId, managerUser);

        // Scope B loads (and tracks) the ticket first, while it is still Assigned.
        using var scopeB = _factory.Services.CreateScope();
        var dbContextB = scopeB.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        await dbContextB.Tickets.AsTracking().SingleAsync(t => t.Id == ticketId);

        // Scope A independently loads the same ticket and genuinely commits first — a real
        // transition, through the real service, that bumps the row's xmin in the database.
        using (var scopeA = _factory.Services.CreateScope())
        {
            var serviceA = scopeA.ServiceProvider.GetRequiredService<TicketService>();
            await serviceA.StartWorkAsync(ticketId, managerUser);
        }

        // Scope B now drives a second, independently-legal transition through the real
        // DetailsModel PageModel — the exact class and catch block a real browser request would
        // hit. Its DbContext still holds the pre-A tracked entity, so this save is the stale one.
        var currentUserAccessorB = scopeB.ServiceProvider.GetRequiredService<CurrentUserAccessor>();
        var ticketQueryServiceB = scopeB.ServiceProvider.GetRequiredService<TicketQueryService>();
        var ticketServiceB = scopeB.ServiceProvider.GetRequiredService<TicketService>();
        var attentionQueryServiceB = scopeB.ServiceProvider.GetRequiredService<AttentionQueryService>();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, managerId.ToString()),
        ]));
        var httpContext = new DefaultHttpContext { RequestServices = scopeB.ServiceProvider, User = principal };
        var pageContext = new PageContext(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()));

        var detailsModel = new DetailsModel(currentUserAccessorB, ticketQueryServiceB, ticketServiceB, attentionQueryServiceB)
        {
            PageContext = pageContext,
            Input = new DetailsModel.WorkflowInput { PendingReason = "Reviewing after a concurrent edit" },
        };

        var result = await detailsModel.OnPostPutOnHoldAsync(ticketId);

        // The Web layer caught DbUpdateConcurrencyException itself (Details.cshtml.cs) rather than
        // letting it propagate to the global exception handler — the page re-renders with a form
        // error, not a 500.
        Assert.IsType<PageResult>(result);
        var errorMessages = detailsModel.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
        Assert.Contains("This ticket changed while you were working on it. Reload the page and try again.", errorMessages);

        // No silent overwrite: the persisted status is A's InProgress, not B's stale PutOnHold.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var persistedStatus = await verifyDb.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => t.Status)
            .SingleAsync();

        Assert.Equal(Status.InProgress, persistedStatus);
    }
}
