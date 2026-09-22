using FlowOps.Domain.Accounts;
using Xunit;

namespace FlowOps.Domain.Tests.Tickets;

/// <summary>Guards the three appearance names: they are persisted as text with a database check, so a
/// rename or an extra value must be a deliberate, migrated change — never silent drift.</summary>
public class AppearancePreferenceTests
{
    [Fact]
    public void AppearancePreference_HasExactlyDarkLightSystem_AndDarkIsTheDefault()
    {
        Assert.Equal(new[] { AppearancePreference.Dark, AppearancePreference.Light, AppearancePreference.System }, Enum.GetValues<AppearancePreference>());
        Assert.Equal(AppearancePreference.Dark, default(AppearancePreference));
        Assert.Equal(new[] { "Dark", "Light", "System" }, Enum.GetNames<AppearancePreference>());
    }
}
