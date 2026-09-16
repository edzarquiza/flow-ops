using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "registration_rejected_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events",
                sql: "event_type IN ('OrganizationDeactivated', 'OrganizationReactivated', 'UserDeactivated', 'UserReactivated', 'UserApproved', 'UserRejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events");

            migrationBuilder.DropColumn(
                name: "registration_rejected_at",
                table: "AspNetUsers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_platform_audit_events_event_type",
                table: "platform_audit_events",
                sql: "event_type IN ('OrganizationDeactivated', 'OrganizationReactivated', 'UserDeactivated', 'UserReactivated', 'UserApproved')");
        }
    }
}
