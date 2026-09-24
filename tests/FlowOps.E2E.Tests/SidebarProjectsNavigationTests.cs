using Microsoft.Playwright;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// Phase 29D real-browser regression: the sidebar's collapsible "Projects" nav group. Covers what
/// an HTML-string assertion (<c>FlowOps.Web.Tests</c>) cannot — that clicking the toggle genuinely
/// expands/collapses in place without a page navigation, and that the expanded submenu never
/// causes the page to scroll sideways at phone width.
/// </summary>
[Collection("E2E")]
public sealed class SidebarProjectsNavigationTests
{
    private readonly PlaywrightAppFactory _app;
    private readonly IBrowser _browser;

    public SidebarProjectsNavigationTests(E2EFixture fixture)
    {
        _app = fixture.App;
        _browser = fixture.Browser;
    }

    [Fact]
    public async Task Dashboard_ProjectsGroupIsCollapsed_ClickingItTogglesOpenAndClosed_WithoutNavigating()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 900);
        await page.GotoAsync($"{_app.BaseAddress}/");

        var group = page.Locator(".fo-sidebar__nav-group");
        Assert.False(await group.EvaluateAsync<bool>("el => el.open"));

        await page.ClickAsync(".fo-sidebar__nav-group > summary");
        Assert.True(await group.EvaluateAsync<bool>("el => el.open"));
        await page.Locator(".fo-sidebar__nav-sub a", new() { HasTextString = "Reference Project" }).WaitForAsync();
        Assert.EndsWith("/", page.Url); // the click never navigated away from the Dashboard

        await page.ClickAsync(".fo-sidebar__nav-group > summary");
        Assert.False(await group.EvaluateAsync<bool>("el => el.open"));
    }

    [Fact]
    public async Task InsideAProject_TheGroupAutoExpands_AndTheCurrentProjectIsMarkedActive()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 900);

        await page.GotoAsync($"{_app.BaseAddress}/Projects/Details/{_app.ProjectId}");

        var group = page.Locator(".fo-sidebar__nav-group");
        Assert.True(await group.EvaluateAsync<bool>("el => el.open"));

        var activeLink = page.Locator($".fo-sidebar__nav-sub a[href='/Projects/Details/{_app.ProjectId}']");
        Assert.Equal("page", await activeLink.GetAttributeAsync("aria-current"));
    }

    [Fact]
    public async Task ClickingAnotherProjectLink_NavigatesToItsOwnOverviewPage()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 900);
        await page.GotoAsync($"{_app.BaseAddress}/");

        await page.ClickAsync(".fo-sidebar__nav-group > summary");
        await page.ClickAsync($".fo-sidebar__nav-sub a[href='/Projects/Details/{_app.ProjectId}']");

        await page.WaitForSelectorAsync("h1:has-text('Reference Project')");
        Assert.Contains($"/Projects/Details/{_app.ProjectId}", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewAllProjects_NavigatesToTheManagementPage()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 1440, height: 900);
        await page.GotoAsync($"{_app.BaseAddress}/");

        await page.ClickAsync(".fo-sidebar__nav-group > summary");
        await page.ClickAsync("text=View all projects");

        await page.WaitForSelectorAsync("h1:has-text('Projects')");
        Assert.EndsWith("/Projects", page.Url);
    }

    [Fact]
    public async Task MobileViewport_ExpandedProjectsSubmenu_CausesNoHorizontalOverflow()
    {
        var page = await SignInAsync(PlaywrightAppFactory.ManagerEmail, width: 390, height: 844);
        await page.GotoAsync($"{_app.BaseAddress}/");

        await page.ClickAsync(".fo-nav-toggle-btn"); // opens the off-canvas sidebar
        await page.ClickAsync(".fo-sidebar__nav-group > summary");
        await page.Locator(".fo-sidebar__nav-sub a", new() { HasTextString = "Reference Project" }).WaitForAsync();

        var hasHorizontalScroll = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(hasHorizontalScroll, "expanded Projects submenu scrolls the page sideways at 390px");
    }

    private async Task<IPage> SignInAsync(string email, int width, int height)
    {
        var ctx = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var page = await ctx.NewPageAsync();

        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", email);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");
        await page.WaitForSelectorAsync("text=Sign out");

        return page;
    }
}
