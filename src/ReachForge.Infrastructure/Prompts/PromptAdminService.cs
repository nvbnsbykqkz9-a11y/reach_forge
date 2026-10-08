using Microsoft.EntityFrameworkCore;
using ReachForge.AI.Prompts;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Infrastructure.Prompts;

public sealed record PromptKeySummary(string Key, string Variables, PromptTemplate? Active, PromptTemplate? Candidate, int Versions);

/// <summary>
/// プロンプトの版管理（運用管理画面 SCR-16）。版は作ったら本文を変えない（下書きだけ編集可）。
/// 公開すると前の公開版は保管になり、候補版は割合を指定して一部のテナントから段階適用する。
/// 呼び出し側（画面）で運用者であることを確認すること。
/// </summary>
public sealed class PromptAdminService(DbContextOptions<ReachForgeDbContext> dbOptions, DbPromptStore store, TimeProvider clock)
{
    private ReachForgeDbContext Db(string user) =>
        new(dbOptions, new MutableTenantContext { IsSystem = true, UserName = user }, null, clock);

    /// <summary>
    /// DB に版のないキーへ、コードの既定テンプレートを v1（公開）として登録する。
    /// 運用者が手を加えていないキー（版が自動登録の v1 だけ）は、コードの既定テンプレートが変わっていれば v1 を新しい内容にそろえる
    /// （アプリの更新で指示や出力の項目が増えたとき、古い指示のまま動かないように）。
    /// </summary>
    public async Task<int> EnsureSeededAsync(CancellationToken ct)
    {
        await using var db = Db("system");
        var existing = (await db.PromptTemplates.Select(p => p.Key).Distinct().ToListAsync(ct)).ToHashSet();
        var added = 0;
        var untouched = (await db.PromptTemplates.ToListAsync(ct))
            .GroupBy(p => p.Key)
            .Where(g => g.Count() == 1 && g.First() is { Version: PromptCatalog.DefaultVersion, CreatedBy: "system" })
            .Select(g => g.First());
        foreach (var template in untouched)
        {
            if (PromptLibrary.Defaults.TryGetValue(template.Key, out var latest) && template.Body != latest)
            {
                template.Body = latest;
                added++;
            }
        }
        foreach (var (key, body) in PromptLibrary.Defaults.Where(d => !existing.Contains(d.Key)))
        {
            db.PromptTemplates.Add(new PromptTemplate
            {
                Key = key, Version = PromptCatalog.DefaultVersion, Body = body, Status = PromptStatus.Active,
                Note = "初期版", CreatedBy = "system",
            });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task<IReadOnlyList<PromptKeySummary>> ListAsync(CancellationToken ct)
    {
        await using var db = Db("ops");
        var rows = await db.PromptTemplates.AsNoTracking().ToListAsync(ct);
        return PromptLibrary.Defaults.Keys.Order().Select(key =>
        {
            var versions = rows.Where(r => r.Key == key).ToList();
            return new PromptKeySummary(key, PromptLibrary.Variables[key],
                versions.Where(v => v.Status == PromptStatus.Active).MaxBy(v => v.Version),
                versions.Where(v => v.Status == PromptStatus.Candidate).MaxBy(v => v.Version),
                versions.Count);
        }).ToList();
    }

    public async Task<IReadOnlyList<PromptTemplate>> VersionsAsync(string key, CancellationToken ct)
    {
        await using var db = Db("ops");
        return (await db.PromptTemplates.AsNoTracking().Where(p => p.Key == key).ToListAsync(ct))
            .OrderByDescending(p => p.Version).ToList();
    }

    public async Task<PromptTemplate> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = Db("ops");
        return await db.PromptTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
    }

    /// <summary>見本の値で組み立てる（文法・変数名の確認とプレビュー）。安全規約は公開中の版を差し込む。</summary>
    public async Task<string> PreviewAsync(string key, string body, CancellationToken ct)
    {
        Check(key, body);
        var values = PromptLibrary.SampleValues(key);
        if (key != PromptKeys.Safety)
        {
            var safety = await store.ResolveAsync(PromptKeys.Safety, Guid.Empty, ct);
            values["safety"] = PromptCatalog.Render(safety?.Body ?? PromptLibrary.Defaults[PromptKeys.Safety], PromptLibrary.Values()).Trim();
        }
        try
        {
            return PromptCatalog.Render(body, values);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Scriban.Syntax.ScriptRuntimeException)
        {
            throw new DomainException(ErrorCodes.Validation, $"テンプレートを組み立てられません：{ex.Message}");
        }
    }

    public async Task<PromptTemplate> CreateDraftAsync(string key, string body, string note, string user, CancellationToken ct)
    {
        await PreviewAsync(key, body, ct);
        await using var db = Db(user);
        var version = (await db.PromptTemplates.Where(p => p.Key == key).Select(p => (int?)p.Version).MaxAsync(ct) ?? 0) + 1;
        var draft = new PromptTemplate { Key = key, Version = version, Body = body, Note = Trim(note), CreatedBy = user };
        db.PromptTemplates.Add(draft);
        Audit(db, user, "prompt.drafted", draft);
        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<PromptTemplate> UpdateDraftAsync(Guid id, string body, string note, string user, CancellationToken ct)
    {
        await using var db = Db(user);
        var draft = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
        if (draft.Status != PromptStatus.Draft) throw new DomainException(ErrorCodes.Validation, "公開・試験中の版は変更できません。新しい版を作ってください。");
        await PreviewAsync(draft.Key, body, ct);
        draft.Body = body;
        draft.Note = Trim(note);
        draft.Evaluation = null; // 本文が変わったので評価をやり直す
        await db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>全テナントに公開する。前の公開版は保管になる（保管した版を公開し直せば元に戻せる）。</summary>
    public async Task PublishAsync(Guid id, string user, CancellationToken ct)
    {
        await using var db = Db(user);
        var target = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
        foreach (var other in await db.PromptTemplates.Where(p => p.Key == target.Key && p.Id != id
                     && (p.Status == PromptStatus.Active || p.Status == PromptStatus.Candidate)).ToListAsync(ct))
        {
            other.Status = PromptStatus.Archived;
            other.RolloutPercent = 0;
        }
        target.Status = PromptStatus.Active;
        target.RolloutPercent = 0;
        Audit(db, user, "prompt.published", target);
        await db.SaveChangesAsync(ct);
        store.Invalidate(target.Key);
    }

    /// <summary>一部のテナント（1〜99%）で試す。同じキーの別の候補版は下書きに戻す。</summary>
    public async Task StartRolloutAsync(Guid id, int percent, string user, CancellationToken ct)
    {
        if (percent is < 1 or > 99) throw new DomainException(ErrorCodes.Validation, "割合は 1〜99% で指定してください。");
        await using var db = Db(user);
        var target = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
        if (target.Status == PromptStatus.Active) throw new DomainException(ErrorCodes.Validation, "公開中の版です。");
        foreach (var other in await db.PromptTemplates.Where(p => p.Key == target.Key && p.Id != id && p.Status == PromptStatus.Candidate)
                     .ToListAsync(ct))
        {
            other.Status = PromptStatus.Draft;
            other.RolloutPercent = 0;
        }
        target.Status = PromptStatus.Candidate;
        target.RolloutPercent = percent;
        Audit(db, user, "prompt.rollout", target, $"{percent}%");
        await db.SaveChangesAsync(ct);
        store.Invalidate(target.Key);
    }

    public async Task StopRolloutAsync(Guid id, string user, CancellationToken ct)
    {
        await using var db = Db(user);
        var target = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
        if (target.Status != PromptStatus.Candidate) return;
        target.Status = PromptStatus.Draft;
        target.RolloutPercent = 0;
        Audit(db, user, "prompt.rollout_stopped", target);
        await db.SaveChangesAsync(ct);
        store.Invalidate(target.Key);
    }

    public async Task RecordEvaluationAsync(Guid id, PromptEvalResult result, CancellationToken ct)
    {
        await using var db = Db("evals");
        var target = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("プロンプト");
        target.Evaluation = result;
        await db.SaveChangesAsync(ct);
    }

    private static void Check(string key, string body)
    {
        if (!PromptLibrary.Defaults.ContainsKey(key)) throw new DomainException(ErrorCodes.Validation, "不明なプロンプトです。");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException(ErrorCodes.Validation, "本文を入力してください。");
        if (body.Length > PromptTemplate.MaxBodyLength)
        {
            throw new DomainException(ErrorCodes.Validation, $"本文は {PromptTemplate.MaxBodyLength:N0} 字以内にしてください。");
        }
        if (PromptCatalog.Validate(body) is { } error) throw new DomainException(ErrorCodes.Validation, $"テンプレートの書き方に誤りがあります：{error}");
        // 安全規約・ユーザー入力の扱いを外した版は作れない
        if (PromptLibrary.Defaults[key].Contains("{{ safety }}") && !body.Contains("safety", StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.Validation, "このプロンプトには安全規約（{{ safety }}）を必ず入れてください。");
        }
    }

    private static string Trim(string note) => note.Length > 500 ? note[..500] : note.Trim();

    private static void Audit(ReachForgeDbContext db, string user, string action, PromptTemplate p, string? detail = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = Guid.Empty, Actor = user, Action = action, TargetType = nameof(PromptTemplate), TargetId = p.Id,
            Detail = $"{p.Key} v{p.Version}{(detail is null ? "" : $" {detail}")}",
        });
}
