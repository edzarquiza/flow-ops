using System.Net;
using System.Text.RegularExpressions;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests.Tickets;

/// <summary>
/// Phase 7 over real HTTP: derived SLA status reaches both ticket pages, is expressed in text
/// rather than by styling alone (CLAUDE.md §22), and never appears for a ticket the caller cannot
/// view.
/// </summary>
/// <remarks>Two sign-ins, inside the five-per-minute login rate limit (CLAUDE.md §12).</remarks>
public sealed partial class TicketSlaPagesTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public TicketSlaPagesTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Detail_And_Queue_ShowDerivedSlaStatusForAnAuthorizedCaller()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.AgentEmail);

        // --- Detail ---
        var detailResponse = await client.GetAsync($"/Tickets/Details/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detail = await detailResponse.Content.ReadAsStringAsync();

        Assert.Contains("SLA status", detail, StringComparison.Ordinal);
        // Freshly created and well inside its target, so the live branch is Within.
        Assert.Contains("Within", detail, StringComparison.Ordinal);
        // The seeded ticket is High priority: the 480-minute default row (SLA-RULE-02).
        Assert.Contains("480 minutes", detail, StringComparison.Ordinal);
        Assert.Contains("Paused time", detail, StringComparison.Ordinal);
        Assert.Contains("0 minutes", detail, StringComparison.Ordinal);
        Assert.Contains("SLA due", detail, StringComparison.Ordinal);
        // Remaining is rendered as a relative duration, e.g. "7h 59m left".
        Assert.Matches(@"\d+[dhm][^<]*left", detail);

        // --- Queue ---
        var queueResponse = await client.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);

        var queue = await queueResponse.Content.ReadAsStringAsync();

        Assert.Contains("<th scope=\"col\">SLA</th>", queue, StringComparison.Ordinal);
        Assert.Contains("Within", queue, StringComparison.Ordinal);
        Assert.Matches(@"\d+[dhm][^<]*left", queue);

        // Status is carried as text, and the absolute deadline is available in a title attribute
        // rather than being conveyed by colour (CLAUDE.md §22).
        Assert.Matches(SlaTitlePattern(), queue);
    }

    [Fact] // A caller who cannot see the ticket sees none of its SLA data either.
    public async Task Viewer_OutsideTheTeam_SeesNoSlaInformation()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await TestAuthentication.SignInAsync(client, TestUsers.ViewerEmail);

        var detailResponse = await client.GetAsync($"/Tickets/Details/{_factory.SeededTicketId}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        Assert.DoesNotContain("SLA status", await detailResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var queueResponse = await client.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);

        var queue = await queueResponse.Content.ReadAsStringAsync();
        Assert.Contains("No tickets are visible to you right now", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("Within", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("left", queue, StringComparison.Ordinal);
    }

    [GeneratedRegex("""title="SLA due \d{4}-\d{2}-\d{2}""")]
    private static partial Regex SlaTitlePattern();
}
