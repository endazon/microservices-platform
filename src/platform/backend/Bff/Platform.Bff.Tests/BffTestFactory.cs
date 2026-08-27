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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastDashboardForwardedAuthorization = request.Headers.Authorization?.ToString();
            var usage = new DashboardUsageDto(
                5, 3,
                [new UsagePointDto(new DateOnly(2026, 7, 3), "search", 5),
                 new UsagePointDto(new DateOnly(2026, 7, 3), "answer", 3)],
                [new SearchTrendDto("経費", 4), new SearchTrendDto("有給", 1)]);
            // 502 分岐の検証: 2xx でも本文が null（JSON リテラル "null"）なら BFF は 502 を返す。
            var content = owner.DashboardReturnsNullBody
                ? new StringContent("null", System.Text.Encoding.UTF8, "application/json")
                : (HttpContent)JsonContent.Create(usage);
            var response = new HttpResponseMessage(owner.DashboardStubStatusCode)
            {
                Content = content
            };
            return Task.FromResult(response);
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.LastGraphPath = request.RequestUri?.PathAndQuery;
            var auth = request.Headers.Authorization?.ToString()
                ?? (request.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null);
            owner.LastGraphForwardedAuthorization = auth;

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
                return Task.FromResult(new HttpResponseMessage(
                    isCatalog || isSuggestions
                        ? HttpStatusCode.Unauthorized
                        : HttpStatusCode.NotFound));

            if (owner.GraphStubStatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(owner.GraphStubStatusCode));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isSuggestions
                    ? JsonContent.Create(owner.StubAiSuggestions)
                    : isCatalog
                        ? JsonContent.Create(owner.StubEdgeTypeCatalog)
                        : JsonContent.Create(owner.StubGraphView)
            });
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
}
