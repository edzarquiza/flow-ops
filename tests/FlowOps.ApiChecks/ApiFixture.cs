using System.Net;
using Xunit;

namespace FlowOps.ApiChecks;

/// <summary>
/// Waits for the instance to be healthy, then signs in once per role and shares those sessions
/// across every check. Logging in once (3 POSTs) keeps the suite inside the production login rate
/// limit (5/min/IP) without weakening it - the limit itself is one of the things under test.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public ApiSession Anonymous { get; } = new();

    public ApiSession Admin { get; } = new();

    public ApiSession Agent { get; } = new();

    public ApiSession Viewer { get; } = new();

    public ApiSession For(string role) => role switch
    {
        "Admin" => Admin,
        "Agent" => Agent,
        "Viewer" => Viewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    public async Task InitializeAsync()
    {
        await WaitUntilHealthyAsync();

        await Admin.LoginAsync(ApiSettings.AdminEmail, ApiSettings.DemoPassword);
        await Agent.LoginAsync(ApiSettings.AgentEmail, ApiSettings.DemoPassword);
        await Viewer.LoginAsync(ApiSettings.ViewerEmail, ApiSettings.DemoPassword);
    }

    public Task DisposeAsync()
    {
        Anonymous.Dispose();
        Admin.Dispose();
        Agent.Dispose();
        Viewer.Dispose();
        return Task.CompletedTask;
    }

    // A freshly started container (or a cold Render instance) needs a moment before it answers.
    private async Task WaitUntilHealthyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await Anonymous.GetAsync("/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Request timed out while the instance was still starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException($"{ApiSettings.BaseUrl} did not become healthy within 120 seconds.");
    }
}
