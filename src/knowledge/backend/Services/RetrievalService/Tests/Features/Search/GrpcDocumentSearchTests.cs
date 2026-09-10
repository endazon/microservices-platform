using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Tests.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search;

// FR-03, FR-04, FR-05, FR-07, FR-17, NFR-09, NFR-16, UC-01, UC-02, UC-10, SC-01, SC-08,
// ADR-0004, ADR-0029, ADR-0034 決定 1, ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1,
// ADR-0087 決定 2, ADR-0089 決定 1, [[IADR-0009]], [[IADR-0151]], [[IADR-0379]], [[IADR-0401]],
// [[IADR-0410]], [[IADR-0415]], [[IADR-0416]], [[IADR-0417]], [[IADR-0426]] (#1255):
// ハイブリッド検索の gRPC 面（`knowledge.retrieval.v1.DocumentSearch`）を
// **実 Kestrel の h2c ポート**で往復し、REST との同値・s2s の要求・面に出さないものを固定する。
//
// 陽性対照と陰性対照を同じ器で対にする —— 「拒否された」だけでは
// 器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcDocumentSearchTests
{
    private const string ServiceSubject = "service-account-aianalysis-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcDocumentSearchTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private Pb.DocumentSearch.DocumentSearchClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Pb.UserContext User(string id = "alice", string? department = null)
    {
        var user = new Pb.UserContext { UserId = id, Action = "read" };
        if (department is not null) user.UserAttributes["department"] = department;
        return user;
    }

    private static Pb.SearchRequest Request(
        string query = "検索", int topK = 50, Pb.UserContext? user = null)
        => new() { Query = query, TopK = topK, User = user ?? User() };

    // 一意な `dept` を持つ 1 点を索引へ入れる。器（索引）は器の寿命で共有されるため、
    // **各試験は自分が入れた点だけを見る**（他の試験の点と混ざらない鍵を使う）。
    private async Task<(Guid DocumentId, Guid ChunkId)> SeedAsync(
        string dept, string text = "検索 対象", string? markdownUri = "s3://b/x.md",
        DateTimeOffset? updatedAt = null, bool hasBody = true)
    {
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        await _factory.Index.UpsertAsync(new ChunkPayload(
            chunkId, documentId, $"doc:{dept}", text, new float[1536], markdownUri,
            new Dictionary<string, string> { ["dept"] = dept }, ["tag-a"], updatedAt, hasBody));
        return (documentId, chunkId);
    }

    // 自分が入れた点だけが見える権威スコープ（他の試験の点を締め出す）。
    private static AccessScopeResponse OnlyDept(string dept, string userId = "alice") =>
        new(userId, [new AttributeFilter("dept", [dept])], Granted: true);

    // T-01: 陽性対照。s2s トークンを CallCredentials で付けた h2c チャネルで往復し、結果が返る。
    [Fact]
    public async Task Search_over_h2c_with_service_token_returns_results()
    {
        var dept = $"h2c-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = OnlyDept(dept);
        var (documentId, chunkId) = await SeedAsync(dept);

        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new Pb.DocumentSearch.DocumentSearchClient(channel);

        var resp = await client.SearchAsync(
            Request(), cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Should().ContainSingle();
        resp.Results[0].DocumentId.Should().Be(documentId.ToString());
        resp.Results[0].ChunkId.Should().Be(chunkId.ToString());
    }

    // T-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Search_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().SearchAsync(
            Request(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-03: **管理者の利用者トークンでも PERMISSION_DENIED**（confused deputy の防止）。
    // REST の受け口は realm の認証済み主体なら通る（[[IADR-0418]]）ので、
    // 「認証さえあれば通る」形にすると s2s の面が利用者トークンでも開く（[[IADR-0379]] 決定 4）。
    [Fact]
    public async Task Search_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().SearchAsync(
            Request(), headers: Bearer(adminToken),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // 🔴 T-04: **利用者が分からないのは要求の誤りである**（deny へ畳まない）。
    // 畳むと呼び出し元の配線誤りが「該当が 1 件も無い」と見分けられなくなる。
    [Fact]
    public async Task A_request_without_a_user_is_invalid_argument()
    {
        var act = async () => await PlainClient().SearchAsync(
            new Pb.SearchRequest { Query = "検索", TopK = 10 },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    // 🔴 T-05（本スライスの核心）: **呼び出し元が主張した scope を受ける口が存在しない。**
    // 面が運ぶのは利用者文脈と絞り込みだけであり、**呼び出し先が自分で解決する**
    // （[[IADR-0410]] / [[IADR-0416]]）。
    [Fact]
    public void The_face_carries_a_user_context_and_never_a_resolved_scope()
    {
        var fields = Pb.SearchRequest.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).ToList();

        fields.Should().BeEquivalentTo(["query", "top_k", "user", "narrow_to"]);
        fields.Should().NotContain("scope",
            "呼び出し元が解決したスコープを受ける口を開くと、到達できる誰もが任意の scope を主張できる");

        // 🔴 面に出さないもの（[[IADR-0401]] 決定 2）: 検索モード・並び順・総ヒット数・所要時間。
        Pb.SearchResponse.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).Should().Equal(["results"]);
    }

    // 🔴 T-06: **呼び出し先が自分で判定している**ことの直接の観測。
    // 記録が無ければ「呼び出し元のスコープを信じている」実装でも上の試験は緑になり得る。
    [Fact]
    public async Task The_service_resolves_the_scope_from_the_user_context_in_the_body()
    {
        var user = $"probe-{Guid.NewGuid():N}"[..20];
        _factory.Authoritative = OnlyDept($"none-{Guid.NewGuid():N}"[..12], user);

        await PlainClient().SearchAsync(
            Request(user: User(user, department: "engineering")),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var seen = _factory.ResolvedFor.Where(r => r.UserId == user).ToList();
        seen.Should().ContainSingle("本文の文脈で自分の判定を行っている");
        seen[0].Attributes.Should().Contain(
            new KeyValuePair<string, string>("department", "engineering"),
            "属性も本文で運ばれる（判定の入力であって結果ではない）");
    }

    // 🔴 T-07: **絞り込みは権限を広げない**（[[IADR-0415]] の narrowing-only）。
    [Fact]
    public async Task A_narrowing_cannot_reach_beyond_the_resolved_scope()
    {
        var mine = $"mine-{Guid.NewGuid():N}"[..12];
        var other = $"other-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = OnlyDept(mine);
        await SeedAsync(mine);
        var (hidden, _) = await SeedAsync(other);

        var request = Request();
        request.NarrowTo.Add("dept", new Pb.NarrowTo { Values = { other } });

        var resp = await PlainClient().SearchAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Should().BeEmpty("絞り込みは権限の根拠ではない（積が空なら全体 deny）");
        resp.Results.Should().NotContain(r => r.DocumentId == hidden.ToString());
    }

    // ★ T-08（T-07 の陽性対照）: 絞り込みは**実際に絞る**。
    // 🔴 これが無いと「`narrow_to` を読まずに捨てる」実装でも T-07 は緑になる。
    [Fact]
    public async Task A_narrowing_actually_narrows_within_the_resolved_scope()
    {
        var keep = $"keep-{Guid.NewGuid():N}"[..12];
        var drop = $"drop-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = new AccessScopeResponse(
            "alice", [new AttributeFilter("dept", [keep, drop])], Granted: true);
        var (kept, _) = await SeedAsync(keep);
        var (dropped, _) = await SeedAsync(drop);

        var both = await PlainClient().SearchAsync(
            Request(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);
        both.Results.Select(r => r.DocumentId)
            .Should().Contain([kept.ToString(), dropped.ToString()], "★ 陽性対照 —— 両方が権限内である");

        var request = Request();
        request.NarrowTo.Add("dept", new Pb.NarrowTo { Values = { keep } });
        var narrowed = await PlainClient().SearchAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        narrowed.Results.Select(r => r.DocumentId).Should().Equal([kept.ToString()],
            "指定した絞り込みが効いている");
    }

    // T-09: REST と gRPC が**同じ検索**（`SearchEndpoint.ExecuteAsync`）を通る。
    //
    // 🔴 REST が同じプロセスの HTTP/1.1 ポートで応えること自体が、**h2c を有効にしても
    // 8080 側が消えていない**ことの証明でもある。
    [Fact]
    public async Task Rest_and_grpc_report_the_same_results()
    {
        var dept = $"same-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = OnlyDept(dept);
        var (documentId, _) = await SeedAsync(dept);

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        // [[IADR-0418]] (#1318): REST 面は realm の認証済み主体を要する（`ServiceCaller` ではない）。
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"Bearer {GrpcKestrelFactory.IssueToken("alice", [])}");
        var restResp = await http.PostAsJsonAsync("/search",
            new SearchRequest("検索", 50, null, new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);
        var rest = (await restResp.Content.ReadFromJsonAsync<SearchResponse>(
            TestContext.Current.CancellationToken))!;

        var grpc = await PlainClient().SearchAsync(
            Request(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        grpc.Results.Select(r => r.DocumentId).Should().BeEquivalentTo(
            rest.Results.Select(r => r.DocumentId.ToString()), "輸送を替えても応答の意味は変わらない");
        grpc.Results.Select(r => r.DocumentId).Should().Contain(documentId.ToString(),
            "★ 陽性対照 —— 両方が空で一致したのではない");
    }

    // 🔴 T-10: **proto3 の「未指定」を写す**（`docs/api/east-west-grpc.md` の表）。
    // `has_body` は DTO の既定が `true`・proto3 の既定が `false` で**向きが逆**であり、
    // 写し忘れは例外を 1 つも起こさずに**全件を「本文なし」へ**倒す。
    // 陽性・陰性を対で置く —— 片方だけだと「常に true」「常に false」の実装が通る。
    [Fact]
    public async Task Presence_and_defaults_are_copied_across_the_transport()
    {
        var dept = $"map-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = OnlyDept(dept);
        var updatedAt = new DateTimeOffset(2026, 9, 11, 1, 2, 3, TimeSpan.Zero);
        var (withBody, _) = await SeedAsync(dept, updatedAt: updatedAt);
        var (withoutBody, _) = await SeedAsync(
            dept, text: "題名だけの点", markdownUri: null, hasBody: false);

        var resp = await PlainClient().SearchAsync(
            Request(), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        var body = resp.Results.Single(r => r.DocumentId == withBody.ToString());
        body.HasBody.Should().BeTrue("★ 陽性対照 —— 既定の向きが逆でも写せている");
        body.HasMarkdownUri.Should().BeTrue();
        body.UpdatedAt.Should().NotBeNull();
        body.UpdatedAt.ToDateTimeOffset().Should().Be(updatedAt);
        body.Tags.Should().Equal(["tag-a"]);
        body.Attributes.Should().Contain(new KeyValuePair<string, string>("dept", dept));

        var bodyless = resp.Results.Single(r => r.DocumentId == withoutBody.ToString());
        bodyless.HasBody.Should().BeFalse("本文を持たない点は `false` で運ばれる");
        bodyless.HasMarkdownUri.Should().BeFalse("未設定（null）と空文字は別物である");
        bodyless.UpdatedAt.Should().BeNull("未設定は「まだ索引に無い」であり既定値で埋めない");
    }

    // 🔴 T-11: **`top_k` の proto3 の未指定（0）を REST の既定（10）へ写す。**
    // 写し忘れは `Take(0)` になり、**呼び出し元には「該当が無い」に見える**。
    [Fact]
    public async Task An_unset_top_k_falls_back_to_the_rest_default()
    {
        var dept = $"topk-{Guid.NewGuid():N}"[..12];
        _factory.Authoritative = OnlyDept(dept);
        for (var i = 0; i < 12; i++) await SeedAsync(dept, text: $"点 {i}");

        var unset = await PlainClient().SearchAsync(
            new Pb.SearchRequest { Query = "検索", User = User() },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var explicitTopK = await PlainClient().SearchAsync(
            Request(topK: 3), headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        unset.Results.Should().HaveCount(10, "未指定は DTO の既定（10）である");
        explicitTopK.Results.Should().HaveCount(3, "★ 陽性対照 —— 指定した件数は素通しである");
    }

    // T-12: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(DocumentSearchGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // s2s トークンの発行側を固定値へ差し替える（IdP を持たないため）。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }
}
