using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

/// <summary>AI 生成時に必ず注入するブランド知識（RF-DES-001 6.2 (2)）。ワークスペースと 1:1。</summary>
public sealed class BrandProfile : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string BrandName { get; set; }

    /// <summary>業種。規制表現辞書（薬機法など）の選択に使う。</summary>
    public string Industry { get; set; } = "";

    public BrandTone Tone { get; set; } = new();
    public List<Persona> Personas { get; set; } = [];
    public List<string> NgWords { get; set; } = [];
    public List<string> MustPhrases { get; set; } = [];
    public List<string> PreferredHashtags { get; set; } = [];
    public string? WebsiteUrl { get; set; }

    /// <summary>版数。変更のたびに増やし、生成記録に使用版を残す（F-02）。</summary>
    public int Version { get; set; } = 1;
}

/// <summary>口調の設定。</summary>
public sealed class BrandTone
{
    /// <summary>0=とても丁寧 〜 100=とてもくだけた。</summary>
    public int Casualness { get; set; } = 40;

    public string FirstPerson { get; set; } = "私たち";

    /// <summary>0=使わない 〜 3=多め。</summary>
    public int EmojiLevel { get; set; } = 1;

    public string? EndingRule { get; set; }
}

public sealed class Persona
{
    public string Name { get; set; } = "";
    public string AgeRange { get; set; } = "";
    public string Interests { get; set; } = "";
    public string Pains { get; set; } = "";
}
