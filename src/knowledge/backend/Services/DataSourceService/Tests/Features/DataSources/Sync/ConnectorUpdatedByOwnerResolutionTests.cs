using System.Net;
using System.Text;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources;
using DataSourceService.Features.DataSources.Sync;
using DataSourceService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-05, UC-04, ADR-0036, ADR-0074, Issue #752:
// **実コネクタが運んだ更新者が、解決段を通って `owner` になる**ことを端から端まで固定する。
//
// 既存の `DataSourceSyncServiceTests` は**スタブが運ぶ**更新者で解決段（#1194）を測っている。
// 🔴 **本クラスが足すのは「取得元」側である** —— 実 `WikiConnector` に JSON を食わせ、
// 契約 → コネクタ → `SourceItem.UpdatedBy` → `ResolveOwner` → 発行イベントの属性まで通す。
// スタブで測っていると、**コネクタが値を載せ忘れても試験は緑のままになる**（それが #752 の状態だった）。
[Trait("TestKind", "Integration")]
public sealed class ConnectorUpdatedByOwnerResolutionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string Base = "https://wiki.example.com";

    // ---- 陽性 -------------------------------------------------------------

    [Fact]
    public async Task Sync_WithARealWikiConnector_ResolvesOwnerFromTheSourceUpdater()
    {
        var published = await SyncWikiAsync(
            WikiConnector(),
            ownerMappings: new Dictionary<string, string> { ["hr-tanaka"] = "alice" });

        published.Should().ContainSingle().Which.Attributes[DataSource.OwnerKey]
            .Should().Be("alice", "ソースが運んだ更新者を写像表が解決し、それが owner になる");
    }

    // ---- 陰性 -------------------------------------------------------------

    [Fact]
    public async Task Sync_WhenTheSourceUpdaterIsNotMapped_OwnerStaysReserved_AndTheRawIdentifierNeverLeaks()
    {
        // 🔴 計画は「別名前空間の識別子をそのまま `owner` へ入れてはならない」
        // 「安全側は『解決しない』」と定める（09_datasource-connectors / ADR-0036）。
        var published = await SyncWikiAsync(
            WikiConnector(),
            ownerMappings: new Dictionary<string, string> { ["someone-else"] = "bob" });

        var owner = published.Should().ContainSingle().Subject.Attributes[DataSource.OwnerKey];
        owner.Should().Be(DataSource.UnresolvedOwner);
        owner.Should().NotContain("tanaka", "生のソース側識別子は owner へ入らない");
        // 他人の写像先が混入しないこと（写像表は完全一致であり、当たらなければ何も返さない）。
        owner.Should().NotBe("bob");
    }

    [Fact]
    public async Task Sync_WhenTheSourceCarriesNoUpdater_OwnerFallsBackToTheReservedValue()
    {
        // 「取れなかった」も「取ったら空だった」も、ここでは同じく予約値へ倒れる。
        // 🔴 **落ち方が同じでも由来は潰していない**（`SourceUpdatedByTests` が分類を固定する）。
        var published = await SyncWikiAsync(
            WikiConnector("""[{"id":"p1","updatedAt":"2026-07-01T00:00:00Z"}]"""),
            ownerMappings: new Dictionary<string, string> { ["hr-tanaka"] = "alice" });

        published.Should().ContainSingle().Which.Attributes[DataSource.OwnerKey]
            .Should().Be(DataSource.UnresolvedOwner);
    }

    // ---- 変異試験 ---------------------------------------------------------

    // 🔴 **更新者の受け渡しを外すと、上の陽性が落ちることを実際に確かめる。**
    //
    // 陽性試験は「実装が値を運んでいる」ことを主張するが、**その主張が空でない**ことは
    // 陽性試験だけでは分からない（`owner` が別の理由で "alice" になっていても緑になり得る）。
    // 受け渡しだけを外した実装で同じ経路を回し、**結論が変わること**を対で置く。
    [Fact]
    public async Task Mutation_DroppingTheUpdatedByPassThrough_BreaksTheOwnerResolution()
    {
        var mapping = new Dictionary<string, string> { ["hr-tanaka"] = "alice" };

        var intact = await SyncWikiAsync(WikiConnector(), mapping);
        var mutated = await SyncWikiAsync(
            new UpdatedByDroppingConnector(WikiConnector()), mapping);

        intact.Should().ContainSingle().Which.Attributes[DataSource.OwnerKey].Should().Be("alice");
        mutated.Should().ContainSingle().Which.Attributes[DataSource.OwnerKey]
            .Should().Be(DataSource.UnresolvedOwner,
                "更新者の受け渡しを外すと owner は解決できず予約値へ倒れる（＝陽性は空の主張ではない）");
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<List<RawDocumentFetched>> SyncWikiAsync(
        IDataSourceConnector connector, Dictionary<string, string> ownerMappings)
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var svc = new DataSourceSyncService(
            new ConnectorRegistry([connector]), storage, bus,
            NullLogger<DataSourceSyncService>.Instance);
        var source = DataSource.Create("wiki", "wiki", Base, ownerMappings: ownerMappings);

        await svc.SyncAsync(source, TestContext.Current.CancellationToken);

        return bus.PublishedOf<RawDocumentFetched>()
            .Where(m => m.SourceId == source.Id)
            .ToList();
    }

    private static WikiConnector WikiConnector(string? listJson = null)
        => new(
            new StubFactory(new StubHandler(
                listJson ?? """[{"id":"p1","updatedAt":"2026-07-01T00:00:00Z","updatedBy":"hr-tanaka"}]""")),
            NullLogger<WikiConnector>.Instance);

    // 実コネクタから**更新者の受け渡しだけ**を外す変異体。他の振る舞いは一切変えない。
    private sealed class UpdatedByDroppingConnector(IDataSourceConnector inner) : IDataSourceConnector
    {
        public string SourceType => inner.SourceType;

        public async Task<IReadOnlyList<SourceItem>> DiscoverAsync(
            DataSource source, DateTimeOffset? since, CancellationToken ct)
        {
            var items = await inner.DiscoverAsync(source, since, ct);
            return items.Select(i => i with { UpdatedBy = null }).ToList();
        }

        public Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct)
            => inner.FetchAsync(source, item, ct);
    }

    private sealed class StubHandler(string listJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("content", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# Body", Encoding.UTF8, "text/markdown"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listJson, Encoding.UTF8, "application/json"),
                });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
