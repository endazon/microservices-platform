using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.Catalog;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.Documents.Catalog;

// FR-01, FR-06, ADR-0027, ADR-0141 決定 1 (a), [[IADR-0596]] 決定 5〜7 (#2102): カタログの受け手（`DocumentNormalized`）の順序。
// `DocumentNormalized` は少なくとも 1 回届き、順序は保証されない（再配送・デッドレターからの再投入・変換の再実行）。
// 古い再正規化が台帳の新しい状態を上書きしないこと、アーカイブ・削除の後に古い再正規化で戻らないこと、
// アーカイブ・削除より後の再正規化は従来どおり反映されること（ADR-0141 決定 1 (a)・データソースの再同期）を固定する。
[Trait("TestKind", "Unit")]
public sealed class CatalogNormalizationOrderingTests
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(1);
    private static readonly DateTimeOffset T3 = T1.AddMinutes(2);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public CountingStorage Storage { get; } = new();
        public RecordingUpdatedPublisher Published { get; } = new();
        public Guid DocId { get; } = Guid.NewGuid();

        public Harness()
        {
            var dbName = $"catalog-ordering-{Guid.NewGuid():N}";
            _provider = new ServiceCollection()
                .AddLogging()
                .AddMetrics()
                .AddDbContext<DocumentDbContext>(o => o.UseInMemoryDatabase(dbName))
                .AddSingleton<IngestTagMetrics>()
                .AddCatalogTimeoutsForTests()
                .BuildServiceProvider(validateScopes: true);
        }

        public async Task ConsumeAsync(DocumentNormalized ev)
        {
            using var scope = _provider.CreateScope();
            var sp = scope.ServiceProvider;
            await new DocumentNormalizedConsumer(
                    sp.GetRequiredService<DocumentDbContext>(), Published, Storage,
                    sp.GetRequiredService<IngestTagMetrics>(),
                    sp.GetRequiredService<ConsumerCallTimeouts>(),
                    sp.GetRequiredService<CatalogTimeouts>(),
                    sp.GetRequiredService<ILogger<DocumentNormalizedConsumer>>())
                .ConsumeAsync(ev, TestContext.Current.CancellationToken);
        }

        public async Task WithDbAsync(Func<DocumentDbContext, Task> act)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            await act(db);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Document? Doc()
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.AsNoTracking()
                .FirstOrDefault(d => d.Id == DocId);
        }

        public DocumentNormalized Normalized(string title, DateTimeOffset at, string confidentiality = "internal")
            => new(DocId, Guid.NewGuid(), title, $"storage://knowledge-normalized/{DocId:N}/document.md", [],
                new Dictionary<string, string> { ["confidentiality"] = confidentiality }, [], at);

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    // 受け入れ基準 8: 新しい再正規化（confidential）の後に古い再正規化（internal）が届いても台帳は新しいまま。
    // 古い版は本文を読まず、DocumentUpdated も出さない。
    [Fact]
    public async Task 保持中より古い再正規化は反映されない_厳格化後に緩和が復活しない()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("最初の版", T1, confidentiality: "internal"));      // 台帳へ作る
        await h.ConsumeAsync(h.Normalized("新しい版", T3, confidentiality: "confidential"));  // 再正規化で厳格化
        var reads = h.Storage.Reads;
        var published = h.Published.Events.Count;

        await h.ConsumeAsync(h.Normalized("古い版", T2, confidentiality: "public"));

        var doc = h.Doc()!;
        doc.Title.Should().Be("新しい版");
        doc.Attributes["confidentiality"].Should().Be("confidential");
        doc.LastNormalizedAt.Should().Be(T3);
        h.Storage.Reads.Should().Be(reads, "古い版は本文を読まない（本文の URI は文書 ID 固定で、読めば新しい本文の指紋になる）");
        h.Published.Events.Count.Should().Be(published, "古い版は下流へ何も出さない");
    }

    // 受け入れ基準 11: 同じ NormalizedAt の再配送は反映する（冪等）。
    [Fact]
    public async Task 同じ版の再配送は反映される()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("版", T1));
        await h.ConsumeAsync(h.Normalized("版", T1.AddTicks(3))); // 保存の精度（マイクロ秒）の内は同じ版

        h.Published.Events.Should().HaveCount(2);
        h.Doc()!.Status.Should().Be(DocumentStatus.Normalized);
    }

    // 受け入れ基準 9: アーカイブより前に作られた再正規化はアーカイブを戻さない（内容は反映する）。
    [Fact]
    public async Task アーカイブより前の再正規化はアーカイブを戻さない()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("版", T1));
        await h.WithDbAsync(async db => (await db.Documents.FindAsync([h.DocId], TestContext.Current.CancellationToken))!.Archive());
        var archivedAt = h.Doc()!.ArchivedAt!.Value;

        // アーカイブより前に作られ、遅れて届いた再正規化（反映済みの T1 より新しい）。
        await h.ConsumeAsync(h.Normalized("アーカイブ前に変換した版", archivedAt.AddSeconds(-1)));

        var doc = h.Doc()!;
        doc.Status.Should().Be(DocumentStatus.Archived);
        doc.Title.Should().Be("アーカイブ前に変換した版");
        h.Published.Events.Last().Status.Should().Be(DocumentStatus.Archived, "下流へもアーカイブのまま伝わる");
    }

    // 受け入れ基準 9（陽性対照・ADR-0141 決定 1 (a)）: アーカイブより後の再正規化（データソースの再同期）はアーカイブを戻す。
    [Fact]
    public async Task アーカイブより後の再正規化はアーカイブを戻す()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("版", T1));
        await h.WithDbAsync(async db => (await db.Documents.FindAsync([h.DocId], TestContext.Current.CancellationToken))!.Archive());
        var archivedAt = h.Doc()!.ArchivedAt!.Value;

        await h.ConsumeAsync(h.Normalized("再同期した版", archivedAt.AddSeconds(1)));

        var doc = h.Doc()!;
        doc.Status.Should().Be(DocumentStatus.Normalized);
        doc.ArchivedAt.Should().BeNull();
    }

    // 受け入れ基準 9（移行）: 本欄の追加以前のアーカイブ（ArchivedAt が無い）は UpdatedAt で読む。
    [Fact]
    public void アーカイブの時刻が無ければ更新時刻で読む()
    {
        var doc = Document.CreateNormalized(Guid.NewGuid(), "版", "storage://b/d.md", normalizedAt: T1);
        doc.Archive();
        typeof(Document).GetProperty(nameof(Document.ArchivedAt))!.SetValue(doc, null); // 移行前の行を模す

        doc.KeepsArchiveAgainst(doc.UpdatedAt.AddSeconds(-1)).Should().BeTrue();
        doc.KeepsArchiveAgainst(doc.UpdatedAt.AddSeconds(1)).Should().BeFalse();
    }

    // 受け入れ基準 10: 削除より前に作られた再正規化で、削除した文書を台帳へ作り直さない。
    [Fact]
    public async Task 削除より前の再正規化は台帳へ作り直さない()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("版", T1));
        await DeleteAsync(h, T3);
        var published = h.Published.Events.Count;

        await h.ConsumeAsync(h.Normalized("削除より前の版", T2));
        await h.ConsumeAsync(h.Normalized("削除と同じ時刻の版", T3));

        h.Doc().Should().BeNull();
        h.Published.Events.Count.Should().Be(published);
    }

    // 受け入れ基準 10（陽性対照）: 削除より後の再正規化（データソースの再同期・決定的な文書 ID）は台帳へ作る。
    [Fact]
    public async Task 削除より後の再正規化は作り直す()
    {
        await using var h = new Harness();
        await h.ConsumeAsync(h.Normalized("版", T1));
        await DeleteAsync(h, T2);

        await h.ConsumeAsync(h.Normalized("再同期した版", T3));

        h.Doc()!.Title.Should().Be("再同期した版");
    }

    // 削除の口と同じ書き込み（行を消し、削除の事実を残す。`Delete/Endpoint`）。口そのものは下の結合試験が通す。
    private static Task DeleteAsync(Harness h, DateTimeOffset at) => h.WithDbAsync(async db =>
    {
        db.Documents.Remove((await db.Documents.FindAsync([h.DocId], TestContext.Current.CancellationToken))!);
        db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(h.DocId, at));
    });

    internal sealed class CountingStorage : IObjectStorageClient
    {
        private int _reads;
        public int Reads => _reads;
        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => Task.FromResult($"storage://test/{key}");
        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
        public bool CanResolve(string? uri) => true;
        public Task<string> GetTextAsync(string uri, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult("# 本文");
        }
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();
    }

    internal sealed record PublishedUpdate(Guid DocumentId, string Status, DateTimeOffset UpdatedAt);

    internal sealed class RecordingUpdatedPublisher : IDocumentUpdatedPublisher
    {
        // IADR-0608 (#2168): 確定の口。この器は関数（保存 → 発行を積む）を呼ぶだけでよい（DB は InMemory でトランザクションが無い）。
        public Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default) =>
            saveAndPublish(ct);

        private readonly ConcurrentQueue<PublishedUpdate> _events = new();
        public IReadOnlyList<PublishedUpdate> Events => [.. _events];

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null,
            long? sequence = null,
            CancellationToken ct = default)
        {
            _events.Enqueue(new PublishedUpdate(documentId, status, updatedAt));
            return Task.CompletedTask;
        }
    }
}

// FR-06, ADR-0057 決定 2, [[IADR-0596]] 決定 7 (#2102): 削除の口が削除の事実（文書 ID と時刻だけ）を残し、下流へ同じ時刻を伝える。
[Trait("TestKind", "Integration")]
public sealed class DocumentDeletionMarkEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task 削除の口は削除の事実を残す()
    {
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            db.Documents.Add(Document.CreateNormalized(id, "消す文書", $"storage://knowledge-normalized/{id:N}/document.md",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var resp = await factory.CreateClient().DeleteAsync($"/documents/{id}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var check = factory.Services.CreateScope();
        var mark = await check.ServiceProvider.GetRequiredService<DocumentDbContext>().DocumentDeletionMarks
            .FindAsync([id], TestContext.Current.CancellationToken);
        mark.Should().NotBeNull();
        mark!.DeletedAt.Should().BeAfter(before);
        mark.Supersedes(before).Should().BeTrue("削除より前に作られた再正規化は作り直さない");
        mark.Supersedes(DateTimeOffset.UtcNow.AddSeconds(1)).Should().BeFalse("削除より後の再正規化は作る");
    }
}
