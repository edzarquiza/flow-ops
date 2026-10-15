namespace FlowOps.ApiChecks;

/// <summary>
/// Where to point the checks. Defaults match the repo's own docker-compose.yml (app on
/// localhost:8080, demo seeding on with its development-only persona password), so
/// `docker compose up -d` followed by `dotnet test tests/FlowOps.ApiChecks` works with no setup.
/// </summary>
public static class ApiSettings
{
    public static Uri BaseUrl { get; } = new(
        Environment.GetEnvironmentVariable("FLOWOPS_BASE_URL") ?? "http://localhost:8080");

    // The same development-only value docker-compose.yml already publishes; never a real secret.
    public static string DemoPassword { get; } =
        Environment.GetEnvironmentVariable("FLOWOPS_DEMO_PASSWORD") ?? "Demo-Only-Passw0rd!1";

    public const string AdminEmail = "demo.admin@demo.flowops.dev";
    public const string AgentEmail = "it.support.agent@demo.flowops.dev";
    public const string ViewerEmail = "executive.viewer@demo.flowops.dev";
}
