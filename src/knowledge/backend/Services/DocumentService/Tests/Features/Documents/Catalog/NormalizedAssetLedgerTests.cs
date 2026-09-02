using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using DocumentService.Features.Documents.Catalog;

namespace DocumentService.Tests.Features.Documents.Catalog;

// FR-01, FR-12, UC-04, ADR-0057 決定 1, [[IADR-0296]]:
// **`DocumentNormalized.AssetUris` を台帳へ写す。**
//
// 🔴 従前、イベントは資産 URI を運んでいたが `DocumentNormalizedConsumer` が
// `CreateNormalized` / `ApplyNormalized` へ渡しておらず、**図表資産は台帳から辿れなかった**。
// 削除が届かない原因はここであり、ADR-0057 の受け入れ基準①は構造的に満たせなかった。
//
// 器は **MassTransit の in-memory ハーネス**である —— 本 consumer はサービス本体の
// テストホストには登録されていない（`IngestTagFilterTests` の実測どおり）ため、
// `ConsumeContext` を得るには consumer だけを載せた最小の provider を組むのが唯一の手である。
// RabbitMQ もテストサーバも要らない。
public sealed class NormalizedAssetLedgerTests
{
    private sealed class NoopUpdatedPublisher : IDocumentUpdatedPublisher
    {
        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    // 本文の指紋計算はストレージ解決を要求する。ここでは資産の台帳だけを見るので縮退させる。
    private sealed class UnresolvableStorage : IObjectStorageClient
    {
        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
        public bool CanResolve(string? uri) => false;
        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();
    }

    private static ServiceProvider BuildProvider(string dbName) =>
        new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            // IngestTagMetrics は IMeterFactory を要求する（Web ホストでは既定で入る）。
            .AddMetrics()
            .AddDbContext<DocumentDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IngestTagMetrics>()
            .AddSingleton<IObjectStorageClient, UnresolvableStorage>()
            .AddSingleton<IDocumentUpdatedPublisher, NoopUpdatedPublisher>()
            .AddMassTransitTestHarness(x => x.AddConsumer<DocumentNormalizedConsumer>())
            .BuildServiceProvider(validateScopes: true);

    private static DocumentNormalized Event(Guid id, string title, params string[] assets) => new(
        DocumentId: id,
        SourceId: Guid.NewGuid(),
        Title: title,
        MarkdownUri: $"storage://knowledge-normalized/{id:N}/document.md",
        AssetUris: [.. assets],
        Attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags: [],
        NormalizedAt: DateTimeOffset.UtcNow);

    // 新規登録: イベントが運んだ資産 URI が台帳へ入る。
    [Fact]
    public async Task 取り込みは資産URIを台帳へ書く()
    {
        var id = Guid.NewGuid();
        var fig = $"storage://knowledge-normalized/{id:N}/assets/fig-1.png";
        await using var provider = BuildProvider($"assets-{Guid.NewGuid():N}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Event(id, "図のある文書", fig), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
            .Should().BeTrue();
        // 🔴 消費されたことと成功したことは別である（fault でも Consumed には載る）。
        (await harness.Published.Any<Fault<DocumentNormalized>>(TestContext.Current.CancellationToken))
            .Should().BeFalse("consumer が落ちていれば台帳の検査は無意味になる");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc.Should().NotBeNull();
        doc!.AssetUris.Should().Equal([fig],
            "資産を台帳へ写さないと、削除は図表へ届かない（ADR-0057 受け入れ基準①）");
    }

    // 再正規化: 資産の集合は差し替わる（属性と同じ扱い。タグとは違う）。
    //
    // 🔴 **1 つのハーネスへ 2 通流さない。** 当初はそう書いて、単体では緑・knowledge 全件では
    // 赤になった。原因は待ち合わせの API ではなく**器の性質**である ——
    // `Consumed.Any(filter)` も `Consumed.SelectAsync` も、ハーネスの**非活動タイムアウト**が
    // 切れると「まだ来ていない」を「来ない」として打ち切る（実測: 2 通目が届かないまま
    // 列挙が 1 件で終了した）。全テスト同時実行で機械が混むと 1 通目と 2 通目の間の空きが
    // その窓を超え、**実装が正しいのに落ちる**。#1038 と同じ型である。
    //
    // **タイムアウトを延ばして逃げない。** 1 通目は「再正規化前の状態を作る」だけの前提であり、
    // バスを通す必要が無い。**前提は台帳へ直接置き、バスへ流すのは検査対象の 2 通目だけにする。**
    // ハーネスに空きが生じないので、負荷に依存する窓そのものが消える。
    [Fact]
    public async Task 再正規化は資産URIを差し替える()
    {
        var id = Guid.NewGuid();
        var first = $"storage://knowledge-normalized/{id:N}/assets/fig-1.png";
        var second = $"storage://knowledge-normalized/{id:N}/assets/fig-2.png";
        var dbName = $"assets-{Guid.NewGuid():N}";
        await using var provider = BuildProvider(dbName);

        // 前提: 資産 fig-1 を持つ正規化済み文書が既に在る（1 通目の消費と同じ状態）。
        using (var seed = provider.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var before = Event(id, "図のある文書", first);
            seedDb.Documents.Add(DocumentService.Domain.Document.CreateNormalized(
                before.DocumentId, before.Title, before.MarkdownUri, before.Attributes,
                assetUris: [.. before.AssetUris]));
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish(Event(id, "図のある文書（再変換）", second), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
            .Should().BeTrue();
        (await harness.Published.Any<Fault<DocumentNormalized>>(TestContext.Current.CancellationToken))
            .Should().BeFalse("consumer が落ちていれば台帳の検査は無意味になる");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc.Should().NotBeNull();
        doc!.AssetUris.Should().Equal([second],
            "再正規化は資産の集合を差し替える（追加ではない）");
    }

    // 資産を持たない文書（大多数）は空配列である —— 既存文書と同じ形になることを固定する。
    [Fact]
    public async Task 資産の無い文書は空配列になる()
    {
        var id = Guid.NewGuid();
        await using var provider = BuildProvider($"assets-{Guid.NewGuid():N}");
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Event(id, "図の無い文書"), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
            .Should().BeTrue();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc.Should().NotBeNull();
        doc!.AssetUris.Should().BeEmpty();
    }
}
