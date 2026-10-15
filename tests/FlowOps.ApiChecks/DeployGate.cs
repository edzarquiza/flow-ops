using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FlowOps.ApiChecks;

/// <summary>
/// The deploy gate: real HTTP calls against a running FlowOps instance. If any of these fail, the
/// `api-checks` CI job fails. Everything here asserts on what a client actually sees - status
/// codes, redirects, JSON - not on internal types, so it checks the built container end to end.
/// </summary>
public sealed partial class DeployGate : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _api;

    public DeployGate(ApiFixture api) => _api = api;

    // ---- Health -------------------------------------------------------------------------------

    [Fact]
    public async Task Health_liveness_returns_200()
    {
        using var response = await _api.Anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_readiness_confirms_the_database_is_reachable()
    {
        using var response = await _api.Anonymous.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Anonymous callers --------------------------------------------------------------------

    [Fact]
    public async Task Login_page_renders_the_sign_in_form()
    {
        using var response = await _api.Anonymous.GetAsync("/Account/Login");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Input.Email", body, StringComparison.Ordinal);
        Assert.Contains("Input.Password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_request_to_a_protected_page_is_redirected_to_login()
    {
        using var response = await _api.Anonymous.GetAsync("/Tickets");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", ApiSession.RedirectPath(response));
    }

    [Fact]
    public async Task Pending_approval_status_endpoint_returns_json()
    {
        using var response = await _api.Anonymous.GetAsync("/Account/PendingApproval?handler=Status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unknown", json.RootElement.GetProperty("status").GetString());
    }

    // ---- Signed-in access (role -> page) ------------------------------------------------------

    public static TheoryData<string, string> OpenablePages()
    {
        var shared = new[] { "/", "/Tickets", "/Tickets/AtRisk", "/Projects", "/TeamWorkload", "/Guide" };
        var data = new TheoryData<string, string>();

        foreach (var path in shared.Concat(["/Organization/Members", "/Admin"]))
        {
            data.Add("Admin", path);
        }

        foreach (var role in new[] { "Agent", "Viewer" })
        {
            foreach (var path in shared)
            {
                data.Add(role, path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OpenablePages))]
    public async Task Signed_in_role_can_open_page(string role, string path)
    {
        using var response = await _api.For(role).GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Authorization boundaries -------------------------------------------------------------

    public static TheoryData<string, string> ForbiddenPages() => new()
    {
        { "Agent", "/Organization/Members" },
        { "Agent", "/Admin" },
        { "Agent", "/Platform" },
        { "Viewer", "/Organization/Members" },
        { "Viewer", "/Admin" },
        { "Viewer", "/Platform" },
        { "Admin", "/Platform" },
    };

    [Theory]
    [MemberData(nameof(ForbiddenPages))]
    public async Task Signed_in_role_is_denied_page_above_its_authority(string role, string path)
    {
        using var response = await _api.For(role).GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/AccessDenied", ApiSession.RedirectPath(response));
    }

    // ---- Write path ---------------------------------------------------------------------------

    [Fact]
    public async Task Admin_can_create_a_ticket_and_open_it()
    {
        var createPage = await _api.Admin.GetBodyAsync("/Tickets/Create");
        var categoryId = FirstCategoryId(createPage)
            ?? throw new InvalidOperationException("No category option found on /Tickets/Create.");

        var title = $"API check {Guid.NewGuid():N}";
        using var created = await _api.Admin.PostFormAsync("/Tickets/Create", new Dictionary<string, string>
        {
            ["Input.Title"] = title,
            ["Input.Description"] = "Created by the automated API checks.",
            ["Input.WorkType"] = "Incident",
            ["Input.Priority"] = "Medium",
            ["Input.CategoryId"] = categoryId,
        });

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var detailsPath = ApiSession.RedirectPathAndQuery(created)
            ?? throw new InvalidOperationException("Create did not redirect anywhere.");
        Assert.StartsWith("/Tickets/Details", detailsPath, StringComparison.Ordinal);

        var details = await _api.Admin.GetBodyAsync(detailsPath);
        Assert.Contains(title, details, StringComparison.Ordinal);
    }

    // ---- Login hardening ----------------------------------------------------------------------

    // Runs against its own session and consumes the login limiter, so it only uses an account
    // that does not exist - no real persona's lockout counter is touched. The shared sessions were
    // all signed in during fixture setup, before any check ran.
    [Fact]
    public async Task Login_rejects_bad_credentials_generically_and_is_rate_limited()
    {
        using var session = new ApiSession();
        var badLogin = new Dictionary<string, string>
        {
            ["Input.Email"] = "nobody@demo.flowops.dev",
            ["Input.Password"] = "not-the-password",
        };

        using var rejected = await session.PostFormAsync("/Account/Login", badLogin);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("Invalid login attempt.", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The limit is 5 POSTs per fixed one-minute window per IP. Twelve in a burst always
        // includes a rejection, even if the window happens to roll over partway through.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var response = await session.PostFormAsync("/Account/Login", badLogin);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private static string? FirstCategoryId(string html)
    {
        var select = CategorySelectPattern().Match(html);
        if (!select.Success)
        {
            return null;
        }

        var option = OptionValuePattern().Match(select.Groups[1].Value);
        return option.Success ? option.Groups[1].Value : null;
    }

    [GeneratedRegex("name=\"Input\\.CategoryId\"[^>]*>(.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex CategorySelectPattern();

    [GeneratedRegex("<option[^>]*value=\"(\\d+)\"")]
    private static partial Regex OptionValuePattern();
}
