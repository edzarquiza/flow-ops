using System.Net;
using System.Text.RegularExpressions;

namespace FlowOps.ApiChecks;

/// <summary>
/// One cookie-carrying HTTP client against the target instance. Redirects are never followed
/// automatically: the checks assert the redirect itself (e.g. "anonymous caller is sent to the
/// login page", "a forbidden page redirects to Access Denied").
/// </summary>
public sealed partial class ApiSession : IDisposable
{
    private readonly HttpClient _client;

    public ApiSession()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        _client = new HttpClient(handler)
        {
            BaseAddress = ApiSettings.BaseUrl,
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<string> GetBodyAsync(string path)
    {
        using var response = await GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// POSTs a form to <paramref name="path"/>, first loading the same path to obtain the
    /// antiforgery cookie and request token, exactly as a browser would.
    /// </summary>
    public async Task<HttpResponseMessage> PostFormAsync(string path, IDictionary<string, string> fields)
    {
        var page = await GetBodyAsync(path);
        var token = ExtractAntiforgeryToken(page)
            ?? throw new InvalidOperationException($"No antiforgery token found on {path}.");

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        using var content = new FormUrlEncodedContent(form);
        return await _client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    /// <summary>Signs in through the real login form; the redirect after success proves it worked.</summary>
    public async Task LoginAsync(string email, string password)
    {
        using var response = await PostFormAsync(
            "/Account/Login",
            new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password });

        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException(
                $"Login as {email} returned {(int)response.StatusCode}, expected a 302 redirect. " +
                "429 means the login rate limit (5/min/IP) was hit - wait a minute and rerun.");
        }
    }

    /// <summary>The path of a redirect's Location header, whether it is absolute or relative.</summary>
    public static string? RedirectPath(HttpResponseMessage response) =>
        RedirectPathAndQuery(response)?.Split('?')[0];

    /// <summary>The path plus query of a redirect's Location header, absolute or relative.</summary>
    public static string? RedirectPathAndQuery(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    public static string? ExtractAntiforgeryToken(string html)
    {
        var match = AntiforgeryTokenPattern().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}
