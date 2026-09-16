namespace FlowOps.Application;

/// <summary>
/// The one place a caller-supplied search term is trimmed, length-capped, and turned into a safe
/// <c>LIKE</c>/<c>ILIKE</c> pattern — shared by every query service that added search (Work Queue,
/// At-Risk, Members) rather than reimplemented three times. Not a search framework: it has no
/// notion of fields, entities, or query building, only string hygiene.
/// </summary>
public static class SearchTermNormalizer
{
    /// <summary>A generous cap for a search box, not a query-builder — long enough for any
    /// realistic query, short enough to keep a pathological input cheap to match against.</summary>
    public const int MaxLength = 100;

    /// <summary>The escape character passed to <c>EF.Functions.ILike</c> alongside a pattern from
    /// <see cref="ToLikePattern"/>, so the two always agree.</summary>
    public const string LikeEscapeCharacter = "\\";

    /// <summary>Trims the term and treats empty/whitespace as "no search" (<c>null</c>) — the
    /// single place every search-capable query decides that. Overlong input is capped rather than
    /// rejected, so a pasted paragraph degrades to "search for its first 100 characters" instead
    /// of an error.</summary>
    public static string? Normalize(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var trimmed = term.Trim();
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }

    /// <summary>
    /// Wraps an already-normalized term as a <c>%term%</c> pattern with <c>LIKE</c>'s own wildcard
    /// characters (<c>%</c>, <c>_</c>) escaped first — so a search for a literal <c>%</c> or <c>_</c>
    /// searches for that character, rather than silently changing what the pattern matches. Pass
    /// the result to <c>EF.Functions.ILike(column, pattern, SearchTermNormalizer.LikeEscapeCharacter)</c>.
    /// </summary>
    public static string ToLikePattern(string normalizedTerm)
    {
        var escaped = normalizedTerm
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
