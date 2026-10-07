using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Persistence;

/// <summary>
/// 行レベルセキュリティ（RLS）用に、接続を開くたびにセッション変数（app.tenant_id / app.is_system）を設定する（RF-DES-001 6.2）。
/// アプリのグローバルクエリフィルタと二重化し、フィルタを外した SQL や不具合があっても他テナントの行は読めない・書けない。
/// インスタンスは1つ（EF の内部サービスプロバイダを増やさないため）で、テナントは接続を開いた DbContext から読む。
/// </summary>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    public static readonly TenantSessionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Command(connection, eventData);
        command?.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var command = Command(connection, eventData);
        if (command is not null) await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand? Command(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (eventData.Context is not ReachForgeDbContext db) return null;
        var (tenantId, isSystem) = db.SessionTenant;
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant, false), set_config('app.is_system', @system, false)";
        var tenant = command.CreateParameter();
        tenant.ParameterName = "tenant";
        tenant.Value = tenantId == Guid.Empty ? "" : tenantId.ToString();
        command.Parameters.Add(tenant);
        var system = command.CreateParameter();
        system.ParameterName = "system";
        system.Value = isSystem ? "on" : "off";
        command.Parameters.Add(system);
        return command;
    }
}

/// <summary>PostgreSQL の timestamptz は UTC（オフセット0）のみ書き込めるため、保存時に UTC へそろえる。</summary>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

/// <summary>
/// マイグレーション作成用（dotnet ef）。PostgreSQL 向けのマイグレーションを作る。
/// 例：dotnet ef migrations add &lt;名前&gt; -p src/ReachForge.Infrastructure -s src/ReachForge.Infrastructure -o Persistence/Migrations
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReachForgeDbContext>
{
    public ReachForgeDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("REACHFORGE_MIGRATIONS_CONNECTION")
                         ?? "Host=localhost;Database=reachforge;Username=postgres";
        var options = new DbContextOptionsBuilder<ReachForgeDbContext>().UseNpgsql(connection).Options;
        return new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true, UserName = "migrations" });
    }
}
