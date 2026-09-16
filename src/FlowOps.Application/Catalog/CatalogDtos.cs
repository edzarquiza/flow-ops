using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Catalog;

/// <summary>The outcome of creating a category — <see cref="Error"/> is a plain, user-facing
/// validation message, never a <see cref="CategoryAccessDeniedException"/> (authorization failures
/// are exceptions, not results — see that type's own doc comment).</summary>
public sealed record CreateCategoryResult(bool Succeeded, int? CategoryId, string? Error)
{
    public static CreateCategoryResult Success(int categoryId) => new(true, categoryId, null);

    public static CreateCategoryResult Failed(string error) => new(false, null, error);
}

/// <summary>One category on a team, for the team-detail page's Categories section — active and
/// inactive alike, so the page can show each, clearly marked.</summary>
public sealed record CategoryListItem(int CategoryId, string Name, WorkType DefaultWorkType, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>The outcome of renaming or deactivating a category — <see cref="Error"/> is a plain,
/// user-facing validation message, never a <see cref="CategoryAccessDeniedException"/>
/// (authorization/organization-boundary failures are exceptions, not results).</summary>
public sealed record CategoryMutationResult(bool Succeeded, string? Error)
{
    public static CategoryMutationResult Success() => new(true, null);

    public static CategoryMutationResult Failed(string error) => new(false, error);
}

/// <summary>One project in the caller's organization, for the /Admin/Projects list — both active and
/// inactive rows are returned so the page can show each, clearly marked (§6.1 non-goal: no separate
/// "archive" view).</summary>
public sealed record ProjectListItem(int ProjectId, string Name, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>The outcome of creating, renaming, or deactivating a project — <see cref="Error"/> is a
/// plain, user-facing validation message, never a <see cref="ProjectAccessDeniedException"/>
/// (authorization/organization-boundary failures are exceptions, not results — see that type's own
/// doc comment). Shared across all three project mutations since none needs a distinct success
/// payload beyond the project's id.</summary>
public sealed record ProjectMutationResult(bool Succeeded, int? ProjectId, string? Error)
{
    public static ProjectMutationResult Success(int projectId) => new(true, projectId, null);

    public static ProjectMutationResult Failed(string error) => new(false, null, error);
}
