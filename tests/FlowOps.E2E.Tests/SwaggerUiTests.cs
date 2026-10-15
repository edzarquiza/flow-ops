using Microsoft.Playwright;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// ADR-0037 real-browser regression: Swagger UI genuinely renders under the app's Content-Security-
/// Policy, and its "Try it out" reuses the login cookie. An HTML-string or header assertion
/// (<c>FlowOps.Web.Tests</c>) cannot catch a script or style the browser silently refuses to run —
/// only a real browser reports that — and the whole point of the page is the clickable demo.
/// </summary>
[Collection("E2E")]
public sealed class SwaggerUiTests
{
    private readonly PlaywrightAppFactory _app;
    private readonly IBrowser _browser;

    public SwaggerUiTests(E2EFixture fixture)
    {
        _app = fixture.App;
        _browser = fixture.Browser;
    }

    [Fact]
    public async Task SwaggerUi_Renders_AllFourEndpoints_WithNoContentSecurityPolicyViolations()
    {
        var context = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();

        var violations = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("Refused to", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(message.Text);
            }
        };

        await page.GotoAsync($"{_app.BaseAddress}/swagger");
        await page.WaitForSelectorAsync(".swagger-ui .opblock-summary-path");

        var paths = await page.Locator(".opblock-summary-path").AllInnerTextsAsync();
        var normalized = paths.Select(p => p.Trim()).Order().ToList();
        Assert.Equal(["/api/v1/attention", "/api/v1/tickets", "/api/v1/tickets/{id}", "/api/v1/workload"], normalized);

        Assert.Empty(violations);
    }

    [Fact]
    public async Task SwaggerUi_TryItOut_UsesTheLoginCookie_AndReturns200()
    {
        var context = await _browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();

        // Sign in through the real form first: Swagger UI is same-origin, so its requests carry the cookie.
        await page.GotoAsync($"{_app.BaseAddress}/Account/Login");
        await page.FillAsync("input[name='Input.Email']", PlaywrightAppFactory.ManagerEmail);
        await page.FillAsync("input[name='Input.Password']", PlaywrightAppFactory.Password);
        await page.ClickAsync("button.login-submit");
        await page.WaitForSelectorAsync("text=Sign out");

        await page.GotoAsync($"{_app.BaseAddress}/swagger");
        var workload = page.Locator(".opblock", new() { Has = page.Locator(".opblock-summary-path[data-path='/api/v1/workload']") });
        await workload.Locator(".opblock-summary-control").ClickAsync();
        await workload.Locator("button.try-out__btn").ClickAsync();
        await workload.Locator("button.execute").ClickAsync();

        var status = workload.Locator(".live-responses-table tbody .response-col_status").First;
        await status.WaitForAsync();
        Assert.Equal("200", (await status.InnerTextAsync()).Trim());

        var body = await workload.Locator(".live-responses-table .microlight").First.InnerTextAsync();
        Assert.Contains("summary", body, StringComparison.Ordinal);
    }
}
