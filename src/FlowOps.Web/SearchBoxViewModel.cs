namespace FlowOps.Web;

/// <summary>
/// Presentation-only data for the shared <c>_SearchBox</c> partial — the one compact search
/// control used identically on Work Queue, At-Risk, and Members (CLAUDE.md's "one reusable
/// mechanism" rule, applied to search the same way it was already applied to badges). It renders a
/// plain GET form: no JavaScript, bookmarkable, and it degrades to a normal page navigation.
/// </summary>
/// <param name="Placeholder">The field's placeholder text — the one thing that differs in wording
/// between pages ("Search tickets...", "Search at-risk work...", "Search members...").</param>
/// <param name="Value">The active search term, echoed back into the input so a submitted search
/// is still visible in the box afterward.</param>
/// <param name="PreserveFields">Other active query-string state (e.g. Work Queue's <c>filter</c>)
/// carried as hidden fields, so submitting a new search does not silently clear it. A null or
/// empty value is omitted rather than rendered as a blank hidden field.</param>
public sealed record SearchBoxViewModel(
    string Placeholder,
    string? Value,
    IReadOnlyDictionary<string, string?>? PreserveFields = null);
