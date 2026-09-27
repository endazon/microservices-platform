using AwesomeAssertions;
using Google.Protobuf;
using GraphService.Domain;
using GraphService.Features.McpTools.Declare;
using GraphService.Features.McpTools.Execute;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace GraphService.Tests.Features.McpTools.Execute;

// FR-16, FR-17, FR-05, UC-08, UC-10, NFR-09, ADR-0034 決定 1・2・3・9, 計画 ADR-0086 決定 1, ADR-0088 決定 1, ADR-0117 決定 1〜3,
// [[IADR-0479]]（2026-09-27 追記 / #1611 段 3）:
// **GraphService の MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）を、**本番の Program.cs のまま・実 Kestrel の h2c ポート**
// （`GrpcKestrelFactory`。127.0.0.1）と本物の JwtBearer で往復して固定する（テスト仕様書 FR-16 の X-40〜X-47）。
//
// 🔴 否定の試験と陽性対照を**同じ器・同じグラフ**で対にする ——「拒否された」「返らなかった」だけでは、器が壊れているのか
//   判定が効いているのか区別できない。器の解決器（`ScopeFor`）は本文の利用者で答えを決め、`ResolvedFor` に問い合わせを記録する
//   （判定の位置の観測点。呼び出し元の scope を信じているなら 1 度も呼ばれない）。
//
// グラフ（試験ごとに一意の ID。器の DB は寿命で共有される）:
//   B1 → O → L1 → L2        （組織文書・権限内。O が起点）
//   O → X → Z               （X は権限外の部署。Z は権限内だが X の先にしか無い＝橋の検査）
//   O → P → Q               （P は個人資料〔所有者の分岐でだけ見える〕。Q は権限内だが P の先にしか無い）
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcMcpToolExecutionTests
{
    private const string Trusted = "mcp-server";
    private const string SharedWith = "bob-secret,grp-hr-1234";
    private readonly GrpcKestrelFactory _factory;

    public GrpcMcpToolExecutionTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        _factory.ResolvedFor.Clear();
        _factory.ScopeFor = (user, _) => new AccessScopeResponse(user.UserId, [], false);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // 実 Keycloak のサービスアカウントの形（`preferred_username = service-account-<clientId>` と `azp`）。
    private static string ServiceAccountToken(string clientId) =>
        GrpcKestrelFactory.IssueToken($"service-account-{clientId}", [PlatformAuthPolicies.ServiceRole], azp: clientId);

    // 既定スコープが `roles` だけの機械クライアントの実形（`preferred_username` が無く `azp` だけ）。
    private static string AzpOnlyToken(string clientId) =>
        GrpcKestrelFactory.IssueToken(Guid.NewGuid().ToString("D"), [PlatformAuthPolicies.ServiceRole],
            azp: clientId, withUsername: false);

    private GrpcChannel Channel() => GrpcChannel.ForAddress(_factory.GrpcAddress);

    private Pb.McpToolExecution.McpToolExecutionClient Grpc() => new(Channel());

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private static Pb.ExecuteMcpToolRequest As(string userId, string tool, string args, string action = "read") => new()
    {
        Tool = tool,
        ArgumentsJson = args,
        User = new Pb.McpToolUserContext { UserId = userId, Action = action },
    };

    private static string Args(Guid documentId, string extra = "") => $$"""{"document_id":"{{documentId}}"{{extra}}}""";

    private sealed record Seeded(
        Guid O, Guid B1, Guid L1, Guid L2, Guid X, Guid Z, Guid P, Guid Q,
        Guid LinkType, Guid OtherType, string AllowedDept, string ForbiddenDept, string Owner)
    {
        public string Id(Guid g) => g.ToString();
    }

    private async Task<Seeded> SeedAsync()
    {
        var s = new Seeded(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Unique("allowed"), Unique("forbidden"), Unique("owner"));

        Dictionary<string, string> Org(string dept) => new()
        {
            ["dept"] = dept,
            [DocumentScopes.Key] = DocumentScopes.Organization,
            ["confidentiality"] = "internal",
        };

        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(s.O, "起点", Org(s.AllowedDept), null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.B1, "被参照元", Org(s.AllowedDept), null, DateTimeOffset.UtcNow));
            // L1 は共有付きの組織文書（同期時に重ねる ABAC 判定用の像の形）と、許可リストの `project` を持つ。
            var l1 = Org(s.AllowedDept);
            l1[AttributeValueKeys.SharedWith] = SharedWith;
            l1["owner"] = "carol-owner";
            l1[RestrictedProject.DocumentKey] = "alpha";
            db.Documents.Add(GraphDocument.Create(s.L1, "参照先", l1, null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.L2, "二つ先", Org(s.AllowedDept), null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.X, "権限外", Org(s.ForbiddenDept), null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.Z, "権限外の先", Org(s.AllowedDept), null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.P, "個人資料", new Dictionary<string, string>
            {
                ["dept"] = s.AllowedDept,
                [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                ["owner"] = s.Owner,
                [DocumentExposure.GraphKey] = DocumentExposure.Included,
            }, null, DateTimeOffset.UtcNow));
            db.Documents.Add(GraphDocument.Create(s.Q, "個人資料の先", Org(s.AllowedDept), null, DateTimeOffset.UtcNow));

            db.Edges.Add(Edge.Create(s.B1, s.O, s.LinkType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.O, s.L1, s.OtherType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.L1, s.L2, s.LinkType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.O, s.X, s.LinkType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.X, s.Z, s.LinkType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.O, s.P, s.LinkType, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(s.P, s.Q, s.LinkType, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });

        // 組織の分岐（部署）と裁量の分岐（所有者）。個人資料を開けるのは裁量の分岐だけ（`PrivateNoteVisibility`）。
        // 🔴 利用者を問わず同じスコープを返す —— サービスアカウントでも「許可が開けていても」落ちることを見るため。
        var scope = new AccessScopeResponse(
            "resolved",
            [new AttributeFilter("dept", [s.AllowedDept])],
            Granted: true,
            [
                new AccessScopeBranch("organization", [new AttributeFilter("dept", [s.AllowedDept])]),
                new AccessScopeBranch("owner", [new AttributeFilter("owner", [s.Owner])]),
            ]);
        _factory.ScopeFor = (user, _) => scope with { UserId = user.UserId };
        return s;
    }

    private async Task<Pb.McpToolResult> ExecuteAsync(Pb.ExecuteMcpToolRequest request, string? token = null) =>
        await Grpc().ExecuteAsync(request, Bearer(token ?? ServiceAccountToken(Trusted)), cancellationToken: Ct);

    private static IEnumerable<string> Ids(Pb.McpToolResult r) => r.Documents.Select(d => d.DocumentId);

    private async Task ShouldFailAsync(
        Pb.ExecuteMcpToolRequest request, string token, StatusCode expected, string because)
    {
        var act = async () => await Grpc().ExecuteAsync(request, Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected, because);
        _factory.ResolvedFor.Should().NotContain(r => r.User.UserId == request.User.UserId,
            "拒否した呼び出しで認可サービスへ問い合わせない: " + because);
    }

    // X-40（陽性対照）: MCP サーバーの s2s（利用者名の形・`azp` だけの形）で、3 ツールを本文の利用者として実行でき、
    // 受け口が**自分で**その利用者の判定を問う（属性は空・操作は read）。応答は共通エンベロープ（題名・属性。本文・参照リンクは無い）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MCPサーバーのs2sなら利用者として3つのツールを実行でき受け口が自分で判定を問う(bool azpOnly)
    {
        var s = await SeedAsync();
        var user = Unique("alice");
        var token = azpOnly ? AzpOnlyToken(Trusted) : ServiceAccountToken(Trusted);

        var traverse = await ExecuteAsync(As(user, McpToolExecutionGrpcService.TraverseTool, Args(s.O)), token);
        var links = await ExecuteAsync(As(user, McpToolExecutionGrpcService.GetLinksTool, Args(s.O)), token);
        var backlinks = await ExecuteAsync(As(user, McpToolExecutionGrpcService.GetBacklinksTool, Args(s.O)), token);

        Ids(traverse).Should().BeEquivalentTo([s.Id(s.B1), s.Id(s.L1), s.Id(s.L2), s.Id(s.P), s.Id(s.Q)],
            "既定 2 ホップ。起点は含めない。有人（所有者の分岐）には個人資料とその先も見える（対照）");
        traverse.TotalCount.Should().Be(5);
        traverse.Truncated.Should().BeFalse();
        Ids(links).Should().BeEquivalentTo([s.Id(s.L1), s.Id(s.P)], "起点 → 相手の辺だけ");
        Ids(backlinks).Should().Equal([s.Id(s.B1)], "相手 → 起点の辺だけ");

        var l1 = links.Documents.Single(d => d.DocumentId == s.Id(s.L1));
        l1.Title.Should().Be("参照先");
        l1.Attributes.Should().Contain("confidentiality", "internal").And.Contain(DocumentScopes.Key, DocumentScopes.Organization)
            .And.Contain(RestrictedProject.DocumentKey, "alpha");
        l1.Attributes.Keys.Should().BeSubsetOf(McpEnvelopeAttributes.Keys,
            "エンベロープの属性は MCP サーバーが読むキーの許可リストだけ（部署・所有者・共有先は運ばない）");
        l1.HasBody.Should().BeFalse("グラフは本文を持たない");
        l1.HasReferenceUrl.Should().BeFalse();

        _factory.ResolvedFor.Where(r => r.User.UserId == user).Should().HaveCount(3)
            .And.OnlyContain(r => r.Action == "read" && r.User.Attributes.Count == 0 && r.User.IsAuthenticated,
                "属性は運ばない（認可サービスが引き直す）。操作は受け口が決めた read");
    }

    // 🔴 X-41（否定）: MCP サーバー以外の `platform-service` の主体は、利用者文脈を運んでも PERMISSION_DENIED。
    // 同じグラフ・同じ起点で MCP サーバーには返ることを先に確かめる（対照）。認可の問い合わせも走らない。
    [Theory]
    [InlineData("retrieval-service")]
    [InlineData("bff")]
    [InlineData("aianalysis-service")]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("document-service")]
    [InlineData("graph-service")]
    public async Task MCPサーバー以外の主体からの実行はPERMISSION_DENIED(string clientId)
    {
        var s = await SeedAsync();
        Ids(await ExecuteAsync(As(Unique("control"), McpToolExecutionGrpcService.GetLinksTool, Args(s.O))))
            .Should().Contain(s.Id(s.L1), "対照: 同じ起点が MCP サーバーには返る");

        await ShouldFailAsync(As(Unique("victim"), McpToolExecutionGrpcService.GetLinksTool, Args(s.O)),
            ServiceAccountToken(clientId), StatusCode.PermissionDenied, clientId);
        await ShouldFailAsync(As(Unique("victim"), McpToolExecutionGrpcService.TraverseTool, Args(s.O)),
            AzpOnlyToken(clientId), StatusCode.PermissionDenied, clientId + "（azp だけの形）");
    }

    // 🔴 X-41（否定）: クライアント識別の接頭辞・大小文字の変種、`azp` の食い違い、人のトークンは信じない。
    // 資格情報なしは UNAUTHENTICATED、管理者の利用者トークンは PERMISSION_DENIED。
    [Theory]
    [InlineData("mcp-server-x")]
    [InlineData("mcp-serve")]
    [InlineData("xmcp-server")]
    [InlineData("MCP-SERVER")]
    public async Task MCPサーバーの変種や人のトークンは信じない(string variant)
    {
        var s = await SeedAsync();
        var request = () => As(Unique("victim"), McpToolExecutionGrpcService.TraverseTool, Args(s.O));

        await ShouldFailAsync(request(), ServiceAccountToken(variant), StatusCode.PermissionDenied, variant);
        await ShouldFailAsync(request(),
            GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole], azp: variant),
            StatusCode.PermissionDenied, "利用者名は mcp-server でも azp が別");
        await ShouldFailAsync(request(),
            GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted),
            StatusCode.PermissionDenied, "人のトークン（azp=mcp-server・platform-service つき）");
        await ShouldFailAsync(request(),
            GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]), StatusCode.PermissionDenied, "管理者の利用者トークン");

        var anonymous = async () => await Grpc().ExecuteAsync(request(), cancellationToken: Ct);
        (await anonymous.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 X-42（否定）: 利用者に権限の無い文書は返らず、**権限の無い文書を橋にした先も返らない**（ホップごとの ABAC。ADR-0034 決定 1）。
    // Z は権限内だが権限外の X の先にしか無い —— 「探索してから濾す」形なら Z が浮上する。
    [Fact]
    public async Task 権限の無い文書もそれを橋にした先も返らない()
    {
        var s = await SeedAsync();

        var result = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, Args(s.O, ""","hops":3""")));

        Ids(result).Should().Contain(s.Id(s.L2), "対照: 権限内は 2 ホップ先まで返る");
        Ids(result).Should().NotContain(s.Id(s.X), "権限外の部署");
        Ids(result).Should().NotContain(s.Id(s.Z), "権限外の文書の先（橋）");
        result.TotalCount.Should().Be(result.Documents.Count);
        Ids(await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.GetLinksTool, Args(s.O))))
            .Should().NotContain(s.Id(s.X));
    }

    // 🔴 X-42（否定）: 判定が許可を返さなければ（granted=false。認可サービス不達の縮退も同じ値）1 件も返らない。
    // 起点が権限外・存在しないも同じ空の応答（区別しない。ADR-0034 決定 2）。同じ利用者・同じ起点で許可なら返る（対照）。
    [Fact]
    public async Task 許可が無い起点が見えない起点が無いはどれも空で区別できない()
    {
        var s = await SeedAsync();
        var granted = _factory.ScopeFor;
        var alice = Unique("alice");
        Ids(await ExecuteAsync(As(alice, McpToolExecutionGrpcService.TraverseTool, Args(s.O)))).Should().NotBeEmpty("対照");

        _factory.ScopeFor = (user, _) => new AccessScopeResponse(user.UserId, [], false);
        var denied = await ExecuteAsync(As(alice, McpToolExecutionGrpcService.TraverseTool, Args(s.O)));

        _factory.ScopeFor = granted;
        var hidden = await ExecuteAsync(As(alice, McpToolExecutionGrpcService.GetLinksTool, Args(s.X)));
        var missing = await ExecuteAsync(As(alice, McpToolExecutionGrpcService.GetLinksTool, Args(Guid.NewGuid())));

        foreach (var r in new[] { denied, hidden, missing })
        {
            r.Documents.Should().BeEmpty();
            r.TotalCount.Should().Be(0);
            r.Truncated.Should().BeFalse();
            r.ToByteArray().Should().Equal(missing.ToByteArray(), "応答のバイト列で区別できない");
        }
    }

    // 🔴 X-43（否定）: 本文に scope を入れても効かない。旧い呼び出し元の形（番号 3。権限外の部署を開ける属性つき）を
    // **生のバイト列で実際にワイヤへ載せても**、引数に `scope` / `filters` を書いても、受け口が自分で引いたスコープのとおり。
    [Fact]
    public async Task 旧い番号3のscopeや引数のscopeは効かない()
    {
        var s = await SeedAsync();
        var user = Unique("alice");
        var body = As(user, McpToolExecutionGrpcService.GetLinksTool, Args(s.O)).ToByteArray()
            .Concat(LegacyScopeField(s.ForbiddenDept)).ToArray();

        Pb.ExecuteMcpToolRequest.Parser.ParseFrom(body).ToByteArray().Length.Should().Be(body.Length,
            "番号 3 が未知のフィールドとしてワイヤに乗っている（空振りの試験ではない）");

        var legacy = await RawExecuteAsync(body, ServiceAccountToken(Trusted));
        legacy.Documents.Select(d => d.DocumentId).Should().Contain(s.Id(s.L1), "対照: 実行は通る");
        legacy.Documents.Select(d => d.DocumentId).Should().NotContain(s.Id(s.X), "本文の scope が開けようとした部署は開かない");
        _factory.ResolvedFor.Should().ContainSingle(r => r.User.UserId == user, "受け口は本文の利用者で自分で判定を問う");

        var extra = $$$""","scope":{"filters":[{"key":"dept","allowed_values":["{{{s.ForbiddenDept}}}"]}],"grants_access":true},"filters":{"dept":"{{{s.ForbiddenDept}}}"},"attributes":{"role":"admin"}""";
        var args = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, Args(s.O, extra)));
        Ids(args).Should().Contain(s.Id(s.L1)).And.NotContain(s.Id(s.X)).And.NotContain(s.Id(s.Z));
    }

    // 🔴 X-44（ADR-0034 決定 9 の要求側の 1 層目）: サービスアカウント実行（`service-account-` の利用者名）は、スコープが
    // 開けていても個人資料を返さず、**個人資料を橋にした先も返さない**。起点が個人資料なら空。同じ点・同じスコープで有人には返る（X-40 が対照）。
    [Fact]
    public async Task サービスアカウント実行は個人資料を返さず橋にもしない()
    {
        var s = await SeedAsync();
        const string agent = "service-account-batch-agent";

        var traverse = await ExecuteAsync(As(agent, McpToolExecutionGrpcService.TraverseTool, Args(s.O)));
        var links = await ExecuteAsync(As(agent, McpToolExecutionGrpcService.GetLinksTool, Args(s.O)));
        var fromPrivate = await ExecuteAsync(As(agent, McpToolExecutionGrpcService.GetLinksTool, Args(s.P)));
        var human = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.GetLinksTool, Args(s.P)));

        Ids(traverse).Should().BeEquivalentTo([s.Id(s.B1), s.Id(s.L1), s.Id(s.L2)], "P もその先の Q も返らない");
        traverse.TotalCount.Should().Be(3);
        Ids(links).Should().Equal(s.Id(s.L1));
        fromPrivate.Documents.Should().BeEmpty("起点が個人資料なら見えないのと同じ");
        Ids(human).Should().Equal([s.Id(s.Q)], "対照: 有人には個人資料の先が返る");
    }

    // X-45: 本文の `action` は受け口が決めた操作（read）と突き合わせるだけ。違えば INVALID_ARGUMENT で、判定を問わない。
    [Theory]
    [InlineData("write")]
    [InlineData("")]
    [InlineData("READ")]
    public async Task 操作がツールの要する操作と違えばINVALID_ARGUMENT(string action)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, Args(s.O), action),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, action);
    }

    // X-46: 利用者文脈が無い・空は INVALID_ARGUMENT（機械の主体へ読み替えない）。
    [Fact]
    public async Task 利用者文脈が無い実行はINVALID_ARGUMENT()
    {
        var s = await SeedAsync();
        var noUser = new Pb.ExecuteMcpToolRequest { Tool = McpToolExecutionGrpcService.TraverseTool, ArgumentsJson = Args(s.O) };

        var act = async () => await Grpc().ExecuteAsync(noUser, Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        _factory.ResolvedFor.Should().BeEmpty();

        await ShouldFailAsync(As("  ", McpToolExecutionGrpcService.TraverseTool, Args(s.O)),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, "空白の user_id");
    }

    // X-46: 宛先は申告したサービス＋ツール名。自分の申告に無い名前（他のサービスのツール・非公開の要約系・公開名・空白の変種）は NOT_FOUND。
    [Theory]
    [InlineData("retrieval.search_documents")]
    [InlineData("document.get_document")]
    [InlineData("graph.get_cluster_summary")]
    [InlineData("traverse")]
    [InlineData("graph.traverse ")]
    [InlineData("GRAPH.TRAVERSE")]
    [InlineData("")]
    public async Task 自分の申告に無いツールはNOT_FOUND(string tool)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(As(Unique("alice"), tool, Args(s.O)), ServiceAccountToken(Trusted), StatusCode.NotFound, tool);
    }

    // X-47: 引数は申告の `input_schema` のとおりに検証し、丸めない（INVALID_ARGUMENT。判定を問わない）。
    [Theory]
    [InlineData("graph.traverse", """{}""")]
    [InlineData("graph.traverse", """{"document_id":""}""")]
    [InlineData("graph.traverse", """{"document_id":"not-a-guid"}""")]
    [InlineData("graph.traverse", """{"document_id":7}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","hops":0}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","hops":4}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","hops":2.5}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","hops":"2"}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","edge_types":"TYPE"}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","edge_types":["not-a-guid"]}""")]
    [InlineData("graph.traverse", """{"document_id":"DOC","edge_types":[7]}""")]
    [InlineData("graph.get_links", """{"document_id":"not-a-guid"}""")]
    [InlineData("graph.get_backlinks", """{}""")]
    [InlineData("graph.get_backlinks", """["DOC"]""")]
    [InlineData("graph.get_links", """not json""")]
    public async Task 引数が申告の形でなければINVALID_ARGUMENT(string tool, string args)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(
            As(Unique("alice"), tool, args.Replace("DOC", s.Id(s.O)).Replace("TYPE", s.Id(s.LinkType))),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, tool + " " + args);
    }

    // X-47（対照）: hops の境界ちょうど・省略（既定 2）、辺の型の絞り込み・空の配列（絞らない）は通る。
    [Theory]
    [InlineData(""","hops":1""", new[] { "B1", "L1", "P" })]
    [InlineData(""","hops":3""", new[] { "B1", "L1", "L2", "P", "Q" })]
    [InlineData("", new[] { "B1", "L1", "L2", "P", "Q" })]
    [InlineData(""","edge_types":[]""", new[] { "B1", "L1", "L2", "P", "Q" })]
    [InlineData(""","edge_types":["OTHER"]""", new[] { "L1" })]
    public async Task 引数の境界と既定と辺の型は通る(string extra, string[] expected)
    {
        var s = await SeedAsync();
        var names = new Dictionary<string, Guid> { ["B1"] = s.B1, ["L1"] = s.L1, ["L2"] = s.L2, ["P"] = s.P, ["Q"] = s.Q };
        var json = extra.Replace("OTHER", s.Id(s.OtherType));

        var result = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, Args(s.O, json)));

        Ids(result).Should().BeEquivalentTo(expected.Select(n => s.Id(names[n])));
    }

    // 🔴 X-50（否定。監査 B-1）: 共有先（`shared_with`）は所有者にだけ返す規則を MCP の経路で迂回しない。
    // 所有者でない有人とサービスアカウントの両方で、共有付きの組織文書の応答の属性に共有先も所有者も部署も載らない。
    // 陽性対照: 同じ文書・同じ応答に許可リストのキー（機密区分・プロジェクト）は載る。
    [Theory]
    [InlineData("alice-probe")]
    [InlineData("service-account-batch-agent")]
    public async Task 共有先と所有者はエンベロープの属性に載らない(string userId)
    {
        var s = await SeedAsync();

        foreach (var tool in new[] { McpToolExecutionGrpcService.GetLinksTool, McpToolExecutionGrpcService.TraverseTool })
        {
            var result = await ExecuteAsync(As(userId, tool, Args(s.O)));
            var l1 = result.Documents.Should().ContainSingle(d => d.DocumentId == s.Id(s.L1), "対照: 共有付きの文書そのものは見える").Which;

            l1.Attributes.Should().Contain("confidentiality", "internal").And.Contain(RestrictedProject.DocumentKey, "alpha");
            l1.Attributes.Should().NotContainKey(AttributeValueKeys.SharedWith, tool);
            l1.Attributes.Values.Should().NotContain(v => v.Contains("bob-secret"), tool);
            l1.Attributes.Should().NotContainKey("owner").And.NotContainKey("dept");
        }
    }

    // X-51（監査 N-1）: 近傍探索が表示上限で打ち切ったら `truncated` を立て、件数は許可済みの全体（起点を除く）を返す。
    // 返した件数ではない（打ち切ったら全体件数〔判定後〕）。権限外の近傍は数えない。
    [Fact]
    public async Task 表示上限で打ち切ったら全体件数を返す()
    {
        var s = await SeedAsync();
        var hub = Guid.NewGuid();
        var extra = GraphTraversal.MaxNodes + 5;
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(hub, "ハブ", new() { ["dept"] = s.AllowedDept, ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
            for (var i = 0; i < extra; i++)
            {
                var n = Guid.NewGuid();
                db.Documents.Add(GraphDocument.Create(n, $"近傍{i}", new() { ["dept"] = s.AllowedDept, ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
                db.Edges.Add(Edge.Create(hub, n, s.LinkType, false, EdgeProvenance.Auto));
            }
            // 権限外の近傍（数えない）。
            var hidden = Guid.NewGuid();
            db.Documents.Add(GraphDocument.Create(hidden, "権限外の近傍", new() { ["dept"] = s.ForbiddenDept }, null, DateTimeOffset.UtcNow));
            db.Edges.Add(Edge.Create(hub, hidden, s.LinkType, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });

        var result = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, Args(hub, ""","hops":1""")));

        result.Truncated.Should().BeTrue();
        result.Documents.Should().HaveCount(GraphTraversal.MaxNodes - 1, "表示は起点を含めて上限まで");
        result.TotalCount.Should().Be(extra, "許可済みの全体件数（起点・権限外を除く）");
    }

    // 🔴 X-54（X-51 のサービスアカウント版。ADR-0034 決定 4・9 / #1671）: 打ち切ったときの全体件数に、探索で刈った個人資料を数えない。
    // 数えると件数そのものが「見えない何かがある」ことを明かす。同じハブ・同じスコープで有人は個人資料を数える（陽性対照）。
    [Fact]
    public async Task サービスアカウント実行で打ち切ったら全体件数に刈った個人資料を数えない()
    {
        var s = await SeedAsync();
        var hub = Guid.NewGuid();
        var extra = GraphTraversal.MaxNodes + 5;
        const int privateNotes = 3;
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(GraphDocument.Create(hub, "ハブ", new() { ["dept"] = s.AllowedDept, ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
            for (var i = 0; i < extra; i++)
            {
                var n = Guid.NewGuid();
                db.Documents.Add(GraphDocument.Create(n, $"近傍{i}", new() { ["dept"] = s.AllowedDept, ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
                db.Edges.Add(Edge.Create(hub, n, s.LinkType, false, EdgeProvenance.Auto));
            }
            // 所有者の分岐でだけ見える個人資料（有人には数えられ、サービスアカウントでは刈られる）。
            for (var i = 0; i < privateNotes; i++)
            {
                var p = Guid.NewGuid();
                db.Documents.Add(GraphDocument.Create(p, $"個人資料{i}", new()
                {
                    ["dept"] = s.AllowedDept,
                    [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                    ["owner"] = s.Owner,
                    [DocumentExposure.GraphKey] = DocumentExposure.Included,
                }, null, DateTimeOffset.UtcNow));
                db.Edges.Add(Edge.Create(hub, p, s.LinkType, false, EdgeProvenance.Auto));
            }
            return Task.CompletedTask;
        });

        var args = Args(hub, ""","hops":1""");
        var agent = await ExecuteAsync(As("service-account-batch-agent", McpToolExecutionGrpcService.TraverseTool, args));
        var human = await ExecuteAsync(As(Unique("alice"), McpToolExecutionGrpcService.TraverseTool, args));

        agent.Truncated.Should().BeTrue();
        agent.TotalCount.Should().Be(extra, "刈った個人資料は全体件数に入らない");
        agent.Documents.Should().NotContain(d => d.Title.StartsWith("個人資料"));
        human.Truncated.Should().BeTrue();
        human.TotalCount.Should().Be(extra + privateNotes, "対照: 有人は同じハブで個人資料も数える");
    }

    // 構造の門: 面は ServiceCaller を宣言している（外れると上の門の試験が落ちるが、どの層で外れたかを名指しする）。
    [Fact]
    public void 実行口はServiceCallerを要求する()
    {
        typeof(McpToolExecutionGrpcService).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().Should().ContainSingle()
            .Which.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // X-48: 受け口が実行するツールと hops の上限・既定は、申告（`McpToolDeclarationSource`）と同じである（2 か所に持つ値の一致）。
    [Fact]
    public void 実行するツールとhopsの上限既定は申告と一致する()
    {
        var declared = McpToolDeclarationSource.Declare().Tools;

        declared.Select(t => t.Name).Should().BeEquivalentTo(
            [McpToolExecutionGrpcService.GetBacklinksTool, McpToolExecutionGrpcService.GetLinksTool, McpToolExecutionGrpcService.TraverseTool]);
        declared.Should().OnlyContain(t => t.InputSchema.Contains("\"document_id\""));
        declared.Single(t => t.Name == McpToolExecutionGrpcService.TraverseTool).InputSchema
            .Should().Contain($"\"minimum\":1,\"maximum\":{GraphTraversal.MaxHops},\"default\":{GraphTraversal.DefaultHops}")
            .And.Contain("\"edge_types\"");
    }

    // 旧い `ExecuteMcpToolRequest.scope`（番号 3）の形を手で組む: subject_id=1・subject_attributes=3（map）・
    // exclude_private_note=4・required_scope=5。権限外の部署を開ける属性を載せる。
    private static byte[] LegacyScopeField(string forbiddenDept)
    {
        using var inner = new MemoryStream();
        var io = new CodedOutputStream(inner);
        io.WriteTag(1, WireFormat.WireType.LengthDelimited);
        io.WriteString("admin");
        foreach (var (key, value) in new[] { ("dept", forbiddenDept), ("role", "admin") })
        {
            using var entry = new MemoryStream();
            var e = new CodedOutputStream(entry);
            e.WriteTag(1, WireFormat.WireType.LengthDelimited);
            e.WriteString(key);
            e.WriteTag(2, WireFormat.WireType.LengthDelimited);
            e.WriteString(value);
            e.Flush();
            io.WriteTag(3, WireFormat.WireType.LengthDelimited);
            io.WriteBytes(ByteString.CopyFrom(entry.ToArray()));
        }
        io.WriteTag(4, WireFormat.WireType.Varint);
        io.WriteBool(false);
        io.WriteTag(5, WireFormat.WireType.LengthDelimited);
        io.WriteString("graph:read");
        io.Flush();

        using var outer = new MemoryStream();
        var o = new CodedOutputStream(outer);
        o.WriteTag(3, WireFormat.WireType.LengthDelimited);
        o.WriteBytes(ByteString.CopyFrom(inner.ToArray()));
        o.Flush();
        return outer.ToArray();
    }

    // 生成された型を通さずにバイト列をそのまま送る（旧い呼び出し元の形を実際にワイヤへ載せるため）。
    private async Task<Pb.McpToolResult> RawExecuteAsync(byte[] body, string token)
    {
        var marshaller = Marshallers.Create(static (byte[] b) => b, static b => b);
        var method = new Method<byte[], byte[]>(
            MethodType.Unary, Pb.McpToolExecution.Descriptor.FullName, "Execute", marshaller, marshaller);
        var bytes = await Channel().CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(Bearer(token), cancellationToken: Ct), body);
        return Pb.McpToolResult.Parser.ParseFrom(bytes);
    }
}
