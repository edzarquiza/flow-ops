using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FlowOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultSlaConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "sla_configurations",
                columns: new[] { "id", "priority", "risk_threshold_percent", "target_minutes", "work_type" },
                values: new object[,]
                {
                    { 1, "Critical", 80, 240, null },
                    { 2, "High", 80, 480, null },
                    { 3, "Medium", 80, 1440, null },
                    { 4, "Low", 80, 4320, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "sla_configurations",
                keyColumn: "id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "sla_configurations",
                keyColumn: "id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "sla_configurations",
                keyColumn: "id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "sla_configurations",
                keyColumn: "id",
                keyValue: 4);
        }
    }
}
