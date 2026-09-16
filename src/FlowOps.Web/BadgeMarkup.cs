namespace FlowOps.Web;

/// <summary>
/// Renders the one shared semantic-badge shape used for Status, Priority, Role, SLA, and
/// Attention-severity: an icon plus a text label, both on a soft tinted background whose color
/// visibly surrounds the whole container — background, border, text, and icon all carry the same
/// hue. Every caller passes a fixed enum's own display text (never user input), so this is safe to
/// emit raw.
/// </summary>
/// <remarks>
/// Deliberately explicit <c>rgba(...)</c> values rather than <c>color-mix()</c>: a badge's color
/// is the entire point of this component, so it must render correctly in every browser, not only
/// ones with a recent CSS Color 5 implementation. The RGB triple for each tone lives in exactly
/// one place (<see cref="Rgb"/>) — every badge family (Status/Priority/Role/SLA/Severity) reads
/// the same table through its own <c>Tone(...)</c> mapping, so "what does danger look like" is
/// never redefined per call site.
/// </remarks>
public static class BadgeMarkup
{
    public static string Chip(string icon, string text, SemanticTone tone, string? extraClass = null)
    {
        var rgb = Rgb(tone);
        var classAttr = extraClass is null ? "chip chip--icon" : $"chip chip--icon {extraClass}";
        var style = $"background:rgba({rgb},0.18);border-color:rgba({rgb},0.55);color:{TextVar(tone)};";
        return $"<span class=\"{classAttr}\" style=\"{style}\">{icon}<span>{text}</span></span>";
    }

    private static string Rgb(SemanticTone tone) => tone switch
    {
        SemanticTone.Danger => "229, 107, 111",
        SemanticTone.Warn => "232, 180, 92",
        SemanticTone.Info => "99, 167, 232",
        SemanticTone.Ok => "76, 195, 138",
        SemanticTone.Teal => "95, 211, 196",
        SemanticTone.Violet => "141, 133, 201",
        SemanticTone.Muted => "113, 130, 133",
        _ => "113, 130, 133",
    };

    private static string TextVar(SemanticTone tone) => tone switch
    {
        SemanticTone.Danger => "var(--fo-danger)",
        SemanticTone.Warn => "var(--fo-warn)",
        SemanticTone.Info => "var(--fo-info)",
        SemanticTone.Ok => "var(--fo-ok)",
        SemanticTone.Teal => "var(--fo-teal)",
        SemanticTone.Violet => "var(--fo-violet)",
        SemanticTone.Muted => "var(--fo-text-3)",
        _ => "var(--fo-text-3)",
    };
}
