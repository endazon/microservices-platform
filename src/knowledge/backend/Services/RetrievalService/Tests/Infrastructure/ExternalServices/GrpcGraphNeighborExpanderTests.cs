using System.Security.Claims;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetrievalService.Domain;
using RetrievalService.Infrastructure.ExternalServices;
using Pb = Knowledge.Contracts.Grpc.Graph.V1;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-10, SC-18, ADR-0004, ADR-0029, ADR-0034 決定 1・2,
// ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1・3, [[IADR-0242]], [[IADR-0379]], [[IADR-0410]] (#1255):
// 近傍展開の gRPC 実装が、**REST 実装と同じ枝・同じ副作用**であり、
// **利用者の資格情報を面へ載せず本文で運ぶ**ことを固定する。
//
// 🔴 ここが本スライスの不変条件そのものである —— 輸送を替えたときに
// **利用者トークンが面へ漏れる**か**故障が「該当なし」に化ける**かのどちらかへ倒れると、
// 前者は confused deputy、後者は「グラフには何も無い」という嘘になる。
[Trait("TestKind", "Unit")]
public class GrpcGraphNeighborExpanderTests
{
    private static readonly Guid Seed = Guid.NewGuid();

    // 🔴 T-01 陽性対照。**利用者文脈は本文で運ばれる**（`user_id` / 属性 / `action`）。
    // これが無いと、以下の陰性はすべて「何も送らない」実装でも緑になる。
    [Fact]
    public async Task 利用者文脈を本文で運ぶ()
    {
        var fake = new FakeClient(Neighborhood(), Weights());

        await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice", clearance: "internal", department: "hr"), TestContext.Current.CancellationToken);

        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.User.UserId.Should().Be("alice");
        fake.LastRequest.User.Action.Should().Be("read", "action は既定へ丸めない");
        fake.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("clearance", "internal"));
        fake.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("department", "hr"));
        fake.LastRequest.DocumentId.Should().Be(Seed.ToString());
    }

    // 🔴 T-01b（#1323 / ADR-0080 決定 1）: **集合値の利用者属性も本文で運ぶ。**
    // この経路だけ載せ忘れると、近傍展開の ABAC だけが`tags`を条件に持つポリシーへマッチしなくなる。
    // 属性の抽出は共有点（`BffScopeResolver.ExtractUserAttributes`）1 つに集約してある（[[IADR-0411]]）。
    [Fact]
    public async Task 集合値の利用者属性も本文で運ぶ()
    {
        var fake = new FakeClient(Neighborhood(), Weights());

        await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice", clearance: "internal", tags: ["sales", "hr"]), TestContext.Current.CancellationToken);

        var carried = fake.LastRequest!.User.UserAttributes;
        carried.Should().ContainKey("tags");
        Platform.Shared.Contracts.Dtos.UserAttributeEncoding.Split(carried["tags"])
            .Should().BeEquivalentTo(["sales", "hr"], "先頭 1 値へ畳むと 2 つ目のタグが効かない");
        // 陽性対照: 単値キーは従来どおり運ばれている（「集合値だけ運ぶ」実装を落とす）。
        carried.Should().Contain(new KeyValuePair<string, string>("clearance", "internal"));
    }

    // 🔴 T-02: **利用者の JWT をメタデータへ載せない**（confused deputy の防止。
    // 計画 `ADR-0086` 決定 1 / [[IADR-0379]] 決定 4）。載るのは**チャネルに付いた s2s だけ**であり、
    // 呼び出しごとのヘッダは 1 本も足さない。
    [Fact]
    public async Task 利用者のトークンをメタデータへ載せない()
    {
        var fake = new FakeClient(Neighborhood(), Weights());
        // 🔴 入口が利用者のトークンを**持っている**状態で測る（持っていなければ自明に緑になる）。
        var user = Authenticated("alice", authorization: "Bearer 利用者のトークン");

        await Expander(fake).ExpandAsync([Seed], 1, user, TestContext.Current.CancellationToken);

        (fake.LastOptions.Headers ?? []).Should().BeEmpty(
            "利用者の資格情報は面を通らない —— 通ると呼び出し先が「利用者が直接呼んだ」と区別できない");
    }

    // 🔴 T-03 陰性（T-01 と対）。**利用者が分からなければ呼ばない。**
    // 呼ぶと呼び出し先が `INVALID_ARGUMENT` を返し、それは「引けなかった」枝で全部空になる ——
    // それは「グラフには何も無い」と読める形の静かな故障である（REST 実装と同じ判断）。
    [Fact]
    public async Task 未認証なら1度も呼ばない()
    {
        var fake = new FakeClient(Neighborhood(), Weights());

        var result = await Expander(fake)
            .ExpandAsync([Seed], 1, Anonymous(), TestContext.Current.CancellationToken);

        fake.LastRequest.Should().BeNull();
        result.Edges.Should().BeEmpty();
    }

    // 🔴 T-04: **`found=false` は「見えない・無い」であり、例外ではない**（存在秘匿）。
    [Fact]
    public async Task 見えない起点は空で返る()
    {
        var fake = new FakeClient(new Pb.ExpandNeighborsResponse { Found = false }, Weights());

        var result = await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);

        result.Edges.Should().BeEmpty();
    }

    // 🔴 T-05: **近傍の取得が失敗しても検索そのものを落とさない**（REST 実装の非 2xx / 不達と同値）。
    // 落とすと「グラフが不調なら検索が死ぬ」ことになる。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task 近傍の取得の失敗は空へ縮退する(StatusCode status)
    {
        var fake = new FakeClient(Neighborhood(), Weights())
        {
            NeighborsException = new RpcException(new Status(status, "失敗")),
        };

        var act = async () => await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Edges.Should().BeEmpty();
    }

    // 🔴 T-06: s2s トークンの取得失敗（`InvalidOperationException`）も**同じ縮退**である。
    [Fact]
    public async Task s2sトークン取得失敗も空へ縮退する()
    {
        var fake = new FakeClient(Neighborhood(), Weights())
        {
            NeighborsException = new InvalidOperationException("ServiceToken:ClientId が未設定です。"),
            WeightsException = new InvalidOperationException("ServiceToken:ClientId が未設定です。"),
        };

        var act = async () => await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Edges.Should().BeEmpty();
    }

    // 🔴 T-07: **辞書が引けなければ全辺がフォールバック重みへ倒れる**（REST 実装と同値）。
    // 陽性対照（辞書が引けたときは実重みが載る）と対で置く ——
    // 対が無いと「常にフォールバック」の実装でも緑になる。
    [Fact]
    public async Task 辞書が引けなければフォールバック重みへ倒れる()
    {
        var typeId = Guid.NewGuid();
        var withCatalog = new FakeClient(Neighborhood(typeId), Weights((typeId, 0.9)));
        var withoutCatalog = new FakeClient(Neighborhood(typeId), Weights())
        {
            WeightsException = new RpcException(new Status(StatusCode.Unavailable, "不達")),
        };

        var real = await Expander(withCatalog)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);
        var fallback = await Expander(withoutCatalog)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);

        real.Edges.Should().ContainSingle().Which.Weight.Should().Be(0.9, "★ 陽性対照");
        fallback.Edges.Should().ContainSingle().Which.Weight
            .Should().Be(GraphServiceNeighborExpander.FallbackEdgeWeight);
    }

    // 🔴 T-08: 辞書に**無い型**の辺もフォールバック重みである（黙って無差別へ落ちない）。
    [Fact]
    public async Task 辞書に無い型はフォールバック重みになる()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var fake = new FakeClient(Neighborhood(unknown), Weights((known, 0.9)));

        var result = await Expander(fake)
            .ExpandAsync([Seed], 1, Authenticated("alice"), TestContext.Current.CancellationToken);

        result.Edges.Should().ContainSingle().Which.Weight
            .Should().Be(GraphServiceNeighborExpander.FallbackEdgeWeight);
    }

    // 🔴 T-09: **切替は構成の有無だけである。** `Services:GraphServiceGrpc` が無ければ
    // 生成クライアントを**1 つも登録しない** —— 登録の有無で `Program.cs` が REST 実装と
    // gRPC 実装を選ぶ（並走中の正は REST。戻すのは構成を外すだけでコードは変えない）。
    //
    // 🔴 **`Program.cs` の DI をテストホストの構成で切り替えて測ることはできない**
    // （選択は組み立て時に行われ、`WebApplicationFactory` の構成は Build 時に載る）。
    // したがって**登録関数そのもの**を陽性・陰性の対で固定する。
    [Fact]
    public void 宛先が未設定なら生成クライアントを登録しない()
    {
        var services = new ServiceCollection()
            .AddGraphNeighborsGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(
            d => d.ServiceType == typeof(Pb.GraphNeighbors.GraphNeighborsClient),
            "未設定なら何も登録しない（REST のまま）");
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GraphNeighborsGrpcClientExtensions.AddressKey] = "http://graph-service:8081",
        }).Build();

        var services = new ServiceCollection().AddGraphNeighborsGrpcClient(config);

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.GraphNeighbors.GraphNeighborsClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static GrpcGraphNeighborExpander Expander(
        Pb.GraphNeighbors.GraphNeighborsClient client) =>
        new(client, NullLogger<GrpcGraphNeighborExpander>.Instance);

    // 🔴 [[IADR-0426]] 決定 2 (#1255): 利用者文脈は**引数で**渡る。器（`IHttpContextAccessor`）は
    // 実装から外れたが、**属性の抽出は入口の実物を通す** —— `SearchUserContext.FromRequest` は
    // `BffScopeResolver.ExtractUserAttributes` を呼ぶので、集合値の扱い（T-01b）は本物のままである。
    private static SearchUserContext Anonymous() =>
        SearchUserContext.FromRequest(new DefaultHttpContext());

    private static SearchUserContext Authenticated(
        string name, string? clearance = null, string? department = null, string[]? tags = null,
        string? authorization = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (clearance is not null) claims.Add(new Claim("clearance", clearance));
        if (department is not null) claims.Add(new Claim("department", department));
        // ADR-0080 決定 1 (#1323): 集合値は**多値クレーム**として届く（Keycloak の multivalued マッパー）。
        foreach (var tag in tags ?? []) claims.Add(new Claim("tags", tag));
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;
        return SearchUserContext.FromRequest(ctx);
    }

    private static Pb.ExpandNeighborsResponse Neighborhood(Guid? edgeTypeId = null) =>
        new()
        {
            Found = true,
            Edges =
            {
                new Pb.NeighborEdge
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceDocumentId = Seed.ToString(),
                    TargetDocumentId = Guid.NewGuid().ToString(),
                    EdgeTypeId = (edgeTypeId ?? Guid.NewGuid()).ToString(),
                },
            },
        };

    private static Pb.ListEdgeTypeWeightsResponse Weights(params (Guid Id, double Weight)[] items)
    {
        var resp = new Pb.ListEdgeTypeWeightsResponse();
        foreach (var (id, weight) in items)
            resp.Weights.Add(new Pb.EdgeTypeWeight { EdgeTypeId = id.ToString(), Weight = weight });
        return resp;
    }

    private sealed class FakeClient(
        Pb.ExpandNeighborsResponse neighbors, Pb.ListEdgeTypeWeightsResponse weights)
        : Pb.GraphNeighbors.GraphNeighborsClient
    {
        public Pb.ExpandNeighborsRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }
        public Exception? NeighborsException { get; init; }
        public Exception? WeightsException { get; init; }

        public override AsyncUnaryCall<Pb.ExpandNeighborsResponse> ExpandNeighborsAsync(
            Pb.ExpandNeighborsRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            return Call(NeighborsException is null
                ? Task.FromResult(neighbors)
                : Task.FromException<Pb.ExpandNeighborsResponse>(NeighborsException));
        }

        public override AsyncUnaryCall<Pb.ListEdgeTypeWeightsResponse> ListEdgeTypeWeightsAsync(
            Pb.ListEdgeTypeWeightsRequest request, CallOptions options) =>
            Call(WeightsException is null
                ? Task.FromResult(weights)
                : Task.FromException<Pb.ListEdgeTypeWeightsResponse>(WeightsException));

        private static AsyncUnaryCall<T> Call<T>(Task<T> response) =>
            new(response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }
}
