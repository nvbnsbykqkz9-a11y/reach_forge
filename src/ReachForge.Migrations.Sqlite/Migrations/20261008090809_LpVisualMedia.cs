using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class LpVisualMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VideoJobId",
                table: "LpProjects",
                newName: "MediaJobId");

            migrationBuilder.AddColumn<bool>(
                name: "MakeVideo",
                table: "LpProjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Sources",
                table: "LpProjects",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Videos",
                table: "LpProjects",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Visuals",
                table: "LpProjects",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            // 以前につくったもの：動画のジョブがあれば動画をつくる設定にし、できあがった縦型の動画を引き継ぐ
            migrationBuilder.Sql("""
                UPDATE LpProjects SET MakeVideo = 1 WHERE MediaJobId IS NOT NULL;
                UPDATE LpProjects SET Videos = json_object('Portrait', (
                    SELECT json_extract(j.ResultAssetIds, '$[0]') FROM AiJobs j WHERE j.Id = LpProjects.MediaJobId AND j.Status = 3))
                WHERE MediaJobId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM AiJobs j WHERE j.Id = LpProjects.MediaJobId AND j.Status = 3 AND json_array_length(j.ResultAssetIds) > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MakeVideo",
                table: "LpProjects");

            migrationBuilder.DropColumn(
                name: "Sources",
                table: "LpProjects");

            migrationBuilder.DropColumn(
                name: "Videos",
                table: "LpProjects");

            migrationBuilder.DropColumn(
                name: "Visuals",
                table: "LpProjects");

            migrationBuilder.RenameColumn(
                name: "MediaJobId",
                table: "LpProjects",
                newName: "VideoJobId");
        }
    }
}
