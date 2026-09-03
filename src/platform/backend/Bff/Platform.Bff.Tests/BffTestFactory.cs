using Platform.Shared.Contracts.Dtos;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

public class BffTestFactory : WebApplicationFactory<Program>
{
    // FR-15 BFF テスト: 構成情報 API の集約対象の自己申告をスタブ化し、監査記録を捕捉する。
    public EffectiveCollection StubEffective { get; set; } = EffectiveCollection.Empty;
    public List<(string Action, string Subject, string Outcome)> RecordedAudits { get; } = [];

    // FR-15 (#145): 即時ドリフト検出のアラート発火（IDriftAlertSink）を捕捉する。
    public List<DriftReportDto> AlertedReports { get; } = [];

    // FR-04 BFF テスト: 後段 AiAnalysisService への転送を捕捉・スタブ化する
    public string? LastForwardedAuthorization { get; private set; }

    // FR-07 BFF テスト: 後段が返すステータスコードを差し替え、非 2xx の透過を検証する。
    public HttpStatusCode StubStatusCode { get; set; } = HttpStatusCode.OK;

    public AiAnswerDto StubAnswer { get; set; } = new(
        "集約された回答 [1]",
        [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(),
            "s3://bucket/a.md", 0.92f, "抜粋")],
        "claude-sonnet-4-6", 12, 34);

    // FR-08 BFF テスト: 後段 FeedbackService への転送を捕捉・スタブ化する。
    public string? LastFeedbackForwardedAuthorization { get; private set; }

    // FR-10 BFF テスト: 後段 DashboardService への転送を捕捉・スタブ化する。
    public string? LastDashboardForwardedAuthorization { get; private set; }

    // FR-10 BFF テスト: 後段が返すステータスの差し替え・null 応答の再現（非 2xx 透過・502 分岐の検証用）。
    public HttpStatusCode DashboardStubStatusCode { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode FeedbackStatsStubStatusCode { get; set; } = HttpStatusCode.OK;

    // #948: FeedbackService の実体は `/feedback/stats` に RequireRole(admin, operator) を持つ
    // （#521 / IADR-0158「判断軸は PII の有無ではなく権限で絞るか」）。**スタブが常に成功を返す作りだと、
    // 「BFF が資格情報を渡し忘れた」が緑を通る**——実際に通っていた（#948）。この knob を立てると、
    // スタブは実体と同じく「Authorization が無ければ 401」を返す。既定 false は既存テストの挙動を変えない。
    public bool FeedbackRequiresAuthorization { get; set; }
    public bool DashboardReturnsNullBody { get; set; }

    // FR-10, SC-10, ADR-0071 決定 2 (#1197): 後段が返す検索傾向のしきい値。
    // **既定 3 と違う値にしてある** —— 既定と同じにすると、BFF が透過せず自前の既定を
    // 埋めていても緑を通る（透過の検査にならない）。
    public const int DashboardSearchTermMinCount = 7;

    // FR-10, SC-10, IADR-0343 (#1103): 受け口 `POST /dashboard/events` に届いた利用状況イベント。
    // **発火側が本番コードに 1 本も無かった**ため、届いたことを観測できる場所がここに要る。
    public sealed record RecordedUsageEvent(string? EventType, string? Query, string? Authorization);

    public System.Collections.Concurrent.ConcurrentQueue<RecordedUsageEvent> RecordedUsageEvents { get; } = new();

    // 受け口の応答を差し替える（非 2xx の fail-open の検証用）。既定は実体と同じ 201。
    public HttpStatusCode UsageEventStubStatusCode { get; set; } = HttpStatusCode.Created;

    // 受け口へ到達できない状況を再現する（到達不能の fail-open の検証用）。
    public bool UsageEventStubThrows { get; set; }

    private readonly SemaphoreSlim _usageEventArrived = new(0);

    // 送出は要求の応答経路から外れている（有界の列 ＋ 常駐ドレイン）ため、**届くのを待つ**。
    // 待たずに数えると「まだ届いていない」を「発火していない」と読み違える。
    public async Task<bool> WaitForUsageEventAsync(TimeSpan timeout, CancellationToken ct = default)
        => await _usageEventArrived.WaitAsync(timeout, ct);

    public void ResetUsageEvents()
    {
        RecordedUsageEvents.Clear();
        while (_usageEventArrived.CurrentCount > 0) _usageEventArrived.Wait(0);
        UsageEventStubStatusCode = HttpStatusCode.Created;
        UsageEventStubThrows = false;
    }

    // FR-03/FR-05 BFF テスト（SC-01 横断検索）: ABAC スコープ解決の許可可否と検索結果をスタブ制御する。
    public bool SearchScopeGranted { get; set; } = true;
    // FR-05, FR-06 (#1010): write スコープの許可可否と文書条件（read とは独立に制御する）。
    // 既定は「許可・条件なし」—— 既存テスト（作成 201 等）の挙動を変えないため。
    // 「read しか持たない主体」は WriteScopeGranted=false で再現する。
    public bool WriteScopeGranted { get; set; } = true;
    public List<AttributeFilter> WriteScopeFilters { get; set; } = [];
    // #1010: /authz/scope へ発行された action の列（読み取り経路 read / 書き込み経路 write の観測点）。
    // **テスト間で共有される**（IClassFixture）ため、観測する側が呼ぶ前に Clear() すること。
    public List<string> ScopeActionsRequested { get; } = [];
    // FR-19, IADR-0253 段 3 (#989): read スコープ応答に載せる名前つき分岐（既定 null＝旧形式の応答）。
    public List<AccessScopeBranch>? ScopeBranches { get; set; }
    // FR-03, SC-02, #532: BFF が後段へ渡した並び順（縮退させずそのまま運ぶことを固定するため）。
    public string? LastSearchSortBy { get; private set; }
    // FR-05, FR-17, ADR-0034 (#970): /bff/search が後段（RetrievalService）へ伝播した Authorization。
    // 二段検索の段はこのヘッダを GraphService まで運んでホップごと ABAC を効かせる（方式 A）。
    // **テスト間で共有される**（IClassFixture）ため、観測する側が呼ぶ前に null へ戻すこと。
    public string? LastSearchForwardedAuthorization { get; set; }
    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会。後段が返す候補と、BFF が渡した本文。
    public List<string> StubAttributeValues { get; set; } = ["社内", "規程"];
    // **テスト間で共有される**（IClassFixture）ため、観測する側が呼ぶ前に null へ戻すこと。
    public string? LastAttributeValuesBody { get; set; }
    // AttributeValuesStatusCode を 500/400 に差し替えると、後段の非 2xx 透過を検証できる。
    // **縮退（空配列）で潰さないことを固定するために要る** —— 潰すと運用側が後段の不調に気づけない。
    public HttpStatusCode AttributeValuesStatusCode { get; set; } = HttpStatusCode.OK;

    // FR-09, SC-05, SC-09, #634: タグ辞書（IADR-0152）。**テスト間で共有される**（IClassFixture）ため、
    // 観測する側が呼ぶ前に既定へ戻すこと。
    public List<TagDto> StubTagDictionary { get; set; } = [new(Guid.NewGuid(), "経理", 3)];
    // FR-17, UC-10, #916a: グラフ読み取りの中継。
    //
    // 🔴 **スタブは Authorization の有無で応答を変える。** GraphService は自分で JWT から ABAC を
    // 解決し、利用者を特定できなければ Granted=false へ縮退して 404 を返す型である。
    // その挙動をスタブでも再現しないと、**BFF がヘッダを伝播し忘れても全部 404 で緑のまま**になり、
    // 伝播が効いていることを測れない（陽性対照が成立しない）。
    public string? LastGraphForwardedAuthorization { get; private set; }
    public HttpStatusCode GraphStubStatusCode { get; set; } = HttpStatusCode.OK;
    public string? LastGraphPath { get; private set; }
    public GraphViewDto StubGraphView { get; set; } = new([], [], false);

    // FR-17, #962: 辺の型カタログのスタブ応答。
    public List<EdgeTypeCatalogItemDto> StubEdgeTypeCatalog { get; set; } = [];

    // FR-18, SC-21, #918: AI 提案一覧のスタブ応答。**テスト間で共有される**（IClassFixture）ため、
    // 観測する側が呼ぶ前に既定へ戻すこと。
    public List<AiSuggestionDto> StubAiSuggestions { get; set; } = [];

    // FR-18, SC-03, #450: AI 提案の**承認・却下**（書き込み）。
    //
    // 🔴 **読み取りとは別の knob にする。** 一覧の状態コードを流用すると、承認の 409 を測るために
    // 一覧まで 409 にすることになり、**「承認だけが失敗する」形を再現できない**。
    // 🔴 **本文も差し替えられる。** 409（`invalid_transition`）・400（`unknown_edge_type`）の本文が
    // 画面の文言の根拠であり、**BFF が本文を捨てていないこと**を測るために要る。
    public HttpStatusCode GraphWriteStubStatusCode { get; set; } = HttpStatusCode.OK;
    public string? GraphWriteStubBody { get; set; }
    // 後段不達を再現する（BFF の catch → 502 縮退の検証用）。
    public bool GraphWriteStubThrows { get; set; }
    // 承認・却下の成功応答（**単票**である。一覧と違って配列ではない）。
    public AiSuggestionDto? StubSuggestionWriteResult { get; set; }
    // BFF が後段へ渡したメソッド。**テスト間で共有される**（IClassFixture）ため、観測する側が
    // 呼ぶ前に null へ戻すこと。
    public string? LastGraphMethod { get; private set; }
    // 🔴 BFF が後段へ渡した本文。**却下は本文を送らない**（指紋を公開面へ出さないため）ことの観測点。
    public string? LastGraphBody { get; private set; }

    // FR-16, UC-09, SC-12, #452: McpServer（/mcp-clients*）のスタブ。**テスト間で共有される**
    // （IClassFixture）ため、観測する側が呼ぶ前に既定へ戻すこと。
    //
    // 🔴 **後段の状態コードをそのまま返せることを測るための可変値である。**
    // BFF は透過中継であり、400（属性割当の拒否）・404（不在）・409 を作り替えてはならない。
    public HttpStatusCode McpStubStatusCode { get; set; } = HttpStatusCode.OK;
    public bool McpStubThrows { get; set; }
    public string? LastMcpPath { get; private set; }
    public string? LastMcpMethod { get; private set; }
    public string? LastMcpBody { get; private set; }

    // 🔴 **資格情報が後段へ届いているかの観測点**（陽性対照）。伝播を落とすと後段は自分で
    // 401 を返す型なので、ここを測らないと「全部 401 でも緑」になる。
    public string? LastMcpForwardedAuthorization { get; private set; }

    // FR-22, UC-11, #600: NotificationService（/notifications*）のスタブ。**テスト間で共有される**
    // （IClassFixture）ため、観測する側が呼ぶ前に既定へ戻すこと。
    //
    // 🔴 **後段の状態コードをそのまま返せることを測るための可変値である。** BFF は透過中継であり、
    // 404（存在秘匿。「無い」と「本人のものでない」を区別しない）を作り替えてはならない。
    public HttpStatusCode NotificationStubStatusCode { get; set; } = HttpStatusCode.OK;
    public bool NotificationStubThrows { get; set; }
    public string? LastNotificationPath { get; private set; }
    public string? LastNotificationMethod { get; private set; }

    // 🔴 **資格情報が後段へ届いているかの観測点**（陽性対照）。後段は主体を JWT からしか採らないため、
    // 伝播が切れると全部 401 になる。ここを測らないと「全部 401 でも緑」になる。
    public string? LastNotificationForwardedAuthorization { get; private set; }

    public bool TagDictionaryFetched { get; set; }
    public HttpStatusCode TagDictionaryStatusCode { get; set; } = HttpStatusCode.OK;

    // FR-09, SC-09, #640: 辞書の書き込み（追加・改名・削除）。
    // **後段が返す識別子を固定する** —— BFF は中継するだけなので、応答が素通りすることを検証できる。
    public static readonly Guid StubCreatedTagId = new("11111111-2222-3333-4444-555555555555");
    // 追加・改名の後段応答を差し替える（409 の透過を検証する）。
    public HttpStatusCode TagWriteStatusCode { get; set; } = HttpStatusCode.OK;
    // 改名で再発行した文書数（[[IADR-0153]] 決定 3。画面が「まだ届いていない」と切り分けるために要る）。
    public int StubRenameRepublished { get; set; } = 2;
    // **削除時の使用件数。0 なら 204、1 以上なら 409 ＋ `usageCount`**（SC-09 の確定規則）。
    public int StubDeleteUsageCount { get; set; }
    // FR-05 (SC-03): スコープ解決が返す許可フィルタ。既定は空（＝条件なしで全件許可）。SC-03 の
    // 属性不一致 → 404 秘匿を検証する際に非空へ差し替える。
    public List<AttributeFilter> ScopeFilters { get; set; } = [];
    // FR-03, SC-02, #536: 更新日時（`UpdatedAt`）を持たせる。BFF は型付きで中継するだけなので
    // 実装は変わらないが、契約のメンバーが増えたときに落ちる場所が無いと静かに欠落する。
    public static readonly DateTimeOffset StubSearchUpdatedAt =
        new(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);
    public SearchResponse StubSearchResponse { get; set; } = new(
        [new SearchResultDto(Guid.NewGuid(), Guid.NewGuid(), "経費規程 2025",
            "第3条 …", 0.91f, "s3://bucket/expense.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"],
            StubSearchUpdatedAt)],
        1, 5);

    // FR-06 BFF テスト（SC-03 文書閲覧）: DocumentService の応答をスタブ制御する。
    public static readonly Guid StubDocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public HttpStatusCode DocumentStatusCode { get; set; } = HttpStatusCode.OK;
    // FR-06 BFF テスト（SC-05 文書管理・書き込み）: 書き込み（POST/PUT/PATCH/DELETE）応答のステータス。
    // GET（スコープ確認）とは独立に差し替えられる（検証 400・楽観ロック競合 409 の透過検証用）。
    public HttpStatusCode DocumentWriteStatusCode { get; set; } = HttpStatusCode.OK;
    public DocumentDto StubDocument { get; set; } = new()
    {
        Id = StubDocumentId,
        Title = "経費規程 2025",
        Status = "published",
        MarkdownUri = "storage://bucket/expense.md",
        Version = 3,
        Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags = ["hr"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
    public List<DocumentDto> StubDocumentList { get; set; } =
    [
        new()
        {
            Id = StubDocumentId, Title = "経費規程 2025", Status = "published",
            Version = 3, Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            Tags = ["hr"],
        },
        new()
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Title = "取締役会議事録",
            Status = "published", Version = 1,
            Attributes = new Dictionary<string, string> { ["confidentiality"] = "secret" }, Tags = ["board"],
        },
    ];
    public List<DocumentVersionDto> StubVersions { get; set; } =
    [
        new() { DocumentId = StubDocumentId, Version = 3, Title = "経費規程 2025", Status = "published", ChangeNote = "第3条改定", CreatedAt = DateTimeOffset.UtcNow },
        new() { DocumentId = StubDocumentId, Version = 2, Title = "経費規程 2025", Status = "published", CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) },
    ];

    // ── FR-19, FR-20, UC-11, SC-19, SC-20, #451: 個人資料（private-note）と同期端末 ──────
    //
    // 🔴 **このスタブは実体（DocumentService）と同じく「主体をトークンから採り、台帳の所有者で絞る」**。
    // スタブが常に成功を返す作りだと「**BFF が資格情報を渡し忘れた**」が緑を通る（#948 の再発）。
    // 主体は転送された `Authorization: Bearer <subject>` の値そのものとする（テスト用の単純化）。
    // 転送が無ければ **401**（実体は `RequireAuthorization` ＋ `SubjectOf` で同じ結果になる）。
    public const string NoteOwner = "alice";
    public const string OtherNoteOwner = "bob";
    public static readonly Guid StubPrivateNoteId = Guid.Parse("19191919-1919-1919-1919-191919191919");
    // **他人（bob）の資料・端末。到達できないこと（404）を測るための対照物である。**
    public static readonly Guid OtherOwnerNoteId = Guid.Parse("20202020-2020-2020-2020-202020202020");
    public static readonly Guid StubSyncDeviceId = Guid.Parse("d0d0d0d0-d0d0-d0d0-d0d0-d0d0d0d0d0d0");
    public static readonly Guid OtherOwnerDeviceId = Guid.Parse("d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1");
    // 発行応答にだけ現れる平文トークン（一覧に載らないことを測る）。
    public const string StubSyncTokenPlaintext = "sync-token-plaintext-once";
    // BFF が後段へ渡した Authorization の観測点。**テスト間で共有される**（IClassFixture）ため、
    // 観測する側が呼ぶ前に null へ戻すこと。
    public string? LastPrivateNoteForwardedAuthorization { get; set; }
    // ADR-0037 決定 17: 容量 100% の再現。**新規作成だけ**が 507 で拒まれ、本文には SC-19 の
    // 固定文言の根拠（使用量・上限・容量を空ける手段）が入る。BFF が詰め替えないことを測る。
    public bool PrivateNoteQuotaExceeded { get; set; }
    public const string QuotaProblemMarker = "論理削除では容量は空きません";

    // FR-12 BFF テスト（SC-07 変換ジョブ）: ConversionService の応答をスタブ制御する。
    public static readonly Guid StubJobId = Guid.Parse("12121212-1212-1212-1212-121212121212");
    public HttpStatusCode ConversionStatusCode { get; set; } = HttpStatusCode.OK;
    // 後段（ConversionService）不達を再現する（BFF が 502 へ縮退することの検証用）。
    public bool ConversionThrows { get; set; }
    // IADR-0154 決定 4: 409 の本文を BFF が落とさないことの検証用（#640 と同型の回帰）。
    public string? ConversionConflictBody { get; set; }
    // BFF が後段へ渡したパス（?discardCorrections=true が伝わることの観測点）。
    public string? LastConversionPath { get; set; }
    public List<ConversionJobDto> StubJobs { get; set; } =
    [
        new(StubJobId, Guid.NewGuid(), "filesystem", "/docs/a.docx", ConversionJobStatus.Failed,
            "pandoc がタイムアウトしました。", null, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new(Guid.Parse("34343434-3434-3434-3434-343434343434"), Guid.NewGuid(), "wiki", "/wiki/b.md",
            ConversionJobStatus.Succeeded, null, Guid.NewGuid(), "storage://bucket/b.md", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    ];

    // FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1 の図。コード化済み 1・画像保持へ縮退 1。
    public List<ConversionFigureDto> StubFigures { get; set; } =
    [
        new("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null),
        new("fig-1", false, null, null, "storage://normalized/assets/fig-1.png", "image/png", "全体構成"),
    ];

    // FR-09 BFF テスト（SC-09 管理者設定 ABAC）: AuthorizationService 管理 API の応答をスタブ制御する。
    // AuthzManagementStatusCode を 400/409/404 に差し替えると、書き込みの検証エラー・競合・不在の透過を検証できる。
    public static readonly Guid StubPolicyId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid StubAttributeId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public HttpStatusCode AuthzManagementStatusCode { get; set; } = HttpStatusCode.OK;
    // 後段（AuthorizationService）不達を再現する（BFF が 502 へ縮退することの検証用）。
    public bool AuthzManagementThrows { get; set; }
    public List<AbacPolicyDto> StubPolicies { get; set; } =
    [
        new(StubPolicyId, "社員は社内文書を閲覧可", "read",
            new Dictionary<string, List<string>> { ["clearance"] = ["internal", "confidential"] },
            new Dictionary<string, List<string>> { ["confidentiality"] = ["public", "internal"] },
            true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    ];
    public List<AttributeDefinitionDto> StubAttributes { get; set; } =
    [
        new(StubAttributeId, "confidentiality", "機密区分",
            ["public", "internal", "confidential", "restricted"], true, "document",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    ];

    // FR-05, FR-09, UC-05, SC-17, #452: 利用者アカウント管理（/authz/users*）のスタブ。
    // **後段は AuthorizationService と同じ named client** なので、AuthzStubHandler がパスで振り分ける。
    // 不達（502 への縮退）は AuthzManagementThrows を共用する。
    public HttpStatusCode UserAdminStatusCode { get; set; } = HttpStatusCode.OK;
    public string? LastUserAdminPath { get; private set; }
    public string? LastUserAdminMethod { get; private set; }
    public string? LastUserAdminBody { get; private set; }
    // 🔴 **資格情報の伝播の観測点。** 後段も AdminOnly を強制する二重ゲートなので、
    // 伝播が切れると実サービスでは全部 401 になる。
    public string? LastUserAdminForwardedAuthorization { get; private set; }
    public List<PlatformUserDto> StubUsers { get; set; } =
    [
        new("u-tanaka", "tanaka.taro", "田中 太郎", true, ["platform-operator"],
            new Dictionary<string, string> { ["department"] = "finance", ["clearance"] = "internal" }),
        new("u-takahashi", "takahashi.jiro", "高橋 次郎", false, ["platform-operator"],
            new Dictionary<string, string> { ["department"] = "hr", ["clearance"] = "public" }),
    ];
    public List<string> StubAssignableRoles { get; set; } = ["platform-admin", "platform-operator"];

    internal void RecordUserAdmin(string? path, string method, string? body, string? authorization)
    {
        LastUserAdminPath = path;
        LastUserAdminMethod = method;
        LastUserAdminBody = body;
        LastUserAdminForwardedAuthorization = authorization;
    }

    // FR-01/FR-02 BFF テスト（SC-06 データソース管理）: DataSourceService の応答をスタブ制御する。
    public static readonly Guid StubDataSourceId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public HttpStatusCode DataSourceStatusCode { get; set; } = HttpStatusCode.OK;

    // SC-06（planning#200 / 裁定 Q15）: 次回同期は共通間隔の次回実行時刻であり**全ソース同値**である。
    // スタブも同値を返す（後段が計算した値を BFF がそのまま透過することの検証に用いる。固定値＝決定的）。
    public static readonly DateTimeOffset StubNextSyncAt = new(2026, 8, 6, 14, 5, 0, TimeSpan.Zero);

    // SC-06（planning#200 / 裁定 Q14 / #537）: 同期健全性。2 件目を**継続失敗（上限到達）**にして、
    // BFF が健全性を欠落させず透過することを検証できるようにする。
    public const int StubRetryLimit = DataSourceSyncHealth.DefaultRetryLimit;

    public List<DataSourceDto> StubDataSources { get; set; } =
    [
        new(StubDataSourceId, "社内共有フォルダ", "filesystem", "smb://share/docs", "active",
            DateTimeOffset.UtcNow, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, DateTimeOffset.UtcNow,
            StubNextSyncAt, ConsecutiveFailureCount: 0, RetryLimit: StubRetryLimit),
        new(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), "社内 Wiki", "wiki", "https://wiki.example",
            "disabled", null, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, DateTimeOffset.UtcNow,
            StubNextSyncAt, ConsecutiveFailureCount: StubRetryLimit, RetryLimit: StubRetryLimit,
            LastSyncError: "connect failed: Host=db;Password=***",
            LastSyncErrorAt: new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero)),
    ];

    // SC-06（裁定 Q16 / #534）: 更新系で後段へ転送された本文を捕捉する（PATCH の意味論が
    // BFF で潰れていないことの検証に用いる）。
    public string? LastDataSourceUpdateBody { get; internal set; }
    public HttpMethod? LastDataSourceUpdateMethod { get; internal set; }

    // Issue #283 (AST/SC-01 設定画面): ConfigurationService(/assumptions) への pass-through をスタブ制御する。
    // AssumptionsStatusCode を 403/400/409 に差し替えると、後段の非 2xx 透過（非 owner・検証・競合）を検証できる。
    public HttpStatusCode AssumptionsStatusCode { get; set; } = HttpStatusCode.OK;
    // 後段（ConfigurationService）不達を再現する（BFF が 502 へ縮退することの検証用）。
    public bool AssumptionsThrows { get; set; }
    // 伝播したトークン・転送された PUT 本文を捕捉して検証可能にする。
    public string? LastAssumptionsForwardedAuthorization { get; private set; }
    public string? LastAssumptionsPutBody { get; private set; }

    // Issue #287 (AST/SC-02/AST/SC-03): RiskManagementService(/risk-controls/*) への pass-through をスタブ制御する。
    // RiskControlsStatusCode を 403/400/409 に差し替えると、後段の非 2xx 透過（非 owner・検証・競合）を検証できる。
    public HttpStatusCode RiskControlsStatusCode { get; set; } = HttpStatusCode.OK;
    // 後段（RiskManagementService）不達を再現する（BFF が 502 へ縮退することの検証用）。
    public bool RiskControlsThrows { get; set; }
    // 伝播したトークン・転送された PUT 本文を捕捉して検証可能にする。
    public string? LastRiskControlsForwardedAuthorization { get; private set; }
    public string? LastRiskControlsPutBody { get; private set; }

    // Issue #288 (AST/SC-02 watchlist): MarketMonitorService(/monitor/*) への pass-through をスタブ制御する。
    // MonitorStatusCode を 403/400/409 に差し替えると、後段の非 2xx 透過（非 owner・検証・競合）を検証できる。
    public HttpStatusCode MonitorStatusCode { get; set; } = HttpStatusCode.OK;
    // 後段（MarketMonitorService）不達を再現する（BFF が 502 へ縮退することの検証用）。
    public bool MonitorThrows { get; set; }
    // 伝播したトークン・転送された本文（POST/DELETE）を捕捉して検証可能にする。
    public string? LastMonitorForwardedAuthorization { get; private set; }
    public string? LastMonitorForwardedBody { get; private set; }
    // DELETE でも本文が後段へ届いたことを個別に検証するため、最後の DELETE 本文を保持する。
    public string? LastMonitorDeleteBody { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "localhost:6379",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:DocumentService"] = "http://localhost:5001",
                ["Services:AiAnalysisService"] = "http://localhost:5004",
                ["Services:FeedbackService"] = "http://localhost:5008",
                ["Services:DashboardService"] = "http://localhost:5009",
                // FR-01/FR-02 (SC-06), IADR-0089 (#342): DataSourceService の集約先（テスト用）。実デプロイの
                // メッシュポート（:8080）を明示注入し、named client の BaseAddress が Services 設定駆動である
                // ことを固定する（コード既定 5002 の直書き退行を検出する。BffDownstreamResolutionTests 参照）。
                ["Services:DataSourceService"] = "http://datasource-service:8080",
                // Issue #283 (AST/SC-01): AST ConfigurationService の集約先（テスト用）。
                ["Services:ConfigurationService"] = "http://localhost:5011",
                // Issue #287 (AST/SC-02/AST/SC-03): AST RiskManagementService の集約先（テスト用）。
                ["Services:RiskManagementService"] = "http://localhost:5012",
                // Issue #288 (AST/SC-02 watchlist): AST MarketMonitorService の集約先（テスト用）。
                ["Services:MarketMonitorService"] = "http://localhost:5013",
                // FR-16, UC-09, SC-12 (#452): McpServer の集約先（テスト用）。実デプロイの Service 名
                // （mcp-service）とメッシュポート（:8080）を明示注入し、named client の BaseAddress が
                // Services 設定駆動であることを固定する（BffDownstreamResolutionTests 参照）。
                ["Services:McpServer"] = "http://mcp-service:8080",
                // FR-22, UC-11 (#600): NotificationService の集約先（テスト用）。named client の
                // BaseAddress が Services 設定駆動である（コード既定の直書きに退行していない）ことを
                // BffNotificationEndpointTests が固定する。
                ["Services:NotificationService"] = "http://notification-service:8080",
                // FR-15: 構成情報 API テスト。定期ドリフト検出は無効化し、構成バージョンを固定する。
                ["Drift:Enabled"] = "false",
                ["Config:GitCommit"] = "abc1234",
                ["Config:AppliedAt"] = "2026-07-07T00:00:00Z",
                ["Config:AppliedBy"] = "argocd"
            }));

        builder.ConfigureServices(services =>
        {
            // 名前付きクライアント "AiAnalysisService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("AiAnalysisService")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
            // FR-08: 名前付きクライアント "FeedbackService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("FeedbackService")
                .ConfigurePrimaryHttpMessageHandler(() => new FeedbackStubHandler(this));
            // FR-10: 名前付きクライアント "DashboardService" の通信をスタブハンドラに差し替える
            services.AddHttpClient("DashboardService")
                .ConfigurePrimaryHttpMessageHandler(() => new DashboardStubHandler(this));
            // FR-03/FR-05 (SC-01 横断検索): AuthorizationService / RetrievalService をスタブ化する。
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => new AuthzStubHandler(this));
            services.AddHttpClient("RetrievalService")
                .ConfigurePrimaryHttpMessageHandler(() => new RetrievalStubHandler(this));
            services.AddHttpClient("GraphService")
                .ConfigurePrimaryHttpMessageHandler(() => new GraphStubHandler(this));
            // FR-06 (SC-03 文書閲覧): DocumentService をスタブ化する。
            services.AddHttpClient("DocumentService")
                .ConfigurePrimaryHttpMessageHandler(() => new DocumentStubHandler(this));
            // FR-12 (SC-07 変換ジョブ): ConversionService をスタブ化する。
            services.AddHttpClient("ConversionService")
                .ConfigurePrimaryHttpMessageHandler(() => new ConversionStubHandler(this));
            // FR-01/FR-02 (SC-06 データソース管理): DataSourceService をスタブ化する。
            services.AddHttpClient("DataSourceService")
                .ConfigurePrimaryHttpMessageHandler(() => new DataSourceStubHandler(this));
            // Issue #283 (AST/SC-01 設定画面): ConfigurationService(/assumptions) をスタブ化する。
            services.AddHttpClient("ConfigurationService")
                .ConfigurePrimaryHttpMessageHandler(() => new AssumptionsStubHandler(this));
            // Issue #287 (AST/SC-02/AST/SC-03): RiskManagementService(/risk-controls/*) をスタブ化する。
            services.AddHttpClient("RiskManagementService")
                .ConfigurePrimaryHttpMessageHandler(() => new RiskControlsStubHandler(this));
            // Issue #288 (AST/SC-02 watchlist): MarketMonitorService(/monitor/*) をスタブ化する。
            services.AddHttpClient("MarketMonitorService")
                .ConfigurePrimaryHttpMessageHandler(() => new MonitorStubHandler(this));

            // FR-16, UC-09, SC-12 (#452): McpServer(/mcp-clients*) をスタブ化する。
            services.AddHttpClient("McpServer")
                .ConfigurePrimaryHttpMessageHandler(() => new McpStubHandler(this));

            // FR-22, UC-11 (#600): NotificationService(/notifications*) をスタブ化する。
            services.AddHttpClient("NotificationService")
                .ConfigurePrimaryHttpMessageHandler(() => new NotificationStubHandler(this));

            // FR-10: /bff/dashboard/summary は管理系ロール（admin ＋ operator。#544）を要求する。テストでは Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // FR-15: 自己申告の収集と監査ログをスタブ化する（最後の登録が解決される）。
            services.AddSingleton<IEffectiveConfigCollector>(new StubEffectiveConfigCollector(this));
            services.AddSingleton<IAuditLogger>(new RecordingAuditLogger(this));
            // FR-15 (#145): アラート発火を捕捉するシンクに差し替える（既定の LoggingDriftAlertSink を上書き）。
            services.AddSingleton<IDriftAlertSink>(new RecordingDriftAlertSink(this));
        });
    }

    // FR-15 (#145): ドリフト警告（IDriftAlertSink.AlertAsync）の呼び出しを捕捉する。
    private sealed class RecordingDriftAlertSink(BffTestFactory owner) : IDriftAlertSink
    {
        public Task AlertAsync(DriftReportDto report, CancellationToken ct = default)
        {
            owner.AlertedReports.Add(report);
            return Task.CompletedTask;
        }
    }

    // FR-15: 自己申告の収集をテスト制御の EffectiveCollection に差し替える。
    private sealed class StubEffectiveConfigCollector(BffTestFactory owner) : IEffectiveConfigCollector
    {
        public Task<EffectiveCollection> CollectAsync(CancellationToken ct = default) =>
            Task.FromResult(owner.StubEffective);
    }

    // FR-15: 監査記録を捕捉して検証可能にする。
    private sealed class RecordingAuditLogger(BffTestFactory owner) : IAuditLogger
    {
        public void Record(string action, string subject, string outcome, string? detail = null) =>
            owner.RecordedAudits.Add((action, subject, outcome));
    }

    private sealed class StubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            // IADR-0037: /analysis/ask/stream は SSE を返す（BFF は逐次中継する）。
            if (path.EndsWith("/ask/stream", StringComparison.Ordinal))
            {
                const string sse =
                    "event: citations\ndata: {\"citations\":[{\"number\":1,\"documentTitle\":\"文書A\"}]}\n\n" +
                    "event: token\ndata: {\"text\":\"回答\"}\n\n" +
                    "event: done\ndata: {\"answerId\":\"11111111-1111-1111-1111-111111111111\",\"model\":\"m\",\"inputTokens\":1,\"outputTokens\":2}\n\n";
                var sseResp = new HttpResponseMessage(owner.StubStatusCode)
                {
                    Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
                };
                return Task.FromResult(sseResp);
            }

            var response = new HttpResponseMessage(owner.StubStatusCode)
            {
                Content = JsonContent.Create(owner.StubAnswer)
            };
            return Task.FromResult(response);
        }
    }

    // FR-08: FeedbackService への転送をスタブ化する。POST は 201+FeedbackDto、stats は FeedbackStatsDto を返す。
    private sealed class FeedbackStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastFeedbackForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            HttpResponseMessage response;
            if (path.Contains("/stats"))
            {
                // #948: 実体（FeedbackService）は資格情報が無ければ 401 で challenge する。
                if (owner.FeedbackRequiresAuthorization && request.Headers.Authorization is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }
                response = new HttpResponseMessage(owner.FeedbackStatsStubStatusCode)
                {
                    Content = JsonContent.Create(new FeedbackStatsDto(3, 1, 4, 0.75))
                };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new FeedbackDto(
                        Guid.NewGuid(), Guid.NewGuid(), "up", null, null,
                        "anonymous", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
                };
            }
            return Task.FromResult(response);
        }
    }

    // FR-10: DashboardService への転送をスタブ化する。/dashboard/summary は DashboardUsageDto を返す。
    private sealed class DashboardStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // FR-10, SC-10, IADR-0343 (#1103): 利用状況イベントの受け口。**まず記録してから応答する**
            // —— 非 2xx・到達不能を再現するときも「発火はした」ことを観測できるようにするためである。
            if (request.RequestUri?.AbsolutePath == "/dashboard/events")
            {
                var body = await request.Content!.ReadFromJsonAsync<UsageEventRequest>(cancellationToken);
                owner.RecordedUsageEvents.Enqueue(new RecordedUsageEvent(
                    body?.EventType, body?.Query, request.Headers.Authorization?.ToString()));
                owner._usageEventArrived.Release();

                if (owner.UsageEventStubThrows)
                    throw new HttpRequestException("dashboard-service unreachable");
                return new HttpResponseMessage(owner.UsageEventStubStatusCode)
                {
                    Content = JsonContent.Create(new { id = Guid.NewGuid() })
                };
            }

            owner.LastDashboardForwardedAuthorization = request.Headers.Authorization?.ToString();
            var usage = new DashboardUsageDto(
                5, 3,
                [new UsagePointDto(new DateOnly(2026, 7, 3), "search", 5),
                 new UsagePointDto(new DateOnly(2026, 7, 3), "answer", 3)],
                // FR-10, ADR-0071 決定 1（#1197）: 後段（DashboardService）は既にしきい値で
                // ふるった結果を返す。**スタブも同じ姿にする** —— 有給 1 件は落ちた後の姿である。
                [new SearchTrendDto("経費", 4)],
                DashboardSearchTermMinCount);
            // 502 分岐の検証: 2xx でも本文が null（JSON リテラル "null"）なら BFF は 502 を返す。
            var content = owner.DashboardReturnsNullBody
                ? new StringContent("null", System.Text.Encoding.UTF8, "application/json")
                : (HttpContent)JsonContent.Create(usage);
            return new HttpResponseMessage(owner.DashboardStubStatusCode)
            {
                Content = content
            };
        }
    }

    // FR-05 (SC-01/SC-03): AuthorizationService /authz/scope をスタブ化する。Granted は SearchScopeGranted、
    // 許可フィルタは ScopeFilters（既定は空＝全件許可）で制御する。
    // FR-09 (SC-09): 管理 API（/authz/policies・/authz/attributes）もパスで振り分けてスタブ化する。
    private sealed class AuthzStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        // 実サービス（AuthzEndpoints + AbacValidation）が受理する action の値域の写し（#1010）。
        private static readonly string[] ValidActions = ["read", "analyze", "manage", "write"];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            // FR-09 (SC-09): 後段不達を再現する（BFF が 502 へ縮退することの検証用）。scope 解決は対象外。
            if (owner.AuthzManagementThrows && path.StartsWith("/authz/", StringComparison.Ordinal)
                && !path.StartsWith("/authz/scope", StringComparison.Ordinal))
                throw new HttpRequestException("authorization-service unreachable");

            // FR-09 (SC-09): 管理系は AuthzManagementStatusCode で状態を差し替えられる（400/409/404 透過検証）。
            // FR-05, FR-09, UC-05, SC-17 (#452): 利用者アカウント管理。**観測してから応答する** ——
            // 伝播（Authorization）と後段パス・本文の陽性対照に使う。
            if (path.StartsWith("/authz/users", StringComparison.Ordinal))
            {
                var userBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                owner.RecordUserAdmin(path, method.Method, userBody,
                    request.Headers.TryGetValues("Authorization", out var userAuth)
                        ? string.Join(",", userAuth) : null);

                if (owner.UserAdminStatusCode != HttpStatusCode.OK)
                    return Json(owner.UserAdminStatusCode, new { errors = new[] { "invalid" } });
                if (path == "/authz/users/assignable-roles")
                    return Json(HttpStatusCode.OK, owner.StubAssignableRoles);
                if (path == "/authz/users")
                    return Json(HttpStatusCode.OK, owner.StubUsers);
                return Json(HttpStatusCode.OK, owner.StubUsers[0]); // PUT attributes/roles・POST disable/enable
            }

            if (path.StartsWith("/authz/policies", StringComparison.Ordinal))
            {
                if (owner.AuthzManagementStatusCode != HttpStatusCode.OK)
                    return Json(owner.AuthzManagementStatusCode, new { errors = new[] { "invalid" } });
                if (method == HttpMethod.Delete)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                // FR-05, FR-09, SC-09, #535: dry-run 検証は **200 ＋ { valid, errors }** で返る。
                // **POST の分岐より先に置く**——`/authz/policies/validate` も
                // `StartsWith("/authz/policies")` に一致するため、後ろに置くと 201 Created に化ける。
                if (path == "/authz/policies/validate")
                    return Json(HttpStatusCode.OK, new { valid = true, errors = Array.Empty<string>() });
                if (method == HttpMethod.Post)
                    return Json(HttpStatusCode.Created, owner.StubPolicies[0]);
                if (path == "/authz/policies")
                    return Json(HttpStatusCode.OK, owner.StubPolicies);
                return Json(HttpStatusCode.OK, owner.StubPolicies[0]); // GET/PUT/PATCH by id
            }
            if (path.StartsWith("/authz/attributes", StringComparison.Ordinal))
            {
                if (owner.AuthzManagementStatusCode != HttpStatusCode.OK)
                    return Json(owner.AuthzManagementStatusCode, new { errors = new[] { "invalid" } });
                if (method == HttpMethod.Delete)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                if (method == HttpMethod.Post)
                    return Json(HttpStatusCode.Created, owner.StubAttributes[0]);
                if (path == "/authz/attributes")
                    return Json(HttpStatusCode.OK, owner.StubAttributes);
                return Json(HttpStatusCode.OK, owner.StubAttributes[0]); // GET/PUT by id
            }

            // 既定（/authz/scope）: SC-01/SC-03/SC-05 のスコープ解決。
            //
            // #1010: **実サービスと同じく action 別に応答する。** 要求本文の action を捕捉し
            // （ScopeActionsRequested。観測点）、値域外は 400 を返す（AuthzEndpoints は
            // PolicyAction.IsValid で検証して 400。呼び出し側は null＝deny へ縮退する）。
            // write は WriteScopeGranted / WriteScopeFilters、それ以外（read 等）は従来の
            // SearchScopeGranted / ScopeFilters で制御する —— read と write を独立に差し替え
            // られないと「read しか持たない主体が write 経路で拒まれる」ことを測れない。
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var action = "read";
            if (!string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("action", out var a)
                    && a.ValueKind == JsonValueKind.String)
                    action = a.GetString() ?? "read";
            }
            owner.ScopeActionsRequested.Add(action);
            if (!ValidActions.Contains(action))
                return Json(HttpStatusCode.BadRequest, new { errors = new[] { $"未知のアクション: {action}" } });

            var scope = action == "write"
                ? new AccessScopeResponse("tester", owner.WriteScopeFilters, owner.WriteScopeGranted)
                : new AccessScopeResponse("tester", owner.ScopeFilters, owner.SearchScopeGranted,
                    owner.ScopeBranches);
            return Json(HttpStatusCode.OK, scope);
        }

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });
    }

    // FR-06 (SC-03): DocumentService をスタブ化する。/documents（一覧）・/documents/{id}（詳細・状態可変）・
    // /documents/{id}/versions（版履歴）をパスで振り分ける。
    private sealed class DocumentStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            // FR-19, FR-20, SC-19, SC-20, #451: 個人資料・同期端末。
            // **他の分岐より先に置く** —— 末尾の総称分岐（GET なら StubDocument を返す）に
            // 吸われると、所有者スコープも 401 も測れなくなる。
            if (path.StartsWith("/private-notes", StringComparison.Ordinal))
                return PrivateNotes(owner, request, path, method, cancellationToken);

            // FR-09, SC-05, SC-09, #634: タグ辞書（IADR-0152）。
            // BFF は管理者・運用者のときだけここを呼ぶ。**呼ばれたこと自体を観測する**
            // （一般利用者のとき呼んでいないことを固定するため）。
            if (path == "/tags")
            {
                // FR-09, SC-09, #640: 追加。**名前の重複は 409**（後段が判定する）。
                if (method == HttpMethod.Post)
                    return owner.TagWriteStatusCode != HttpStatusCode.OK
                        ? Status(owner.TagWriteStatusCode, new { message = "タグ「経理」は既に辞書にあります。" })
                        : Json(HttpStatusCode.Created, new TagDto(BffTestFactory.StubCreatedTagId, "新規タグ", 0));

                owner.TagDictionaryFetched = true;
                if (owner.TagDictionaryStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.TagDictionaryStatusCode));
                return Ok(new TagDictionaryResponse(owner.StubTagDictionary));
            }

            // FR-09, SC-09, #640: 改名（`PUT /tags/{id}`）・削除（`DELETE /tags/{id}`）。
            if (path.StartsWith("/tags/", StringComparison.Ordinal))
            {
                if (method == HttpMethod.Put)
                    return owner.TagWriteStatusCode != HttpStatusCode.OK
                        ? Status(owner.TagWriteStatusCode, new { message = "タグ「経理」は既に辞書にあります。" })
                        : Ok(new RenameTagResponse(
                            new TagDto(BffTestFactory.StubCreatedTagId, "改名後", owner.StubRenameRepublished),
                            owner.StubRenameRepublished));

                if (method == HttpMethod.Delete)
                    // **使用件数が 1 件以上なら 409 を件数つきで返す**（SC-09「削除前に使用件数を示す」）。
                    // **BFF は本文を詰め替えず透過する**ので、この `usageCount` がそのまま画面へ届く。
                    return owner.StubDeleteUsageCount > 0
                        ? Status(HttpStatusCode.Conflict, new
                        {
                            error = "tag_in_use",
                            message = $"タグ「経理」は {owner.StubDeleteUsageCount} 件の文書で使われているため削除できません。",
                            usageCount = owner.StubDeleteUsageCount,
                        })
                        : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (path.EndsWith("/versions", StringComparison.Ordinal))
                return Ok(owner.StubVersions);

            // FR-06 (SC-03, #449): GET /documents/{id}/versions/{version}（特定版の取得）。
            // 当該版が無ければ後段は 404 を返す —— BFF がそれを 404 として透過することを検証する。
            var versionsMarker = path.IndexOf("/versions/", StringComparison.Ordinal);
            if (versionsMarker >= 0)
            {
                var requested = path[(versionsMarker + "/versions/".Length)..];
                var snapshot = int.TryParse(requested, out var number)
                    ? owner.StubVersions.Find(v => v.Version == number)
                    : null;
                return snapshot is null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                    : Ok(snapshot);
            }

            if (path == "/documents")
            {
                // FR-06 (SC-05): 新規作成。検証エラーは DocumentWriteStatusCode で再現し透過を確認する。
                if (method == HttpMethod.Post)
                    return owner.DocumentWriteStatusCode != HttpStatusCode.OK
                        ? Status(owner.DocumentWriteStatusCode, new { errors = new { title = new[] { "タイトルは必須です。" } } })
                        : Json(HttpStatusCode.Created, owner.StubDocument);
                return Ok(owner.StubDocumentList);
            }

            // GET /documents/{id}（詳細・スコープ確認にも使われる）
            if (method == HttpMethod.Get)
            {
                if (owner.DocumentStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.DocumentStatusCode));
                return Ok(owner.StubDocument);
            }

            // FR-06 (SC-05): 書き込み（PUT/PATCH/POST publish・archive/DELETE）。
            if (owner.DocumentWriteStatusCode != HttpStatusCode.OK)
                return Status(owner.DocumentWriteStatusCode,
                    new { error = "version_conflict", expectedVersion = 1, currentVersion = 3 });
            if (method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Ok(owner.StubDocument);
        }

        // FR-19, FR-20, UC-11, SC-19, SC-20, #451: 個人資料・同期端末の後段を、**実体と同じ判定の形**で
        // 再現する。実体（`PrivateNoteEndpoints` / `SyncDeviceEndpoints`）の要点は 3 つである。
        //   ① 主体は**トークンからしか採らない**（クエリ・本文に主体の口が無い）
        //   ② 所有者スコープは台帳（`OwnerId`）で判定する
        //   ③ 他者の資料・端末は **404**（403 にすると他人の ID の実在が漏れる）
        // ここで ① を再現しないと「BFF が資格情報を渡し忘れた」欠陥が緑を通る（#948 と同型）。
        private static Task<HttpResponseMessage> PrivateNotes(
            BffTestFactory owner, HttpRequestMessage request, string path, HttpMethod method,
            CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString();
            owner.LastPrivateNoteForwardedAuthorization = auth;

            // ① 主体はトークンから。転送が無ければ 401（実体は RequireAuthorization が同じ結果を出す）。
            var subject = auth is not null
                && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? auth["Bearer ".Length..].Trim()
                    : null;
            if (string.IsNullOrEmpty(subject))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            // ② 台帳: 誰が何を持つか。alice の資料・端末と bob のそれを 1 件ずつ置く。
            static string? OwnerOf(Guid id) =>
                id == StubPrivateNoteId || id == StubSyncDeviceId ? NoteOwner
                : id == OtherOwnerNoteId || id == OtherOwnerDeviceId ? OtherNoteOwner
                : null;
            bool Owns(Guid id) => OwnerOf(id) == subject;

            var now = DateTimeOffset.UtcNow;
            PrivateNoteDto Note(Guid id, string title, bool deleted = false) => new(
                id, title, $"{title}.md", 3, 1024, "sha256:stub", false, false, false, deleted,
                deleted ? now : null, deleted ? now.AddDays(90) : null, now, now);
            SyncDeviceDto Device(Guid id, string name) =>
                new(id, name, now.AddDays(-3), now.AddDays(27), false, now.AddHours(-1), true);
            var issued = new SyncTokenIssuedResponse(
                StubSyncDeviceId, "Obsidian（自宅 PC）", StubSyncTokenPlaintext, now.AddDays(30));

            var rest = path["/private-notes".Length..].Trim('/');
            var segments = rest.Length == 0 ? [] : rest.Split('/');

            // ── /private-notes（一覧・作成）────────────────────────────
            if (segments.Length == 0)
            {
                if (method == HttpMethod.Post)
                {
                    // ADR-0037 決定 17: 100% では**新規作成だけ**が 507。本文は詰め替えず届くこと。
                    if (owner.PrivateNoteQuotaExceeded)
                        return Task.FromResult(new HttpResponseMessage(
                            HttpStatusCode.InsufficientStorage)
                        {
                            Content = new StringContent(
                                "{\"title\":\"保存容量の上限に達しています。\",\"status\":507,"
                                + "\"detail\":\"削除済み資料の完全削除で容量を空けてください（"
                                + QuotaProblemMarker + "）。\"}",
                                System.Text.Encoding.UTF8, "application/problem+json"),
                        });
                    return Json(HttpStatusCode.Created, Note(StubPrivateNoteId, "新しい資料"));
                }

                // 一覧は**呼び出し者の資料だけ**を返す（②）。他人の資料は 1 件も混ざらない。
                List<PrivateNoteDto> owned =
                    subject == NoteOwner ? [Note(StubPrivateNoteId, "設計メモ")]
                    : subject == OtherNoteOwner ? [Note(OtherOwnerNoteId, "他人のメモ")]
                    : [];
                return Ok(new PrivateNoteListResponse(
                    new PrivateNoteUsageDto(1024, 1_073_741_824, 0), owned));
            }

            // ── /private-notes/purge（完全削除。単票も一括も同じ口）─────────────
            if (segments[0] == "purge")
            {
                var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var ids = new List<Guid>();
                if (!string.IsNullOrEmpty(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("ids", out var arr))
                        ids.AddRange(arr.EnumerateArray()
                            .Select(e => Guid.TryParse(e.GetString(), out var g) ? g : Guid.Empty));
                }
                if (ids.Count == 0 || ids.Any(id => !Owns(id)))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Ok(new PurgePrivateNotesResponse(ids.Count, 1024L * ids.Count));
            }

            // ── /private-notes/devices/*（端末・トークン）──────────────────
            if (segments[0] == "devices")
            {
                if (segments.Length == 1)
                {
                    if (method == HttpMethod.Post)
                        return Json(HttpStatusCode.Created, issued);
                    List<SyncDeviceDto> mine =
                        subject == NoteOwner ? [Device(StubSyncDeviceId, "Obsidian（自宅 PC）")]
                        : subject == OtherNoteOwner ? [Device(OtherOwnerDeviceId, "他人の端末")]
                        : [];
                    return Ok(mine);
                }
                if (segments[1] == "revoke-all")
                    return Ok(new RevokeAllSyncDevicesResponse(1));
                if (!Guid.TryParse(segments[1], out var deviceId) || !Owns(deviceId))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                if (segments.Length == 3 && segments[2] == "reissue")
                    return Ok(issued);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            // ── /private-notes/{id}（論理削除・復元・露出）────────────────────
            // ③ 他人の資料は**不在と同じ 404**。403 を返すと他人の資料 ID の実在が漏れる。
            if (!Guid.TryParse(segments[0], out var noteId) || !Owns(noteId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (segments.Length == 1 && method == HttpMethod.Delete)
                // 決定 19・20: 論理削除しても容量は空かない（`capacityFreed=false`）。
                return Ok(new PrivateNoteDeletedResponse(now, now.AddDays(90), false));
            if (segments.Length == 2 && segments[1] == "restore")
                return Ok(Note(noteId, "設計メモ"));
            if (segments.Length == 2 && segments[1] == "exposure")
                return Ok(Note(noteId, "設計メモ"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok<T>(T body) => Json(HttpStatusCode.OK, body);

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });

        private static Task<HttpResponseMessage> Status<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });
    }

    // FR-01/FR-02 (SC-06): DataSourceService をスタブ化する。/datasources（一覧・登録）・
    // /datasources/{id}（取得・状態可変）・/{id}/sync（202）・DELETE（204）をメソッド／パスで振り分ける。
    private sealed class DataSourceStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (path.EndsWith("/sync", StringComparison.Ordinal))
                return Json(HttpStatusCode.Accepted,
                    new { fetchId = Guid.NewGuid(), status = "queued" });

            if (path == "/datasources")
            {
                if (method == HttpMethod.Post)
                    return Json(HttpStatusCode.Created, owner.StubDataSources[0]);
                // GET 一覧: 後段障害の伝播検証のため DataSourceStatusCode を反映する。
                if (owner.DataSourceStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.DataSourceStatusCode));
                return Json(HttpStatusCode.OK, owner.StubDataSources);
            }

            // SC-06（裁定 Q16 / #534）: 更新（PUT 全置換 / PATCH 部分更新）。転送された本文を捕捉し、
            // 後段が返す更新後の姿を中継する。
            if (method == HttpMethod.Put || method == HttpMethod.Patch)
            {
                owner.LastDataSourceUpdateMethod = method;
                owner.LastDataSourceUpdateBody =
                    request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                if (owner.DataSourceStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.DataSourceStatusCode));
                return Json(HttpStatusCode.OK, owner.StubDataSources[0]);
            }

            // /datasources/{id}
            if (method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(
                    owner.DataSourceStatusCode == HttpStatusCode.OK ? HttpStatusCode.NoContent : owner.DataSourceStatusCode));

            if (owner.DataSourceStatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(owner.DataSourceStatusCode));
            return Json(HttpStatusCode.OK, owner.StubDataSources[0]);
        }

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });
    }

    // FR-12 (SC-07): ConversionService /jobs をスタブ化する。一覧（?status 絞り込み）・個別（404 可変）・
    // retry（202/404）をパス／メソッドで振り分ける。
    private sealed class ConversionStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            var method = request.Method;
            owner.LastConversionPath = request.RequestUri?.PathAndQuery;

            // 後段不達を再現する（BFF の catch → 502 縮退の検証用）。
            if (owner.ConversionThrows)
                throw new HttpRequestException("conversion-service unreachable");

            // IADR-0154: 人手補正 Phase 1。補正投稿は 200（結果 DTO）を返す。
            if (path.EndsWith("/correction", StringComparison.Ordinal))
                return owner.ConversionStatusCode != HttpStatusCode.OK
                    ? Task.FromResult(new HttpResponseMessage(owner.ConversionStatusCode))
                    : Json(HttpStatusCode.OK, new FigureCorrectionResultDto("fig-1",
                        "storage://normalized/doc.md", 1));

            // IADR-0154: 図の一覧（2 ペインの材料）。
            if (path.EndsWith("/figures", StringComparison.Ordinal))
                return owner.ConversionStatusCode != HttpStatusCode.OK
                    ? Task.FromResult(new HttpResponseMessage(owner.ConversionStatusCode))
                    : Json(HttpStatusCode.OK, owner.StubFigures);

            if (path.EndsWith("/retry", StringComparison.Ordinal))
            {
                if (owner.ConversionStatusCode == HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
                var resp = new HttpResponseMessage(owner.ConversionStatusCode);
                // 後段が本文を載せる場合は、それを BFF が透過することを検証できるようにする。
                if (owner.ConversionConflictBody is not null)
                    resp.Content = new StringContent(owner.ConversionConflictBody,
                        System.Text.Encoding.UTF8, "application/json");
                return Task.FromResult(resp);
            }

            if (path == "/jobs")
            {
                // GET 一覧: 後段障害の伝播検証のため ConversionStatusCode を反映する。
                if (owner.ConversionStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(owner.ConversionStatusCode));
                var jobs = owner.StubJobs.AsEnumerable();
                if (query.Contains("status=failed"))
                    jobs = jobs.Where(j => j.Status == ConversionJobStatus.Failed);
                return Json(HttpStatusCode.OK, jobs.ToList());
            }

            // GET /jobs/{id}
            if (owner.ConversionStatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(owner.ConversionStatusCode));
            return Json(HttpStatusCode.OK, owner.StubJobs[0]);
        }

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode code, T body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = JsonContent.Create(body) });
    }

    // FR-03 (SC-01): RetrievalService /search をスタブ化する。StubSearchResponse を返す。
    // FR-03, SC-02, #532: BFF が後段へ渡した並び順を観測できるようにする（縮退させず運ぶことの検証用）。
    // FR-17, UC-10, #916a: GraphService のスタブ。
    private sealed class GraphStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastGraphPath = request.RequestUri?.PathAndQuery;
            owner.LastGraphMethod = request.Method.Method;
            owner.LastGraphBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var auth = request.Headers.Authorization?.ToString()
                ?? (request.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null);
            owner.LastGraphForwardedAuthorization = auth;

            // FR-18, SC-03, #450: 承認・却下（書き込み）。**読み取りとは別の knob で応答を決める。**
            var isWrite = owner.LastGraphPath is { } p
                && (p.EndsWith("/approve", StringComparison.Ordinal)
                    || p.EndsWith("/reject", StringComparison.Ordinal));
            if (isWrite)
            {
                if (owner.GraphWriteStubThrows) throw new HttpRequestException("graph unreachable");
                // 🔴 群の `RequireAuthorization()` が先に弾くので、資格情報が無ければ 401 である。
                if (string.IsNullOrEmpty(auth))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var write = new HttpResponseMessage(owner.GraphWriteStubStatusCode);
                if (owner.GraphWriteStubBody is { } body)
                    write.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                else if (owner.GraphWriteStubStatusCode == HttpStatusCode.OK
                         && owner.StubSuggestionWriteResult is { } dto)
                    write.Content = JsonContent.Create(dto);
                return write;
            }

            var isCatalog = owner.LastGraphPath?.Contains("edge-types", StringComparison.Ordinal) == true;
            // FR-18, SC-21, #918: 提案の一覧。**カタログと同じく `RequireAuthorization()` が
            // 先に弾く群**なので、資格情報が届かなければ 401 である（404 ではない）。
            var isSuggestions =
                owner.LastGraphPath?.Contains("suggestions", StringComparison.Ordinal) == true;

            // 🔴 **資格情報が届かないときの応答は口によって違う。実サービスに合わせる。**
            //   グラフ読み取り: GraphAccessResolver が anonymous → Granted=false → **404**（存在秘匿）
            //   カタログ / 提案: `RequireAuthorization()` が弾く → **401**（隠すものが無いので秘匿しない）
            // ここを一律にすると、片方の伝播が切れても気付けないテストになる。
            if (string.IsNullOrEmpty(auth))
                return new HttpResponseMessage(
                    isCatalog || isSuggestions
                        ? HttpStatusCode.Unauthorized
                        : HttpStatusCode.NotFound);

            if (owner.GraphStubStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(owner.GraphStubStatusCode);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isSuggestions
                    ? JsonContent.Create(owner.StubAiSuggestions)
                    : isCatalog
                        ? JsonContent.Create(owner.StubEdgeTypeCatalog)
                        : JsonContent.Create(owner.StubGraphView)
            };
        }
    }

    private sealed class RetrievalStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // FR-04, #540: 権限内属性値の照会は別の応答を返す（同じ後段の別の口）。
            if (request.RequestUri?.AbsolutePath.EndsWith("/attribute-values", StringComparison.Ordinal) == true)
            {
                owner.LastAttributeValuesBody = body;
                if (owner.AttributeValuesStatusCode != HttpStatusCode.OK)
                    return new HttpResponseMessage(owner.AttributeValuesStatusCode);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AttributeValuesResponse(owner.StubAttributeValues))
                };
            }

            if (body is not null)
            {
                using var doc = JsonDocument.Parse(body);
                owner.LastSearchSortBy = doc.RootElement.TryGetProperty("sortBy", out var sort)
                    && sort.ValueKind == JsonValueKind.String
                        ? sort.GetString()
                        : null;
            }

            // FR-05, ADR-0034 (#970): BFF が伝播した Authorization を記録する（方式 A の観測点）。
            owner.LastSearchForwardedAuthorization = request.Headers.TryGetValues("Authorization", out var auth)
                ? string.Join(' ', auth)
                : null;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(owner.StubSearchResponse)
            };
        }
    }

    // Issue #283 (AST/SC-01 設定画面): ConfigurationService(/assumptions) をスタブ化する。BFF の pass-through
    // （ステータス・本文・Content-Type 透過、トークン伝播、PUT 本文転送、502 縮退）を検証するための最小スタブ。
    private sealed class AssumptionsStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 後段不達を再現する（BFF の catch → 502 縮退の検証用）。
            if (owner.AssumptionsThrows)
                throw new HttpRequestException("configuration-service unreachable");

            owner.LastAssumptionsForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Content is not null)
                owner.LastAssumptionsPutBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // 後段の非 2xx（非 owner 403・検証 400・競合 409 等）を透過する検証のため、状態を差し替え可能にする。
            if (owner.AssumptionsStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(owner.AssumptionsStatusCode)
                {
                    Content = new StringContent(
                        "{\"error\":\"stub\"}", System.Text.Encoding.UTF8, "application/json"),
                };

            // 履歴（新しい順）。
            if (path.EndsWith("/assumptions/history", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"actor\":\"owner\",\"reason\":\"初期値\",\"version\":1}]",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            // 現在値（GET）／変更（PUT）はいずれも前提条件オブジェクトを返す（型は BFF で結合しないため素の JSON）。
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"assumptions\":{\"capitalGainsTaxRate\":0.20315},\"version\":1,\"isResolved\":true}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // Issue #287 (AST/SC-02/AST/SC-03): RiskManagementService(/risk-controls/*) をスタブ化する。BFF の pass-through
    // （ステータス・本文・Content-Type 透過、トークン伝播、PUT 本文転送、502 縮退）を検証するための最小スタブ。
    private sealed class RiskControlsStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 後段不達を再現する（BFF の catch → 502 縮退の検証用）。
            if (owner.RiskControlsThrows)
                throw new HttpRequestException("risk-management-service unreachable");

            owner.LastRiskControlsForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Content is not null)
                owner.LastRiskControlsPutBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // 後段の非 2xx（非 owner 403・検証 400・競合 409 等）を透過する検証のため、状態を差し替え可能にする。
            if (owner.RiskControlsStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(owner.RiskControlsStatusCode)
                {
                    Content = new StringContent(
                        "{\"error\":\"stub\"}", System.Text.Encoding.UTF8, "application/json"),
                };

            // 変更履歴（新しい順）。
            if (path.EndsWith("/settings/history", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"actor\":\"owner\",\"changeType\":1,\"reason\":\"上限見直し\",\"changedAt\":\"2026-07-18T00:00:00Z\"}]",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            // 統制状態（GET /status）。
            if (path.EndsWith("/risk-controls/status", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"killSwitchEngaged\":false,\"tradingPaused\":false,\"stage\":1,\"capital\":1000000}",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            // 段階ゲート（GET /stage-gate）。
            if (path.EndsWith("/risk-controls/stage-gate", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"currentStage\":1,\"promotion\":{\"eligible\":false},\"withdrawal\":{\"triggered\":false}}",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            // 設定の現在値（GET /settings）／変更（PUT /settings/limits・/settings/guard）は設定オブジェクトを返す
            // （型は BFF で結合しないため素の JSON）。
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"limits\":{\"maxOpenPositions\":5},\"stage\":{\"stage\":1,\"mode\":0},\"guard\":{\"preventSameDayReentry\":true}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // Issue #288 (AST/SC-02 watchlist): MarketMonitorService(/monitor/*) をスタブ化する。BFF の pass-through
    // （ステータス・本文・Content-Type 透過、トークン伝播、POST/DELETE 本文転送、502 縮退）を検証するための最小スタブ。
    private sealed class MonitorStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 後段不達を再現する（BFF の catch → 502 縮退の検証用）。
            if (owner.MonitorThrows)
                throw new HttpRequestException("market-monitor-service unreachable");

            owner.LastMonitorForwardedAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Content is not null)
            {
                owner.LastMonitorForwardedBody = await request.Content.ReadAsStringAsync(cancellationToken);
                // DELETE も本文（銘柄・理由）を後段へ届けることを個別検証できるよう保持する。
                if (request.Method == HttpMethod.Delete)
                    owner.LastMonitorDeleteBody = owner.LastMonitorForwardedBody;
            }

            // 後段の非 2xx（非 owner 403・検証 400・競合 409 等）を透過する検証のため、状態を差し替え可能にする。
            if (owner.MonitorStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(owner.MonitorStatusCode)
                {
                    Content = new StringContent(
                        "{\"error\":\"stub\"}", System.Text.Encoding.UTF8, "application/json"),
                };

            // 変更履歴（新しい順・GET /monitor/watchlist/history）。
            if (path.EndsWith("/monitor/watchlist/history", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"actor\":\"owner\",\"changeType\":0,\"symbol\":\"7203\",\"reason\":\"監視追加\",\"changedAt\":\"2026-07-18T00:00:00Z\"}]",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            // 監視銘柄一覧（GET /monitor/watchlist）／追加（POST）／削除（DELETE）は銘柄配列を返す
            // （型は BFF で結合しないため素の JSON）。
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"symbol\":\"7203\",\"market\":\"TSE\",\"addedAt\":\"2026-07-18T00:00:00Z\"}]",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // FR-16, UC-09, SC-12 (#452): McpServer(/mcp-clients*) のスタブ。BFF の pass-through
    // （ステータス・本文・Content-Type 透過、資格情報の伝播、書き込み本文の転送、502 縮退）を
    // 検証するための最小スタブである。
    //
    // 🔴 **資格情報が届かないときは 401 を返す。** 後段（McpServer）の管理 API は
    // `RequireAuthorization(AdminOnly)` の群であり、無資格の要求はそこで弾かれる。
    // ここを一律 200 にすると、BFF が Authorization を伝播し忘れても緑のままになる。
    private sealed class McpStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastMcpPath = request.RequestUri?.PathAndQuery;
            owner.LastMcpMethod = request.Method.Method;
            owner.LastMcpBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            owner.LastMcpForwardedAuthorization = request.Headers.TryGetValues("Authorization", out var auth)
                ? string.Join(' ', auth)
                : null;

            if (owner.McpStubThrows) throw new HttpRequestException("mcp-service unreachable");

            if (string.IsNullOrEmpty(owner.LastMcpForwardedAuthorization))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            if (owner.McpStubStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(owner.McpStubStatusCode)
                {
                    // 後段は RFC7807 の本文を返す。**本文まで透過することを測る**ため空にしない。
                    Content = new StringContent(
                        "{\"errors\":{\"request\":[\"stub-detail\"]}}",
                        System.Text.Encoding.UTF8, "application/problem+json"),
                };

            // 公開ツール一覧（GET /mcp-clients/tools）と登録クライアント一覧（GET /mcp-clients）は形が違う。
            // 型は BFF で結合しないため素の JSON で返す。
            if (owner.LastMcpPath?.EndsWith("/mcp-clients/tools", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"version\":3,\"tools\":[{\"name\":\"retrieval.search_documents\",\"service\":\"retrieval-service\","
                        + "\"description\":\"横断検索\",\"requiredScope\":\"document:read\",\"egressClass\":\"metadata-only\"}],"
                        + "\"drifts\":[{\"kind\":\"UndeclaredTool\",\"target\":\"graph.traverse\",\"detail\":\"申告に無い\"}]}",
                        System.Text.Encoding.UTF8, "application/json"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"nightly-digest-bot\","
                    + "\"displayName\":\"夜間ダイジェスト\",\"kind\":\"service-account\",\"enabled\":true,"
                    + "\"attributes\":{\"confidentiality\":\"internal\"},\"egressTier\":\"self-hosted\","
                    + "\"registeredAt\":\"2026-08-28T00:00:00Z\",\"updatedAt\":\"2026-08-28T00:00:00Z\"}]",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // FR-22, UC-11 (#600): NotificationService(/notifications*) のスタブ。BFF の pass-through
    // （状態・本文・Content-Type の透過、資格情報の伝播、クエリの載せ替え、502 縮退）を検証する最小スタブ。
    //
    // 🔴 **資格情報が届かないときは 401 を返す。** 後段の /notifications 群は RequireAuthorization()
    // であり、主体はトークンからしか採られない。ここを一律 200 にすると、BFF が Authorization を
    // 伝播し忘れても緑のままになる（伝播の陽性対照が成立しなくなる）。
    private sealed class NotificationStubHandler(BffTestFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastNotificationPath = request.RequestUri?.PathAndQuery;
            owner.LastNotificationMethod = request.Method.Method;
            owner.LastNotificationForwardedAuthorization =
                request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(' ', auth) : null;

            if (owner.NotificationStubThrows)
                throw new HttpRequestException("notification-service unreachable");

            if (string.IsNullOrEmpty(owner.LastNotificationForwardedAuthorization))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            // 後段の 404（存在秘匿）などを再現する。**本文まで透過することを測る**ため空にしない。
            if (owner.NotificationStubStatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(owner.NotificationStubStatusCode)
                {
                    Content = new StringContent(
                        "{\"errors\":{\"request\":[\"stub-detail\"]}}",
                        System.Text.Encoding.UTF8, "application/problem+json"),
                });

            // 既読化（POST /notifications/{id}/read）は NotificationReadResultDto、
            // 一覧（GET /notifications）は NotificationListDto。形が違うので分ける。
            // 🔴 **自由文の項目を 1 つも置かない**（契約が持たないものをテストデータで作らない）。
            if (owner.LastNotificationPath?.EndsWith("/read", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"unreadCount\":0}",
                        System.Text.Encoding.UTF8, "application/json"),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"items\":[{\"id\":\"22222222-2222-2222-2222-222222222222\","
                    + "\"kind\":\"private-note-purge-imminent\",\"count\":3,\"thresholdPercent\":null,"
                    + "\"deadline\":\"2026-09-09T00:00:00Z\",\"occurredAt\":\"2026-09-02T00:00:00Z\","
                    + "\"read\":false}],\"unreadCount\":1}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

}
