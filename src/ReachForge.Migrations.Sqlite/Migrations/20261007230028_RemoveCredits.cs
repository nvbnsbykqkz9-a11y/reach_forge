using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditAccounts");

            migrationBuilder.DropColumn(
                name: "CreditsUsed",
                table: "LpProjects");

            migrationBuilder.DropColumn(
                name: "CreditsCharged",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "CreditsHeld",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "Credits",
                table: "AiGenerations");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_TenantId_CreatedAt",
                table: "AiUsageLogs",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_TenantId_CreatedAt",
                table: "AiUsageLogs");

            migrationBuilder.AddColumn<int>(
                name: "CreditsUsed",
                table: "LpProjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreditsCharged",
                table: "AiJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreditsHeld",
                table: "AiJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Credits",
                table: "AiGenerations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CreditAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Balance = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedThisPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Held = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthlyGrant = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditAccounts_TenantId",
                table: "CreditAccounts",
                column: "TenantId",
                unique: true);
        }
    }
}
