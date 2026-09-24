using System.Text.RegularExpressions;
using FlowOps.Web.Tests.Fixtures;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 30A: two of the three small pre-existing defects recorded at the end of Phase 29C — the
/// Team Details overflow at a phone width, and the sub-3:1 form-control border contrast.
/// </summary>
/// <remarks>
/// The third (the validation-summary tag helper's hidden placeholder <c>&lt;li style="display:none"&gt;</c>,
/// which is what the CSP console message was about) turned out not to have a safe narrow fix: the only
/// lever that removes it (<c>HtmlHelperOptions.ClientValidationEnabled = false</c>) also makes the tag
/// helper suppress the whole validation-summary <c>&lt;div role="alert"&gt;</c> on a clean page load, which
/// broke the existing accessibility contract <see cref="Phase11UiTests.SharedLayout_HasNavLandmarkAndSkipLinkWhenAuthenticated_LoginPageHasNeitherNavNorMissingAlertRole"/>
/// asserts (an always-present alert landmark, so a later client-side error would have somewhere to be
/// announced into). A real fix would mean hand-authoring the validation summary on every one of the ~17
/// pages that use it, which is a much bigger change than this phase's "small defects" scope — left as
/// the same recorded, harmless, cosmetic-only limitation Phase 29C already documented.
/// </remarks>
public sealed class Phase30ADefectFixTests : IClassFixture<FlowOpsWebApplicationFactory>
{
    private readonly FlowOpsWebApplicationFactory _factory;

    public Phase30ADefectFixTests(FlowOpsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Stylesheet_ProjectActionsWrapsAtPhoneWidth_AndRenameInputIsFlexible()
    {
        var css = await _factory.CreateClient().GetStringAsync("/flowops.css");

        var phoneBlock = Block(css, @"@media \(max-width: 768px\)\s*\{");
        Assert.Matches(new Regex(@"\.project-actions\s*\{[^}]*flex-wrap:\s*wrap"), phoneBlock);
        Assert.Matches(new Regex(@"\.project-actions__rename input\[type=""text""\]\s*\{[^}]*flex:"), phoneBlock);
    }

    [Fact] // The token-parity contract (Phase 29C) already proves Light restates this; this pins
           // that the actual form controls read from it, not from the structural-line token.
    public async Task Stylesheet_FormControlsUseTheDedicatedBorderToken_NotTheStructuralLineToken()
    {
        var css = await _factory.CreateClient().GetStringAsync("/flowops.css");

        var sharedInputRule = Block(css, @"input\[type=""text""\][^{]*\{");
        Assert.Contains("border: 1px solid var(--fo-control-border);", sharedInputRule, StringComparison.Ordinal);

        var selectTrigger = Block(css, @"\.fo-select__trigger\s*\{");
        Assert.Contains("border: 1px solid var(--fo-control-border);", selectTrigger, StringComparison.Ordinal);
    }

    private static string Block(string css, string openPattern)
    {
        var start = Regex.Match(css, openPattern);
        Assert.True(start.Success, $"block not found: {openPattern}");
        var depth = 1;
        var i = start.Index + start.Length;
        var from = i;
        while (i < css.Length && depth > 0)
        {
            depth += css[i] == '{' ? 1 : css[i] == '}' ? -1 : 0;
            i++;
        }

        return css[from..(i - 1)];
    }
}
