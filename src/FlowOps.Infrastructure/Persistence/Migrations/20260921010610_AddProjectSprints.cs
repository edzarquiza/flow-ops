using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_events_event_type",
                table: "ticket_events");

            migrationBuilder.AddColumn<bool>(
                name: "sprint_backlog",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "sprint_id",
                table: "tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprints",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprints", x => x.id);
                    table.CheckConstraint("ck_sprints_completed_at", "status <> 'Completed' OR completed_at IS NOT NULL");
                    table.CheckConstraint("ck_sprints_date_range", "start_date <= end_date");
                    table.CheckConstraint("ck_sprints_status", "status IN ('Planned', 'Active', 'Completed')");
                    table.ForeignKey(
                        name: "fk_sprints_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_sprint",
                table: "tickets",
                column: "sprint_id",
                filter: "sprint_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_sprint_backlog",
                table: "tickets",
                sql: "sprint_backlog = false OR sprint_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_events_event_type",
                table: "ticket_events",
                sql: "event_type IN ('Created', 'Assigned', 'Reassigned', 'Unassigned', 'StatusChanged', 'PriorityChanged', 'CategoryChanged', 'TeamChanged', 'DueDateChanged', 'SlaRecalculated', 'PutOnHold', 'Resumed', 'Resolved', 'Reopened', 'Closed', 'CommentAdded', 'SprintChanged')");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_project_start",
                table: "sprints",
                columns: new[] { "project_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ux_sprints_one_active_per_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_sprints_sprint_id",
                table: "tickets",
                column: "sprint_id",
                principalTable: "sprints",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_sprints_sprint_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "sprints");

            migrationBuilder.DropIndex(
                name: "ix_tickets_sprint",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_sprint_backlog",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_events_event_type",
                table: "ticket_events");

            migrationBuilder.DropColumn(
                name: "sprint_backlog",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "sprint_id",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_events_event_type",
                table: "ticket_events",
                sql: "event_type IN ('Created', 'Assigned', 'Reassigned', 'Unassigned', 'StatusChanged', 'PriorityChanged', 'CategoryChanged', 'TeamChanged', 'DueDateChanged', 'SlaRecalculated', 'PutOnHold', 'Resumed', 'Resolved', 'Reopened', 'Closed', 'CommentAdded')");
        }
    }
}
