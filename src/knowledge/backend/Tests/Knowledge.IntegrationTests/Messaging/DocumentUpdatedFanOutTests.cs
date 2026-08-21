using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IngestionService.Worker.Foundation.Ports;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;

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
[Trait("Category", "Integration")]
public sealed class DocumentUpdatedFanOutTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    // 両ホストが受信を報告する先。ホストは別々の DI コンテナを持つので、テスト側の
    // シングルトンを両方へ注入して待ち合わせる。
    private readonly RecordingProbe _probe = new();

    private IngestionServiceFactory _ingestionRoot = null!;
    private WikiServiceFactory _wikiRoot = null!;
    private WebApplicationFactory<global::IngestionService.Worker.IngestionServiceTestMarker> _ingestion = null!;
    private WebApplicationFactory<global::WikiService.Api.WikiServiceTestMarker> _wiki = null!;
    private HttpClient _ingestionClient = null!;
    private HttpClient _wikiClient = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;

        _ingestionRoot = new IngestionServiceFactory(postgres, rabbit);
        _wikiRoot = new WikiServiceFactory(postgres, rabbit);

        // #887 設計判断 2: ポートの差し替えは WithWebHostBuilder（派生ファクトリ）で行う。
        // 共有の IntegrationTestFactory.cs を編集すると他のテストへ波及するため触らない。
        //
        // 🔴 差し替えるのは**外向きのポート（Qdrant / LLM ゲートウェイ / Wiki.js）だけ**である。
        // メッセージングの配線（AddMassTransit / AddPlatformPipelineStep / UsePlatformRetry /
        // ConfigureEndpoints）は 1 行も差し替えない —— 本テストが試験したいのは**トポロジ**であり、
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
        // 明示的に起こす。** ここを省くと片方がまだキューをバインドしておらず、手順 3 が正しくても
        // テストが落ちる（偽陽性）。バインド完了までの待機は基底が設定する
        // MassTransitHostOptions.WaitUntilStarted=true が担う（issue #33）。
        _ingestionClient = _ingestion.CreateClient();
        _wikiClient = _wiki.CreateClient();

        await using var scope = _wiki.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _ingestionClient?.Dispose();
        _wikiClient?.Dispose();
        if (_ingestion is not null) await _ingestion.DisposeAsync();
        if (_wiki is not null) await _wiki.DisposeAsync();
        if (_ingestionRoot is not null) await _ingestionRoot.DisposeAsync();
        if (_wikiRoot is not null) await _wikiRoot.DisposeAsync();
    }

    // ADR-0027 手順 3: 1 回の発行で **両方の購読者**が受信することを固定する。
    // 手順 3 を誤って競合コンシューマ化すると、片方の待ち受けがタイムアウトしてここが落ちる。
    [DockerFact]
    public async Task PublishOnce_BothSubscribersReceive()
    {
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

        // 発行は 1 回だけ。どちらのホストのバスから出しても exchange は同じ
        // （Knowledge.Contracts.Events:DocumentUpdated）なので、発行側の選択は結果に影響しない。
        await using (var scope = _wiki.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBus>().Publish(evt);
        }

        // ── 購読者 1: IngestionService（終端副作用 = ベクトルストアへの upsert）
        //
        // 🔴 Qdrant / LLM ゲートウェイをコンテナで立てていないため、ここだけ記録フェイクである。
        // 本物の永続化を見ていないという意味で妥協であり、そう明記しておく。
        var ingested = await _probe.IngestionUpserts.WaitAsync(docId, TimeSpan.FromSeconds(30));
        ingested.Should().BeTrue(
            "IngestionService が DocumentUpdated を受信し取り込みを実行すること"
            + "（受信しなかった場合、手順 3 の競合コンシューマ化を疑う）");

        // ── 購読者 2: WikiService（終端副作用 = wiki_svc への行 upsert）
        //
        // こちらは Testcontainers Postgres への**本物の永続化**を見る。
        var synced = await WaitForWikiPageAsync(docId, TimeSpan.FromSeconds(30));
        synced.Should().BeTrue(
            "WikiService が DocumentUpdated を受信し Wiki 同期メタデータを永続化すること"
            + "（受信しなかった場合、手順 3 の競合コンシューマ化を疑う）");
    }

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
        DateTimeOffset? updatedAt = null, CancellationToken ct = default)
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
