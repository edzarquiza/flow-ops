using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuideIntroducedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "guide_introduced_at",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Every row that already exists at the moment this migration runs predates the Guide
            // feature entirely — none of them are "a brand-new account reaching FlowOps for the
            // first time." Backfilling them all to introduced-as-of-now means only an account
            // created after this migration runs can ever see the first-time discovery cue,
            // mirroring AddAccountApproval's identical backfill for registration_approved_at.
            migrationBuilder.Sql(
                "UPDATE \"AspNetUsers\" SET guide_introduced_at = NOW() WHERE guide_introduced_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "guide_introduced_at",
                table: "AspNetUsers");
        }
    }
}
