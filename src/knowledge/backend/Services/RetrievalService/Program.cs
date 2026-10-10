using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Qdrant.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetrievalService.Common.Observability;
using RetrievalService.Features.McpTools;
using RetrievalService.Features.McpTools.Declare;
using RetrievalService.Features.Search;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Features.Search.RemoveDeleted;
using Wolverine;
using Wolverine.RabbitMQ;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;
using Platform.Shared.Infrastructure.Foundation.Authz;
using RetrievalService.Features.Search.AttributeValues;
using Platform.Shared.Infrastructure.Foundation.Grpc;

const string ServiceName = "microservices-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
var qdrantHealthUri = new Uri(
    $"http://{builder.Configuration["Qdrant:Host"] ?? "qdrant"}:6333/healthz");
builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / #1016: Wolverine 購読側（retrieval-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddUrlGroup(qdrantHealthUri, "qdrant", tags: ["ready"])
    // FR-03, NFR-06, #1116 / [[IADR-0318]] 決定 3: **全文ペイロードインデックスの有無**を readiness へ載せる。
    // 🔴 Qdrant への疎通（上の "qdrant"）が緑でも、索引が無ければキーワード検索は
    // 全文検索として機能しない。しかも**例外が出ないので応答からもログからも分からない**。
    // ここが唯一の運用上の検出点である。**Degraded 止まり**（検索全体は落とさない。NFR-06）。
    .AddCheck<QdrantFullTextIndexHealthCheck>(
        QdrantFullTextIndexHealthCheck.Name,
        failureStatus: HealthStatus.Degraded, tags: ["ready"])
    // FR-03, #1118 / [[IADR-0339]] 決定 3: 日本語 2-gram（`text_ngram`）の索引の有無も同型で載せる。
    // `text` の索引が在っても、こちらが無ければ**日本語の語だけが 0 件**になる（識別子は当たる）。
    .AddCheck<QdrantCjkNgramIndexHealthCheck>(
        QdrantCjkNgramIndexHealthCheck.Name,
        failureStatus: HealthStatus.Degraded, tags: ["ready"]);
builder.Services.AddOpenApi();

// ADR-0009: Qdrant ベクトルDB クライアント
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "qdrant";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

// FR-03, SC-10, #1116 / [[IADR-0318]] 決定 3: 全文（キーワード）側の縮退を数える（0 が正常）。
// **応答へは載せない**（存在秘匿・[[IADR-0313]] 決定 1）。観測は応答の外側に置く。
builder.Services.AddSingleton<KeywordSearchMetrics>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
//
// FR-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0397, [[IADR-0533]] (#1255): クエリ埋め込みの輸送は east-west gRPC だけである
// （［2026-10-10］REST の `LlmGatewayEmbeddingService` は撤去した）。`Services:LlmGatewayGrpc` が構成されていなければ
// 生成クライアントは常に `UNAVAILABLE` を受け取り、埋め込みは例外として上がる（故障を「該当なし」に化けさせない。IADR-0256 決定 3）。
// FR-02, FR-03, ADR-0016, [[IADR-0422]] 決定 3 (#336): **クエリ埋め込みの照合先。**
// 埋め込みの客体（REST / gRPC）が、ゲートウェイの答えたコレクションと**この値**を突き合わせ、
// 食い違えば空ベクトルへ降りる。**値はベクトルストアと同じ関数から引く**（別の規則で読み直すと、
// 照合しているつもりで別のものを比べることになる）。
builder.Services.AddSingleton(
    new QueryEmbeddingTarget(QdrantVectorStore.ResolveCollectionName(builder.Configuration)));

builder.Services.AddLlmGatewayGrpcClient(builder.Configuration);
builder.Services.AddSingleton<IEmbeddingService, LlmGatewayGrpcEmbeddingService>();

// FR-03, FR-05, ADR-0016, ADR-0092 決定 1・2・3, [[IADR-0467]] (#336): **束ねる追加コレクション。**
//
// `Qdrant:FusedCollections`（既定は空）に挙げたコレクションを、主（`Qdrant:CollectionName`）と
// **1 回の検索で束ねる**（順位ベースの RRF。スコアは比べない）。各コレクションのクエリは
// **そのコレクションのモデルで**埋める（要求にコレクション名を載せ、ゲートウェイが越境判定の後で絞る）。
// 🔴 **既定は空であり、そのとき検索・属性値・削除は従来と同一である**（`FusedCollections.None`）。
// Helm は `embedding.enabled=true` のときだけティア A のコレクションをここへ描画する。
var fusedCollectionNames = QdrantVectorStore.ResolveFusedCollectionNames(builder.Configuration);
// 🔴 FR-03, ADR-0127 決定 1・2, [[IADR-0497]] 決定 5 (#1746): **語彙索引を常に束ねる**（高機密文書の置き場所）。
// 全文の系統だけで RRF に入り、意味検索のモードには入らない。ABAC は他のコレクションと同じフィルタを掛ける。
// 主・追加コレクションと同名なら起動を止める。無効化の口は持たない（ADR-0127 決定 1）。
var lexicalCollectionName = QdrantVectorStore.ResolveLexicalCollectionName(builder.Configuration);
QdrantVectorStore.EnsureLexicalCollectionDistinct(lexicalCollectionName,
    QdrantVectorStore.ResolveCollectionName(builder.Configuration), fusedCollectionNames);
builder.Services.AddScoped(sp =>
    FusedCollectionsComposition.Build(sp, fusedCollectionNames, lexicalCollectionName));

// FR-06, ADR-0027 (#1640): 索引からの削除の受け口の時間の上限（Qdrant 1 回ごとの期限）。
// 「(主 ＋ 追加コレクション数) × 期限」が Wolverine の既定の実行期限に収まらない構成は、ここで起動を止める。
// ［2026-10-05 / #1746］[[IADR-0497]] 決定 4: 語彙索引からも消すので 1 本足す。
builder.Services.AddSingleton(DocumentDeletedTimeouts.From(builder.Configuration, fusedCollectionNames.Count + 1));
builder.Services.AddPlatformConsumerTimeouts();

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 RRF 統合）
builder.Services.AddScoped<HybridSearchService>();

// FR-03, NFR-06, ADR-0016, [[IADR-0534]] (#1871): 応答へ載せた縮退の印を同じ符号で数える（0 が正常）。
// 埋め込みの縮退はこれまでログにしか無かった。全文側（`KeywordSearchMetrics`）は応答へ載せず、従来どおり計器だけで観る。
builder.Services.AddSingleton<SearchDegradationMetrics>();

// FR-03, FR-04, FR-10, FR-11, SC-02, ADR-0127 決定 3・4, ADR-0010, ADR-0018, [[IADR-0498]] (#1746 段 S2):
// **Claude による再順位付けの段**（検索結果の一覧と RAG 回答の候補の両方。出口 `FinishAsync` に挟まる）。
//
// 🔴 **既定オフ**（`Rerank:Enabled=false`）。無効なら段の型を **DI に登録しない** —— `HybridSearchService` の
// 省略可能な引数が null のまま残り、結果は段を足す前と同一である（二段検索と同じ「着脱可能な段」）。
// 有効なら、用途 `rerank` で LLM ゲートウェイだけを呼ぶ（east-west gRPC だけ。［2026-10-10 / [[IADR-0533]]］REST の並走は撤去した）。
// 計器（縮退の観測）は無効でも登録する（0 が「掛けていない」の観測になる）。
var rerank = (builder.Configuration.GetSection(SearchRerankOptions.SectionName).Get<SearchRerankOptions>()
    ?? new SearchRerankOptions()).Normalize();
builder.Services.AddSingleton(rerank);
builder.Services.AddSingleton<RerankMetrics>();
if (rerank.Enabled)
{
    builder.Services.AddSingleton<IRerankCompletionClient, GrpcRerankCompletionClient>();
    builder.Services.AddScoped<ISearchReranker, ClaudeSearchReranker>();
}

// FR-04, FR-14, FR-17, UC-10, ADR-0035 決定 1・2, ADR-0018 (#970): 二段検索の段（グラフ近傍展開）。
//
// 🔴 **既定オフ・opt-in である**（ADR-0035 決定 2）。既定では段の型を **DI に登録しない** ——
// フラグを見て中で分岐するのではなく、**構成そのものが素のハイブリッド検索に戻る**。
// これが ADR-0018 / FR-14 の「着脱可能な段」の実現形であり、既存 RAG との A/B 比較の単位である。
//
// **`pipeline.json` の段としては宣言しない。** あの機構（`AddPlatformWolverineStep`）は
// 入力イベント型を持つ**購読段**専用であり、同期の検索経路には入力イベントが無い。
// 載せるには存在しないイベント型を捏造することになり、`input` 照合が意味を失う。
var graphExpansion = builder.Configuration
    .GetSection(GraphExpansionOptions.SectionName).Get<GraphExpansionOptions>()
    ?? new GraphExpansionOptions();
builder.Services.AddSingleton(graphExpansion.Normalize());

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0426]] 追記 1 (#1635): gRPC `DocumentSearch/Search` の
// 本文の利用者文脈を信じる呼び出し元（クライアント識別子の許可集合）。**未構成なら `aianalysis-service` だけ。構成したら置き換える。**
// 配列でなく 1 つの値が書かれていたら起動を止める（静かに既定へ戻さない。#1658）。
DocumentSearchRelayOptions.ThrowIfScalar(builder.Configuration);
builder.Services.Configure<DocumentSearchRelayOptions>(
    builder.Configuration.GetSection(DocumentSearchRelayOptions.SectionName));

// FR-04, FR-05, NFR-09, 計画 ADR-0086 決定 1, [[IADR-0417]] 追記 1 (#1636): gRPC `AttributeValues/ListValues` の
// 本文の利用者文脈を信じる呼び出し元。**未構成なら `bff` だけ。構成したら置き換える**（`DocumentSearch:` とは別の集合）。
// 配列でなく 1 つの値が書かれていたら起動を止める（静かに既定へ戻さない）。
RetrievalService.Features.Search.AttributeValues.AttributeValuesRelayOptions.ThrowIfScalar(builder.Configuration);
builder.Services.Configure<RetrievalService.Features.Search.AttributeValues.AttributeValuesRelayOptions>(
    builder.Configuration.GetSection(RetrievalService.Features.Search.AttributeValues.AttributeValuesRelayOptions.SectionName));

// FR-16, NFR-09, 計画 ADR-0117 決定 3 (#1611): MCP のツールの実行口の本文の利用者文脈を信じる呼び出し元。
// **未構成なら `mcp-server` だけ。構成したら置き換える**（他の面の集合とは別）。配列でない値なら起動を止める。
RetrievalService.Features.McpTools.Execute.McpToolExecutionRegistration.AddMcpToolExecution(builder.Services, builder.Configuration);

// FR-03, FR-04, FR-05, NFR-09, UC-01, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034 決定 1,
// ADR-0075, [[IADR-0044]], [[IADR-0379]] 決定 5, [[IADR-0410]], [[IADR-0416]] (#1339):
// 🔴 **本サービスが自分で ABAC 許可スコープを解決する。**
//
// 従前、権限の根拠は「呼び出し元が本文で送ってきた `Scope`」だった ——
// **ネットワーク到達可能な相手が任意の scope を主張できた**（[[IADR-0410]] が gRPC 面について
// 明示的に拒んだ形が、REST 面には入っていなかった）。
//
// 🔴 **`IHttpContextAccessor` は無条件で要る。** 従前は二段検索（`graphExpansion.Enabled`）の
// ときだけ登録していたが、**利用者を読むのは検索そのものになった** ——
// 段の有無で権限の根拠が変わってはならない。
//
// ［2026-10-10 / [[IADR-0533]]］解決の輸送は east-west gRPC だけである（REST `POST /authz/scope` の並走は撤去した。
// `WikiService` / `GraphService` と同型）。`Services:AuthorizationServiceGrpc` が無ければ deny-by-default へ倒れる。
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthzScopeGrpcClient(builder.Configuration);
builder.Services.AddScoped<ISearchAccessResolver, SearchAccessResolver>();

// FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0417]] (#1255):
// east-west gRPC の h2c リスナ（`Grpc:Port`。**未設定なら立てない**）。
// HTTP/1.1 のポート（REST・/health/*・introspection）はそのまま残り、readiness も 8080 のままである。
builder.AddPlatformGrpcListener();

if (graphExpansion.Enabled)
{
    // FR-04 / FR-05 / NFR-09 / NFR-16, ADR-0029, ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1・3,
    // 計画 ADR-0089 決定 1, [[IADR-0379]] 決定 4, [[IADR-0410]], [[IADR-0533]] 決定 4 (#1255): 近傍展開の輸送は east-west gRPC だけである。
    // ［2026-10-10］REST の `GraphServiceNeighborExpander`（`/graph/{id}/neighbors`・利用者の `Authorization` を転送）は撤去した ——
    // gRPC の入口（AI 分析 → 検索）からは転送できる利用者の資格情報が無く、REST 実装は呼ばずに警告するだけだった（[[IADR-0426]] 決定 2）。
    // 🔴 **GraphService が自分で ABAC を解決する**（ADR-0034 決定 1）—— 利用者文脈は本文で運ぶ。
    // `Services:GraphServiceGrpc` が無ければ生成クライアントは常に `UNAVAILABLE` を受け取り、近傍展開は縮退する（一次の結果のまま）。
    builder.Services.AddGraphNeighborsGrpcClient(builder.Configuration);
    builder.Services.AddScoped<IGraphNeighborExpander, GrpcGraphNeighborExpander>();
    builder.Services.AddScoped<IHybridSearchService, GraphExpandingSearchService>();
}
else
{
    builder.Services.AddScoped<IHybridSearchService>(sp => sp.GetRequiredService<HybridSearchService>());
}

// FR-14, ADR-0018 / #1016: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// 🔴 ADR-0027, ADR-0057 / #1016: DocumentDeleted を購読し、検索索引から当該文書のチャンクを削除する。
// 本サービス初のメッセージング導入であり、最初から Wolverine である（MassTransit は選べない ——
// backend-library-baseline 非掲載のため新規参照は即 fail。ADR-0030）。
// NFR, ADR-0027, #1022: ブローカ接続。**既定資格情報をイメージへ焼かない** —— appsettings.json からも
// 撤去したため、構成が注入されていなければここで落ちる（注入漏れが「既定の資格情報で接続成功」へ
// 倒れない。#1012 / IADR-0286 の DB と同型。IADR-0291）。**1 サービス 1 解決点にする。**
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（k8s は helm の global.messaging、compose は x-rabbit-env が注入する）。"
        + " 既定値は持たない —— 未注入をブローカへの接続失敗として現れさせないためである。");

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "retrieval-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var step = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);

    // 手順 3（購読側の束ね）/ #992: 自分のキューをイベント型名の fan-out exchange へ束ねる。
    // **キュー名を分けるだけでは何も届かない** —— 束ねて初めて発行が届く。
    // FR-14 / #1801: 段宣言（step）を渡す。queue 宣言があればそれ、無ければイベント型名で束ね・購読し、
    // `enabled: false` の段ではキューを束ねずリスナーも立てない（WolverineExtensions の段宣言版）。
    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision()
        .BindPlatformQueue<DocumentDeleted>("retrieval-service", step);

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う
    // （fan-out の保存: wiki-service / graph-service と別キューになりサービス名前置で分かれる）。
    opts.ListenToPlatformQueue<DocumentDeleted>("retrieval-service", step);

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。retrieval-delete 段（#1016）と、
// 選択中の合成可能ポート（ベクトルDB・埋め込み）を申告する。メッシュ内部限定で公開する。
// FR-04, FR-17 (#970): 二段検索の段は**有効なときだけ**ポートとして申告する。
// **段が入っているかどうかを外から読めること**が A/B 比較の前提である
// （応答は同じ形なので、結果だけを見ても段の有無は判らない）。
builder.Services.AddPlatformIntrospection("retrieval-service", pipeline,
    i =>
    {
        i.AddWolverineStep<DocumentDeletedConsumer>();
        i.AddPort("vector-store", nameof(QdrantVectorStore), $"qdrant:{qdrantPort}")
         .AddPort("embedding", nameof(LlmGatewayGrpcEmbeddingService), "llm-gateway");

        if (graphExpansion.Enabled)
            i.AddPort("graph-expansion", nameof(GrpcGraphNeighborExpander),
                builder.Configuration[GraphNeighborsGrpcClientExtensions.AddressKey] ?? "(未構成)");

        // FR-03, ADR-0127 決定 3, [[IADR-0498]] 決定 11 (#1746 段 S2): 再順位付けの段も**有効なときだけ**申告する
        // （応答の形は同じなので、段の有無は外から申告でしか読めない）。
        if (rerank.Enabled)
            i.AddPort("search-rerank", nameof(ClaudeSearchReranker), "llm-gateway");
    });

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapSearchEndpoints();
// FR-16, ADR-0024 §2: MCP ツール定義の自己申告（メッシュ内部限定。#1020）。
app.MapMcpToolEndpoints();

// FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, 計画 ADR-0086 決定 1,
// [[IADR-0379]], [[IADR-0410]], [[IADR-0416]], [[IADR-0417]] (#1255):
// 権限内属性値の照会の east-west gRPC 面。
// 🔴 **REST の口は残す**（［2026-10-10 / #1255・[[IADR-0533]]］east-west の REST 呼び出し元＝BFF の属性値照会は撤去した。受け口の撤去は残余）。
// 🔴 **本体は REST と同じ関数を通る**（`AttributeValuesEndpoint.ListAsync`）。
app.MapGrpcService<AttributeValuesGrpcService>();

// FR-03, FR-04, FR-05, FR-07, FR-17, NFR-09, NFR-16, ADR-0029, ADR-0034 決定 1, ADR-0035 決定 2,
// ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2, ADR-0089 決定 1,
// [[IADR-0379]], [[IADR-0410]], [[IADR-0416]], [[IADR-0426]] (#1255):
// ハイブリッド検索の east-west gRPC 面（呼び出し元は AI 分析の RAG 文脈収集）。
// 🔴 **REST の口は残す**（north-south の `POST /search` でもある。［2026-10-10 / #1255・[[IADR-0533]]］east-west の REST 呼び出し元は撤去した）。
// 🔴 **本体は REST と同じ関数を通る**（`SearchEndpoint.ExecuteAsync`）。
// 🔴 **利用者の JWT はこの面を通らない** —— 利用者文脈は本文で運ばれ、受け口が自分で解決する。
app.MapGrpcService<DocumentSearchGrpcService>();

app.Run();

public partial class Program { }
