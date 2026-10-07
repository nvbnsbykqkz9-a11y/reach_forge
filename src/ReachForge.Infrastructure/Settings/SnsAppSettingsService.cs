using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Settings;

/// <summary>SNS アプリの設定項目（設定のキーと画面の表示）。</summary>
public sealed record SnsAppField(string Key, string Label, bool Secret = false, bool IsSwitch = false, string? Help = null);

/// <summary>SNS ごとのアプリ（Facebook と Instagram は同じ Meta のアプリ）。</summary>
public sealed record SnsApp(string Id, string Name, IReadOnlyList<SocialPlatform> Platforms, string DeveloperUrl,
    IReadOnlyList<SnsAppField> Fields, string Guide);

public enum SettingSource
{
    /// <summary>未設定。</summary>
    None,

    /// <summary>設定ファイル・環境変数（画面では変えられない値として表示する）。</summary>
    File,

    /// <summary>画面で設定した値。</summary>
    Screen,
}

/// <param name="Value">シークレットでない項目の現在の値（シークレットは画面に出さない）。</param>
public sealed record SnsFieldStatus(SnsAppField Field, SettingSource Source, string? Value);

/// <summary>
/// SNS アプリの ID・シークレットを画面から設定する（運用者・Windows 版のオーナー）。値は AppSettings 表に保存し、
/// シークレットは Data Protection で暗号化する。保存するとすぐに読み直して、連携・投稿に使われる。
/// シークレットは画面に返さない（入力欄が空なら変更しない）。変更は監査ログに残す（値は残さない）。
/// </summary>
public sealed class SnsAppSettingsService(DbContextOptions<ReachForgeDbContext> dbOptions, IDataProtectionProvider protection,
    IConfiguration configuration, AppSettingsReloader reloader, TimeProvider clock)
{
    public const string UseMockKey = "Social:UseMock";

    public static readonly IReadOnlyList<SnsApp> Apps =
    [
        new("x", "X", [SocialPlatform.X], "https://developer.x.com/en/portal/dashboard",
            [new("Social:X:ClientId", "Client ID"), new("Social:X:ClientSecret", "Client Secret", Secret: true)],
            "Developer Portal でアプリを作成し、User authentication settings で OAuth 2.0（Web App）を有効にして、コールバック URL を登録します。"),
        new("meta", "Facebook・Instagram（Meta）", [SocialPlatform.Facebook, SocialPlatform.Instagram], "https://developers.facebook.com/apps/",
            [new("Social:Meta:AppId", "アプリ ID"), new("Social:Meta:AppSecret", "app secret", Secret: true),
             new("Social:Meta:WebhookVerifyToken", "Webhook の確認トークン（任意）", Secret: true, Help: "受信箱の Webhook を使う場合だけ")],
            "Meta for Developers で「ビジネス」タイプのアプリを作成し、Facebook ログイン for Business を追加して、有効な OAuth リダイレクト URI にコールバック URL を登録します。"),
        new("threads", "Threads", [SocialPlatform.Threads], "https://developers.facebook.com/apps/",
            [new("Social:Threads:AppId", "Threads アプリ ID"), new("Social:Threads:AppSecret", "Threads app secret", Secret: true)],
            "Meta for Developers で Threads API のユースケースを追加し、リダイレクト URI にコールバック URL を登録します。"),
        new("tiktok", "TikTok", [SocialPlatform.TikTok], "https://developers.tiktok.com/apps/",
            [new("Social:TikTok:ClientKey", "Client key"), new("Social:TikTok:ClientSecret", "Client secret", Secret: true),
             new("Social:TikTok:Audited", "審査（Content Posting API の audit）を通過した", IsSwitch: true, Help: "通過するまでは投稿が「自分のみ」になります"),
             new("Social:TikTok:Desktop", "デスクトップアプリとして登録した", IsSwitch: true, Help: "Windows 版でデスクトップ用に登録した場合")],
            "TikTok for Developers でアプリを作成し、Login Kit と Content Posting API を追加して、リダイレクト URI にコールバック URL を登録します。"),
        new("youtube", "YouTube（Google）", [SocialPlatform.YouTube], "https://console.cloud.google.com/apis/credentials",
            [new("Social:YouTube:ClientId", "クライアント ID"), new("Social:YouTube:ClientSecret", "クライアント シークレット", Secret: true)],
            "Google Cloud で YouTube Data API v3 を有効にし、OAuth クライアント ID（ウェブ アプリケーション）を作成して、承認済みのリダイレクト URI にコールバック URL を登録します。"),
    ];

    private static readonly HashSet<string> s_keys =
        new(Apps.SelectMany(a => a.Fields).Select(f => f.Key).Append(UseMockKey), StringComparer.OrdinalIgnoreCase);

    private ReachForgeDbContext Db(string user) =>
        new(dbOptions, new MutableTenantContext { IsSystem = true, UserName = user }, null, clock);

    private IDataProtector Protector => protection.CreateProtector(DbSettingsConfigurationProvider.Purpose);

    /// <summary>各項目の状態（画面で設定済み／設定ファイル／未設定）。</summary>
    public async Task<IReadOnlyDictionary<string, SnsFieldStatus>> StatusAsync(CancellationToken ct)
    {
        await using var db = Db("settings");
        var screen = (await db.AppSettings.AsNoTracking().Select(s => s.Key).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Apps.SelectMany(a => a.Fields).ToDictionary(f => f.Key, f =>
        {
            var value = configuration[f.Key];
            var source = screen.Contains(f.Key) ? SettingSource.Screen
                : string.IsNullOrWhiteSpace(value) ? SettingSource.None : SettingSource.File;
            return new SnsFieldStatus(f, source, f.Secret ? null : value);
        }, StringComparer.OrdinalIgnoreCase);
    }

    public bool UseMock => configuration.GetValue(UseMockKey, false);

    /// <summary>
    /// 保存する。シークレットは空なら変更しない。シークレットでない項目は空にすると画面での設定を消す（設定ファイルの値に戻る）。
    /// </summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> values, string user, CancellationToken ct)
    {
        foreach (var key in values.Keys.Where(k => !s_keys.Contains(k)))
        {
            throw new DomainException(ErrorCodes.Validation, $"この項目は設定できません（{key}）。");
        }
        var fields = Apps.SelectMany(a => a.Fields).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        await using var db = Db(user);
        var existing = await db.AppSettings.Where(s => values.Keys.Contains(s.Key)).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var changed = new List<string>();
        foreach (var (key, raw) in values)
        {
            var value = raw?.Trim();
            var secret = fields.TryGetValue(key, out var f) && f.Secret;
            var row = existing.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(value))
            {
                if (secret || row is null) continue; // シークレットは空なら変えない
                db.AppSettings.Remove(row);
                changed.Add(key);
                continue;
            }
            if (value.Length > 1000) throw new DomainException(ErrorCodes.Validation, $"{f?.Label ?? key} が長すぎます。");
            if (value.Any(char.IsControl)) throw new DomainException(ErrorCodes.Validation, $"{f?.Label ?? key} に使えない文字が含まれています。");
            var stored = secret ? Protector.Protect(value) : value;
            if (row is null)
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = stored, IsSecret = secret, UpdatedBy = user });
            }
            else
            {
                if (!secret && row.Value == stored) continue;
                row.Value = stored;
                row.IsSecret = secret;
                row.UpdatedBy = user;
                row.UpdatedAt = now;
            }
            changed.Add(key);
        }
        if (changed.Count == 0) return;
        Audit(db, user, "settings.updated", string.Join(",", changed));
        await db.SaveChangesAsync(ct);
        reloader.Reload();
    }

    /// <summary>SNS アプリの画面での設定をすべて消す（設定ファイルの値があればそれに戻る）。</summary>
    public async Task ClearAsync(SnsApp app, string user, CancellationToken ct)
    {
        var keys = app.Fields.Select(f => f.Key).ToList();
        await using var db = Db(user);
        var rows = await db.AppSettings.Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        if (rows.Count == 0) return;
        db.AppSettings.RemoveRange(rows);
        Audit(db, user, "settings.cleared", app.Id);
        await db.SaveChangesAsync(ct);
        reloader.Reload();
    }

    private static void Audit(ReachForgeDbContext db, string user, string action, string detail) =>
        db.AuditLogs.Add(new AuditLog { Actor = user, Action = action, TargetType = nameof(AppSetting), Detail = detail });
}
