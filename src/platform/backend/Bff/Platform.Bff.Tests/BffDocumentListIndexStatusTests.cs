using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Knowledge.Contracts.Grpc.Ingestion.V1;

namespace Platform.Bff.Tests;

// FR-06, SC-05, NFR-09, NFR-16, ADR-0029, [[IADR-0009]], [[IADR-0379]], [[IADR-0402]], [[IADR-0565]] (#1993):
// 文書管理の一覧（`GET /bff/documents`）の**索引の状態**（`indexStatuses`）の結合。
//
// 受け入れ基準（作業仕様書 20261020_1993_document-list-index-status の写像）:
//   A1 窓の文書の状態が 3 値で出る / A2 取り込み側が応答しないときは「不明」で一覧は 200 /
//   A3 🔴 権限外の文書の状態は問い合わせない（存在秘匿）/ A4 1 ページ分を 1 回で引く。
public class BffDocumentListIndexStatusTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentListIndexStatusTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public"])];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentReadServerPageSize = null;
        _factory.DocumentReadFailContinuation = false;
        _factory.StubIndexStatuses.Clear();
        _factory.StubIndexStatusThrows = null;
        while (_factory.IndexStatusRequests.TryDequeue(out _)) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static DocumentDto Doc(int n, string confidentiality = "internal", bool personal = false)
    {
        var attrs = new Dictionary<string, string> { ["confidentiality"] = confidentiality };
        if (personal)
        {
            attrs["doc_scope"] = "private-note";
            attrs["owner"] = "someone-else";
        }
        return new DocumentDto
        {
            Id = Id(n),
            Title = $"文書 {n}",
            Status = "published",
            Attributes = attrs,
            CreatedAt = Day0,
            UpdatedAt = Day0.AddMinutes(100 - n), // 番号の小さい順に並ぶ（更新日時の降順）
        };
    }

    // 読める 4 件（1・2・4・6）と、権限外（3）・他人の個人資料（5）。
    private static List<DocumentDto> Mixed() =>
        [Doc(1), Doc(2, "public"), Doc(3, "confidential"), Doc(4), Doc(5, personal: true), Doc(6)];

    private static List<DocumentDto> VisibleOnly() => [Doc(1), Doc(2, "public"), Doc(4), Doc(6)];

    private async Task<DocumentListPageDto> PageAsync(string query)
    {
        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<DocumentListPageDto>(Ct))!;
    }

    private List<List<string>> Requests() => [.. _factory.IndexStatusRequests];

    // ── A1・A4: 窓の文書の状態を 1 回で引く ─────────────────────────────

    [Fact]
    public async Task 窓の文書の状態が3値で出て_照会は窓のIDを1回だけ()
    {
        _factory.StubDocumentList = VisibleOnly();
        _factory.StubIndexStatuses[Id(2)] = Pb.IndexStatus.EmbeddingPending;
        _factory.StubIndexStatuses[Id(4)] = Pb.IndexStatus.NotIndexed;

        var page = await PageAsync("?limit=3");

        page.Items.Select(d => d.Id).Should().Equal([Id(1), Id(2), Id(4)]);
        page.IndexStatuses.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [Id(1).ToString()] = DocumentIndexStatuses.Indexed,
            [Id(2).ToString()] = DocumentIndexStatuses.EmbeddingPending,
            [Id(4).ToString()] = DocumentIndexStatuses.NotIndexed,
        });
        Requests().Should().ContainSingle("1 ページ分を 1 回で引く（N+1 にしない）")
            .Which.Should().Equal([Id(1).ToString(), Id(2).ToString(), Id(4).ToString()], "窓の中の ID だけ（ページの外の 6 は渡さない）");
    }

    [Fact]
    public async Task 窓が空なら照会しない()
    {
        _factory.StubDocumentList = VisibleOnly();

        var page = await PageAsync("?offset=10");

        page.Items.Should().BeEmpty();
        page.IndexStatuses.Should().BeEmpty();
        Requests().Should().BeEmpty();
    }

    [Fact]
    public async Task 許可ポリシーが無いときは照会しない()
    {
        _factory.StubDocumentList = Mixed();
        _factory.SearchScopeGranted = false;

        var page = await PageAsync("");

        page.IndexStatuses.Should().BeEmpty();
        Requests().Should().BeEmpty();
    }

    // 🔴 A3: 権限外の文書・他人の個人資料の ID を取り込み側へ渡さない。混ぜた台帳と混ぜない台帳で応答と照会が一致する。
    [Theory]
    [InlineData("")]
    [InlineData("?limit=2&offset=1")]
    [InlineData("?status=published")]
    public async Task 権限外の文書と他人の個人資料の状態は問い合わせない(string query)
    {
        // 陽性対照: 区分を広げると権限外の 3 は窓に入り、照会にも出る（落としているのは ABAC である）。
        _factory.StubDocumentList = Mixed();
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public", "confidential"])];
        await PageAsync("");
        Requests().Single().Should().Contain(Id(3).ToString());
        while (_factory.IndexStatusRequests.TryDequeue(out _)) { }
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public"])];

        var withHidden = await PageAsync(query);
        var askedWithHidden = Requests().Single();
        while (_factory.IndexStatusRequests.TryDequeue(out _)) { }
        _factory.StubDocumentList = VisibleOnly();
        var withoutHidden = await PageAsync(query);
        var askedWithoutHidden = Requests().Single();

        withHidden.Should().BeEquivalentTo(withoutHidden, o => o.WithStrictOrdering());
        askedWithHidden.Should().Equal(askedWithoutHidden);
        askedWithHidden.Should().NotContain([Id(3).ToString(), Id(5).ToString()]);
        withHidden.IndexStatuses.Keys.Should().BeEquivalentTo(withHidden.Items.Select(d => d.Id.ToString()),
            "鍵は窓の中の文書の ID だけ");
    }

    // ── A2: 縮退 ─────────────────────────────────────────────────

    public static TheoryData<string> Failures() => ["unavailable", "internal", "deadline", "token", "invalid"];

    private static Exception Failure(string kind) => kind switch
    {
        "unavailable" => new RpcException(new Status(StatusCode.Unavailable, "stub")),
        "internal" => new RpcException(new Status(StatusCode.Internal, "stub")),
        "deadline" => new RpcException(new Status(StatusCode.DeadlineExceeded, "stub")),
        "invalid" => new RpcException(new Status(StatusCode.InvalidArgument, "stub")),
        // s2s トークン取得失敗（`ClientCredentialsServiceTokenProvider`）。
        _ => new InvalidOperationException("token endpoint failed"),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task 取り込み側が応答しないときは全件不明で一覧は失敗しない(string kind)
    {
        _factory.StubDocumentList = VisibleOnly();
        _factory.StubIndexStatusThrows = Failure(kind);

        var page = await PageAsync("");

        page.Items.Should().HaveCount(4, "一覧そのものは返る");
        page.IndexStatuses.Values.Should().HaveCount(4).And.OnlyContain(v => v == DocumentIndexStatuses.Unknown);
    }

    [Fact]
    public async Task 未指定の値は不明として読む()
    {
        _factory.StubDocumentList = [Doc(1)];
        _factory.StubIndexStatuses[Id(1)] = Pb.IndexStatus.Unspecified;

        var page = await PageAsync("");

        page.IndexStatuses[Id(1).ToString()].Should().Be(DocumentIndexStatuses.Unknown, "未索引へ倒さない");
    }

    [Fact]
    public void 値の写しは3値と不明()
    {
        DocumentIndexStatusGrpcClient.FromProto(Pb.IndexStatus.Indexed).Should().Be("indexed");
        DocumentIndexStatusGrpcClient.FromProto(Pb.IndexStatus.EmbeddingPending).Should().Be("embedding_pending");
        DocumentIndexStatusGrpcClient.FromProto(Pb.IndexStatus.NotIndexed).Should().Be("not_indexed");
        DocumentIndexStatusGrpcClient.FromProto((Pb.IndexStatus)99).Should().Be("unknown");
    }

    // ── 登録（構成でしか切り替わらないこと） ─────────────────────────────

    // 宛先が無くても登録し、呼ぶと UNAVAILABLE → 全件「不明」（一覧は失敗しない）。
    [Fact]
    public async Task 宛先が無ければ登録はされ_呼ぶと全件不明()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentIndexStatusGrpcClient(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetService<GrpcChannel>().Should().BeNull("宛先が無ければチャネルは張らない");
        var client = provider.GetRequiredService<DocumentIndexStatusGrpcClient>();
        var http = new DefaultHttpContext { RequestServices = provider };

        var statuses = await client.GetStatusesAsync(http, [Id(1)], Ct);

        statuses.Should().BeEquivalentTo(new Dictionary<string, string> { [Id(1).ToString()] = "unknown" });
    }

    // 陽性対照・チャネル: 宛先が在れば、キー付きのチャネルを 1 本だけ張る（二重に登録しても 1 本）。キー無しのチャネルとぶつからない。
    [Fact]
    public void 宛先が在ればキー付きのチャネルを1本だけ張る()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthzScopeGrpcClient.AddressKey] = "http://authorization-service:8081",
            [DocumentIndexStatusGrpcClient.AddressKey] = "http://ingestion-service:8081",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthzScopeGrpcClient(config);
        services.AddDocumentIndexStatusGrpcClient(config);
        services.AddDocumentIndexStatusGrpcClient(config);

        services.Count(d => d.ServiceType == typeof(GrpcChannel) && Equals(d.ServiceKey, DocumentIndexStatusGrpcClient.ChannelKey))
            .Should().Be(1);
        services.Count(d => d.ServiceType == typeof(GrpcChannel) && d.ServiceKey is null)
            .Should().Be(1, "キー無しのチャネルは認可サービス宛の 1 本だけである");
        services.Count(d => d.ServiceType == typeof(DocumentIndexStatusGrpcClient)).Should().Be(1);
    }

    // 上限は取り込み側の面と同じ（窓の上限 100 を超える ID を 1 回で渡さない）。
    [Fact]
    public void 窓の上限は取り込み側の上限と同じ100()
    {
        DocumentListWindow.MaxLimit.Should().Be(100);
    }
}
