using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReachForge.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AbTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Variable = table.Column<short>(type: "INTEGER", nullable: false),
                    Mode = table.Column<short>(type: "INTEGER", nullable: false),
                    VariantAId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VariantBId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", nullable: true),
                    RateA = table.Column<double>(type: "REAL", nullable: true),
                    RateB = table.Column<double>(type: "REAL", nullable: true),
                    PValue = table.Column<double>(type: "REAL", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    EvaluatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    WinnerRegistered = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbTests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskType = table.Column<short>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    PromptKey = table.Column<string>(type: "TEXT", nullable: false),
                    PromptVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BrandProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    InputSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Credits = table.Column<int>(type: "INTEGER", nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    FallbackUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGenerations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskType = table.Column<short>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    Stage = table.Column<short>(type: "INTEGER", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    CreditsHeld = table.Column<int>(type: "INTEGER", nullable: false),
                    CreditsCharged = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskType = table.Column<short>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Images = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CostUsd = table.Column<double>(type: "REAL", nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    FallbackUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Role = table.Column<short>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Decision = table.Column<short>(type: "INTEGER", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    LastWorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<long>(type: "INTEGER", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrandName = table.Column<string>(type: "TEXT", nullable: false),
                    Industry = table.Column<string>(type: "TEXT", nullable: false),
                    Tone = table.Column<string>(type: "TEXT", nullable: false),
                    Personas = table.Column<string>(type: "TEXT", nullable: false),
                    NgWords = table.Column<string>(type: "TEXT", nullable: false),
                    MustPhrases = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredHashtags = table.Column<string>(type: "TEXT", nullable: false),
                    WebsiteUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BrandColors = table.Column<string>(type: "TEXT", nullable: false),
                    LogoAssetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FewShotExamples = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    IsAdvertisement = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Budget = table.Column<double>(type: "REAL", nullable: true),
                    Kpi = table.Column<short>(type: "INTEGER", nullable: false),
                    KpiTarget = table.Column<double>(type: "REAL", nullable: true),
                    Platforms = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Followers = table.Column<long>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CredentialSecretRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    InboxSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    Protected = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSecrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonthlyGrant = table.Column<int>(type: "INTEGER", nullable: false),
                    Balance = table.Column<int>(type: "INTEGER", nullable: false),
                    Held = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedThisPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    NegativeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecentNegativeRatio = table.Column<double>(type: "REAL", nullable: false),
                    BaselineNegativeRatio = table.Column<double>(type: "REAL", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    HandledBy = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    InReplyToExternalPostId = table.Column<string>(type: "TEXT", nullable: true),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sentiment = table.Column<short>(type: "INTEGER", nullable: false),
                    Intent = table.Column<short>(type: "INTEGER", nullable: false),
                    Urgency = table.Column<short>(type: "INTEGER", nullable: false),
                    Sensitive = table.Column<short>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    IsClassified = table.Column<bool>(type: "INTEGER", nullable: false),
                    HumanCorrected = table.Column<bool>(type: "INTEGER", nullable: false),
                    AiLabels = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    AssignedTo = table.Column<string>(type: "TEXT", nullable: true),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplyText = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    RepliedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RepliedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AutoHandled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TypingBy = table.Column<string>(type: "TEXT", nullable: true),
                    TypingAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<short>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    InvitedBy = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    AllowAutoReply = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Objective = table.Column<short>(type: "INTEGER", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    CoreMessage = table.Column<string>(type: "TEXT", nullable: false),
                    Cta = table.Column<string>(type: "TEXT", nullable: false),
                    Hashtags = table.Column<string>(type: "TEXT", nullable: false),
                    ProductIds = table.Column<string>(type: "TEXT", nullable: false),
                    MediaAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsAiEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    Source = table.Column<short>(type: "INTEGER", nullable: false),
                    BlobPath = table.Column<string>(type: "TEXT", nullable: false),
                    Mime = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentAssetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DerivationKey = table.Column<string>(type: "TEXT", nullable: true),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsAiLabeled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: true),
                    C2paManifest = table.Column<string>(type: "TEXT", nullable: true),
                    C2paSigned = table.Column<bool>(type: "INTEGER", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: true),
                    AltTextIsAi = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubtitlesSrt = table.Column<string>(type: "TEXT", nullable: true),
                    SafetyResult = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PostMetricRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Snapshots = table.Column<int>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: false),
                    Likes = table.Column<int>(type: "INTEGER", nullable: false),
                    Comments = table.Column<int>(type: "INTEGER", nullable: false),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false),
                    Saves = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkClicks = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<int>(type: "INTEGER", nullable: false),
                    Follows = table.Column<int>(type: "INTEGER", nullable: false),
                    Conversions = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    PostVariantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PostedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Impressions = table.Column<long>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: false),
                    Likes = table.Column<int>(type: "INTEGER", nullable: false),
                    Comments = table.Column<int>(type: "INTEGER", nullable: false),
                    Shares = table.Column<int>(type: "INTEGER", nullable: false),
                    Saves = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkClicks = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileVisits = table.Column<int>(type: "INTEGER", nullable: false),
                    Follows = table.Column<int>(type: "INTEGER", nullable: false),
                    Conversions = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
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
                    MasterPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<short>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Hashtags = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    MediaAssetIds = table.Column<string>(type: "TEXT", nullable: false),
                    AspectMethod = table.Column<short>(type: "INTEGER", nullable: false),
                    PlatformOptions = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestedPublishAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExternalPostId = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    AbGroup = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    AiGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsAiEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    UrlCostAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuardrailFindings = table.Column<string>(type: "TEXT", nullable: false),
                    GuardrailLevel = table.Column<short>(type: "INTEGER", nullable: false),
                    ApprovedContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", nullable: true),
                    HoldReason = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostVariants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<double>(type: "REAL", nullable: true),
                    AvailableFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AvailableUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    RolloutPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Evaluation = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<short>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodTo = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    InsightJson = table.Column<string>(type: "TEXT", nullable: false),
                    RejectedClaims = table.Column<int>(type: "INTEGER", nullable: false),
                    PdfPath = table.Column<string>(type: "TEXT", nullable: true),
                    AiJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    MaxChannels = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishingPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrendIdeas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<short>(type: "INTEGER", nullable: false),
                    RecommendedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    Relevance = table.Column<double>(type: "REAL", nullable: false),
                    Angles = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<short>(type: "INTEGER", nullable: false),
                    GeneratedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrendIdeas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<short>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BrandColor = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    Reports = table.Column<string>(type: "TEXT", nullable: false),
                    Inbox = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "IX_AiGenerations_TenantId",
                table: "AiGenerations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiGenerations_TenantId_CreatedAt",
                table: "AiGenerations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_Status_CreatedAt",
                table: "AiJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiJobs_TenantId",
                table: "AiJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_TenantId",
                table: "AiUsageLogs",
                column: "TenantId");

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
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId",
                table: "AuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BrandProfiles_TenantId",
                table: "BrandProfiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BrandProfiles_WorkspaceId",
                table: "BrandProfiles",
                column: "WorkspaceId",
                unique: true);

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
                name: "IX_CreditAccounts_TenantId",
                table: "CreditAccounts",
                column: "TenantId",
                unique: true);

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
                name: "IX_Invitations_TenantId",
                table: "Invitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

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
                name: "IX_MediaAssets_ParentAssetId",
                table: "MediaAssets",
                column: "ParentAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_TenantId",
                table: "MediaAssets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_WorkspaceId_DerivationKey",
                table: "MediaAssets",
                columns: new[] { "WorkspaceId", "DerivationKey" });

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
                name: "IX_Products_TenantId",
                table: "Products",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplates_Key_Version",
                table: "PromptTemplates",
                columns: new[] { "Key", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplates_TenantId",
                table: "PromptTemplates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TenantId",
                table: "Reports",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_WorkspaceId_CreatedAt",
                table: "Reports",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantId",
                table: "Tenants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendIdeas_TenantId",
                table: "TrendIdeas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TrendIdeas_WorkspaceId_Status_GeneratedOn",
                table: "TrendIdeas",
                columns: new[] { "WorkspaceId", "Status", "GeneratedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_TenantId",
                table: "WorkspaceMembers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_WorkspaceId_UserId",
                table: "WorkspaceMembers",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TenantId",
                table: "Workspaces",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbTests");

            migrationBuilder.DropTable(
                name: "AiGenerations");

            migrationBuilder.DropTable(
                name: "AiJobs");

            migrationBuilder.DropTable(
                name: "AiUsageLogs");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApprovalActions");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BrandProfiles");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "ChannelMetrics");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "ChannelSecrets");

            migrationBuilder.DropTable(
                name: "CreditAccounts");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "InboxAlerts");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "KnowledgeEntries");

            migrationBuilder.DropTable(
                name: "MasterPosts");

            migrationBuilder.DropTable(
                name: "MediaAssets");

            migrationBuilder.DropTable(
                name: "PostMetricRollups");

            migrationBuilder.DropTable(
                name: "PostMetrics");

            migrationBuilder.DropTable(
                name: "PostVariants");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "PromptTemplates");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "TrendIdeas");

            migrationBuilder.DropTable(
                name: "WorkspaceMembers");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
