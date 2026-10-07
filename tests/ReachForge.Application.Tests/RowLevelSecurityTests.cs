using Npgsql;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

/// <summary>PostgreSQL の RLS（RF_TEST_POSTGRES があるときだけ実行）。</summary>
public class RowLevelSecurityTests
{
    private static long Count(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task App_role_sees_only_its_tenant_rows_even_without_query_filters()
    {
        if (AppFixture.PostgresAdmin is null) Assert.Skip("RF_TEST_POSTGRES が未設定のため PostgreSQL のテストは行わない");
        await using var f = await AppFixture.CreateAsync();
        await using var conn = new NpgsqlConnection(f.PostgresAppConnection);
        await conn.OpenAsync();

        // セッション変数なし：何も見えない
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM \"PostVariants\""));

        // 自テナント：見える
        Count(conn, $"SELECT count(*) FROM (SELECT set_config('app.tenant_id', '{DemoSeeder.TenantId}', false)) s");
        Assert.True(Count(conn, "SELECT count(*) FROM \"PostVariants\"") > 0);

        // 他テナント：見えない・書けない
        var other = Guid.NewGuid();
        Count(conn, $"SELECT count(*) FROM (SELECT set_config('app.tenant_id', '{other}', false)) s");
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM \"PostVariants\""));
        var ex = Assert.Throws<PostgresException>(() => Count(conn,
            $"WITH i AS (INSERT INTO \"AuditLogs\" (\"Id\", \"TenantId\", \"Actor\", \"Action\", \"TargetType\", \"CreatedAt\", \"UpdatedAt\", \"RowVersion\") " +
            $"VALUES ('{Guid.NewGuid()}', '{DemoSeeder.TenantId}', 'x', 'x', '', now(), now(), 0) RETURNING 1) SELECT count(*) FROM i"));
        Assert.Equal("42501", ex.SqlState); // row-level security policy violation

        // すべてのテナント表に RLS が付いている（新しい表で rf_enable_rls() を呼び忘れていない）
        Assert.Equal(0, Count(conn, """
            SELECT count(*) FROM information_schema.columns c
            JOIN pg_class p ON p.relname = c.table_name
            WHERE c.table_schema = 'public' AND c.column_name = 'TenantId' AND c.table_name NOT LIKE 'AspNet%'
              AND p.relkind = 'r' AND NOT (p.relrowsecurity AND p.relforcerowsecurity)
            """));
    }
}
