using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.Catalog;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.Documents.Catalog;

// FR-19, ADR-0061 決定 2・4, [[IADR-0396]] 決定 5, [[IADR-0455]] 決定 1 (#1471):
// **再正規化で個人資料の露出が外れたら、撤収のイベントが出る。**
//
// 再正規化（`DocumentNormalizedConsumer`）は属性を辞書ごと差し替えるので、管理者の全置換と同じく
// ON → OFF を作り得る。「今」索引可のときだけ出す単純な門を当てると**消させるためのイベントが弾かれ、
// 本文が索引とグラフに残る**。PR #1473 のフェーズ末監査で、この経路だけ撤収を固定する試験が無く
// 単純な門へ戻す変異（M7）が生き残ると指摘された。
//
// 🔴 **陰性（全 OFF のまま再正規化しても出ない）は陽性（ON → OFF で出る）と対で置く。**
// 片方だけだと「常に出す」「常に出さない」実装のどちらかが通ってしまう。
//
// 器は `NormalizedBodyPresenceTests` と同じ MassTransit in-memory ハーネスである。
[Trait("TestKind", "Unit")]
public sealed class NormalizedExposureWithdrawalTests
{
    private const string Owner = "normalized-withdrawal-owner";

    private sealed class RecordingUpdatedPublisher : IDocumentUpdatedPublisher
    {
        public List<Dictionary<string, string>> Attributes { get; } = [];

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null,
            CancellationToken ct = default)
        {
            Attributes.Add(new Dictionary<string, string>(attributes));
            return Task.CompletedTask;
        }
    }

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

    private static Dictionary<string, string> PrivateNoteAttributes(string searchExposure) => new()
    {
        [DocumentScopes.Key] = DocumentScopes.PrivateNote,
        ["owner"] = Owner,
        [DocumentExposure.SearchKey] = searchExposure,
        [DocumentExposure.GraphKey] = DocumentExposure.Excluded,
        [DocumentExposure.AiKey] = DocumentExposure.Excluded,
    };

    // 既存の個人資料を台帳へ置いてから、属性を差し替える `DocumentNormalized` を 1 通消費させる。
    private static async Task<(RecordingUpdatedPublisher Publisher, DocumentDbContext Db, ServiceProvider Provider)>
        RenormalizeAsync(Guid id, Dictionary<string, string> before, Dictionary<string, string> after, string newTitle)
    {
        var ct = TestContext.Current.CancellationToken;
        var publisher = new RecordingUpdatedPublisher();
        // 🔴 名前は 1 度だけ決める。ラムダの中で生成すると DbContext ごとに別の DB になり、
        //    台帳へ置いた既存の個人資料が consumer から見えない（新規作成の経路を通ってしまう）。
        var dbName = $"withdrawal-{Guid.NewGuid():N}";
        var provider = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddMetrics()
            .AddDbContext<DocumentDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IngestTagMetrics>()
            .AddSingleton<IObjectStorageClient, UnresolvableStorage>()
            .AddSingleton<IDocumentUpdatedPublisher>(publisher)
            .AddMassTransitTestHarness(x => x.AddConsumer<DocumentNormalizedConsumer>())
            .BuildServiceProvider(validateScopes: true);

        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<DocumentDbContext>();
            db.Documents.Add(Document.CreateNormalized(id, "再正規化の前",
                $"storage://knowledge-normalized/{id:N}/document.md", before));
            await db.SaveChangesAsync(ct);
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        await harness.Bus.Publish(new DocumentNormalized(
            DocumentId: id,
            SourceId: Guid.NewGuid(),
            Title: newTitle,
            MarkdownUri: $"storage://knowledge-normalized/{id:N}/document.md",
            AssetUris: [],
            Attributes: after,
            Tags: [],
            NormalizedAt: DateTimeOffset.UtcNow), ct);
        (await harness.Consumed.Any<DocumentNormalized>(ct)).Should().BeTrue();
        // 消費されたことと成功したことは別である（fault でも Consumed には載る）。
        (await harness.Published.Any<Fault<DocumentNormalized>>(ct))
            .Should().BeFalse("consumer が落ちていれば発行の有無の検査は無意味になる");

        var scope = provider.CreateScope();
        return (publisher, scope.ServiceProvider.GetRequiredService<DocumentDbContext>(), provider);
    }

    [Fact]
    public async Task 再正規化で露出が外れた個人資料は撤収のイベントが発行される()
    {
        var id = Guid.NewGuid();
        var (publisher, _, provider) = await RenormalizeAsync(id,
            before: PrivateNoteAttributes(DocumentExposure.Included),
            after: PrivateNoteAttributes(DocumentExposure.Excluded),
            newTitle: "再正規化の後");
        await using var _ = provider;

        publisher.Attributes.Should().ContainSingle(
            "ON → OFF は索引からの削除まで及ぶ。再正規化でもイベントが出ないと撤収の契機が無い")
            .Which.Should().Contain(DocumentExposure.SearchKey, DocumentExposure.Excluded);
    }

    [Fact]
    public async Task 全てOFFの個人資料を再正規化しても発行されない()
    {
        var id = Guid.NewGuid();
        var (publisher, db, provider) = await RenormalizeAsync(id,
            before: PrivateNoteAttributes(DocumentExposure.Excluded),
            after: PrivateNoteAttributes(DocumentExposure.Excluded),
            newTitle: "全OFFのまま再正規化");
        await using var _ = provider;

        // 陽性対照: 再正規化そのものは台帳へ届いている（何も起きなかったから出なかった、ではない）。
        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc!.Title.Should().Be("全OFFのまま再正規化");

        publisher.Attributes.Should().BeEmpty(
            "3 つとも OFF の資料はイベントそのものを出さない（索引に存在しないまま保つ）");
    }
}
