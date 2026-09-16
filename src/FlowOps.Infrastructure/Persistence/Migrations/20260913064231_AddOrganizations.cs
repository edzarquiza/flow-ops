using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_teams_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_projects_name",
                table: "projects");

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "teams",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organization_memberships",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_organization_memberships_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_organization_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Phase 16 migration safety: this project may already have real seeded data (teams,
            // projects, and users with an Identity role) predating organizations entirely — the
            // AddColumn calls above gave every existing team/project row organization_id = 0,
            // which references no real Organization and would fail the AddForeignKey calls below
            // outright. This block backfills exactly the way DemoDataSeeder itself would have if
            // organizations had existed from the start: one "Demo Organization" owning every
            // pre-existing team/project, and one OrganizationMembership per pre-existing user,
            // with role taken from their existing Identity role assignment (CurrentUserAccessor no
            // longer reads that assignment at all after this migration — see its own remarks — so
            // without this backfill every pre-existing user would be locked out immediately).
            // A brand-new, never-seeded database has no teams/projects yet, so this is a no-op for
            // it; DemoDataSeeder creates its own "Demo Organization" the first time it runs there.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    legacy_org_id integer;
                BEGIN
                    IF EXISTS (SELECT 1 FROM teams) OR EXISTS (SELECT 1 FROM projects) THEN
                        INSERT INTO organizations (name, created_at)
                        VALUES ('Demo Organization', now())
                        RETURNING id INTO legacy_org_id;

                        UPDATE teams SET organization_id = legacy_org_id;
                        UPDATE projects SET organization_id = legacy_org_id;

                        INSERT INTO organization_memberships (organization_id, user_id, role, joined_at)
                        SELECT DISTINCT legacy_org_id, ur.user_id, r.name, now()
                        FROM "AspNetUserRoles" ur
                        JOIN "AspNetRoles" r ON r.id = ur.role_id
                        WHERE r.name IN ('Admin', 'Manager', 'Agent', 'Viewer');
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_name",
                table: "teams",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id_name",
                table: "projects",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_organization_id_user_id",
                table: "organization_memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_user_id",
                table: "organization_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_name",
                table: "organizations",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_organizations_organization_id",
                table: "projects",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_teams_organizations_organization_id",
                table: "teams",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_projects_organizations_organization_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_teams_organizations_organization_id",
                table: "teams");

            migrationBuilder.DropTable(
                name: "organization_memberships");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropIndex(
                name: "ix_teams_organization_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id_name",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "ix_teams_name",
                table: "teams",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_name",
                table: "projects",
                column: "name",
                unique: true);
        }
    }
}
