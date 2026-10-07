using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Migrations.Sqlite;

/// <summary>
/// SQLite 用のマイグレーション作成（dotnet ef）。モデルを変えたら追加する（変え忘れはテストで検出する）。
/// 例：dotnet ef migrations add &lt;名前&gt; -p src/ReachForge.Migrations.Sqlite -s src/ReachForge.Migrations.Sqlite
/// </summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<ReachForgeDbContext>
{
    public const string AssemblyName = "ReachForge.Migrations.Sqlite";

    public ReachForgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReachForgeDbContext>()
            .UseSqlite("Data Source=design.db", x => x.MigrationsAssembly(AssemblyName))
            .Options;
        return new ReachForgeDbContext(options, new MutableTenantContext { IsSystem = true, UserName = "migrations" });
    }
}
