using System.Net.Http.Json;
using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using RetrievalService.Infrastructure.ExternalServices;
using Wolverine;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-03, FR-21, ADR-0009, ADR-0016, [[IADR-0390]] (#1247):
// **取り込み → 索引 → 検索ヒット**を 1 本で通す段間結合テスト（層 1）。
//
// ## なぜ必要か
//
// 単体テストは 98 件あるが（陽性対照。`RetrievalService/Tests` の検索走査）、**そのどれも
// 取り込み側の書き込みを経由していない。** 索引へ入れた点が検索に当たるかは in-repo で
// 1 度も測られておらず、稼働 dev クラスタで検索が全件 0 件になった事故（#1215）を
// リポジトリの中では再現も検知もできなかった。
//
// ## 🔴 Docker を要さない。**PR の CI で必ず走る。**
//
// トレイトは `TestKind=Integration` **だけ**である（[[IADR-0368]] 決定 1）。
// **`Category=Integration` を付けてはならない** —— `ci.yml` の `--filter "Category!=Integration"` が
// PR から落とすため、本テストは「書いたが走らない」に退化する（#1247 が名指しした再生産の型）。
// 実 Qdrant を要する層 2 は `IngestToSearchQdrantTests` が `Category=Integration` で担う。
//
// ## 何を測り、何を測らないか
//
// 測る: 本文取得 → 分割 → 埋め込み（機密区分ルーティング）→ **索引への書き込み** →
//       **索引からの読み出し** → ハイブリッド検索の合成 → 応答。
// 測らない:
//   - **ブローカの配送**（`DocumentUpdatedFanOutTests` が実 RabbitMQ で担当）。
//   - **2 つの Qdrant アダプタのペイロード表現の一致**（層 2 の担当。ここでは橋が自分と一致するだけ）。
//   - **NFR「登録から 15 分以内に検索へ反映」**（同期呼び出しなので原理的に測れない。[[IADR-0390]] 決定 4）。
[Trait("TestKind", "Integration")]
public sealed class IngestToSearchInProcessTests : IAsyncLifetime
{
    // 書き手と読み手が共有する索引の実体（RetrievalService の本番コードにあるポート実装）。
    private readonly InMemoryVectorStore _index = new();
    private SharedIndexIngestionVectorStore _writes = null!;
    private RetrievalHost _retrieval = null!;
    private HttpClient _client = null!;

    // ADR-0016: 機密区分 public のモデル別コレクション名（本テスト内で閉じた値）。
    private const string Collection = "knowledge_chunks_test_public";

    // 本文に含める語（検索で当てにいく）と、本文にも題名にもタグにも**含まれない**語（陰性対照）。
    private const string PresentTerm = "ホログラフィック索引";
    private const string AbsentTerm = "アンチグラビティ";

    public ValueTask InitializeAsync()
    {
        _writes = new SharedIndexIngestionVectorStore(_index);
        _retrieval = new RetrievalHost(_index);
        _client = _retrieval.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_retrieval is not null) await _retrieval.DisposeAsync();
    }

    // 🔴 **本試験の中心。0 件で緑にならない。**
    [Fact]
    public async Task IngestedDocument_IsFoundBySearch()
    {
        var documentId = Guid.NewGuid();

        await IngestAsync(documentId,
            title: "段間結合テストの文書",
            body: $"# 段間結合テストの文書\n\n本文には {PresentTerm} という語が含まれる。");

        // 索引への書き込みが実際に起きたこと（段 5）。**ここで止まっていれば下の 0 件は
        // 「検索が壊れている」ではなく「そもそも入っていない」である** —— 区別できるように
        // 段ごとに主張する。
        _writes.WrittenCollections.Should().NotBeEmpty("取り込みが索引へ書き込むこと");
        _writes.WrittenCollections.Should().AllBe(Collection,
            "ADR-0016 の機密区分ルーティングが決めたコレクションへ書くこと"
            + "（読み書き先の不一致は #1215 で検索が全件 0 件になった一因である）");

        // 検索（段 6・7）。**本番の POST /search をそのまま叩く。**
        // 既定（ハイブリッド）と全文の両方で当たることを見る —— ハイブリッドだけだと、
        // 全文側が丸ごと壊れていても意味側が拾って緑になり得る。
        foreach (var mode in new string?[] { null, SearchModes.Keyword })
        {
            var hits = await SearchAsync(PresentTerm, mode);

            hits.Should().NotBeEmpty(
                $"取り込んだ文書が検索でヒットすること（mode={mode ?? "hybrid"}）。"
                + "0 件なら、取り込みは索引へ書いたのに検索が同じ索引を読めていない");
            hits.Should().Contain(r => r.DocumentId == documentId,
                "ヒットした点が取り込んだ当の文書であること");
        }
    }

    // 陰性対照。**上のテストと対で置く** —— 「何を検索しても当たる」実装でも上は緑になるため、
    // 単独では「検索が効いている」ことの証拠にならない。
    //
    // 🔴 **全文モードで測る。** 索引の実体（`InMemoryVectorStore`）の意味検索は
    // **問い合わせベクトルを見ずに全件へスコア 0.9 を付ける**（当該クラスのコメントが明示している）。
    // 既定のハイブリッドで陰性を主張すると、**その test double の性質**を測ってしまい、
    // 検索の欠陥とは無関係に落ちる。語が効いているかを測れるのは全文側である。
    [Fact]
    public async Task UnrelatedTerm_DoesNotHit()
    {
        await IngestAsync(Guid.NewGuid(),
            title: "段間結合テストの文書",
            body: $"# 段間結合テストの文書\n\n本文には {PresentTerm} という語が含まれる。");

        // 陽性対照（走査が生きていること）。これが 0 件なら下の 0 件は何の証拠にもならない。
        (await SearchAsync(PresentTerm, SearchModes.Keyword)).Should().NotBeEmpty(
            "陽性対照: 本文にある語では当たること");

        var hits = await SearchAsync(AbsentTerm, SearchModes.Keyword);

        hits.Should().BeEmpty(
            $"本文・題名・タグのいずれにも無い語（{AbsentTerm}）では当たらないこと。"
            + "当たるなら、検索が語を見ずに全件返している");
    }

    // FR-05, ADR-0016: **索引から消したものは当たらない。**
    // 取り込みの冪等化（`DeleteByDocumentFromAllAsync`）が段 6 側にも効いていることを見る ——
    // ここが効かないと、機密区分変更で旧コレクションに残った点が ABAC を跨いで当たる。
    [Fact]
    public async Task ReingestWithoutTerm_RemovesTheOldHit()
    {
        var documentId = Guid.NewGuid();

        await IngestAsync(documentId, "段間結合テストの文書",
            $"# 段間結合テストの文書\n\n本文には {PresentTerm} という語が含まれる。");
        (await SearchAsync(PresentTerm, SearchModes.Keyword))
            .Should().NotBeEmpty("陽性対照: 1 回目の取り込みで当たること");

        // 同じ文書 ID で、その語を含まない本文を取り込み直す。
        await IngestAsync(documentId, "段間結合テストの文書",
            "# 段間結合テストの文書\n\n本文は書き換えられた。");

        (await SearchAsync(PresentTerm, SearchModes.Keyword)).Should().BeEmpty(
            "再取り込みで旧チャンクが索引から消えること（残ると検索が古い本文に当たり続ける）");
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, [[IADR-0358]] (#1193):
    // **本文なしの文書はメタデータ点 1 つで索引に載り、題名で当たる。**
    // かつ **本文抜粋は空**である（題名由来の索引テキストを本文として返さない）。
    [Fact]
    public async Task DocumentWithoutBody_IsFoundByTitle_ButReturnsNoExcerpt()
    {
        var documentId = Guid.NewGuid();

        await IngestAsync(documentId, title: $"{PresentTerm} の手引き", body: "   ");

        var hits = await SearchAsync(PresentTerm, SearchModes.Keyword);

        hits.Should().Contain(r => r.DocumentId == documentId,
            "本文が無くてもメタデータ点で索引に載り、題名の語で当たること");
        hits.Single(r => r.DocumentId == documentId).Text.Should().BeEmpty(
            "本文なしの点は抜粋を返さないこと（索引テキストは突合には使うが利用者へは返さない）");
    }

    // ── 段の駆動 ──────────────────────────────────────────

    // 本番の `DocumentUpdatedConsumer` をそのまま回す（購読の配線だけをブローカから外す）。
    private async Task IngestAsync(Guid documentId, string title, string body)
    {
        var consumer = new DocumentUpdatedConsumer(
            new FixedContentReader(body),
            new MarkdownChunkingService(),
            new DeterministicEmbeddingService(Collection),
            _writes,
            new RecordingCompletedPublisher(),
            NullLogger<DocumentUpdatedConsumer>.Instance);

        await consumer.Handle(
            new DocumentUpdated(
                DocumentId: documentId,
                Title: title,
                Status: "published",
                MarkdownUri: $"storage://knowledge/{documentId}.md",
                Attributes: new Dictionary<string, string> { ["confidentiality"] = "public" },
                Tags: ["段間結合"],
                UpdatedAt: DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
    }

    private async Task<List<SearchResultDto>> SearchAsync(string query, string? mode = null)
    {
        // FR-05: 検索には許可スコープが要る（**fail-closed**。渡さないと 0 件になる）。
        // 🔴 ここを省くと本テストは「索引が繋がっていない」と区別できない 0 件で落ちる ——
        // ABAC そのものは本テストの主張ではないので、全件許可の空フィルタを明示的に渡す。
        var resp = await _client.PostAsJsonAsync("/search",
            new SearchRequest(query, TopK: 10, Mode: mode,
                Scope: new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken);
        return body!.Results;
    }
}

// RetrievalService の**本番ホスト**を起こし、外向きの依存（Qdrant / LLM ゲートウェイ / ブローカ）
// だけを差し替える器。`RetrievalService.Tests.TestWebApplicationFactory` と同じ作法だが、
// 本プロジェクトは複数サービスを参照するのでマーカー型を使う（`Program` は CS0433 で衝突する）。
internal sealed class RetrievalHost(InMemoryVectorStore index)
    : WebApplicationFactory<global::RetrievalService.RetrievalServiceTestMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // 🔴 **`UseSetting` でなければ間に合わない。** `Program.cs` は `builder.Build()` より前に
        // `RabbitMq:ConnectionString` を読み、未設定なら起動時に落ちる（#1022）。
        // `ConfigureAppConfiguration` で足した値はその時点ではまだ見えない
        // （`IntegrationTestFactoryBase` が同じ理由で同じことをしている）。
        // **接続はしない** —— 下で外部トランスポートを全部落とすので、これは起動を通すためだけの値である。
        builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost:5672");

        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Qdrant:Host"] = "localhost",
                ["Qdrant:Port"] = "6334",
                ["Services:LlmGateway"] = "http://localhost:5007"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<QdrantClient>();
            services.RemoveAll<global::RetrievalService.Domain.Ports.IVectorStore>();
            // 🔴 **テストが作った 1 インスタンスを渡す。** ここを `AddSingleton<IVectorStore,
            // InMemoryVectorStore>()` にすると取り込み側と別の索引になり、
            // **本テストは必ず 0 件で落ちる**（あるいは 0 件を検索の欠陥と読み違える）。
            services.AddSingleton<global::RetrievalService.Domain.Ports.IVectorStore>(index);

            services.RemoveAll<global::RetrievalService.Domain.Ports.IEmbeddingService>();
            services.AddSingleton<global::RetrievalService.Domain.Ports.IEmbeddingService>(
                new QueryEmbeddingService());

            // 🔴 これが無いとホストの起動が実ブローカへ接続を試みてハングする。
            services.DisableAllExternalWolverineTransports();
        });
    }
}

// 検索語の埋め込み。**取り込み側と同じ写像**を使う —— 別の写像にすると
// ベクトル側が常に無関係になり、意味検索の経路が「効いていないのに緑」になる。
internal sealed class QueryEmbeddingService : global::RetrievalService.Domain.Ports.IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(DeterministicEmbeddingService.Vectorize(text));
}
