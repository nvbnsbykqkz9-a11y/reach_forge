using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

public class MigrationTests
{
    /// <summary>モデルを変えたらマイグレーションを追加する（PostgreSQL 向け。接続は不要）。</summary>
    [Fact]
    public void Postgres_migrations_match_the_model()
    {
        var options = new DbContextOptionsBuilder<ReachForgeDbContext>().UseNpgsql("Host=localhost;Database=unused").Options;
        using var db = new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true });
        Assert.False(db.Database.HasPendingModelChanges(),
            "モデルの変更に対応するマイグレーションがありません。dotnet ef migrations add <名前> -p src/ReachForge.Infrastructure -s src/ReachForge.Infrastructure -o Persistence/Migrations を実行してください（新しい表には SELECT rf_enable_rls(); も）。");
    }
}
