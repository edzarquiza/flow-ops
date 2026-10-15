using Npgsql;

namespace FabricDataGenerator;

/// <summary>
/// Exports each analytics-relevant table to CSV using PostgreSQL's native <c>COPY ... TO STDOUT
/// (FORMAT CSV, HEADER)</c> — a byte-exact server-side dump straight from the tables the EF Core
/// migrations created, not a hand-built serialization that could drift from what is actually in
/// the database.
/// </summary>
/// <remarks>
/// CSV was chosen over Parquet to keep this tool dependency-free (no new NuGet package beyond the
/// Testcontainers reference already used for the ephemeral database) — the brief explicitly allows
/// CSV as a fallback. Microsoft Fabric's Data Factory / Lakehouse "Get Data" and COPY INTO paths
/// both ingest CSV natively.
/// </remarks>
internal static class CsvExporter
{
    /// <summary>Table name -> the exact column list to export (also controls column order and
    /// lets us leave out sensitive/irrelevant Identity columns on <c>AspNetUsers</c>).</summary>
    private static readonly (string OutputFile, string Sql)[] Exports =
    [
        ("organizations.csv", "SELECT id, name, created_at, is_active FROM organizations ORDER BY id"),
        ("teams.csv", "SELECT id, organization_id, name, created_at, is_active FROM teams ORDER BY id"),
        ("categories.csv", "SELECT id, team_id, name, default_work_type, created_at, is_active FROM categories ORDER BY id"),
        ("projects.csv", "SELECT id, organization_id, name, created_at, is_active FROM projects ORDER BY id"),
        (
            "users.csv",
            """
            SELECT id, user_name, email, display_name, job_title, is_active, primary_team_id,
                   appearance, registration_approved_at
            FROM "AspNetUsers"
            ORDER BY id
            """
        ),
        ("team_members.csv", "SELECT team_id, user_id, is_team_manager, joined_at FROM team_members ORDER BY team_id, user_id"),
        (
            "sla_configurations.csv",
            "SELECT id, work_type, priority, target_minutes, risk_threshold_percent FROM sla_configurations ORDER BY id"
        ),
        (
            "tickets.csv",
            """
            SELECT id, reference, title, description, work_type, priority, status, requester_id,
                   assignee_id, team_id, project_id, category_id, created_at, updated_at,
                   planned_start_date, due_date, sla_target_minutes, sla_started_at, sla_due_at,
                   sla_paused_minutes, pending_since, sla_met, resolved_at, closed_at,
                   resolution_code, resolution_notes, reopen_count, assignment_change_count,
                   pending_reason
            FROM tickets
            ORDER BY id
            """
        ),
        (
            "ticket_comments.csv",
            "SELECT id, ticket_id, author_id, body, created_at, is_internal FROM ticket_comments ORDER BY id"
        ),
        (
            "ticket_events.csv",
            """
            SELECT id, ticket_id, event_type, actor_user_id, occurred_at, field, old_value, new_value, note
            FROM ticket_events
            ORDER BY id
            """
        ),
    ];

    public static async Task ExportAllAsync(string connectionString, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var (outputFile, sql) in Exports)
        {
            var path = Path.Combine(outputDirectory, outputFile);
            await using var writer = new StreamWriter(path);
            await using var reader = await connection.BeginTextExportAsync($"COPY ({sql}) TO STDOUT (FORMAT CSV, HEADER)");

            var buffer = new char[8192];
            int charsRead;
            while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await writer.WriteAsync(buffer, 0, charsRead);
            }

            Console.WriteLine($"  wrote {outputFile}");
        }
    }
}
