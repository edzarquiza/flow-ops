using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationTeamId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "team_id",
                table: "invitations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitations_team_id",
                table: "invitations",
                column: "team_id");

            migrationBuilder.AddForeignKey(
                name: "fk_invitations_teams_team_id",
                table: "invitations",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invitations_teams_team_id",
                table: "invitations");

            migrationBuilder.DropIndex(
                name: "ix_invitations_team_id",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "invitations");
        }
    }
}
