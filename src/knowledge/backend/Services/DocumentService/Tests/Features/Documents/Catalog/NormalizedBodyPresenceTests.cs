using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.Catalog;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests.Features.Documents.Catalog;

// FR-01, FR-02, FR-03, FR-12, UC-04, SC-03, ADR-0070 決定 3・決定 4, #1254 / #1253,
// [[IADR-0388]] 決定 2・4:
//
// 🔴 **`DocumentNormalized.HasBody` の読み手がここに在ることを固定する。**
// 従前この項目は契約に在るのに読む箇所が 1 つも無く（write-only）、SC-03 は本文なしの文書を
// 区別できなかった。併せて原本の所在・データソース名が台帳へ入り、`DocumentUpdated` へ
// 写ることを見る —— 索引テキストの材料はこの経路でしか届かない。
//
// 器は `NormalizedAssetLedgerTests` と同じ MassTransit in-memory ハーネスである
// （本 consumer はサービス本体のテストホストに登録されていない）。
[Trait("TestKind", "Unit")]
public sealed class NormalizedBodyPresenceTests
{
    // **発行口へ渡った値を記録する。** 台帳だけを見ると「保持したが下流へ渡していない」形を
    // 見逃す（それが #1254 の元の事故と同じ形である）。
    private sealed class RecordingUpdatedPublisher : IDocumentUpdatedPublisher
    {
        public sealed record Call(Guid DocumentId, bool HasBody, string? OriginalPath, string? DataSourceName);

        public List<Call> Calls { get; } = [];

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null,
            CancellationToken ct = default)
        {
            Calls.Add(new Call(documentId, hasBody, originalPath, dataSourceName));
            return Task.CompletedTask;
        }
    }

    // 本文の指紋計算はストレージ解決を要求する。ここでは有無の標識だけを見るので縮退させる。
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

    private static ServiceProvider BuildProvider(string dbName, RecordingUpdatedPublisher publisher) =>
        new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddMetrics()
            .AddDbContext<DocumentDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IngestTagMetrics>()
            .AddSingleton<IObjectStorageClient, UnresolvableStorage>()
            .AddSingleton<IDocumentUpdatedPublisher>(publisher)
            .AddMassTransitTestHarness(x => x.AddConsumer<DocumentNormalizedConsumer>())
            .BuildServiceProvider(validateScopes: true);

    private static DocumentNormalized Event(Guid id, bool hasBody, string? path, string? sourceName) => new(
        DocumentId: id,
        SourceId: Guid.NewGuid(),
        Title: "テスト文書",
        MarkdownUri: $"storage://knowledge-normalized/{id:N}/document.md",
        AssetUris: [],
        Attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" },
        Tags: [],
        NormalizedAt: DateTimeOffset.UtcNow,
        HasBody: hasBody,
        OriginalPath: path,
        DataSourceName: sourceName);

    private static async Task<(DocumentDbContext Db, RecordingUpdatedPublisher Publisher, ServiceProvider Provider)>
        ConsumeAsync(DocumentNormalized ev)
    {
        var publisher = new RecordingUpdatedPublisher();
        var provider = BuildProvider($"body-{Guid.NewGuid():N}", publisher);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(ev, TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
            .Should().BeTrue();
        // 🔴 消費されたことと成功したことは別である（fault でも Consumed には載る）。
        (await harness.Published.Any<Fault<DocumentNormalized>>(TestContext.Current.CancellationToken))
            .Should().BeFalse("consumer が落ちていれば台帳の検査は無意味になる");

        var scope = provider.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<DocumentDbContext>(), publisher, provider);
    }

    // B-1: 本文なしの標識が台帳へ入り、`DocumentUpdated` へ写る。
    // 所在・データソース名も同じ経路で運ばれる（索引テキストの材料）。
    [Fact]
    public async Task 本文なしの標識と所在が台帳と後続イベントへ届く()
    {
        var id = Guid.NewGuid();
        var (db, publisher, provider) = await ConsumeAsync(
            Event(id, hasBody: false, "/共有/経理/2026年度経費.pdf", "本社ファイルサーバー"));
        await using var _ = provider;

        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc.Should().NotBeNull();
        doc!.HasBody.Should().BeFalse("読み手が無いと SC-03 は本文なしの文書を区別できない");
        doc.OriginalPath.Should().Be("/共有/経理/2026年度経費.pdf");
        doc.DataSourceName.Should().Be("本社ファイルサーバー");

        var call = publisher.Calls.Should().ContainSingle().Subject;
        call.HasBody.Should().BeFalse();
        call.OriginalPath.Should().Be("/共有/経理/2026年度経費.pdf");
        call.DataSourceName.Should().Be("本社ファイルサーバー");
    }

    // **陽性対照**: 本文ありの文書は `HasBody = true` のまま。
    // これが無いと「常に false を入れる」実装でも上の 1 本が緑になる。
    [Fact]
    public async Task 本文ありの文書は標識が真のまま()
    {
        var id = Guid.NewGuid();
        var (db, publisher, provider) = await ConsumeAsync(
            Event(id, hasBody: true, "/共有/規程/就業規則.docx", "本社ファイルサーバー"));
        await using var _ = provider;

        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc!.HasBody.Should().BeTrue();
        publisher.Calls.Should().ContainSingle().Which.HasBody.Should().BeTrue();
    }

    // **旧発行者**（所在もデータソース名も運ばない）でも例外にならず、既定で読める。
    // 末尾・既定値つきで足したことの実測である。
    [Fact]
    public async Task 所在を運ばない発行元でも既定で読める()
    {
        var id = Guid.NewGuid();
        var (db, publisher, provider) = await ConsumeAsync(Event(id, hasBody: true, null, null));
        await using var _ = provider;

        var doc = await db.Documents.FindAsync([id], TestContext.Current.CancellationToken);
        doc!.HasBody.Should().BeTrue();
        doc.OriginalPath.Should().BeNull();
        publisher.Calls.Should().ContainSingle().Which.OriginalPath.Should().BeNull();
    }
}
