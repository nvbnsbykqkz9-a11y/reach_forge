using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class LpStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbTests");

            migrationBuilder.DropTable(
                name: "AdAccounts");

            migrationBuilder.DropTable(
                name: "AdCampaigns");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApprovalActions");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "ChannelMetrics");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "ChannelSecrets");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "InboxAlerts");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "KnowledgeEntries");

            migrationBuilder.DropTable(
                name: "MasterPosts");

            migrationBuilder.DropTable(
                name: "PostMetricRollups");

            migrationBuilder.DropTable(
                name: "PostMetrics");

            migrationBuilder.DropTable(
                name: "PostVariants");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "TrendIdeas");

            migrationBuilder.DropColumn(
                name: "ApprovalSteps",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Inbox",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Reports",
                table: "Workspaces");

            migrationBuilder.CreateTable(
                name: "LpProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Platforms = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    SourceImageAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    Outputs = table.Column<string>(type: "TEXT", nullable: false),
                    VideoJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreditsUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LpProjects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LpProjects_TenantId",
                table: "LpProjects",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LpProjects_WorkspaceId",
                table: "LpProjects",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LpProjects");

            migrationBuilder.AddColumn<int>(
                name: "ApprovalSteps",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Inbox",
                table: "Workspaces",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reports",
                table: "Workspaces",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AbTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    EvaluatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Mode = table.Column<short>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PValue = table.Column<double>(type: "REAL", nullable: true),
                    RateA = table.Column<double>(type: "REAL", nullable: true),
                    RateB = table.Column<double>(type: "REAL", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Variable = table.Column<short>(type: "INTEGER", nullable: false),
                    VariantAId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VariantBId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Verdict = table.Column<string>(type: "TEXT", nullable: true),
                    WinnerRegistered = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbTests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CredentialSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Extra = table.Column<string>(type: "TEXT", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Network = table.Column<short>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Creative = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    DailyBudget = table.Column<double>(type: "REAL", precision: 18, scale: 2, nullable: false),
                    EndAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalIds = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Network = table.Column<short>(type: "INTEGER", nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Results = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    SubmittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Targeting = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdCampaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Role = table.Column<short>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Decision = table.Column<short>(type: "INTEGER", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Budget = table.Column<double>(type: "REAL", nullable: true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    IsAdvertisement = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kpi = table.Column<short>(type: "INTEGER", nullable: false),
                    KpiTarget = table.Column<double>(type: "REAL", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    Platforms = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Followers = table.Column<long>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CredentialSecretRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    InboxSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Protected = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSecrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaselineNegativeRatio = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    HandledBy = table.Column<string>(type: "TEXT", nullable: true),
                    NegativeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RecentNegativeRatio = table.Column<double>(type: "REAL", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AiLabels = table.Column<string>(type: "TEXT", nullable: true),
                    AssignedTo = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: false),
                    AutoHandled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HumanCorrected = table.Column<bool>(type: "INTEGER", nullable: false),
                    InReplyToExternalPostId = table.Column<string>(type: "TEXT", nullable: true),
                    Intent = table.Column<short>(type: "INTEGER", nullable: false),
                    IsClassified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RepliedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RepliedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyText = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Sensitive = table.Column<short>(type: "INTEGER", nullable: false),
                    Sentiment = table.Column<short>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    TypingAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TypingBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Urgency = table.Column<short>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllowAutoReply = table.Column<bool>(type: "INTEGER", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MasterPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CoreMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Cta = table.Column<string>(type: "TEXT", nullable: false),
                    Hashtags = table.Column<string>(type: "TEXT", nullable: false),
                    IsAiEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    MediaAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    ProductIds = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PostMetricRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Comments = table.Column<int>(type: "INTEGER", nullable: false),
                    Conversions = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Follows = table.Column<int>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Likes = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkClicks = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<int>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Saves = table.Column<int>(type: "INTEGER", nullable: false),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false),
                    Snapshots = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMetricRollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PostMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Comments = table.Column<int>(type: "INTEGER", nullable: false),
                    Conversions = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Follows = table.Column<int>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    Likes = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkClicks = table.Column<int>(type: "INTEGER", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<int>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Saves = table.Column<int>(type: "INTEGER", nullable: false),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PostVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AbGroup = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    AspectMethod = table.Column<short>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalPostId = table.Column<string>(type: "TEXT", nullable: true),
                    GuardrailFindings = table.Column<string>(type: "TEXT", nullable: false),
                    GuardrailLevel = table.Column<short>(type: "INTEGER", nullable: false),
                    Hashtags = table.Column<string>(type: "TEXT", nullable: false),
                    HoldReason = table.Column<string>(type: "TEXT", nullable: true),
                    IsAiEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    MasterPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    PlatformOptions = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedPublishAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    UrlCostAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostVariants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AiJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    InsightJson = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    PdfPath = table.Column<string>(type: "TEXT", nullable: true),
                    PeriodFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodTo = table.Column<long>(type: "INTEGER", nullable: false),
                    RejectedClaims = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrendIdeas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Angles = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Relevance = table.Column<double>(type: "REAL", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<short>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrendIdeas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbTests_TenantId",
                table: "AbTests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AbTests_WorkspaceId_Status",
                table: "AbTests",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AdAccounts_TenantId",
                table: "AdAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdAccounts_WorkspaceId_Network_ExternalAccountId",
                table: "AdAccounts",
                columns: new[] { "WorkspaceId", "Network", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_Status",
                table: "AdCampaigns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_TenantId",
                table: "AdCampaigns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdCampaigns_WorkspaceId_Platform",
                table: "AdCampaigns",
                columns: new[] { "WorkspaceId", "Platform" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_SecretHash",
                table: "ApiKeys",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalActions_TenantId",
                table: "ApprovalActions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId",
                table: "Campaigns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_WorkspaceId_Code",
                table: "Campaigns",
                columns: new[] { "WorkspaceId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMetrics_ChannelId_Date",
                table: "ChannelMetrics",
                columns: new[] { "ChannelId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMetrics_TenantId",
                table: "ChannelMetrics",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TenantId",
                table: "Channels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TenantId_Platform_ExternalAccountId",
                table: "Channels",
                columns: new[] { "TenantId", "Platform", "ExternalAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_WorkspaceId",
                table: "Channels",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSecrets_ChannelId",
                table: "ChannelSecrets",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSecrets_TenantId",
                table: "ChannelSecrets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_CreatedAt",
                table: "IdempotencyRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Scope_Key",
                table: "IdempotencyRecords",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_TenantId",
                table: "IdempotencyRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxAlerts_TenantId",
                table: "InboxAlerts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxAlerts_WorkspaceId_Status",
                table: "InboxAlerts",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ChannelId_ExternalId",
                table: "InboxMessages",
                columns: new[] { "ChannelId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_TenantId",
                table: "InboxMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_WorkspaceId_Status_ReceivedAt",
                table: "InboxMessages",
                columns: new[] { "WorkspaceId", "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_TenantId",
                table: "KnowledgeEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_WorkspaceId",
                table: "KnowledgeEntries",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterPosts_TenantId",
                table: "MasterPosts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMetricRollups_PostVariantId_Month",
                table: "PostMetricRollups",
                columns: new[] { "PostVariantId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostMetricRollups_TenantId",
                table: "PostMetricRollups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMetrics_CapturedAt",
                table: "PostMetrics",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PostMetrics_PostVariantId_CapturedAt",
                table: "PostMetrics",
                columns: new[] { "PostVariantId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostMetrics_TenantId",
                table: "PostMetrics",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PostVariants_MasterPostId",
                table: "PostVariants",
                column: "MasterPostId");

            migrationBuilder.CreateIndex(
                name: "IX_PostVariants_Status_NextAttemptAt",
                table: "PostVariants",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostVariants_TenantId",
                table: "PostVariants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PostVariants_WorkspaceId_Status",
                table: "PostVariants",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TenantId",
                table: "Reports",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_WorkspaceId_CreatedAt",
                table: "Reports",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrendIdeas_TenantId",
                table: "TrendIdeas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendIdeas_WorkspaceId_Status_GeneratedOn",
                table: "TrendIdeas",
                columns: new[] { "WorkspaceId", "Status", "GeneratedOn" });
        }
    }
}
