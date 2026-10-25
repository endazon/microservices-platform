using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using LlmGateway.Features.Completions;
using LlmGateway.Features.Embeddings.Embed;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

const string ServiceName = "microservices-platform.llm-gateway";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

// FR-02, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3, IADR-0397 (#1255): east-west gRPC の
// h2c リスナ（`Grpc:Port`。既定 8081・未設定なら立てない）。HTTP/1.1 の 8080（REST・/health/*・
// introspection）はヘルパが再宣言するので消えない。**readiness は 8080 の /health/ready のまま。**
// AddGrpc() は常に呼ばれる（MapGrpcService は AddGrpc 無しだと起動時に落ちるため、リスナの有無と
// サービス登録の可否を切り離す。TestServer の in-memory HTTP/2 でも gRPC が動く）。
builder.AddPlatformGrpcListener();

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// FR-11, NFR, ADR-0006, IADR-0110 (#395): 補完の終了理由（拒否率）を計上する独自 Meter を
// 既存の OTLP パイプラインへ載せる。OpenTelemetry の builder は加算的なので、全サービス共通の
// AddPlatformObservability を変更せずにサービス固有の Meter を追加できる。
builder.Services.AddMetrics();
builder.Services.AddSingleton<LlmCompletionMetrics>();
// FR-11, IADR-0535 (#1917), IADR-0562 (#1749): Anthropic 応答の content のうち本文に使わず捨てたブロックの件数（同じ Meter）。
// ClaudeProvider の必須の引数（省略可能にすると本番の配線で黙って欠ける。IADR-0535 決定 3）。
builder.Services.AddSingleton<AnthropicContentBlockMetrics>();

// FR-10, NFR, ADR-0006, ADR-0044 決定 1・3 (#443): LLM 利用実績（用途別・モデル別のトークン累計と
// 金額換算）。**金額換算は単価表を読む側＝このゲートウェイで行う**（Grafana のクエリに単価を書かない）。
// 単価表は有効期間つきの設定であり、区間の重なり・負値は起動時に落とす（ValidateOnStart）。
builder.Services.AddOptions<ModelPricingOptions>()
    .Bind(builder.Configuration.GetSection(ModelPricingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ModelPricingOptions>, ModelPricingOptionsValidator>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ModelPriceTable>();
builder.Services.AddSingleton<LlmUsageMetrics>();

// FR-11, NFR-21, ADR-0044, IADR-0466 (#1111): 用途別の月次予算の上限（ゲージ）。**金額の既定は無い**
// （計画が「金額は定めない」と定めている。所有者が Llm:Budget:MonthlyLimits に設定する）。
// 未知の用途・0 以下の金額は起動時に落とす。未設定は正常であり、そのときゲージは系列を持たず
// 上限アラートは発火しない（不活性）。
builder.Services.AddOptions<LlmBudgetOptions>()
    .Bind(builder.Configuration.GetSection(LlmBudgetOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LlmBudgetOptions>, LlmBudgetOptionsValidator>();
builder.Services.AddSingleton<LlmBudgetMetrics>();

// Meter は 1 本（サービス名と一致）。補完カウンタと利用実績の計器は同じ Meter に載る。
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(LlmCompletionMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks();
builder.Services.AddOpenApi();

// ADR-0010, ADR-0128, IADR-0562 (#1749): Claude SDK は公式 `Anthropic`（12.53.0。計画 04_ai-rag-stack の指定）。
// 生成点は AnthropicClientFactory の 1 か所で、SDK の既定を変える値（再試行 0・期限・送信先・資格情報）をそこで固定する。
// 旧 `Anthropic.SDK` 4.0.0 向けの委譲ハンドラ（要求本文への effort の注入・未知 content ブロックの除去。IADR-0114 / IADR-0531）は
// 撤去した（公式 SDK は effort を送れ、未知のブロックを例外にせず保持する）。
// FR-11, IADR-0528 (#1872): 期限は `Llm:AnthropicTimeoutSeconds`（既定 100 秒＝従前と同じ。不正値は既定へ倒す）。
// 呼び出し側の期限はこの値より短く保つ（docs/operations/operations.md）。
// FR-11, IADR-0566 (#1997): 逐次の無通信の期限 `Llm:AnthropicStreamIdleTimeoutSeconds` は ClaudeProvider が読む（未設定は上の期限と同じ値）。
builder.Services.AddSingleton(sp => AnthropicClientFactory.Create(
    builder.Configuration,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AnthropicClientFactory).FullName!)));
builder.Services.AddHttpClient();

// FR-11, ADR-0010: 呼び出し先の切り替え（機密区分×ティアの越境マトリクス + 用途別モデル）。
// FR-11, IADR-0641 (#2136): Claude のエンドポイントが有効なのに `Llm:ApiKey` が空なら起動時に落とす
// （従前は最初の呼び出しの 401 で初めて分かった）。鍵なしの検証スタックは `Llm:AllowMissingApiKey=true` を明示する。
builder.Services.AddOptions<LlmRoutingOptions>()
    .Bind(builder.Configuration.GetSection(LlmRoutingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LlmRoutingOptions>, ClaudeApiKeyStartupValidator>();
builder.Services.AddSingleton<ILlmRouter, LlmRouter>();

// FR-11: ルーターの判定に従って呼び出し先を切り替えるため、プロバイダをキー付きで登録する。
// ティアB=保護契約済み外部API（Claude）、ティアA=セルフホスト（OSS, 既定は無効エンドポイント）、
// GitHub Copilot（最難関用途の別経路, ティア確定まで既定は無効エンドポイント）。ADR-0010 / IADR-0022。
// NFR-17, ADR-0140 決定 5, IADR-0552 (#1960): ティアB の Claude は組織の ZDR が未締結（期限付きで受け入れたリスク）で、
// 推論の所在も受け入れたリスクである（運用仕様書「LLM ゲートウェイの送信先ティア（Claude）の認定状況」）。
// FR-11, IADR-0531 (#1875・planning#783): 用途別 effort（`Llm:PurposeEffort`。既定は rerank=low のみ）。
// 値域外は起動時に落とす（実行時に送ると全件 400 になり「上流の失敗」としか見えない）。
builder.Services.AddOptions<ClaudePurposeEffortOptions>()
    .Configure(o => builder.Configuration.GetSection(ClaudePurposeEffortOptions.SectionName).Bind(o.Purposes))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ClaudePurposeEffortOptions>, ClaudePurposeEffortOptionsValidator>();
builder.Services.AddKeyedSingleton<ILlmProvider, ClaudeProvider>("claude");
builder.Services.AddKeyedSingleton<ILlmProvider, SelfHostedProvider>("selfhosted");
builder.Services.AddKeyedSingleton<ILlmProvider, CopilotProvider>("copilot");

// FR-02, FR-05, ADR-0016, ADR-0017（Superseded by ADR-0127・注記は #1746）: 埋め込みは LLM 生成とは別系統で機密区分ルーティングする。
// ティアB=Voyage AI（voyage-3.5 / 1024次元・既定）、ティアA=セルフホスト（Ruri v3 / 768次元・既定は無効）。
// confidential/restricted はティアA固定・無効なら fail-closed（EmbeddingRouter）。
// Issue #98 レビュー対応: インデックス依存の環境変数上書き（Endpoints__N__Enabled）による取り違えを
// 起動時に fail-fast する（ティア↔プロバイダ整合・既定経路の有効性を EmbeddingRoutingOptionsValidator で検証）。
builder.Services.AddOptions<EmbeddingRoutingOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingRoutingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EmbeddingRoutingOptions>, EmbeddingRoutingOptionsValidator>();
builder.Services.AddSingleton<IEmbeddingRouter, EmbeddingRouter>();
builder.Services.AddKeyedSingleton<IEmbeddingProvider, VoyageEmbeddingProvider>("voyage");
builder.Services.AddKeyedSingleton<IEmbeddingProvider, SelfHostedEmbeddingProvider>("selfhosted-embedding");
// FR-02, FR-03, #992 案 2, [[IADR-0313]]: 決定的ローカル埋め込み（ティアA・プロセス内計算）。
// **既定は appsettings.json で Enabled: false**。使い捨ての統合スタックだけが opt-in する。
builder.Services.AddKeyedSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>("deterministic-embedding");

// FR-02, FR-05, ADR-0016, IADR-0379 決定 5, IADR-0397 (#1255): 埋め込みの判定器本体。
// gRPC（LlmEmbedding/Embed）が呼ぶ（［2026-10-15 / #1902］REST の /embed は撤去した）。
builder.Services.AddSingleton<EmbedUseCase>();

// FR-11, NFR-19 (#1819): 同じ事象のログを「初回は即時・以後 5 分ごとに要約 1 行」に抑える状態。
// 越境拒否（CompletionUseCase）と埋め込みの鍵未設定（EmbedUseCase）が共有する（鍵の接頭辞で分ける）。
builder.Services.AddSingleton<LogOccurrenceThrottle>();

// FR-04, FR-11, ADR-0010, IADR-0379 決定 5, IADR-0400 (#1255): テキスト生成の判定器本体。
// REST（/complete）と gRPC（LlmCompletion/Complete・CompleteStream）の
// **両方がこれを呼ぶ**（［2026-10-15 / #1902］逐次の REST /complete/stream は撤去した） —— 越境判定・フォールバック鎖・計器の計上を輸送ごとに分けない。
builder.Services.AddSingleton<CompletionUseCase>();

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段はホストしないが、
// LLM 生成・埋め込みの合成可能ポート（機密区分ルーティングで複数プロバイダを束ねるルータ）を申告する。
builder.Services.AddPlatformIntrospection("llm-gateway", new PipelineOptions(),
    i => i
        .AddPort("llm", nameof(LlmRouter), "claude/selfhosted/copilot")
        .AddPort("embedding", nameof(EmbeddingRouter), "voyage/selfhosted/deterministic"));

var app = builder.Build();

// FR-11, NFR-21, IADR-0466 決定 3 (#1111): 予算のゲージを**起動時に**登録する。
// シングルトンは初めて解決されたときに作られるため、補完の呼び出しを待つと
// 「金額を設定したのに系列が出ない」時間が生まれ、所有者の確認手順（系列が出ることを見る）が成り立たない。
app.Services.GetRequiredService<LlmBudgetMetrics>();

// FR-02, #992, [[IADR-0313]]: 🔴 **決定的ローカル埋め込みが有効なら、起動時に警告を出す。**
// 索引されるベクトルに意味的な近さは無く、**検索品質は保証されない**。
// 有効化は使い捨てのスタックに限る。設定の取り違え（インデックス依存の env 上書き）で
// 本番へ紛れ込んだとき、**ログを見れば分かる**ようにしておく（無言で品質だけが落ちる事故を避ける）。
{
    var embeddingRouting = app.Services.GetRequiredService<IOptions<EmbeddingRoutingOptions>>().Value;
    var deterministic = embeddingRouting.Endpoints
        .FirstOrDefault(e => e.Enabled && e.Provider == "deterministic-embedding");
    if (deterministic is not null)
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LlmGateway.Embed")
            .LogWarning(
                "🔴 決定的ローカル埋め込み '{Endpoint}'（{Model} / {Dimensions}次元 / {Collection}）が有効です。"
                + "表層の文字 3-gram のみで意味的な近さを持たず、検索品質は保証されません。"
                + "使い捨ての検証スタック専用です（#992 / IADR-0313）。",
                deterministic.Name, deterministic.Model, deterministic.Dimensions, deterministic.Collection);
    }
}

// FR-11, IADR-0641 (#2136): 明示の opt-out で鍵なしのまま起動したことを、起動時に 1 行残す（鍵の値は出さない）。
{
    var routing = app.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;
    var enabledClaude = ClaudeApiKeyStartupValidator.EnabledClaudeEndpoints(routing);
    if (enabledClaude.Count > 0
        && ClaudeApiKeyStartupValidator.IsKeyMissing(app.Configuration)
        && ClaudeApiKeyStartupValidator.IsMissingKeyAllowed(app.Configuration))
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LlmGateway.Startup")
            .LogWarning(
                "Claude のエンドポイント（{Endpoints}）が有効だが {ApiKeyConfigKey} が空のまま、{AllowMissingConfigKey}=true で起動した。"
                + "Claude への呼び出しは 401 で失敗する。鍵なしの検証スタック専用である（#2136 / IADR-0641）。",
                string.Join(", ", enabledClaude), AnthropicClientFactory.ApiKeyConfigKey,
                ClaudeApiKeyStartupValidator.AllowMissingConfigKey);
    }
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapCompletionEndpoints();
// ［2026-10-15 / #1902・[[IADR-0533]] 追記］REST の `/embed`・`/complete/stream` は撤去した（呼び出し元は gRPC だけ）。

// FR-02, NFR-09, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255): 埋め込みの gRPC 面。
// `[Authorize(Policy = ServiceCaller)]` を型に持ち、s2s トークン（realm ロール platform-service）だけを通す。
app.MapGrpcService<LlmEmbeddingGrpcService>();

// FR-04, FR-11, NFR-02, NFR-09, ADR-0029, ADR-0075, ADR-0076, IADR-0379, IADR-0400 (#1255):
// テキスト生成の gRPC 面（Complete ＝ 一括 ／ CompleteStream ＝ **サーバストリーミング**）。
// 認可は埋め込みと同じ ServiceCaller。
// 🔴 **［2026-09-09 是正 / #1364］REST（/complete。当時は /complete/stream・/embed も）も同じ `ServiceCaller` を要する。**
// 従前ここには「REST は並走したまま残る」と書いてあり、それは**輸送の並走**を述べたつもりで
// **認可の不在**まで書いてしまっていた（`ADR-0084` 決定 1 に照らして未達だった）。
// 並走しているのは輸送だけであり、**門は 2 つの面で同じ 1 つのポリシーである**（[[IADR-0424]]）。
app.MapGrpcService<LlmCompletionGrpcService>();

app.Run();

public partial class Program { }
