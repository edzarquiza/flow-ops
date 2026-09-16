namespace FlowOps.Web;

/// <summary>
/// Presentation-only data for the shared <c>_SectionHeading</c> partial — the one panel/section
/// heading used everywhere a card or zone needs a title, replacing the icon-square + uppercase
/// eyebrow chrome that had been copy-pasted per page (component-consolidation cleanup). No icon,
/// ever: the title alone is the identity, the same restraint <c>docs/ui/design-system.md</c>
/// already applies to stat figures.
/// </summary>
/// <param name="Title">The heading text.</param>
/// <param name="Subtitle">Optional descriptive text rendered directly beneath the heading, in the
/// same <c>.page-context</c> style used under every other section/page title in the app.</param>
public sealed record SectionHeadingViewModel(string Title, string? Subtitle = null);
