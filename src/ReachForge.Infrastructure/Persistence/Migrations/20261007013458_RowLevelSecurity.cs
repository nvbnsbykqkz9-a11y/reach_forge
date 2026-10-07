using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 行レベルセキュリティ（RLS）。"TenantId" 列を持つすべての表に、セッション変数 app.tenant_id（または app.is_system=on）に
    /// 一致する行だけを読み書きできるポリシーを付ける。FORCE により表の所有者にも適用する（スーパーユーザーは対象外のため、
    /// アプリは BYPASSRLS を持たない専用ロールで接続すること）。表を追加したマイグレーションでは SELECT rf_enable_rls(); を呼ぶ。
    /// </summary>
    public partial class RowLevelSecurity : Migration
    {
        public const string EnableFunction = """
            CREATE OR REPLACE FUNCTION rf_enable_rls() RETURNS void LANGUAGE plpgsql AS $$
            DECLARE t record;
            BEGIN
                FOR t IN
                    SELECT c.table_name
                    FROM information_schema.columns c
                    JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
                    WHERE c.table_schema = current_schema() AND c.column_name = 'TenantId' AND tb.table_type = 'BASE TABLE'
                      AND c.table_name NOT LIKE 'AspNet%'
                LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t.table_name);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t.table_name);
                    EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', t.table_name);
                    EXECUTE format(
                        'CREATE POLICY tenant_isolation ON %I USING (rf_tenant_visible("TenantId")) WITH CHECK (rf_tenant_visible("TenantId"))',
                        t.table_name);
                END LOOP;
            END $$;
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION rf_tenant_visible(row_tenant uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
                    SELECT coalesce(current_setting('app.is_system', true), '') = 'on'
                        OR row_tenant = nullif(current_setting('app.tenant_id', true), '')::uuid
                $$;
                """);
            migrationBuilder.Sql(EnableFunction);
            migrationBuilder.Sql("SELECT rf_enable_rls();");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE t record;
                BEGIN
                    FOR t IN SELECT tablename FROM pg_policies WHERE policyname = 'tenant_isolation' AND schemaname = current_schema()
                    LOOP
                        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', t.tablename);
                        EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY', t.tablename);
                        EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY', t.tablename);
                    END LOOP;
                END $$;
                """);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS rf_enable_rls();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS rf_tenant_visible(uuid);");
        }
    }
}
