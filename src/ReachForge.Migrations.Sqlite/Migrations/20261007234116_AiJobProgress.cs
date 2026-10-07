using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AiJobProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "AiJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProgressText",
                table: "AiJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "AiJobs");

            migrationBuilder.DropColumn(
                name: "ProgressText",
                table: "AiJobs");
        }
    }
}
