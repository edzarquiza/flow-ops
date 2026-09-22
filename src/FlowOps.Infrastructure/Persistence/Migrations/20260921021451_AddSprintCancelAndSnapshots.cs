using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintCancelAndSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sprints_status",
                table: "sprints");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "sprints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprint_ticket_snapshots",
                columns: table => new
                {
                    sprint_id = table.Column<int>(type: "integer", nullable: false),
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    status_at_completion = table.Column<string>(type: "text", nullable: false),
                    was_done = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_ticket_snapshots", x => new { x.sprint_id, x.ticket_id });
                    table.CheckConstraint("ck_sprint_ticket_snapshots_status", "status_at_completion IN ('Open', 'Assigned', 'InProgress', 'Pending', 'Resolved', 'Closed')");
                    table.ForeignKey(
                        name: "fk_sprint_ticket_snapshots_sprints_sprint_id",
                        column: x => x.sprint_id,
                        principalTable: "sprints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprint_ticket_snapshots_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sprints_cancelled_at",
                table: "sprints",
                sql: "status <> 'Cancelled' OR cancelled_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sprints_status",
                table: "sprints",
                sql: "status IN ('Planned', 'Active', 'Completed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "ix_sprint_ticket_snapshots_ticket",
                table: "sprint_ticket_snapshots",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sprint_ticket_snapshots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sprints_cancelled_at",
                table: "sprints");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sprints_status",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "sprints");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sprints_status",
                table: "sprints",
                sql: "status IN ('Planned', 'Active', 'Completed')");
        }
    }
}
