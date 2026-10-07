using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Network = table.Column<short>(type: "INTEGER", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CredentialSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TokenExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Extra = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Network = table.Column<short>(type: "INTEGER", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    DailyBudget = table.Column<double>(type: "REAL", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Targeting = table.Column<string>(type: "TEXT", nullable: false),
                    Creative = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    ExternalIds = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Results = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SubmittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdCampaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdAccounts_TenantId",
                table: "AdAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdAccounts_WorkspaceId_Network_ExternalAccountId",
                table: "AdAccounts",
                columns: new[] { "WorkspaceId", "Network", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_Status",
                table: "AdCampaigns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_TenantId",
                table: "AdCampaigns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_WorkspaceId_Platform",
                table: "AdCampaigns",
                columns: new[] { "WorkspaceId", "Platform" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdAccounts");

            migrationBuilder.DropTable(
                name: "AdCampaigns");
        }
    }
}
