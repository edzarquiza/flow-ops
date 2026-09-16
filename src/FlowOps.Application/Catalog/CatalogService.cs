using FlowOps.Domain.Catalog;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Catalog;

/// <summary>
/// CLAUDE.md §3.3's `Catalog` module public surface ("Categories, projects, SLA configuration admin"
/// -> `CatalogService`), first populated here — category creation for the Workspace Setup
/// checklist's "set up your first team" step, which needs at least one category before a ticket can
/// be filed against the new team (TICKET-INV-02). Orchestration only, backed by
/// <see cref="CatalogAccessPolicy"/> and the existing organization-boundary check
/// <see cref="FlowOps.Application.Tickets.TicketService.CreateAsync"/> already performs for a category's team.
/// </summary>
public sealed class CatalogService
{
    /// <summary>Same cap as <see cref="FlowOps.Application.Directory.TeamService.MaxNameLength"/> — one shared
    /// convention for reference-data names, not two independently chosen limits.</summary>
    public const int MaxNameLength = 100;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public CatalogService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>AUTH-RULE-01: Admin-only. <paramref name="teamId"/> must belong to
    /// <paramref name="actor"/>'s own organization — checked here exactly like
    /// <see cref="FlowOps.Application.Tickets.TicketService.CreateAsync"/> already checks a category's team, since a
    /// category always belongs to a team that must already be organization-scoped correctly.</summary>
    public async Task<CreateCategoryResult> CreateCategoryAsync(
        CurrentUser actor,
        int teamId,
        string name,
        WorkType defaultWorkType,
        CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageCategories(actor))
        {
            throw new CategoryAccessDeniedException("This role may not create categories.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new CategoryAccessDeniedException("This team is not available to you.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CreateCategoryResult.Failed($"Category name must be between 1 and {MaxNameLength} characters.");
        }

        var alreadyExists = await _dbContext.Categories
            .AsNoTracking()
            .AnyAsync(c => c.TeamId == teamId && c.IsActive && c.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return CreateCategoryResult.Failed("An active category with this name already exists for this team.");
        }

        var category = new Category(0, teamId, trimmed, defaultWorkType, _timeProvider.GetUtcNow());
        _dbContext.Categories.Add(category);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent creates for the same (team, name) pair race on the filtered unique
            // index — the same class of race TeamService/CatalogService already handle for their
            // own uniqueness constraints.
            return CreateCategoryResult.Failed("An active category with this name already exists for this team.");
        }

        return CreateCategoryResult.Success(category.Id);
    }

    /// <summary>Every category on <paramref name="teamId"/>, active and inactive alike — Admin-only,
    /// scoped to the caller's own organization via the team.</summary>
    public async Task<IReadOnlyList<CategoryListItem>> GetCategoriesForTeamAsync(CurrentUser actor, int teamId, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageCategories(actor))
        {
            throw new CategoryAccessDeniedException("This role may not view category management.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new CategoryAccessDeniedException("This team is not available to you.");
        }

        return await _dbContext.Categories
            .AsNoTracking()
            .Where(c => c.TeamId == teamId)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryListItem(c.Id, c.Name, c.DefaultWorkType, c.IsActive, c.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Renames an existing category. The organization boundary is checked via the
    /// category's own team, exactly like <see cref="CreateCategoryAsync"/> checks a new category's
    /// team; the active-name-uniqueness check mirrors <see cref="CreateCategoryAsync"/>'s own.</summary>
    public async Task<CategoryMutationResult> RenameCategoryAsync(CurrentUser actor, int categoryId, string name, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageCategories(actor))
        {
            throw new CategoryAccessDeniedException("This role may not rename categories.");
        }

        var category = await _dbContext.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            throw new CategoryAccessDeniedException("This category is not available to you.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == category.TeamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new CategoryAccessDeniedException("This category is not available to you.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return CategoryMutationResult.Failed($"Category name must be between 1 and {MaxNameLength} characters.");
        }

        var alreadyExists = await _dbContext.Categories
            .AsNoTracking()
            .AnyAsync(c => c.Id != categoryId && c.TeamId == category.TeamId && c.IsActive && c.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return CategoryMutationResult.Failed("An active category with this name already exists for this team.");
        }

        category.Rename(trimmed);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return CategoryMutationResult.Failed("An active category with this name already exists for this team.");
        }

        return CategoryMutationResult.Success();
    }

    /// <summary>Deactivates a category — never deletes it. Existing tickets keep their
    /// <c>CategoryId</c> and keep displaying this category's name; the category simply stops being
    /// offered for new ticket creation.</summary>
    public async Task<CategoryMutationResult> DeactivateCategoryAsync(CurrentUser actor, int categoryId, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageCategories(actor))
        {
            throw new CategoryAccessDeniedException("This role may not deactivate categories.");
        }

        var category = await _dbContext.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            throw new CategoryAccessDeniedException("This category is not available to you.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == category.TeamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new CategoryAccessDeniedException("This category is not available to you.");
        }

        if (!category.IsActive)
        {
            return CategoryMutationResult.Failed("This category is already inactive.");
        }

        category.Deactivate();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CategoryMutationResult.Success();
    }

    /// <summary>Every project in <paramref name="actor"/>'s own organization, active and inactive
    /// alike — Admin-only, the same gate as every other project mutation, since this is "manage
    /// projects," not a public listing (the Create Ticket dropdown has its own, active-only query in
    /// <see cref="FlowOps.Application.Tickets.TicketQueryService.GetCreationOptionsAsync"/>).</summary>
    public async Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync(CurrentUser actor, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageProjects(actor))
        {
            throw new ProjectAccessDeniedException("This role may not view project management.");
        }

        return await _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.OrganizationId == actor.OrganizationId)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectListItem(p.Id, p.Name, p.IsActive, p.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>AUTH-RULE-01: Admin-only, scoped to the caller's own organization
    /// (<paramref name="actor"/>.OrganizationId is never client-supplied). Duplicate-name rejection is
    /// scoped to <em>active</em> projects only — the unique index itself
    /// (<c>ix_projects_organization_id_name</c>) is filtered the same way, so a name freed by
    /// deactivation is immediately reusable.</summary>
    public async Task<ProjectMutationResult> CreateProjectAsync(CurrentUser actor, string name, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageProjects(actor))
        {
            throw new ProjectAccessDeniedException("This role may not create projects.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return ProjectMutationResult.Failed($"Project name must be between 1 and {MaxNameLength} characters.");
        }

        var alreadyExists = await _dbContext.Projects
            .AsNoTracking()
            .AnyAsync(p => p.OrganizationId == actor.OrganizationId && p.IsActive && p.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return ProjectMutationResult.Failed("An active project with this name already exists in your organization.");
        }

        var project = new Project(0, actor.OrganizationId, trimmed, _timeProvider.GetUtcNow());
        _dbContext.Projects.Add(project);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent creates for the same (organization, name) pair race on the filtered
            // unique index — the same class of race TeamService.CreateAsync/AddMemberAsync already
            // handle for their own uniqueness constraints.
            return ProjectMutationResult.Failed("An active project with this name already exists in your organization.");
        }

        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>Renames an existing project. The organization-boundary check and the
    /// active-name-uniqueness check are both re-applied exactly as in <see cref="CreateProjectAsync"/>
    /// — a rename is just "create this name" plus "on an existing row."</summary>
    public async Task<ProjectMutationResult> RenameProjectAsync(CurrentUser actor, int projectId, string name, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageProjects(actor))
        {
            throw new ProjectAccessDeniedException("This role may not rename projects.");
        }

        var project = await _dbContext.Projects.SingleOrDefaultAsync(p => p.Id == projectId && p.OrganizationId == actor.OrganizationId, cancellationToken);
        if (project is null)
        {
            throw new ProjectAccessDeniedException("This project is not available to you.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return ProjectMutationResult.Failed($"Project name must be between 1 and {MaxNameLength} characters.");
        }

        var alreadyExists = await _dbContext.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id != projectId && p.OrganizationId == actor.OrganizationId && p.IsActive && p.Name == trimmed, cancellationToken);
        if (alreadyExists)
        {
            return ProjectMutationResult.Failed("An active project with this name already exists in your organization.");
        }

        project.Rename(trimmed);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ProjectMutationResult.Failed("An active project with this name already exists in your organization.");
        }

        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>Deactivates a project — never deletes it. Existing tickets keep their
    /// <c>ProjectId</c> and keep displaying this project's name (the row itself is untouched); the
    /// project simply stops being offered on the Create Ticket dropdown
    /// (<see cref="FlowOps.Application.Tickets.TicketQueryService.GetCreationOptionsAsync"/>'s
    /// active-only filter) and can no longer be selected for a new ticket
    /// (<see cref="FlowOps.Application.Tickets.TicketService.CreateAsync"/>'s own active check).</summary>
    public async Task<ProjectMutationResult> DeactivateProjectAsync(CurrentUser actor, int projectId, CancellationToken cancellationToken = default)
    {
        if (!CatalogAccessPolicy.CanManageProjects(actor))
        {
            throw new ProjectAccessDeniedException("This role may not deactivate projects.");
        }

        var project = await _dbContext.Projects.SingleOrDefaultAsync(p => p.Id == projectId && p.OrganizationId == actor.OrganizationId, cancellationToken);
        if (project is null)
        {
            throw new ProjectAccessDeniedException("This project is not available to you.");
        }

        if (!project.IsActive)
        {
            return ProjectMutationResult.Failed("This project is already inactive.");
        }

        project.Deactivate();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ProjectMutationResult.Success(project.Id);
    }
}
