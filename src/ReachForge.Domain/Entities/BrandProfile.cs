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

    /// <summary>ブランドカラー（HEX）。画像生成の色指定・余白の背景色に使う。</summary>
    public List<string> BrandColors { get; set; } = [];

    /// <summary>ロゴ画像（透過 PNG 推奨）。生成画像への合成は画像処理で正確に行う（F-04-5）。</summary>
    public Guid? LogoAssetId { get; set; }

    /// <summary>反応が良かった投稿の例（A/B テストの勝ちパターンなど）。有効なものだけ生成時のお手本（Few-shot）に使う。</summary>
    public List<FewShotExample> FewShotExamples { get; set; } = [];

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

/// <summary>生成のお手本にする投稿例（F-11-4）。</summary>
public sealed class FewShotExample
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Text { get; set; } = "";

    /// <summary>なぜ良かったか（例：「冒頭を問いかけにしたBが 1.4倍」）。</summary>
    public string Reason { get; set; } = "";
    public Guid? SourceAbTestId { get; set; }

    /// <summary>担当者が確認して「お手本に使う」にしたもの。</summary>
    public bool Enabled { get; set; }
}
