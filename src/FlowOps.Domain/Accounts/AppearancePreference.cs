namespace FlowOps.Domain.Accounts;

/// <summary>
/// A user's chosen appearance (Phase 29C). Personal, never organization-wide, and independent of
/// authorization. <see cref="System"/> is stored as itself — never as the resolved Light/Dark — so the
/// theme keeps following the operating system's own preference. Persisted as text (ADR-0009).
/// </summary>
public enum AppearancePreference
{
    /// <summary>The existing FlowOps look, and the default for every user.</summary>
    Dark,

    Light,

    /// <summary>Follow the browser/OS <c>prefers-color-scheme</c>.</summary>
    System,
}
