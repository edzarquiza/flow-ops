using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id_name",
                table: "projects");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id_name",
                table: "projects",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "is_active = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id_name",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id_name",
                table: "projects",
                columns: new[] { "organization_id", "name" },
                unique: true);
        }
    }
}
