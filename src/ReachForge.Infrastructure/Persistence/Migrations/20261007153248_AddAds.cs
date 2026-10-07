using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Infrastructure.Persistence.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<short>(type: "smallint", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CredentialSecretRef = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    Extra = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<short>(type: "smallint", nullable: false),
                    Platform = table.Column<short>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Objective = table.Column<short>(type: "smallint", nullable: false),
                    DailyBudget = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Targeting = table.Column<string>(type: "text", nullable: false),
                    Creative = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    ExternalIds = table.Column<string>(type: "text", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Results = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
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

            migrationBuilder.Sql("SELECT rf_enable_rls();");
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
