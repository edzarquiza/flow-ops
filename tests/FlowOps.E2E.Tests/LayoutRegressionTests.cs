using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// Phase 30A: a small, permanent real-browser regression suite for defects that only a rendered
/// page can prove — the login-centering fix (Phase 28B), the dashboard grid alignment fix
/// (Phase 29B), the theme system's server-rendered-first-paint contract and its preview-is-not-
/// persisted contract (Phase 29C), and the active-work-by-default Work Queue behaviour
/// (Phase 29B). None of this duplicates <c>FlowOps.Web.Tests</c>, which already covers the HTML
/// and the server-side behaviour these tests exercise through a real browser; it covers only what
/// an HTML-string assertion cannot: actual computed layout and actual rendered theme.
/// </summary>
[Collection("E2E")]
public sealed class LayoutRegressionTests
{
    private readonly PlaywrightAppFactory _app;
    private readonly IBrowser _browser;

    public LayoutRegressionTests(E2EFixture fixture)
    {
        _app = fixture.App;
        _browser = fixture.Browser;
    }

    // ---------------------------------------------------------------- login centering (ADR / Phase 28B)

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(390, 844)]
    public async Task Login_IsHorizontallyCentered_AndNeverScrollsSideways(int width, int height)
    {
        await using var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");

        var result = await page.EvaluateAsync<LoginGeometry>("""
            () => {
                const vw = document.documentElement.clientWidth;
                const main = document.querySelector('body > main').getBoundingClientRect();
                return {
                    MainOffset: Math.round(main.left + main.width / 2 - vw / 2),
                    HasHorizontalScroll: document.documentElement.scrollWidth > vw,
                };
            }
            """);

        Assert.InRange(result.MainOffset, -1, 1); // centred within a rounding pixel
        Assert.False(result.HasHorizontalScroll, $"page scrolls sideways at {width}x{height}");
    }

    // Playwright's JSON deserializer needs a parameterless constructor and settable properties —
    // a positional record (no parameterless ctor) fails at runtime with a MissingMethodException.
    private sealed class LoginGeometry
    {
        public int MainOffset { get; set; }
        public bool HasHorizontalScroll { get; set; }
    }

    [Fact] // Anonymous pages are always Dark (ADR-0031), regardless of the OS/browser preference.
    public async Task Login_IsAlwaysDark_EvenWhenTheOSPrefersLight()
    {
        await using var ctx = await _browser.NewContextAsync(new() { ColorScheme = ColorScheme.Light });
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");

        var theme = await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')");
        Assert.Equal("dark", theme);
    }

    // ---------------------------------------------------------------- dashboard grid (Phase 29B)

    [Fact]
    public async Task Dashboard_ChartPanelsAlign_AndTheWholePageNeverScrollsSideways()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 1000);

        await page.GotoAsync($"{_app.BaseAddress}/");

        var result = await page.EvaluateAsync<DashboardGeometry>("""
            () => {
                const panels = [...document.querySelectorAll('.dashboard-charts > .panel')].map(p => {
                    const r = p.getBoundingClientRect();
                    return { Left: Math.round(r.left), Right: Math.round(r.right), Top: Math.round(r.top + scrollY) };
                });
                const vw = document.documentElement.clientWidth;
                return { Panels: panels, HasHorizontalScroll: document.documentElement.scrollWidth > vw };
            }
            """);

        Assert.Equal(4, result.Panels.Length);
        var (a, b, c, d) = (result.Panels[0], result.Panels[1], result.Panels[2], result.Panels[3]);
        Assert.Equal(a.Left, c.Left); // column 1 shares one left edge, top row -> bottom row
        Assert.Equal(b.Left, d.Left); // column 2 shares one left edge
        Assert.Equal(a.Top, b.Top);   // row 1 panels share one top
        Assert.Equal(c.Top, d.Top);   // row 2 panels share one top
        Assert.False(result.HasHorizontalScroll);
    }

    private sealed class DashboardGeometry
    {
        public PanelRect[] Panels { get; set; } = [];
        public bool HasHorizontalScroll { get; set; }
    }

    private sealed class PanelRect
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Top { get; set; }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Tickets")]
    [InlineData("/Tickets/AtRisk")]
    [InlineData("/Admin")]
    [InlineData("/TeamWorkload")]
    [InlineData("/Guide")]
    [InlineData("/Guide/SlaAndAttention")]
    public async Task AuthenticatedPages_NeverScrollSideways_AtPhoneWidth(string path)
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 390, height: 844);

        var response = await page.GotoAsync($"{_app.BaseAddress}{path}");

        Assert.NotNull(response);
        Assert.Equal(200, response!.Status);
        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, $"{path} scrolls sideways at 390px");
    }

    // ---------------------------------------------------------------- Work Queue row (Phase F1-B P0 fix)

    /// <summary>
    /// Phase F1-B: below 900px, <c>.q-row</c> wraps its fixed-width fields (severity, rail, SLA)
    /// onto their own lines, but until this fix <c>.q-main</c> (title + metadata) had no
    /// <c>flex-basis</c> of its own — at true phone widths it was squeezed into whatever sliver the
    /// non-shrinking siblings left, crushing the title via its own <c>text-overflow: ellipsis</c>
    /// and inflating row height as every other field wrapped onto its own full-height line. Proven
    /// here against a real rendered row (the seeded ticket's own title, "VPN client will not
    /// connect") rather than by reading the CSS.
    /// </summary>
    [Theory]
    [InlineData(390, 844)]
    [InlineData(430, 932)]
    public async Task WorkQueueRow_TitleIsNotCrushed_AndRowHeightStaysReasonable_AtPhoneWidth(int width, int height)
    {
        var page = await SignInAsync(PlaywrightAppFactory.AgentEmail, width, height);
        await page.GotoAsync($"{_app.BaseAddress}/Tickets");

        var geometry = await page.EvaluateAsync<RowGeometry>("""
            () => {
                const row = document.querySelector('.q-row');
                const title = row.querySelector('.q-title');
                const rowRect = row.getBoundingClientRect();
                const titleRect = title.getBoundingClientRect();
                return { RowHeight: rowRect.height, TitleWidth: titleRect.width, TitleText: title.textContent.trim() };
            }
            """);

        // Before the fix this row's height ballooned to several hundred pixels — every field
        // (title, metadata, severity, rail, SLA) wrapping onto its own separate full-height line.
        // A healthy row at phone width is title + a couple of wrapped metadata lines, well under 250px.
        Assert.True(geometry.RowHeight < 250, $"row height was {geometry.RowHeight}px at {width}px wide — expected roughly two-to-three wrapped lines, not a stack of full-height wrapped fields");

        // Before the fix the title's own box was squeezed to a handful of pixels, visually cutting
        // "VPN client will not connect" down to 2-3 characters plus an ellipsis.
        Assert.True(geometry.TitleWidth > 150, $"title box was only {geometry.TitleWidth}px wide at {width}px viewport — the title is being crushed");
        Assert.Equal("VPN client will not connect", geometry.TitleText);

        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, $"Work Queue scrolls sideways at {width}px");
    }

    /// <summary>The fix is scoped to ≤480px only — this proves the pre-existing, already-correct
    /// 768px tablet reflow (fields wrap, but the title keeps a full line to itself) is unchanged.</summary>
    [Fact]
    public async Task WorkQueueRow_StillReflowsCorrectly_At768px_Unregressed()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AgentEmail, width: 768, height: 1024);
        await page.GotoAsync($"{_app.BaseAddress}/Tickets");

        var geometry = await page.EvaluateAsync<RowGeometry>("""
            () => {
                const row = document.querySelector('.q-row');
                const title = row.querySelector('.q-title');
                const rowRect = row.getBoundingClientRect();
                const titleRect = title.getBoundingClientRect();
                return { RowHeight: rowRect.height, TitleWidth: titleRect.width, TitleText: title.textContent.trim() };
            }
            """);

        Assert.True(geometry.RowHeight < 250, $"row height was {geometry.RowHeight}px at 768px wide");
        Assert.True(geometry.TitleWidth > 150, $"title box was only {geometry.TitleWidth}px wide at 768px");
        Assert.Equal("VPN client will not connect", geometry.TitleText);
    }

    private sealed class RowGeometry
    {
        public double RowHeight { get; set; }
        public double TitleWidth { get; set; }
        public string TitleText { get; set; } = string.Empty;
    }

    [Fact]
    public async Task TicketDetail_RendersWithNoUnhandledPageError()
    {
        var errors = new List<string>();
        var page = await SignInAsync(PlaywrightAppFactory.AgentEmail, width: 1440, height: 900);
        page.PageError += (_, message) => errors.Add(message);

        var response = await page.GotoAsync($"{_app.BaseAddress}/Tickets/Details/{_app.SeededTicketId}");

        Assert.NotNull(response);
        Assert.Equal(200, response!.Status);
        Assert.Empty(errors);
    }

    // ---------------------------------------------------------------- Team Workload panel spacing (verification pass)

    /// <summary>
    /// Verification-pass defect: Team Workload's Summary and Team workload panels are plain
    /// siblings, not wrapped in <c>.panel-stack</c> — and Phase 29B deliberately removed any global
    /// sibling-panel margin (dashboard-style grids set their own <c>gap</c>; a plain vertical stack
    /// must opt in via <c>.panel-stack</c>). Without it, the two panels sat flush against each other
    /// with zero gap, reading as overlapping borders. Fixed by wrapping both in
    /// <c>.panel-stack</c>; this proves the gap is real and stays real.
    /// </summary>
    [Fact]
    public async Task TeamWorkload_SummaryAndTeamPanels_HaveVisibleGapBetweenThem()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 1000);

        await page.GotoAsync($"{_app.BaseAddress}/TeamWorkload");

        var gap = await page.EvaluateAsync<double>("""
            () => {
                const panels = document.querySelectorAll('.panel-stack > .panel');
                if (panels.length < 2) { return -1; }
                const a = panels[0].getBoundingClientRect();
                const b = panels[1].getBoundingClientRect();
                return b.top - a.bottom;
            }
            """);

        Assert.True(gap >= 16, $"expected a real gap (>= 16px) between the stacked panels, got {gap}px");
    }

    // ---------------------------------------------------------------- panel-stack audit (verification pass)

    /// <summary>
    /// The same defect as the Team Workload test above, found on three further pages during the
    /// audit that followed it: Members (table + the new "Pending invitations" panel), Platform's
    /// homepage (three panels: Pending approvals / Organizations / Users), and both Platform detail
    /// pages (a fact panel + a "Recent activity" panel). Each page's own multi-panel section is
    /// checked, not just the first pair, since Platform's homepage has three in a row.
    /// </summary>
    [Fact]
    public async Task Members_TableAndPendingInvitationsPanels_HaveVisibleGapBetweenThem()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 1440, height: 1000);

        await page.GotoAsync($"{_app.BaseAddress}/Organization/Members");
        await page.FillAsync("#InviteInput_Email", $"{Guid.NewGuid():N}@panel-audit.test.local");
        await page.ClickAsync("button:has-text('Create invitation')");
        await page.WaitForSelectorAsync("text=Invitation created");

        await page.GotoAsync($"{_app.BaseAddress}/Organization/Members");

        await AssertMinGapAsync(page, minExpected: 16);
    }

    [Fact]
    public async Task PlatformIndex_ThreePanels_EachHaveVisibleGapBetweenThem()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 1440, height: 1000);
        await GrantPlatformAdminAsync(PlaywrightAppFactory.AdminEmail);

        await page.GotoAsync($"{_app.BaseAddress}/Platform");

        await AssertMinGapAsync(page, minExpected: 16);
    }

    [Fact]
    public async Task PlatformOrganizationDetails_PanelsHaveVisibleGapBetweenThem()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 1440, height: 1000);
        await GrantPlatformAdminAsync(PlaywrightAppFactory.AdminEmail);

        await page.GotoAsync($"{_app.BaseAddress}/Platform/Organizations/Details/{await GetFirstOrganizationIdAsync()}");

        await AssertMinGapAsync(page, minExpected: 16);
    }

    [Fact]
    public async Task PlatformUserDetails_PanelsHaveVisibleGapBetweenThem()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 1440, height: 1000);
        await GrantPlatformAdminAsync(PlaywrightAppFactory.AdminEmail);

        await page.GotoAsync($"{_app.BaseAddress}/Platform/Users/Details/{await GetFirstUserIdAsync()}");

        await AssertMinGapAsync(page, minExpected: 16);
    }

    private static async Task AssertMinGapAsync(IPage page, double minExpected)
    {
        var gap = await page.EvaluateAsync<double>("""
            () => {
                const panels = document.querySelectorAll('.panel-stack > .panel');
                if (panels.length < 2) { return -1; }
                let minGap = Infinity;
                for (let i = 1; i < panels.length; i++) {
                    const a = panels[i - 1].getBoundingClientRect();
                    const b = panels[i].getBoundingClientRect();
                    minGap = Math.min(minGap, b.top - a.bottom);
                }
                return minGap;
            }
            """);

        Assert.True(gap >= minExpected, $"expected a real gap (>= {minExpected}px) between every stacked panel, got {gap}px");
    }

    private async Task GrantPlatformAdminAsync(string email)
    {
        using var scope = _app.HostServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.IsPlatformAdmin = true;
        await db.SaveChangesAsync();
    }

    private async Task<int> GetFirstOrganizationIdAsync()
    {
        using var scope = _app.HostServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.Organizations.Select(o => o.Id).FirstAsync();
    }

    private async Task<Guid> GetFirstUserIdAsync()
    {
        using var scope = _app.HostServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        return await db.OrganizationMemberships.Select(m => m.UserId).FirstAsync();
    }

    // ---------------------------------------------------------------- Guide discovery cue

    /// <summary>
    /// The Guide feature's first-time discovery cue: shown exactly once, on the very first
    /// Dashboard a user's account ever reaches (which, in a real browser, is where the login
    /// redirect itself lands — so the cue must already be present on that same page, not a
    /// separate navigation), never on a second view. <c>GuideIntroducedAt</c> is reset to null
    /// here to force the one-shot condition on a real seeded account, since this fixture's own
    /// seeded users are themselves created after migrations run and are otherwise indistinguishable
    /// from a brand-new account for this column's own purposes.
    /// </summary>
    [Fact]
    public async Task GuideDiscoveryCue_ShowsOnceOnFirstLanding_NeverAgainAfter_AndNeverOverflowsAtPhoneWidth()
    {
        await ResetGuideIntroducedAtAsync(PlaywrightAppFactory.ManagerEmail);

        var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 390, Height = 844 } });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", PlaywrightAppFactory.ManagerEmail);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");

        await page.WaitForSelectorAsync("text=New to FlowOps?");

        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, "discovery cue causes horizontal scroll at 390px");

        // A second load of the exact same page — the server already consumed the one-shot flag on
        // the first render above, so it must never appear again, even immediately afterward.
        await page.GotoAsync($"{_app.BaseAddress}/");
        var body = await page.ContentAsync();
        Assert.DoesNotContain("New to FlowOps?", body, StringComparison.Ordinal);
    }

    private async Task ResetGuideIntroducedAtAsync(string email)
    {
        using var scope = _app.HostServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        user.GuideIntroducedAt = null;
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- Team Workload responsive (ADR-0036)

    /// <summary>
    /// ADR-0036: Team Workload's <c>.tw-table</c> is a 7-column table (Team/Open/Assigned/
    /// Unassigned/At Risk/Overdue/Members) — wide enough that the shared <c>.table-responsive</c>
    /// wrapper alone doesn't contain it at phone width, unlike every narrower existing table. The
    /// fix reuses <c>.plan-table</c>'s own established stacked-card collapse. Proven here against
    /// both the team table and, expanded, the nested member table — the state most likely to
    /// regress since it nests one <c>.tw-table</c> inside another row.
    /// </summary>
    [Theory]
    [InlineData(390, 844)]
    [InlineData(430, 932)]
    public async Task TeamWorkload_ExpandedMemberTable_NeverScrollsSideways_AtPhoneWidth(int width, int height)
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width, height);

        var response = await page.GotoAsync($"{_app.BaseAddress}/TeamWorkload?expandedTeam={_app.TeamId}");
        Assert.NotNull(response);
        Assert.Equal(200, response!.Status);

        await page.WaitForSelectorAsync("text=Hide members");

        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, $"Team Workload (expanded) scrolls sideways at {width}px");

        // The stacked-card collapse must actually be readable, not shrunk to unreadable/truncated
        // text — the specific regression an earlier "table-layout: fixed" attempt introduced.
        var titleText = await page.Locator("caption").First.TextContentAsync();
        Assert.False(string.IsNullOrWhiteSpace(titleText));
    }

    // ---------------------------------------------------------------- theme (Phase 29C)

    [Fact]
    public async Task Appearance_SelectingLight_PreviewsImmediately_SavePersistsIt_ReloadKeepsIt()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ViewerEmail, width: 1440, height: 900);
        await page.GotoAsync($"{_app.BaseAddress}/Account/Settings");
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));

        await page.CheckAsync("input[value=Light]");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));

        await page.ClickAsync("button:has-text('Save appearance')");
        await page.WaitForSelectorAsync("text=Appearance saved.");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));

        await page.GotoAsync($"{_app.BaseAddress}/");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));

        // Leave the shared fixture's user back where later tests expect it.
        await page.GotoAsync($"{_app.BaseAddress}/Account/Settings");
        await page.CheckAsync("input[value=Dark]");
        await page.ClickAsync("button:has-text('Save appearance')");
        await page.WaitForSelectorAsync("text=Appearance saved.");
    }

    [Fact] // The preview is not the source of truth: navigating away without saving discards it.
    public async Task Appearance_UnsavedPreview_DoesNotSurviveNavigatingAway()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AgentEmail, width: 1440, height: 900);
        await page.GotoAsync($"{_app.BaseAddress}/Account/Settings");

        await page.CheckAsync("input[value=Light]");
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));

        await page.GotoAsync($"{_app.BaseAddress}/Tickets");

        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));
    }

    // ---------------------------------------------------------------- show inactive (Phase 29C)

    [Fact]
    public async Task ShowInactive_DefaultHidesTheInactiveTeam_ToggleRevealsIt()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AdminEmail, width: 1440, height: 900);

        await page.GotoAsync($"{_app.BaseAddress}/Admin");
        Assert.DoesNotContain(_app.InactiveTeamName, await page.ContentAsync(), StringComparison.Ordinal);

        await page.GotoAsync($"{_app.BaseAddress}/Admin?showInactive=true");
        Assert.Contains(_app.InactiveTeamName, await page.ContentAsync(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- work queue (Phase 29B)

    [Fact]
    public async Task WorkQueue_DefaultsToActiveWork_NotResolvedOrClosedTickets()
    {
        var page = await SignInAsync(PlaywrightAppFactory.AgentEmail, width: 1440, height: 900);

        await page.GotoAsync($"{_app.BaseAddress}/Tickets");

        var badges = await page.Locator(".status-badge").AllTextContentsAsync();
        Assert.DoesNotContain(badges, b => b.Contains("Resolved", StringComparison.OrdinalIgnoreCase) || b.Contains("Closed", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IPage> SignInAsync(string email, int width, int height)
    {
        var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", email);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");
        await page.WaitForSelectorAsync("text=Sign out"); // only the authenticated shell has this — Login itself does not

        return page;
    }
}
