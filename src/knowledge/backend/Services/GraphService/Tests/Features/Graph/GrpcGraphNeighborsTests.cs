using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.Graph;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Knowledge.Contracts.Grpc.Graph.V1;

namespace GraphService.Tests.Features.Graph;

// FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-10, SC-18, ADR-0004, ADR-0029, ADR-0034 決定 1・2,
// ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1・3, [[IADR-0242]], [[IADR-0379]], [[IADR-0410]] (#1255):
// 近傍展開の gRPC 面（`knowledge.graph.v1.GraphNeighbors`）を**実 Kestrel の h2c ポート**で往復し、
// s2s トークンの検証・**判定の位置**・存在秘匿を固定する。
//
// 陽性対照と陰性対照を同じ器で対にする —— 「拒否された」だけでは器が壊れているのか
// 認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcGraphNeighborsTests
{
    private const string ServiceSubject = "service-account-retrieval-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcGraphNeighborsTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        _factory.ResolvedFor.Clear();
        _factory.ScopeFor = (user, _) => new AccessScopeResponse(user.UserId, [], true);
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private Pb.GraphNeighbors.GraphNeighborsClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.UserContext User(string id) => new() { UserId = id, Action = "read" };

    private static GraphDocument Node(Guid id, string name) =>
        GraphDocument.Create(id, name,
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null, DateTimeOffset.UtcNow);

    // A→B の 1 辺を張る。
    private async Task<(Guid A, Guid B, Guid TypeId)> SeedAsync()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create($"t-{typeId:N}", EdgeTypeLayer.Core, false));
            db.Documents.Add(Node(a, "A"));
            db.Documents.Add(Node(b, "B"));
            db.Edges.Add(Edge.Create(a, b, typeId, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return (a, b, typeId);
    }

    // T-01: 陽性対照。s2s トークン（platform-service）を付けた h2c チャネルで往復し、辺が返る。
    [Fact]
    public async Task 近傍展開はs2sトークンで往復し辺を返す()
    {
        var (a, b, _) = await SeedAsync();

        var resp = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1, User = User("alice") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeTrue();
        resp.Edges.Should().ContainSingle();
        resp.Edges[0].SourceDocumentId.Should().Be(a.ToString());
        resp.Edges[0].TargetDocumentId.Should().Be(b.ToString());
    }

    // 🔴 T-02: **判定の位置が動いていない。** 呼び出し先が**本文の利用者文脈で自分の判定を行う**
    // （計画 `ADR-0086` 決定 1 / `ADR-0034` 決定 1 のホップごと ABAC）。
    // 呼び出し元が解決したスコープを信じているなら、解決は 1 度も起きない。
    [Fact]
    public async Task 呼び出し先が本文の利用者文脈で自分のスコープを解決する()
    {
        var (a, _, _) = await SeedAsync();
        _factory.ResolvedFor.Clear();

        await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest
            {
                DocumentId = a.ToString(),
                Hops = 1,
                User = new Pb.UserContext
                {
                    UserId = "carol",
                    Action = "read",
                    UserAttributes = { ["clearance"] = "internal", ["department"] = "hr" },
                },
            },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        _factory.ResolvedFor.Should().ContainSingle();
        var (user, action) = _factory.ResolvedFor[0];
        user.UserId.Should().Be("carol", "本文で運ばれた身元で判定する");
        user.Attributes.Should().Contain(new KeyValuePair<string, string>("clearance", "internal"));
        user.Attributes.Should().Contain(new KeyValuePair<string, string>("department", "hr"));
        action.Should().Be("read", "action は既定へ丸めず明示して運ぶ");
    }

    // 🔴 T-03: 陰性対照（T-01 と対）。**利用者ごとに答えが変わる** ——
    // スコープが無い利用者では `found=false` になる。これが変わらなければ、
    // 上の記録は「呼ばれているが結果に効いていない」ことになる。
    [Fact]
    public async Task スコープの無い利用者には見えない()
    {
        var (a, _, _) = await SeedAsync();
        _factory.ScopeFor = (user, _) => user.UserId == "alice"
            ? new AccessScopeResponse(user.UserId, [], true)
            : new AccessScopeResponse(user.UserId, [], false);

        var granted = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1, User = User("alice") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var denied = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1, User = User("mallory") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        granted.Found.Should().BeTrue("★ 陽性対照");
        denied.Found.Should().BeFalse();
        denied.Edges.Should().BeEmpty();
    }

    // 🔴 T-04: **存在秘匿。** 「見えない」と「無い」は同じ応答である（`ADR-0034` 決定 2）。
    // status で割ると、割り方そのものが実在を漏らす。
    [Fact]
    public async Task 不存在も不可視も同じ応答である()
    {
        var (a, _, _) = await SeedAsync();
        _factory.ScopeFor = (user, _) => user.UserId == "alice"
            ? new AccessScopeResponse(user.UserId, [], true)
            : new AccessScopeResponse(user.UserId, [], false);

        var missing = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest
            {
                DocumentId = Guid.NewGuid().ToString(),
                Hops = 1,
                User = User("alice"),
            },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var hidden = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1, User = User("mallory") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        missing.Should().BeEquivalentTo(hidden);
    }

    // 🔴 T-05: hops の上限超過は `INVALID_ARGUMENT`（REST の 400 と同値）。
    // **黙って切り詰めない**（`ADR-0034` 決定 3）。
    [Fact]
    public async Task ホップ上限の超過は不正な引数である()
    {
        var (a, _, _) = await SeedAsync();

        var act = async () => await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 99, User = User("alice") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 T-06: **利用者文脈の欠落は要求の誤りであって「該当なし」ではない。**
    // deny へ畳むと、呼び出し元の配線誤りが「グラフには何も無い」に化ける。
    [Fact]
    public async Task 利用者文脈の欠落は不正な引数である()
    {
        var (a, _, _) = await SeedAsync();

        var act = async () => await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1 },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // T-07: 辺の型の重みは**利用者文脈を持たない**（REST の描画用カタログと同じ母集合）。
    [Fact]
    public async Task 辺の型の重みは利用者文脈なしで引ける()
    {
        var (_, _, typeId) = await SeedAsync();

        var resp = await PlainClient().ListEdgeTypeWeightsAsync(
            new Pb.ListEdgeTypeWeightsRequest(),
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Weights.Should().NotBeEmpty();
        resp.Weights.Should().OnlyContain(w => w.Weight > 0);
        typeId.Should().NotBeEmpty("★ 種を撒いた事実を使う（撒かずに緑にならない）");
    }

    // 🔴 T-08: 資格情報が無ければ `UNAUTHENTICATED`。
    [Fact]
    public async Task 資格情報が無ければ認証されない()
    {
        var act = async () => await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest
            {
                DocumentId = Guid.NewGuid().ToString(),
                User = User("alice"),
            },
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-09: **利用者のトークンは（管理者であっても）通らない**（confused deputy の防止）。
    // これが本スライスの核である —— 利用者文脈は本文で運び、面には s2s だけを通す。
    [Fact]
    public async Task 管理者の利用者トークンでも権限が拒否される()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest
            {
                DocumentId = Guid.NewGuid().ToString(),
                User = User("alice"),
            },
            Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 🔴 T-10: **`ServiceCaller` の宣言をリフレクションで固定する。**
    // 属性を外しても T-08 / T-09 以外は緑のままなので、宣言そのものを見る。
    [Fact]
    public void Grpc面はServiceCallerポリシーを宣言する()
    {
        var attribute = typeof(GraphNeighborsGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull();
        attribute!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // 🔴 T-11: **HTTP/1.1 のポートは消えていない**（h2c を足したせいで REST が落ちると配備が
    // 静かに壊れる。`AddPlatformGrpcListener` の 🔴）——**同時に、REST と gRPC が同じ答えを返す**。
    // 🔴 REST は利用者のトークンで、gRPC は s2s トークンで通る ——
    // 面ごとに通る資格情報が違うことが、そのまま「利用者トークンを転送していない」ことの現れである。
    [Fact]
    public async Task RESTとgRPCは同じ辺を返す()
    {
        var (a, b, _) = await SeedAsync();

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GrpcKestrelFactory.IssueToken("alice", []));
        var rest = await http.GetFromJsonAsync<RestView>(
            $"/graph/{a}/neighbors?hops=1", TestContext.Current.CancellationToken);

        var grpc = await PlainClient().ExpandNeighborsAsync(
            new Pb.ExpandNeighborsRequest { DocumentId = a.ToString(), Hops = 1, User = User("alice") },
            Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        rest.Should().NotBeNull();
        rest!.Edges.Select(e => (e.SourceDocumentId, e.TargetDocumentId))
            .Should().BeEquivalentTo(grpc.Edges.Select(e =>
                (Guid.Parse(e.SourceDocumentId), Guid.Parse(e.TargetDocumentId))),
                "輸送を替えても応答の意味は変わらない");
        grpc.Edges.Should().ContainSingle(e => e.TargetDocumentId == b.ToString(),
            "★ 陽性対照 —— 両方が空でも一致してしまう");
    }

    private sealed record RestView(List<GraphEdgeDto> Edges);
}
