using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using IngestionService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Knowledge.IntegrationTests.Messaging;

// FR-02, FR-13, UC-04, ADR-0027 手順 3 / 手順 7 / 手順 8, #887:
// DocumentUpdated は fan-out（1 発行 → 2 購読者）である。
//
//   DocumentUpdated: 発行 [DocumentService] → 購読 [IngestionService, WikiService]
//
// 🔴 **移行チェックリスト手順 3（リスニングキュー名にサービス名を前置する）を誤り、2 購読者が
// 同一キューを共有すると、RabbitMQ の競合コンシューマになって片方だけがメッセージを受け取る。**
// この退行は例外もログも出さない —— publisher confirms は成功を返し、受け取らなかった側は
// 「そもそも来なかった」ので何も書かない。ビルドもユニットテストも緑のまま業務イベントが半分消える。
//
// 本テストは **2 つの購読者ホストを同時に立て**、1 回だけ発行し、**両方が仕事をしたこと**を
// 終端の副作用で確かめる。手順 3 の退行を機械で捕まえられる唯一の場所である。
//
// 🔴 ADR-0027 / E3b: 両購読者は Wolverine（本番の Program 配線そのまま）。発行は E1 の
// RawDocumentFetchedEdge と同じ形の**専用発行ホスト**が行う —— 実行ごとに一意な exchange を宣言し、
// 本番の命名（WolverineExtensions.PlatformQueueName）から導いた **2 つの購読キューへ束縛**して
// 1 回だけ発行する。購読側を先に起こす（束縛の無い exchange への publish は黙って落ちる）。
//
// 🔴 ［2026-08-30 / #1038 #1059］**QueueOverrideFanOutTests と同時に走らせない。**
// 赤になった 2 run では両クラスの実行窓が重なり、緑の 3 run では 20〜51 秒離れていた。
// 根拠と交絡の断りは FanOutTestCollection.cs にある（**待ち時間は伸ばしていない**）。
//
// 🔴 ［2026-08-30 追記 / #1073］**直列化では直らなかった。真因はハンドラ探索アセンブリである。**
// Wolverine の `ApplicationAssembly` は**プロセス全体で 1 つの静的値**で、最初に起動したホストが固定する。
// 1 プロセスで IngestionService と WikiService のホストを同時に立てる本テストでは、
// **後発のホストが相手のコンシューマを拾い、自分のコンシューマを拾わない**。
// 受信すると依存未解決で `NotSupportedException` になり、Wolverine はログ 1 行を出して
// **ack し捨てる**（例外も再配信も DLQ も残らない）。当チェーンに居る**正しいハンドラも道連れで走らない**。
// 対処は共通ヘルパ（`AddPlatformWolverineStep`）での明示固定であり、**待ち時間は 1 秒も伸ばしていない**。
[Collection(FanOutTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DocumentUpdatedFanOutTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    // 両ホストが受信を報告する先。ホストは別々の DI コンテナを持つので、テスト側の
    // シングルトンを両方へ注入して待ち合わせる。
    private readonly RecordingProbe _probe = new();

    private IngestionServiceFactory _ingestionRoot = null!;
    private WikiServiceFactory _wikiRoot = null!;
    private IHost? _publisher;
    private WebApplicationFactory<global::IngestionService.IngestionServiceTestMarker> _ingestion = null!;
    private WebApplicationFactory<global::WikiService.WikiServiceTestMarker> _wiki = null!;
    private HttpClient _ingestionClient = null!;
    private HttpClient _wikiClient = null!;

    // #1038 切り分け ①②: 購読が始まるまでに掛かった時間。テスト本体が出力と失敗メッセージへ載せる。
    private TimeSpan _ingestionReady;
    private TimeSpan _wikiReady;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;

        _ingestionRoot = new IngestionServiceFactory(postgres, rabbit);
        _wikiRoot = new WikiServiceFactory(postgres, rabbit);

        // #887 設計判断 2: ポートの差し替えは WithWebHostBuilder（派生ファクトリ）で行う。
        // 共有の IntegrationTestFactory.cs を編集すると他のテストへ波及するため触らない。
        //
        // 🔴 差し替えるのは**外向きのポート（Qdrant / LLM ゲートウェイ / Wiki.js）だけ**である。
        // メッセージングの配線（UseWolverine / AddPlatformWolverineStep / ListenToPlatformQueue /
        // UsePlatformMessagingDefaults）は 1 行も差し替えない —— 本テストが試験したいのは**トポロジ**であり、
        // 取り込み・同期の業務ロジックはユニットテストが担う。
        _ingestion = _ingestionRoot.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton(_probe);
            services.RemoveAll<IIngestionVectorStore>();
            services.AddSingleton<IIngestionVectorStore>(sp =>
                new RecordingVectorStore(sp.GetRequiredService<RecordingProbe>()));
            services.RemoveAll<IDocumentContentReader>();
            services.AddSingleton<IDocumentContentReader, StubContentReader>();
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService, StubEmbeddingService>();
        }));

        _wiki = _wikiRoot.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton(_probe);
            services.RemoveAll<IWikiJsClient>();
            services.AddSingleton<IWikiJsClient, StubWikiJsClient>();
            services.RemoveAll<IWikiContentReader>();
            services.AddSingleton<IWikiContentReader, StubWikiContentReader>();
        }));

        // #887 設計判断 4: WebApplicationFactory はホストを遅延起動する。**publish の前に両ホストを
        // 明示的に起こす。** ここを省くと片方がまだキュー（AutoProvision）を作っておらず、
        // 発行ホストの束縛が空振りする。
        _ingestionClient = _ingestion.CreateClient();
        _wikiClient = _wiki.CreateClient();

        await using (var scope = _wiki.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // E3b: 発行ホスト（E1 の RawDocumentFetchedEdge と同じ作法）。購読側の後に起こす。
        // exchange は実行ごとに一意（固定名だと前回の束縛が残り、誤配線が「前回の状態に助けられて」
        // 緑になる —— W3 変異 M4 の実測）。キュー名は必ず手順 3 の適用点（共通ヘルパ）から導く。
        var exchange = $"e3b-fanout-{Guid.NewGuid():N}";
        var ingestionQueue = WolverineExtensions.PlatformQueueName("ingestion-service", nameof(DocumentUpdated));
        var wikiQueue = WolverineExtensions.PlatformQueueName("wiki-service", nameof(DocumentUpdated));

        // #1038: **購読が Accepting になるまで待ってから発行へ進む。**
        // ここを待たないと、テスト本体の 30 秒が「購読開始待ち ＋ 実処理」の両方を覆ってしまい、
        // 負荷が高い日に**実装が正しいのに落ちる**（かつ、どちらが遅いのか分からない）。
        _ingestionReady = await ListenerReadiness.WaitUntilAcceptingAsync(_ingestion.Services, ingestionQueue);
        _wikiReady = await ListenerReadiness.WaitUntilAcceptingAsync(_wiki.Services, wikiQueue);
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "document-service-under-test";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseRabbitMq(new Uri(rabbit.ConnectionString!))
                    .AutoProvision()
                    .DeclareExchange(exchange, ex =>
                    {
                        ex.BindQueue(ingestionQueue);
                        ex.BindQueue(wikiQueue);
                    });
                opts.PublishMessage<DocumentUpdated>().ToRabbitExchange(exchange);
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
        {
            await _publisher.StopAsync(TimeSpan.FromSeconds(10));
            _publisher.Dispose();
        }
        _ingestionClient?.Dispose();
        _wikiClient?.Dispose();
        if (_ingestion is not null) await _ingestion.DisposeAsync();
        if (_wiki is not null) await _wiki.DisposeAsync();
        if (_ingestionRoot is not null) await _ingestionRoot.DisposeAsync();
        if (_wikiRoot is not null) await _wikiRoot.DisposeAsync();
    }

    // ADR-0027 手順 3: 1 回の発行で **両方の購読者**が受信することを固定する。
    // 手順 3 を誤って競合コンシューマ化すると、片方の待ち受けがタイムアウトしてここが落ちる。
    [Fact]
    public async Task PublishOnce_BothSubscribersReceive()
    {
        DockerRequired.SkipUnlessAvailable();
        var docId = Guid.NewGuid();
        var evt = new DocumentUpdated(
            DocumentId: docId,
            Title: "fan-out 統合テスト文書",
            // WikiService の DocumentSyncConsumer は published / normalized 以外を早期 return する。
            // IngestionService は MarkdownUri が null なら早期 return する。**両方が実際に仕事をする
            // 入力**でなければ、受信していないのか黙って捨てたのかを区別できない。
            Status: "published",
            MarkdownUri: $"storage://knowledge/{docId}.md",
            Attributes: new Dictionary<string, string> { ["confidentiality"] = "public" },
            Tags: ["fanout"],
            UpdatedAt: DateTimeOffset.UtcNow);

        // 発行は 1 回だけ（専用発行ホスト。両購読キューへ束縛済みの exchange へ出す）。
        // #1038 切り分け ③: ここから先が**実処理**である。①② は InitializeAsync で済ませてある。
        var since = System.Diagnostics.Stopwatch.StartNew();
        await _publisher!.Services.GetRequiredService<IMessageBus>().PublishAsync(evt);

        // ── 購読者 1: IngestionService（終端副作用 = ベクトルストアへの upsert）
        //
        // 🔴 Qdrant / LLM ゲートウェイをコンテナで立てていないため、ここだけ記録フェイクである。
        // 本物の永続化を見ていないという意味で妥協であり、そう明記しておく。
        var ingested = await _probe.IngestionUpserts.WaitAsync(docId, ProcessingBudget);
        var ingestedAt = since.Elapsed;
        ingested.Should().BeTrue(
            "IngestionService が DocumentUpdated を受信し取り込みを実行すること"
            + "（受信しなかった場合、手順 3 の競合コンシューマ化を疑う）"
            + Measured());

        // ── 購読者 2: WikiService（終端副作用 = wiki_svc への行 upsert）
        //
        // こちらは Testcontainers Postgres への**本物の永続化**を見る。
        var synced = await WaitForWikiPageAsync(docId, ProcessingBudget);
        var syncedAt = since.Elapsed;
        synced.Should().BeTrue(
            "WikiService が DocumentUpdated を受信し Wiki 同期メタデータを永続化すること"
            + "（受信しなかった場合、手順 3 の競合コンシューマ化を疑う）"
            + Measured());

        // #1038 受け入れ基準①: **フル実行時の実到達時間を記録する。** 緑でも残す ——
        // 「30 秒に対して実際が何秒か」は落ちてからでは測れない。
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"[#1038] 購読開始 ingestion={_ingestionReady.TotalSeconds:F1}s wiki={_wikiReady.TotalSeconds:F1}s"
            + $" / 実処理 ingestion={ingestedAt.TotalSeconds:F1}s wiki={syncedAt.TotalSeconds:F1}s"
            + $" （実処理の予算 {ProcessingBudget.TotalSeconds:F0}s）");
    }

    // 🔴 **実処理だけの予算である。** 購読開始（①②）は InitializeAsync が別枠で待つ。
    // #1038 は「測らずに待ち時間を延ばす」ことを禁じているので、**この 30 秒は据え置く。**
    private static readonly TimeSpan ProcessingBudget = TimeSpan.FromSeconds(30);

    // 失敗時に「どの段が遅かったか」を assert のメッセージへ載せる。
    // ［2026-08-30 / #1038 #1059］**購読キュー名も併せて載せる** —— キューが 1 本に潰れていれば
    // 競合コンシューマ化、2 本に分かれていれば配送／処理側であり、**1 回の実走で分かれる**。
    private string Measured() =>
        $"。実測: 購読開始 ingestion={_ingestionReady.TotalSeconds:F1}s / wiki={_wikiReady.TotalSeconds:F1}s"
        + $"（購読は始まっていたので、これは実処理側の遅さか受信そのものの欠落である）"
        + $"。購読キュー: ingestion=[{ListenerReadiness.DescribeListeners(_ingestion.Services)}]"
        + $" / wiki=[{ListenerReadiness.DescribeListeners(_wiki.Services)}]"
        + $"。ハンドラ探索アセンブリ: ingestion={ListenerReadiness.DescribeHandlerDiscovery(_ingestion.Services)}"
        + $" / wiki={ListenerReadiness.DescribeHandlerDiscovery(_wiki.Services)}"
        + "（サービス名と食い違っていれば、そのホストは**相手のハンドラ**を拾っており、"
        + "受信したメッセージを例外も残さず ack して捨てている。#1073）";

    private async Task<bool> WaitForWikiPageAsync(Guid documentId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _wiki.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            if (await db.Pages.AsNoTracking().AnyAsync(p => p.DocumentId == documentId))
                return true;
            await Task.Delay(250);
        }
        return false;
    }
}

// ── 観測用の器 ──────────────────────────────────────────
//
// 2 つのホストは別々の DI コンテナを持つ。テスト側で生成したこのシングルトンを両方へ注入し、
// 「どちらのホストが仕事をしたか」を 1 か所で待ち合わせる。
public sealed class RecordingProbe
{
    public DocumentSignal IngestionUpserts { get; } = new();
}

// 特定の DocumentId が記録されるまで待つ。ポーリングではなく TaskCompletionSource で待つ。
public sealed class DocumentSignal
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _waiters = [];

    public void Signal(Guid documentId)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _seen.Add(documentId);
            _waiters.Remove(documentId, out waiter);
        }
        waiter?.TrySetResult();
    }

    public async Task<bool> WaitAsync(Guid documentId, TimeSpan timeout)
    {
        Task pending;
        lock (_gate)
        {
            if (_seen.Contains(documentId)) return true;
            if (!_waiters.TryGetValue(documentId, out var tcs))
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[documentId] = tcs;
            }
            pending = tcs.Task;
        }
        return await Task.WhenAny(pending, Task.Delay(timeout)) == pending;
    }
}

// ── 差し替えるポート（外向きの依存のみ） ────────────────────

// Qdrant を立てていないため、upsert の呼び出しを記録するだけのフェイク。
internal sealed class RecordingVectorStore(RecordingProbe probe) : IIngestionVectorStore
{
    public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        probe.IngestionUpserts.Signal(documentId);
        return Task.CompletedTask;
    }

    // #1193: 本文なしの文書のメタデータ点。**本試験の本文は非空**なのでここは呼ばれないが、
    // 呼ばれたら同じ観測点を立てる（fan-out の観測が本文の有無で消えないようにする）。
    public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
        string title, string indexText, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        probe.IngestionUpserts.Signal(documentId);
        return Task.CompletedTask;
    }

    public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        => Task.CompletedTask;
}

// storage:// を実取得できないため、チャンク化に渡せる本文を返すだけのスタブ。
internal sealed class StubContentReader : IDocumentContentReader
{
    public Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}\n\nfan-out 統合テストの本文。");
}

// LLM ゲートウェイを立てていないため、固定次元のベクトルを返すだけのスタブ。
// Embedded=true を返さないと DocumentUpdatedConsumer が upsert を行わず、観測点が消える。
internal sealed class StubEmbeddingService : IEmbeddingService
{
    public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        => Task.FromResult(new EmbeddingResult(new float[8], "knowledge_chunks_test", Embedded: true));
}

// Wiki.js を立てていないため、push を捨てるだけのスタブ。**DB 行の永続化は本物である。**
internal sealed class StubWikiJsClient : IWikiJsClient
{
    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StubWikiContentReader : IWikiContentReader
{
    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}\n\nfan-out 統合テストの本文。");
}
