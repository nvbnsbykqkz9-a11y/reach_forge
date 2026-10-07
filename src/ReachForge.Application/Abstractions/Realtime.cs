using System.Text.Json.Serialization;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Abstractions;

/// <summary>画面へすぐに知らせる出来事（RF-DES-001 3.3：生成進捗のリアルタイム通知に SignalR を使用）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JobProgressEvent), "job")]
public abstract record RealtimeEvent(Guid TenantId, Guid WorkspaceId);

/// <summary>AI ジョブの進み具合（キュー待ち → 生成中 → 検査中 → 完了）。</summary>
public sealed record JobProgressEvent(Guid TenantId, Guid WorkspaceId, Guid JobId, AiTaskType TaskType, AiJobStatus Status, AiJobStage Stage)
    : RealtimeEvent(TenantId, WorkspaceId);

/// <summary>
/// 出来事を知らせる。保存（SaveChanges）が成功した後に呼ばれ、届かなくても処理は失敗させない
/// （画面は一定間隔の再読み込みでも追いつく）。
/// </summary>
public interface IRealtimeNotifier
{
    void Publish(RealtimeEvent e);
}
