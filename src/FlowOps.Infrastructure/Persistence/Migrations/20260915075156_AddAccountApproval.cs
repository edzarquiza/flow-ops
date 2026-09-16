using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "registration_approved_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // ADR-0024 / spec §19,§38: every row that already exists at the moment this migration
            // runs predates the approval gate entirely — none of them are "newly registered and
            // awaiting approval." Backfilling them all to approved-as-of-now preserves their
            // current IsActive value exactly as it already governs access (an already-deactivated
            // user stays deactivated; approval alone never re-activates anyone), so no existing
            // user is ever moved into Pending by this migration.
            migrationBuilder.Sql(
                "UPDATE \"AspNetUsers\" SET registration_approved_at = NOW() WHERE registration_approved_at IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events",
                sql: "event_type IN ('OrganizationDeactivated', 'OrganizationReactivated', 'UserDeactivated', 'UserReactivated', 'UserApproved')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events");

            migrationBuilder.DropColumn(
                name: "registration_approved_at",
                table: "AspNetUsers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events",
                sql: "event_type IN ('OrganizationDeactivated', 'OrganizationReactivated', 'UserDeactivated', 'UserReactivated')");
        }
    }
}
