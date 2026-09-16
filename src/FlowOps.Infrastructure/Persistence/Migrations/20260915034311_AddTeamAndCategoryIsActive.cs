using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamAndCategoryIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_teams_organization_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_categories_team_id_name",
                table: "categories");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "categories",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_name",
                table: "teams",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_categories_team_id_name",
                table: "categories",
                columns: new[] { "team_id", "name" },
                unique: true,
                filter: "is_active = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_teams_organization_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_categories_team_id_name",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_name",
                table: "teams",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_categories_team_id_name",
                table: "categories",
                columns: new[] { "team_id", "name" },
                unique: true);
        }
    }
}
