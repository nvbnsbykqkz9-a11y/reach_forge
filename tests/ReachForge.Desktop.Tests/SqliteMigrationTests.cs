using Microsoft.EntityFrameworkCore;
using ReachForge.Migrations.Sqlite;

namespace ReachForge.Desktop.Tests;

public class SqliteMigrationTests
{
    [Fact]
    public void Sqlite_migrations_match_the_model()
    {
        // モデルを変えたのに SQLite 用のマイグレーションを追加し忘れると、Windows 版の利用者の DB が更新されない
        using var db = new SqliteDesignTimeFactory().CreateDbContext([]);
        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges(),
            "SQLite 用のマイグレーションがモデルと一致しません。dotnet ef migrations add <名前> -p src/ReachForge.Migrations.Sqlite -s src/ReachForge.Migrations.Sqlite を実行してください。");
    }
}
