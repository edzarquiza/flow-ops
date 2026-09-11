using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateSequence<int>(
                name: "ticket_reference_seq");

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sla_configurations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    work_type = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<string>(type: "text", nullable: false),
                    target_minutes = table.Column<int>(type: "integer", nullable: false),
                    risk_threshold_percent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_configurations", x => x.id);
                    table.CheckConstraint("ck_sla_configurations_priority", "priority IN ('Low', 'Medium', 'High', 'Critical')");
                    table.CheckConstraint("ck_sla_configurations_work_type", "work_type IS NULL OR work_type IN ('Incident', 'ServiceRequest', 'Task', 'Problem')");
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    team_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    default_work_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_default_work_type", "default_work_type IN ('Incident', 'ServiceRequest', 'Task', 'Problem')");
                    table.ForeignKey(
                        name: "fk_categories_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    team_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_team_manager = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => new { x.team_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValueSql: "'FO-' || lpad(nextval('ticket_reference_seq')::text, 6, '0')"),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    work_type = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sla_target_minutes = table.Column<int>(type: "integer", nullable: false),
                    sla_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sla_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sla_paused_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    pending_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sla_met = table.Column<bool>(type: "boolean", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_code = table.Column<string>(type: "text", nullable: true),
                    resolution_notes = table.Column<string>(type: "text", nullable: true),
                    reopen_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    assignment_change_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    pending_reason = table.Column<string>(type: "text", nullable: true),
                    status_before_pending = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_closed_at", "status <> 'Closed' OR closed_at IS NOT NULL");
                    table.CheckConstraint("ck_tickets_priority", "priority IN ('Low', 'Medium', 'High', 'Critical')");
                    table.CheckConstraint("ck_tickets_resolution_code", "resolution_code IS NULL OR resolution_code IN ('Fixed', 'Completed', 'WorkaroundProvided', 'NoFaultFound', 'Duplicate', 'Withdrawn')");
                    table.CheckConstraint("ck_tickets_resolution_present", "status NOT IN ('Resolved', 'Closed') OR (resolved_at IS NOT NULL AND resolution_code IS NOT NULL AND resolution_notes IS NOT NULL)");
                    table.CheckConstraint("ck_tickets_sla_due_after_started", "sla_due_at > sla_started_at");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Open', 'Assigned', 'InProgress', 'Pending', 'Resolved', 'Closed')");
                    table.CheckConstraint("ck_tickets_status_before_pending", "status_before_pending IS NULL OR status_before_pending IN ('Open', 'Assigned', 'InProgress', 'Pending', 'Resolved', 'Closed')");
                    table.CheckConstraint("ck_tickets_work_type", "work_type IN ('Incident', 'ServiceRequest', 'Task', 'Problem')");
                    table.ForeignKey(
                        name: "fk_tickets_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_comments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_comments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    field = table.Column<string>(type: "text", nullable: true),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_events", x => x.id);
                    table.CheckConstraint("ck_ticket_events_event_type", "event_type IN ('Created', 'Assigned', 'Reassigned', 'Unassigned', 'StatusChanged', 'PriorityChanged', 'CategoryChanged', 'TeamChanged', 'DueDateChanged', 'SlaRecalculated', 'PutOnHold', 'Resumed', 'Resolved', 'Reopened', 'Closed', 'CommentAdded')");
                    table.ForeignKey(
                        name: "fk_ticket_events_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_team_id_name",
                table: "categories",
                columns: new[] { "team_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_configurations_default_per_priority",
                table: "sla_configurations",
                column: "priority",
                unique: true,
                filter: "work_type IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sla_configurations_work_type_priority",
                table: "sla_configurations",
                columns: new[] { "work_type", "priority" },
                unique: true,
                filter: "work_type IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_teams_name",
                table: "teams",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_comments_ticket",
                table: "ticket_comments",
                columns: new[] { "ticket_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_events_ticket",
                table: "ticket_events",
                columns: new[] { "ticket_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "gin_tickets_title_trgm",
                table: "tickets",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_category_id",
                table: "tickets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_open_assignee",
                table: "tickets",
                column: "assignee_id",
                filter: "status NOT IN ('Resolved', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_open_sla_due",
                table: "tickets",
                column: "sla_due_at",
                filter: "status NOT IN ('Resolved', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_open_team_priority",
                table: "tickets",
                columns: new[] { "team_id", "priority" },
                filter: "status NOT IN ('Resolved', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_open_unassigned",
                table: "tickets",
                column: "created_at",
                filter: "assignee_id IS NULL AND status NOT IN ('Resolved', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_project_id",
                table: "tickets",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_reference",
                table: "tickets",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_resolved_at",
                table: "tickets",
                column: "resolved_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sla_configurations");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "ticket_comments");

            migrationBuilder.DropTable(
                name: "ticket_events");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropSequence(
                name: "ticket_reference_seq");
        }
    }
}
