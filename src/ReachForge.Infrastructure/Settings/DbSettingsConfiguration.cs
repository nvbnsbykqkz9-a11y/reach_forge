using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Settings;

/// <summary>
/// 画面で保存した設定（AppSettings 表）を設定値として読み込む。設定ファイル・環境変数より優先する。
/// 保存したプロセスでは直ちに、ほかのプロセス（Worker など）でも1分以内に読み直し、IOptionsMonitor を通じて反映する。
/// </summary>
public sealed class DbSettingsConfigurationSource(IServiceProvider services) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DbSettingsConfigurationProvider(services);
}

public sealed class DbSettingsConfigurationProvider : ConfigurationProvider, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal const string Purpose = "ReachForge.AppSettings.v1";

    private readonly IServiceProvider _services;
    private readonly Timer _timer;
    private DateTimeOffset? _lastUpdated;

    public DbSettingsConfigurationProvider(IServiceProvider services)
    {
        _services = services;
        services.GetRequiredService<AppSettingsReloader>().Register(this);
        _timer = new Timer(_ => ReloadIfChanged(), null, PollInterval, PollInterval);
    }

    public override void Load() => Data = Read(out _lastUpdated);

    /// <summary>保存した直後に読み直す（このプロセス）。</summary>
    public void Reload()
    {
        Data = Read(out _lastUpdated);
        OnReload();
    }

    private void ReloadIfChanged()
    {
        try
        {
            using var db = Db();
            var latest = db.AppSettings.AsNoTracking().Select(s => (DateTimeOffset?)s.UpdatedAt).ToList().Max();
            var count = db.AppSettings.Count();
            if (latest != _lastUpdated || count != Data.Count) Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or System.Data.Common.DbException)
        {
            // DB に一時的に届かなくても、前の値のまま動き続ける
        }
    }

    private Dictionary<string, string?> Read(out DateTimeOffset? lastUpdated)
    {
        using var db = Db();
        var rows = db.AppSettings.AsNoTracking().ToList();
        var protector = _services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        lastUpdated = rows.Count == 0 ? null : rows.Max(r => r.UpdatedAt);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                data[row.Key] = row.IsSecret ? protector.Unprotect(row.Value) : row.Value;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // 暗号鍵が変わって読めない値は使わない（画面で入れ直してもらう）
            }
        }
        return data;
    }

    private ReachForgeDbContext Db() =>
        new(_services.GetRequiredService<DbContextOptions<ReachForgeDbContext>>(), new MutableTenantContext { IsSystem = true, UserName = "settings" },
            null, _services.GetRequiredService<TimeProvider>());

    public void Dispose() => _timer.Dispose();
}

/// <summary>保存したプロセスで設定をすぐに読み直すための窓口。</summary>
public sealed class AppSettingsReloader
{
    private DbSettingsConfigurationProvider? _provider;

    internal void Register(DbSettingsConfigurationProvider provider) => _provider = provider;

    public void Reload() => _provider?.Reload();
}

public static class DbSettingsConfigurationExtensions
{
    /// <summary>
    /// 画面で保存した設定を読み込む（DB の初期化の後に呼ぶ）。後から追加した設定元は既存の設定元より優先され、
    /// 追加した時点で IOptionsMonitor に変更が通知される。
    /// </summary>
    public static void AddDbSettings(this IConfigurationManager configuration, IServiceProvider services) =>
        configuration.Sources.Add(new DbSettingsConfigurationSource(services));
}
