namespace FlowOps.Web;

/// <summary>Presentation-only: the one- or two-letter avatar-circle initials for a display name.
/// Decides nothing about identity — purely how an already-known name is abbreviated.</summary>
public static class InitialsDisplay
{
    public static string From(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
        };
    }
}
