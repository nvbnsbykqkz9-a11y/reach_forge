using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaC2pa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "C2paManifest",
                table: "MediaAssets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "C2paSigned",
                table: "MediaAssets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "C2paManifest",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "C2paSigned",
                table: "MediaAssets");
        }
    }
}
