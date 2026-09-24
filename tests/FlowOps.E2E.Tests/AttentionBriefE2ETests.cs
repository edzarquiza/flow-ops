using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// Phase 30 (ADR-0035): a genuine-browser check that the attention brief actually renders and its
/// disclosure actually opens — the one thing an HTML-string assertion (<c>FlowOps.Web.Tests</c>'
/// own, more thorough content coverage) cannot prove by itself.
/// </summary>
[Collection("E2E")]
public sealed class AttentionBriefE2ETests
{
    private readonly PlaywrightAppFactory _app;
    private readonly IBrowser _browser;

    public AttentionBriefE2ETests(E2EFixture fixture)
    {
        _app = fixture.App;
        _browser = fixture.Browser;
    }

    [Fact]
    public async Task Details_AttentionBriefPanel_RendersAndNeverScrollsSideways_AtPhoneWidth()
    {
        var ticketId = await CreateAtRiskTicketAsync("E2E core switch stack is down");

        var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 390, Height = 844 } });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", PlaywrightAppFactory.AgentEmail);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");
        await page.WaitForSelectorAsync("text=Sign out");

        var response = await page.GotoAsync($"{_app.BaseAddress}/Tickets/Details/{ticketId}");
        Assert.NotNull(response);
        Assert.Equal(200, response!.Status);

        await page.WaitForSelectorAsync("text=Why this needs attention");
        Assert.Contains("Assign an owner.", await page.ContentAsync(), StringComparison.Ordinal);

        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, "the attention brief panel causes horizontal scroll at phone width");
    }

    [Fact]
    public async Task AtRisk_AttentionDisclosure_OpensOnClick()
    {
        await CreateAtRiskTicketAsync("E2E payroll system is offline");

        var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", PlaywrightAppFactory.AgentEmail);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");
        await page.WaitForSelectorAsync("text=Sign out");

        await page.GotoAsync($"{_app.BaseAddress}/Tickets/AtRisk");

        var summary = page.Locator("summary:has-text('View attention brief')").First;
        await summary.ClickAsync();

        await page.WaitForSelectorAsync("text=Suggested next step");
    }

    private async Task<int> CreateAtRiskTicketAsync(string title)
    {
        using var scope = _app.HostServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var agent = await db.Users.AsNoTracking().SingleAsync(u => u.Email == PlaywrightAppFactory.AgentEmail);
        var organizationId = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == agent.Id)
            .Select(m => m.OrganizationId)
            .SingleAsync();

        var longAgo = TimeProvider.System.GetUtcNow().AddDays(-3);
        var ticketService = new TicketService(db, new BackdatedTimeProvider(longAgo), new NoOpEmailSender(), new FlowOps.Infrastructure.Email.EmailOptions());

        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest(title, "Seeded by a Phase 30 E2E attention brief test.", WorkType.Incident, Priority.Critical, _app.TeamId, _app.CategoryId, null),
            new CurrentUser(agent.Id, organizationId, UserRole.Agent, new HashSet<int> { _app.TeamId }, new HashSet<int>()));

        return ticketId;
    }

    private sealed class BackdatedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoOpEmailSender : FlowOps.Infrastructure.Email.IEmailSender
    {
        public Task<FlowOps.Infrastructure.Email.EmailSendResult> SendAsync(FlowOps.Infrastructure.Email.EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(FlowOps.Infrastructure.Email.EmailSendResult.Success());
    }
}
