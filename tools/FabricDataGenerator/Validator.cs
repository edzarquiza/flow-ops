using Npgsql;

namespace FabricDataGenerator;

/// <summary>
/// Runs read-only SQL checks against the (still-live) generated database before the ephemeral
/// container is destroyed, and produces the concise validation report the brief asks for.
/// Referential integrity, enum values, and duplicate keys are already enforced by the real schema's
/// foreign keys, CHECK constraints, and primary keys — a row that violated one could never have
/// been inserted in the first place. These queries exist to make that guarantee visible and to
/// catch anything schema constraints cannot express (chronology, cross-table date ordering).
/// </summary>
internal static class Validator
{
    public static async Task<string> RunAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var report = new List<string>();

        long TicketCount = await ScalarLongAsync(connection, "SELECT count(*) FROM tickets");
        long EventCount = await ScalarLongAsync(connection, "SELECT count(*) FROM ticket_events");
        long CommentCount = await ScalarLongAsync(connection, "SELECT count(*) FROM ticket_comments");
        long TeamCount = await ScalarLongAsync(connection, "SELECT count(*) FROM teams");
        long CategoryCount = await ScalarLongAsync(connection, "SELECT count(*) FROM categories");
        long ProjectCount = await ScalarLongAsync(connection, "SELECT count(*) FROM projects");
        long UserCount = await ScalarLongAsync(connection, "SELECT count(*) FROM \"AspNetUsers\"");
        long TeamMemberCount = await ScalarLongAsync(connection, "SELECT count(*) FROM team_members");
        long SlaConfigCount = await ScalarLongAsync(connection, "SELECT count(*) FROM sla_configurations");
        long OrgCount = await ScalarLongAsync(connection, "SELECT count(*) FROM organizations");

        long orphanEvents = await ScalarLongAsync(connection,
            "SELECT count(*) FROM ticket_events e LEFT JOIN tickets t ON e.ticket_id = t.id WHERE t.id IS NULL");
        long orphanComments = await ScalarLongAsync(connection,
            "SELECT count(*) FROM ticket_comments c LEFT JOIN tickets t ON c.ticket_id = t.id WHERE t.id IS NULL");
        long invalidRequesters = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets t LEFT JOIN \"AspNetUsers\" u ON t.requester_id = u.id WHERE u.id IS NULL");
        long invalidAssignees = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets t LEFT JOIN \"AspNetUsers\" u ON t.assignee_id = u.id WHERE t.assignee_id IS NOT NULL AND u.id IS NULL");
        long invalidTeams = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets t LEFT JOIN teams tm ON t.team_id = tm.id WHERE tm.id IS NULL");
        long invalidCategories = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets t LEFT JOIN categories c ON t.category_id = c.id WHERE c.id IS NULL");
        long invalidProjects = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets t LEFT JOIN projects p ON t.project_id = p.id WHERE t.project_id IS NOT NULL AND p.id IS NULL");

        long chronologyViolations = await ScalarLongAsync(connection,
            "SELECT count(*) FROM ticket_events e JOIN tickets t ON e.ticket_id = t.id WHERE e.occurred_at < t.created_at");
        long resolutionBeforeCreation = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets WHERE resolved_at IS NOT NULL AND resolved_at < created_at");
        long closedBeforeResolved = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets WHERE closed_at IS NOT NULL AND resolved_at IS NOT NULL AND closed_at < resolved_at");
        long slaDueBeforeStart = await ScalarLongAsync(connection,
            "SELECT count(*) FROM tickets WHERE sla_due_at <= sla_started_at");

        long duplicateTicketIds = await ScalarLongAsync(connection,
            "SELECT count(*) - count(DISTINCT id) FROM tickets");
        long duplicateEventIds = await ScalarLongAsync(connection,
            "SELECT count(*) - count(DISTINCT id) FROM ticket_events");

        long invalidStatuses = await ScalarLongAsync(connection,
            $"SELECT count(*) FROM tickets WHERE status NOT IN ({EnumList("Open", "Assigned", "InProgress", "Pending", "Resolved", "Closed")})");
        long invalidPriorities = await ScalarLongAsync(connection,
            $"SELECT count(*) FROM tickets WHERE priority NOT IN ({EnumList("Low", "Medium", "High", "Critical")})");
        long invalidWorkTypes = await ScalarLongAsync(connection,
            $"SELECT count(*) FROM tickets WHERE work_type NOT IN ({EnumList("Incident", "ServiceRequest", "Task", "Problem")})");

        var fkViolations = orphanEvents + orphanComments + invalidRequesters + invalidAssignees + invalidTeams + invalidCategories + invalidProjects;
        var chronologyTotal = chronologyViolations + resolutionBeforeCreation + closedBeforeResolved + slaDueBeforeStart;
        var duplicateTotal = duplicateTicketIds + duplicateEventIds;
        var invalidEnumTotal = invalidStatuses + invalidPriorities + invalidWorkTypes;

        report.Add("FlowOps Fabric Synthetic Data — Validation Report");
        report.Add("=================================================");
        report.Add($"Organizations:      {OrgCount:N0}");
        report.Add($"Teams:              {TeamCount:N0}");
        report.Add($"Categories:         {CategoryCount:N0}");
        report.Add($"Projects:           {ProjectCount:N0}");
        report.Add($"Users:              {UserCount:N0}");
        report.Add($"Team members:       {TeamMemberCount:N0}");
        report.Add($"SLA configurations: {SlaConfigCount:N0}");
        report.Add($"Tickets:            {TicketCount:N0}");
        report.Add($"Ticket events:      {EventCount:N0}");
        report.Add($"Ticket comments:    {CommentCount:N0}");
        report.Add(string.Empty);
        report.Add($"Foreign-key violations:   {fkViolations:N0} (orphan events {orphanEvents}, orphan comments {orphanComments}, bad requester {invalidRequesters}, bad assignee {invalidAssignees}, bad team {invalidTeams}, bad category {invalidCategories}, bad project {invalidProjects})");
        report.Add($"Duplicate keys:           {duplicateTotal:N0}");
        report.Add($"Invalid enum values:      {invalidEnumTotal:N0}");
        report.Add($"Chronology violations:    {chronologyTotal:N0} (events before creation {chronologyViolations}, resolved<created {resolutionBeforeCreation}, closed<resolved {closedBeforeResolved}, sla_due<=sla_started {slaDueBeforeStart})");

        return string.Join(Environment.NewLine, report);
    }

    private static string EnumList(params string[] values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
