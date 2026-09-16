using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_platform_admin",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "platform_audit_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_organization_id = table.Column<int>(type: "integer", nullable: true),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_audit_events", x => x.id);
                    table.CheckConstraint("ck_platform_audit_events_event_type", "event_type IN ('OrganizationDeactivated', 'OrganizationReactivated', 'UserDeactivated', 'UserReactivated')");
                    table.CheckConstraint("ck_platform_audit_events_exactly_one_target", "(target_organization_id IS NOT NULL)::int + (target_user_id IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "fk_platform_audit_events_asp_net_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_platform_audit_events_asp_net_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_platform_audit_events_organizations_target_organization_id",
                        column: x => x.target_organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_events_actor_user_id",
                table: "platform_audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_events_target_organization_id",
                table: "platform_audit_events",
                column: "target_organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_audit_events_target_user_id",
                table: "platform_audit_events",
                column: "target_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_audit_events");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "is_platform_admin",
                table: "AspNetUsers");
        }
    }
}
