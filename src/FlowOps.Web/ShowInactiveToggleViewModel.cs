namespace FlowOps.Web;

/// <summary>
/// Phase 29C: data for the shared "Show inactive" switch on Admin lists. It is a view choice carried in
/// the query string (<c>?showInactive=true</c>) — bookmarkable, never saved to the database — and it
/// only hides or shows rows the caller is already authorized to see. <paramref name="Preserve"/> carries
/// other query values (for example the Members search term) through the switch's own form.
/// </summary>
public sealed record ShowInactiveToggleViewModel(
    bool ShowInactive,
    int HiddenCount,
    string Noun,
    IReadOnlyDictionary<string, string?>? Preserve = null);
