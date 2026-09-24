using Microsoft.Playwright;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// One <see cref="PlaywrightAppFactory"/> (one Postgres container, one running Kestrel host) and one
/// Chromium instance shared by the whole suite — each test opens its own <see cref="IBrowserContext"/>
/// (cheap, fully isolated cookies/storage) rather than paying for a new container or browser process
/// per test.
/// </summary>
[CollectionDefinition("E2E")]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>;

public sealed class E2EFixture : IAsyncLifetime
{
    public PlaywrightAppFactory App { get; } = new();

    public IPlaywright Playwright { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await ((IAsyncLifetime)App).InitializeAsync();
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
        await ((IAsyncLifetime)App).DisposeAsync();
    }
}
