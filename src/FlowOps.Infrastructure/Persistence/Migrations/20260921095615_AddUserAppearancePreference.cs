using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAppearancePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "appearance",
                table: "AspNetUsers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Dark");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_appearance",
                table: "AspNetUsers",
                sql: "appearance IN ('Dark', 'Light', 'System')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_appearance",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "appearance",
                table: "AspNetUsers");
        }
    }
}
