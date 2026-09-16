namespace FlowOps.Web;

/// <summary>
/// One figure in the shared <c>_StatStrip</c> partial — the one KPI/count-group component used
/// everywhere the app shows a small row of labelled numbers (component-consolidation cleanup). No
/// icon accompanies any figure, on any page: the number is the visual element
/// (<c>docs/ui/design-system.md</c> §8).
/// </summary>
/// <param name="Label">The figure's caption, rendered in the shared <c>.fo-label</c> eyebrow style.</param>
/// <param name="Value">The figure itself, already formatted for display.</param>
/// <param name="Caption">Optional supporting text rendered beneath the value (e.g. a sample size
/// or a breakdown).</param>
/// <param name="Highlight">True to render the value in the identity teal rather than the default
/// high-emphasis text colour — reserved for a genuinely good status word (e.g. "Healthy"), not
/// decoration.</param>
public sealed record StatStripItem(string Label, string Value, string? Caption = null, bool Highlight = false);
