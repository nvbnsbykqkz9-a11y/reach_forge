using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Tests;

/// <summary>PostgreSQL のテスト用 DB（所有者でマイグレーションし、アプリ用ロールに権限を付ける）。</summary>
/// <remarks>Application.Tests と Web.Tests で共有する（Web.Tests からはリンクで取り込む）。</remarks>
public static class PostgresTestDatabase
{
    public const string AppRole = "rf_app";
    public const string AppRolePassword = "rf_app_test";

    private static readonly Lock s_roleLock = new();

    public static string Create(string adminConnection, string database)
    {
        using (var admin = new NpgsqlConnection(adminConnection))
        {
            admin.Open();
            lock (s_roleLock)
            {
                Exec(admin, $"""
                    DO $$ BEGIN
                      IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{AppRole}') THEN
                        CREATE ROLE {AppRole} LOGIN PASSWORD '{AppRolePassword}' NOSUPERUSER NOBYPASSRLS;
                      END IF;
                    END $$;
                    """);
            }
            Exec(admin, $"CREATE DATABASE {database}");
        }

        var owner = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<ReachForgeDbContext>().UseNpgsql(owner).Options;
        using (var db = new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true }))
        {
            db.Database.Migrate();
        }
        using (var conn = new NpgsqlConnection(owner))
        {
            conn.Open();
            Exec(conn, $"""
                GRANT USAGE ON SCHEMA public TO {AppRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {AppRole};
                CREATE SCHEMA IF NOT EXISTS hangfire AUTHORIZATION {AppRole};
                """);
        }
        return new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = database, Username = AppRole, Password = AppRolePassword,
        }.ConnectionString;
    }

    public static void Drop(string adminConnection, string database)
    {
        NpgsqlConnection.ClearAllPools();
        using var admin = new NpgsqlConnection(adminConnection);
        admin.Open();
        Exec(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
