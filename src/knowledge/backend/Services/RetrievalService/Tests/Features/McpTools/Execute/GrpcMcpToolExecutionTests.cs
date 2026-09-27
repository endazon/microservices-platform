using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.McpTools.Declare;
using RetrievalService.Features.McpTools.Execute;
using RetrievalService.Tests.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace RetrievalService.Tests.Features.McpTools.Execute;

// FR-16, FR-05, UC-08, NFR-09, ADR-0034 決定 1・9, 計画 ADR-0086 決定 1, ADR-0088 決定 1, ADR-0117 決定 1〜3 (#1611):
// **MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）を、**本番の Program.cs のまま・実 Kestrel の h2c ポート**
// （`GrpcKestrelFactory`。127.0.0.1）と本物の JwtBearer で往復して固定する。
//
// 🔴 否定の試験（#1611 の本文）と陽性対照を**同じ器・同じ索引の点**で対にする ——「拒否された」「返らなかった」だけでは
//   器が壊れているのか判定が効いているのか区別できない。
//   ① MCP サーバー以外の主体からの呼び出しは拒否（PERMISSION_DENIED。認可の問い合わせも走らない）
//   ② 利用者に権限の無い文書は返らない（受け口が**自分で**引いたスコープだけが効く）
//   ③ 本文に scope を入れても効かない（旧い番号 3 を実際にワイヤへ載せる／引数に `scope` を書く）
// 器の解決器は利用者を問わず `Authoritative` を返し、`ResolvedFor` に問い合わせを記録する（判定の位置の観測点）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcMcpToolExecutionTests
{
    private const string Trusted = "mcp-server";
    private const string Tool = McpToolExecutionGrpcService.SearchDocumentsTool;
    private readonly GrpcKestrelFactory _factory;

    public GrpcMcpToolExecutionTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
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

    private static Pb.ExecuteMcpToolRequest As(string userId, string query, string action = "read", string? args = null) => new()
    {
        Tool = Tool,
        ArgumentsJson = args ?? $$"""{"query":"{{query}}","limit":50}""",
        User = new Pb.McpToolUserContext { UserId = userId, Action = action },
    };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    // 1 語の query に当たる 3 点を入れる: 権限内の組織文書・権限外の文書（別部署）・権限内の個人資料。
    // 受け口が自分で引くスコープ（`Authoritative`）は権限内の部署だけを開ける。
    private async Task<Seeded> SeedAsync()
    {
        var term = Unique("term");
        var allowedDept = Unique("allowed");
        var forbiddenDept = Unique("forbidden");
        var owner = Unique("owner");
        var seeded = new Seeded(term, allowedDept, forbiddenDept, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await _factory.Index.UpsertAsync(Chunk(seeded.Allowed, $"{term} 組織の文書の本文",
            new() { ["dept"] = allowedDept, ["doc_scope"] = "organization", ["confidentiality"] = "internal" }));
        await _factory.Index.UpsertAsync(Chunk(seeded.Forbidden, $"{term} 権限外の文書の本文",
            new() { ["dept"] = forbiddenDept, ["doc_scope"] = "organization", ["confidentiality"] = "confidential" }));
        await _factory.Index.UpsertAsync(Chunk(seeded.PrivateNote, $"{term} 個人資料の本文",
            new()
            {
                ["dept"] = allowedDept,
                [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                ["owner"] = owner,
                [DocumentExposure.SearchKey] = DocumentExposure.Included,
            }));

        // 組織の分岐（部署）と裁量の分岐（所有者）。個人資料を開けるのは裁量の分岐だけ（`PrivateNoteVisibility`）。
        // 索引は器の寿命で共有されるので、部署も所有者も試験ごとに一意にする（前の試験の点を開けない）。
        _factory.Authoritative = new AccessScopeResponse(
            "resolved",
            [new AttributeFilter("dept", [allowedDept])],
            Granted: true,
            [
                new AccessScopeBranch("organization", [new AttributeFilter("dept", [allowedDept])]),
                new AccessScopeBranch("owner", [new AttributeFilter("owner", [owner])]),
            ]);
        return seeded;
    }

    private static ChunkPayload Chunk(Guid documentId, string text, Dictionary<string, string> attributes) =>
        new(Guid.NewGuid(), documentId, $"doc:{documentId:N}", text, new float[1536], "s3://b/x.md", attributes, [], null, true);

    private sealed record Seeded(
        string Term, string AllowedDept, string ForbiddenDept, Guid Allowed, Guid Forbidden, Guid PrivateNote);

    private async Task ShouldFailAsync(
        Pb.ExecuteMcpToolRequest request, string token, StatusCode expected, string because)
    {
        var act = async () => await Grpc().ExecuteAsync(request, Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected, because);
        _factory.ResolvedFor.Should().NotContain(r => r.UserId == request.User.UserId,
            "拒否した呼び出しで認可サービスへ問い合わせない: " + because);
    }

    // X-16（陽性対照）: MCP サーバーの s2s で、本文の利用者として検索でき、受け口が**自分で**その利用者の判定を問う。
    // 属性は送らない（空）—— 認可サービスが利用者名から引き直す（ADR-0088）。応答は共通エンベロープ。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MCPサーバーのs2sなら利用者として実行でき受け口が自分で判定を問う(bool azpOnly)
    {
        var seeded = await SeedAsync();
        var user = Unique("alice");

        var result = await Grpc().ExecuteAsync(
            As(user, seeded.Term), Bearer(azpOnly ? AzpOnlyToken(Trusted) : ServiceAccountToken(Trusted)), cancellationToken: Ct);

        result.Documents.Select(d => d.DocumentId).Should().BeEquivalentTo(
            [seeded.Allowed.ToString(), seeded.PrivateNote.ToString()], "有人は権限内の組織文書と個人資料が返る（対照）");
        result.TotalCount.Should().Be(2);
        var org = result.Documents.Single(d => d.DocumentId == seeded.Allowed.ToString());
        org.Title.Should().Be($"doc:{seeded.Allowed:N}");
        org.Attributes.Should().Contain("dept", seeded.AllowedDept);
        org.HasBody.Should().BeTrue();
        org.Body.Should().Contain("組織の文書の本文");
        org.HasReferenceUrl.Should().BeFalse("索引の内部の格納先を参照リンクとして出さない");
        _factory.ResolvedFor.Should().ContainSingle(r => r.UserId == user)
            .Which.Attributes.Should().BeEmpty("属性は運ばない（認可サービスが引き直す）");
    }

    // 🔴 X-17（否定①）: MCP サーバー以外の `platform-service` の主体は、利用者文脈を運んでも PERMISSION_DENIED。
    // 同じ点・同じ query で MCP サーバーには返ることを先に確かめる（対照）。認可の問い合わせも走らない。
    [Theory]
    [InlineData("bff")]
    [InlineData("aianalysis-service")]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("document-service")]
    [InlineData("retrieval-service")]
    [InlineData("graph-service")]
    public async Task MCPサーバー以外の主体からの実行はPERMISSION_DENIED(string clientId)
    {
        var seeded = await SeedAsync();
        (await Grpc().ExecuteAsync(As(Unique("control"), seeded.Term), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct))
            .Documents.Should().Contain(d => d.DocumentId == seeded.Allowed.ToString(), "対照: 同じ点が MCP サーバーには返る");

        await ShouldFailAsync(As(Unique("victim"), seeded.Term), ServiceAccountToken(clientId), StatusCode.PermissionDenied, clientId);
        await ShouldFailAsync(As(Unique("victim"), seeded.Term), AzpOnlyToken(clientId), StatusCode.PermissionDenied,
            clientId + "（azp だけの形）");
    }

    // 🔴 X-17（否定①）: クライアント識別の接頭辞・大小文字の変種、`azp` の食い違い、人のトークンは信じない。
    [Theory]
    [InlineData("mcp-server-x")]
    [InlineData("mcp-serve")]
    [InlineData("xmcp-server")]
    [InlineData("MCP-SERVER")]
    public async Task MCPサーバーの変種や人のトークンは信じない(string variant)
    {
        var seeded = await SeedAsync();

        await ShouldFailAsync(As(Unique("victim"), seeded.Term), ServiceAccountToken(variant), StatusCode.PermissionDenied, variant);
        await ShouldFailAsync(As(Unique("victim"), seeded.Term),
            GrpcKestrelFactory.IssueToken($"service-account-{Trusted}", [PlatformAuthPolicies.ServiceRole], azp: variant),
            StatusCode.PermissionDenied, "利用者名は mcp-server でも azp が別");
        await ShouldFailAsync(As(Unique("victim"), seeded.Term),
            GrpcKestrelFactory.IssueToken("carol-relay", [PlatformAuthPolicies.ServiceRole], azp: Trusted),
            StatusCode.PermissionDenied, "人のトークン（azp=mcp-server・platform-service つき）");
    }

    // 面の門: 資格情報なしは UNAUTHENTICATED、利用者のトークン（管理者でも）は PERMISSION_DENIED。
    [Fact]
    public async Task 資格情報なしはUNAUTHENTICATEDで管理者の利用者トークンはPERMISSION_DENIED()
    {
        var seeded = await SeedAsync();

        var anonymous = async () => await Grpc().ExecuteAsync(As(Unique("victim"), seeded.Term), cancellationToken: Ct);
        (await anonymous.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        await ShouldFailAsync(As(Unique("victim"), seeded.Term),
            GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]), StatusCode.PermissionDenied, "管理者の利用者トークン");
    }

    // 🔴 X-18（否定②）: 利用者に権限の無い文書は返らない。受け口が自分で引いたスコープが許さない部署の文書は、
    // query に当たっていても 1 件も返らず、件数にも含まれない。許可が無ければ（granted=false）何も返らない。
    [Fact]
    public async Task 利用者に権限の無い文書は返らず件数にも含まれない()
    {
        var seeded = await SeedAsync();

        var result = await Grpc().ExecuteAsync(As(Unique("alice"), seeded.Term), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        result.Documents.Should().Contain(d => d.DocumentId == seeded.Allowed.ToString(), "対照: 権限内は返る");
        result.Documents.Should().NotContain(d => d.DocumentId == seeded.Forbidden.ToString());
        result.TotalCount.Should().Be(result.Documents.Count);

        _factory.Authoritative = new AccessScopeResponse("resolved", [], Granted: false);
        var denied = await Grpc().ExecuteAsync(As(Unique("bob"), seeded.Term), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);
        denied.Documents.Should().BeEmpty();
        denied.TotalCount.Should().Be(0);
    }

    // 🔴 X-19（否定③）: 本文に scope を入れても効かない。旧い呼び出し元の形（番号 3 の `McpToolInvocationScope`。
    // 権限外の部署を開ける属性つき・除外制約なし）を**生のバイト列で実際にワイヤへ載せても**、結果は受け口が自分で引いた
    // スコープのとおりで、利用者の判定も本文の利用者で問う。
    [Fact]
    public async Task 旧い番号3のscopeをワイヤへ載せても効かない()
    {
        var seeded = await SeedAsync();
        var user = Unique("alice");
        var body = As(user, seeded.Term).ToByteArray().Concat(LegacyScopeField(seeded.ForbiddenDept)).ToArray();

        // 前提（対照）: 載せたバイト列は番号 3 を実際に持つ（読み飛ばされる未知のフィールドとして残る）。
        Pb.ExecuteMcpToolRequest.Parser.ParseFrom(body).ToByteArray().Length.Should().Be(body.Length,
            "番号 3 が未知のフィールドとしてワイヤに乗っている（空振りの試験ではない）");

        var response = await RawExecuteAsync(body, ServiceAccountToken(Trusted));

        response.Documents.Should().Contain(d => d.DocumentId == seeded.Allowed.ToString(), "対照: 実行は通る");
        response.Documents.Should().NotContain(d => d.DocumentId == seeded.Forbidden.ToString(),
            "本文の scope が開けようとした部署は開かない");
        _factory.ResolvedFor.Should().ContainSingle(r => r.UserId == user, "受け口は本文の利用者で自分で判定を問う");
    }

    // 🔴 X-19（否定③）: 引数に `scope` / `filters` / `attributes` を書いても効かない（申告の `input_schema` の鍵だけを読む）。
    [Fact]
    public async Task 引数にscopeを書いても効かない()
    {
        var seeded = await SeedAsync();
        var args = $$$"""
            {"query":"{{{seeded.Term}}}","limit":50,
             "scope":{"filters":[{"key":"dept","allowed_values":["{{{seeded.ForbiddenDept}}}"]}],"grants_access":true},
             "filters":{"dept":"{{{seeded.ForbiddenDept}}}"},"attributes":{"role":"admin"}}
            """;

        var result = await Grpc().ExecuteAsync(As(Unique("alice"), seeded.Term, args: args), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        result.Documents.Should().Contain(d => d.DocumentId == seeded.Allowed.ToString(), "対照: 実行は通る");
        result.Documents.Should().NotContain(d => d.DocumentId == seeded.Forbidden.ToString());
    }

    // 🔴 X-20（ADR-0034 決定 9 の要求側の 1 層目）: サービスアカウント実行（`service-account-` の利用者名）は、
    // スコープが開けていても個人資料を 1 件も返さず、件数にも含めない。同じ点・同じスコープで有人には返る（X-16 が対照）。
    [Fact]
    public async Task サービスアカウント実行は個人資料を返さない()
    {
        var seeded = await SeedAsync();

        var result = await Grpc().ExecuteAsync(
            As("service-account-batch-agent", seeded.Term), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        result.Documents.Select(d => d.DocumentId).Should().Equal(seeded.Allowed.ToString());
        result.TotalCount.Should().Be(1);
    }

    // X-21: 本文の `action` は受け口が自分のツールから決めた操作（read）と突き合わせるだけ。違えば INVALID_ARGUMENT で、判定を問わない。
    [Theory]
    [InlineData("write")]
    [InlineData("")]
    [InlineData("READ")]
    public async Task 操作がツールの要する操作と違えばINVALID_ARGUMENT(string action)
    {
        var seeded = await SeedAsync();

        await ShouldFailAsync(As(Unique("alice"), seeded.Term, action), ServiceAccountToken(Trusted), StatusCode.InvalidArgument, action);
    }

    // X-22: 利用者文脈が無い・空は INVALID_ARGUMENT（機械の主体へ読み替えない）。
    [Fact]
    public async Task 利用者文脈が無い実行はINVALID_ARGUMENT()
    {
        var seeded = await SeedAsync();
        var noUser = new Pb.ExecuteMcpToolRequest { Tool = Tool, ArgumentsJson = $$"""{"query":"{{seeded.Term}}"}""" };

        var act = async () => await Grpc().ExecuteAsync(noUser, Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        await ShouldFailAsync(As("  ", seeded.Term), ServiceAccountToken(Trusted), StatusCode.InvalidArgument, "空白の user_id");
    }

    // X-23: 宛先は申告したサービス＋ツール名。自分の申告に無い名前（他のサービスのツール・公開名・未知）は NOT_FOUND。
    [Theory]
    [InlineData("graph.traverse")]
    [InlineData("document.get_document")]
    [InlineData("search_documents")]
    [InlineData("retrieval.search_documents ")]
    [InlineData("")]
    public async Task 自分の申告に無いツールはNOT_FOUND(string tool)
    {
        var seeded = await SeedAsync();
        var request = As(Unique("alice"), seeded.Term);
        request.Tool = tool;

        await ShouldFailAsync(request, ServiceAccountToken(Trusted), StatusCode.NotFound, tool);
    }

    // X-24: 引数は申告の `input_schema` のとおりに検証し、丸めない（INVALID_ARGUMENT。判定を問わない）。
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":7}""")]
    [InlineData("""{"query":"x","limit":0}""")]
    [InlineData("""{"query":"x","limit":51}""")]
    [InlineData("""{"query":"x","limit":2.5}""")]
    [InlineData("""{"query":"x","limit":"10"}""")]
    [InlineData("""["x"]""")]
    [InlineData("""not json""")]
    public async Task 引数が申告の形でなければINVALID_ARGUMENT(string args)
    {
        await SeedAsync();

        await ShouldFailAsync(As(Unique("alice"), "x", args: args), ServiceAccountToken(Trusted), StatusCode.InvalidArgument, args);
    }

    // X-24（対照）: 上限・下限ちょうどと既定（省略）は通る。
    [Theory]
    [InlineData("""{"query":"TERM","limit":1}""", 1)]
    [InlineData("""{"query":"TERM","limit":50}""", 2)]
    [InlineData("""{"query":"TERM"}""", 2)]
    public async Task 引数の境界と既定は通る(string args, int expected)
    {
        var seeded = await SeedAsync();

        var result = await Grpc().ExecuteAsync(
            As(Unique("alice"), seeded.Term, args: args.Replace("TERM", seeded.Term)), Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        result.Documents.Should().HaveCount(expected);
    }

    // 構造の門: 面は ServiceCaller を宣言している（外れると上の門の試験が落ちるが、どの層で外れたかを名指しする）。
    [Fact]
    public void 実行口はServiceCallerを要求する()
    {
        typeof(McpToolExecutionGrpcService).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().Should().ContainSingle()
            .Which.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // X-25: 受け口が実行するツールと引数の上限・既定は、申告（`McpToolDeclarationSource`）と同じである（2 か所に持つ値の一致）。
    [Fact]
    public void 実行するツールと引数の上限既定は申告と一致する()
    {
        var declared = McpToolDeclarationSource.Declare().Tools.Should().ContainSingle().Which;

        declared.Name.Should().Be(McpToolExecutionGrpcService.SearchDocumentsTool);
        declared.InputSchema.Should().Contain($"\"minimum\":{McpToolExecutionGrpcService.MinLimit}")
            .And.Contain($"\"maximum\":{McpToolExecutionGrpcService.MaxLimit}")
            .And.Contain($"\"default\":{McpToolExecutionGrpcService.DefaultLimit}");
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
        io.WriteString("retrieval:search");
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
