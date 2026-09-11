namespace FlowOps.Application.Demo;

/// <summary>
/// CLAUDE.md §14: "Seeding runs at startup when FlowOps__Demo__Enabled=true." Bound from the
/// "Demo" configuration section in the composition root; validated there too (a persona password
/// is required whenever seeding is enabled) so a misconfigured demo deployment fails fast at
/// startup rather than seeding accounts nobody can log into.
/// </summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The one password shared by every seeded demo account (CLAUDE.md §14: "credentials supplied
    /// via environment variables... never in git"). A single shared password is proportionate for
    /// a portfolio demo — only the four named personas are ever meant to be logged into by a
    /// visitor, and Identity's own password policy already governs what values are acceptable.
    /// </summary>
    public string? PersonaPassword { get; set; }
}
