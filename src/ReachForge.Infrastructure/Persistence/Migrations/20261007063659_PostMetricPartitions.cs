using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// post_metric の月次パーティション（RF-DES-001 6章）と、13か月超の明細を移す月次集計表（PostMetricRollups）。
    /// PostMetrics を "CapturedAt" の範囲で月ごとに分割し、DataRetentionJob が先の月のパーティションを作り、
    /// 集計済みで空になった古いパーティションを削除する（どちらも所有者権限の関数で行うため、アプリ用ロールに CREATE 権限は不要）。
    /// </summary>
    public partial class PostMetricPartitions : Migration
    {
        public const string Functions = """
            CREATE OR REPLACE FUNCTION rf_ensure_post_metric_partitions(from_month date, to_month date)
            RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path FROM CURRENT AS $$
            DECLARE
                m date := date_trunc('month', from_month)::date;
                last_month date := date_trunc('month', to_month)::date;
                lo timestamptz; hi timestamptz; part text; created integer := 0; stray bigint;
            BEGIN
                WHILE m <= last_month LOOP
                    part := format('PostMetrics_%s', to_char(m, 'YYYYMM'));
                    IF to_regclass(quote_ident(part)) IS NULL THEN
                        lo := m::timestamp AT TIME ZONE 'UTC';
                        hi := (m + interval '1 month')::timestamp AT TIME ZONE 'UTC';
                        -- 既定パーティションに入っている同じ月の行は、新しいパーティションへ移す
                        EXECUTE format('SELECT count(*) FROM "PostMetrics_default" WHERE "CapturedAt" >= %L AND "CapturedAt" < %L', lo, hi) INTO stray;
                        IF stray > 0 THEN
                            ALTER TABLE "PostMetrics" DETACH PARTITION "PostMetrics_default";
                            EXECUTE format('CREATE TABLE %I PARTITION OF "PostMetrics" FOR VALUES FROM (%L) TO (%L)', part, lo, hi);
                            EXECUTE format('INSERT INTO "PostMetrics" SELECT * FROM "PostMetrics_default" WHERE "CapturedAt" >= %L AND "CapturedAt" < %L', lo, hi);
                            EXECUTE format('DELETE FROM "PostMetrics_default" WHERE "CapturedAt" >= %L AND "CapturedAt" < %L', lo, hi);
                            ALTER TABLE "PostMetrics" ATTACH PARTITION "PostMetrics_default" DEFAULT;
                        ELSE
                            EXECUTE format('CREATE TABLE %I PARTITION OF "PostMetrics" FOR VALUES FROM (%L) TO (%L)', part, lo, hi);
                        END IF;
                        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', part);
                        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', part);
                        EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (rf_tenant_visible("TenantId")) WITH CHECK (rf_tenant_visible("TenantId"))', part);
                        created := created + 1;
                    END IF;
                    m := (m + interval '1 month')::date;
                END LOOP;
                RETURN created;
            END $$;

            CREATE OR REPLACE FUNCTION rf_drop_post_metric_partitions(before_month date)
            RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path FROM CURRENT AS $$
            DECLARE p record; dropped integer := 0; has_rows boolean;
            BEGIN
                FOR p IN
                    SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                    WHERE i.inhparent = '"PostMetrics"'::regclass AND c.relname ~ '^PostMetrics_[0-9]{6}$'
                      AND to_date(substr(c.relname, 13), 'YYYYMM') < date_trunc('month', before_month)::date
                LOOP
                    -- 集計（PostMetricRollups）へ移し終えて空になったものだけを削除する
                    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', p.relname) INTO has_rows;
                    IF NOT has_rows THEN
                        EXECUTE format('DROP TABLE %I', p.relname);
                        dropped := dropped + 1;
                    END IF;
                END LOOP;
                RETURN dropped;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostMetricRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<short>(type: "smallint", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Snapshots = table.Column<int>(type: "integer", nullable: false),
                    Impressions = table.Column<long>(type: "bigint", nullable: false),
                    Reach = table.Column<long>(type: "bigint", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: false),
                    Likes = table.Column<int>(type: "integer", nullable: false),
                    Comments = table.Column<int>(type: "integer", nullable: false),
                    Shares = table.Column<int>(type: "integer", nullable: false),
                    Saves = table.Column<int>(type: "integer", nullable: false),
                    LinkClicks = table.Column<int>(type: "integer", nullable: false),
                    ProfileVisits = table.Column<int>(type: "integer", nullable: false),
                    Follows = table.Column<int>(type: "integer", nullable: false),
                    Conversions = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMetricRollups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostMetrics_CapturedAt",
                table: "PostMetrics",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PostMetricRollups_PostVariantId_Month",
                table: "PostMetricRollups",
                columns: new[] { "PostVariantId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostMetricRollups_TenantId",
                table: "PostMetricRollups",
                column: "TenantId");

            // 既存の表を月次パーティションの表に作り替える（行レベルセキュリティで行が見えなくならないよう、システムとして実行する）
            migrationBuilder.Sql("SELECT set_config('app.is_system', 'on', true);");
            migrationBuilder.Sql("""
                ALTER TABLE "PostMetrics" RENAME TO "PostMetrics_old";
                ALTER TABLE "PostMetrics_old" RENAME CONSTRAINT "PK_PostMetrics" TO "PK_PostMetrics_old";
                ALTER INDEX "IX_PostMetrics_PostVariantId_CapturedAt" RENAME TO "IX_PostMetrics_old_PostVariantId_CapturedAt";
                ALTER INDEX "IX_PostMetrics_TenantId" RENAME TO "IX_PostMetrics_old_TenantId";
                ALTER INDEX "IX_PostMetrics_CapturedAt" RENAME TO "IX_PostMetrics_old_CapturedAt";
                CREATE TABLE "PostMetrics" (LIKE "PostMetrics_old" INCLUDING DEFAULTS) PARTITION BY RANGE ("CapturedAt");
                ALTER TABLE "PostMetrics" ADD CONSTRAINT "PK_PostMetrics" PRIMARY KEY ("Id", "CapturedAt");
                CREATE INDEX "IX_PostMetrics_PostVariantId_CapturedAt" ON "PostMetrics" ("PostVariantId", "CapturedAt");
                CREATE INDEX "IX_PostMetrics_TenantId" ON "PostMetrics" ("TenantId");
                CREATE INDEX "IX_PostMetrics_CapturedAt" ON "PostMetrics" ("CapturedAt");
                CREATE TABLE "PostMetrics_default" PARTITION OF "PostMetrics" DEFAULT;
                """);
            migrationBuilder.Sql(Functions);
            migrationBuilder.Sql("""
                SELECT rf_ensure_post_metric_partitions(
                    coalesce((SELECT min("CapturedAt") AT TIME ZONE 'UTC' FROM "PostMetrics_old")::date, (now() AT TIME ZONE 'UTC')::date),
                    ((now() AT TIME ZONE 'UTC') + interval '3 months')::date);
                INSERT INTO "PostMetrics" SELECT * FROM "PostMetrics_old";
                DROP TABLE "PostMetrics_old";
                """);
            migrationBuilder.Sql("SELECT rf_enable_rls();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT set_config('app.is_system', 'on', true);");
            migrationBuilder.Sql("""
                CREATE TABLE "PostMetrics_flat" (LIKE "PostMetrics" INCLUDING DEFAULTS);
                INSERT INTO "PostMetrics_flat" SELECT * FROM "PostMetrics";
                DROP TABLE "PostMetrics";
                ALTER TABLE "PostMetrics_flat" RENAME TO "PostMetrics";
                ALTER TABLE "PostMetrics" ADD CONSTRAINT "PK_PostMetrics" PRIMARY KEY ("Id");
                CREATE INDEX "IX_PostMetrics_PostVariantId_CapturedAt" ON "PostMetrics" ("PostVariantId", "CapturedAt");
                CREATE INDEX "IX_PostMetrics_TenantId" ON "PostMetrics" ("TenantId");
                CREATE INDEX "IX_PostMetrics_CapturedAt" ON "PostMetrics" ("CapturedAt");
                DROP FUNCTION IF EXISTS rf_ensure_post_metric_partitions(date, date);
                DROP FUNCTION IF EXISTS rf_drop_post_metric_partitions(date);
                SELECT rf_enable_rls();
                """);

            migrationBuilder.DropTable(
                name: "PostMetricRollups");

            migrationBuilder.DropIndex(
                name: "IX_PostMetrics_CapturedAt",
                table: "PostMetrics");
        }
    }
}
