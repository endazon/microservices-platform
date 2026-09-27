using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.McpTools.Declare;
using DocumentService.Features.McpTools.Execute;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace DocumentService.Tests.Features.McpTools.Execute;

// FR-16, FR-06, FR-19, UC-08, UC-03, NFR-09, ADR-0034 決定 2・4・9, 計画 ADR-0086 決定 1, ADR-0088 決定 1, ADR-0117 決定 1〜3,
// ADR-0121 決定 2・4・5, [[IADR-0483]], [[IADR-0479]]（2026-09-28 追記 / #1611 段 2）:
// **DocumentService の MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）を、**本番の Program.cs のまま・実 Kestrel の h2c ポート**
// （`GrpcKestrelFactory`。127.0.0.1）と本物の JwtBearer で往復して固定する（テスト仕様書 FR-16 の X-56〜X-68）。
//
// 🔴 否定の試験と陽性対照を**同じ器・同じ文書**で対にする ——「拒否された」「返らなかった」だけでは、器が壊れているのか
//   判定が効いているのか区別できない。器の認可サービスの代役（`ReadScopes`）は利用者ごとの問い合わせを数える（判定の位置の観測点）。
// 🔴 **門（`ContentAbacGate`）**: 実行口は門が開いているときだけ経路を開く（［2026-09-28 裁定 / #1611 段 2 案 2］）。
//   このクラスの試験は**門を開いた状態で始め**（コンストラクタ）、閉じた状態の試験は `WithGateClosedAsync` の中で行う。
//   後始末（`Dispose`）で門を閉じる（器は同じコレクションの他の試験と共有され、それらは閉じた門を前提にする）。
//   門が開いている間は MCP 経路の結果が REST（同じ器の HTTP/1.1 側。利用者のトークン）の同じ利用者の結果と一致し（X-59）、
//   閉じている間は MCP 経路は 1 件も返さない（REST より狭い＝安全側。X-68）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public sealed class GrpcMcpToolExecutionTests : IDisposable
{
    private const string Trusted = "mcp-server";
    private const string Agent = "service-account-batch-agent";
    private readonly GrpcKestrelFactory _factory;

    public GrpcMcpToolExecutionTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        _factory.ContentAbacGate.Open();
    }

    public void Dispose() => _factory.ContentAbacGate.Close();

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

    private static Pb.ExecuteMcpToolRequest Get(string userId, Guid id, string extra = "") =>
        As(userId, McpToolExecutionGrpcService.GetDocumentTool, $$"""{"document_id":"{{id}}"{{extra}}}""");

    private static Pb.ExecuteMcpToolRequest List(string userId, int limit = McpToolExecutionGrpcService.MaxLimit, string extra = "") =>
        As(userId, McpToolExecutionGrpcService.ListDocumentsTool, $$"""{"limit":{{limit}}{{extra}}}""");

    private async Task<Pb.McpToolResult> ExecuteAsync(Pb.ExecuteMcpToolRequest request, string? token = null) =>
        await Grpc().ExecuteAsync(request, Bearer(token ?? ServiceAccountToken(Trusted)), cancellationToken: Ct);

    private static IEnumerable<string> Ids(Pb.McpToolResult r) => r.Documents.Select(d => d.DocumentId);

    private sealed record Seeded(
        string User, Guid Internal, Guid Restricted, Guid OwnRestricted, Guid OwnNote, Guid OtherNote, Guid AgentNote)
    {
        public IReadOnlyList<Guid> All => [Internal, Restricted, OwnRestricted, OwnNote, OtherNote, AgentNote];
        public static string Id(Guid g) => g.ToString();
    }

    // 試験ごとに一意の利用者と文書（器の DB は器の寿命で共有される）。
    //   Internal      組織・internal（所有者は他人。部署・プロジェクト・共有先の属性つき）
    //   Restricted    組織・restricted（所有者は他人）—— 門が閉じている間は誰にでも返り、開くと属性の合わない利用者に返らない
    //   OwnRestricted 組織・restricted（所有者は本人）—— 開いた後も所有者の分岐で読める
    //   OwnNote       個人資料（所有者は本人）／OtherNote 個人資料（所有者は他人・共有なし）
    //   AgentNote     個人資料（所有者はサービスアカウント。本人にも共有）
    private async Task<Seeded> SeedAsync()
    {
        var user = Unique("alice");
        var carol = Unique("carol");

        Dictionary<string, string> Org(string confidentiality, string owner) => new()
        {
            ["confidentiality"] = confidentiality,
            [DocumentScopes.Key] = DocumentScopes.Organization,
            ["owner"] = owner,
            ["dept"] = "sales",
            [RestrictedProject.DocumentKey] = "alpha",
            [AttributeValueKeys.SharedWith] = "bob-secret",
        };
        Dictionary<string, string> Note(string owner) => new()
        {
            ["confidentiality"] = "restricted",
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = owner,
        };

        var internalDoc = Document.Create("社内の文書", null, null, Org("internal", carol));
        var restricted = Document.Create("機密の文書", null, null, Org("restricted", carol));
        var ownRestricted = Document.Create("自分の機密の文書", null, null, Org("restricted", user));
        var ownNote = Document.Create("自分の個人資料", null, null, Note(user));
        var otherNote = Document.Create("他人の個人資料", null, null, Note(carol));
        var agentNote = Document.Create("エージェントの個人資料", null, null, Note(Agent));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.Documents.AddRange(internalDoc, restricted, ownRestricted, ownNote, otherNote, agentNote);
        db.DocumentShares.Add(DocumentShare.Create(agentNote.Id, ShareSubjectType.User, user, Agent));
        db.DocumentShares.Add(DocumentShare.Create(agentNote.Id, ShareSubjectType.User, Agent, Agent));
        await db.SaveChangesAsync(Ct);

        // 門が開いた後の分岐: dev seed の bob（clearance internal）と同じ形を、この試験の利用者の名前で与える。
        GrantSeedLike(user, "bob");
        GrantSeedLike(Agent, "service-account-ai-stock-trading-kb-writer");
        return new Seeded(user, internalDoc.Id, restricted.Id, ownRestricted.Id, ownNote.Id, otherNote.Id, agentNote.Id);
    }

    private void GrantSeedLike(string user, string seedSubject)
    {
        var branches = OwnerReadSeedScopes.BranchesOf(seedSubject)
            .Select(b => (IReadOnlyList<AttributeFilter>)b
                .Select(f => new AttributeFilter(f.Key, [.. f.AllowedValues.Select(v => v == seedSubject ? user : v)]))
                .ToList())
            .ToList();
        _factory.ReadScopes.Grant(user, branches);
    }

    private async Task WithGateClosedAsync(Func<Task> body)
    {
        _factory.ContentAbacGate.Close();
        try { await body(); }
        finally { _factory.ContentAbacGate.Open(); }
    }

    // REST（同じ器の HTTP/1.1 側）を利用者のトークンで読む。
    private HttpClient Rest(string user)
    {
        var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", GrpcKestrelFactory.IssueToken(user, ["platform-user"]));
        return http;
    }

    private async Task<(HashSet<string> Visible, int ListCount)> RestViewAsync(string user, IEnumerable<Guid> ids)
    {
        using var http = Rest(user);
        var visible = new HashSet<string>();
        foreach (var id in ids)
        {
            var resp = await http.GetAsync($"/documents/{id}", Ct);
            resp.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NotFound], "REST の読み取りが成り立っている");
            if (resp.StatusCode == HttpStatusCode.OK) visible.Add(id.ToString());
        }
        var list = (await http.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;
        list.Select(d => d.Id.ToString()).Where(visible.Contains).Should().BeEquivalentTo(visible,
            "REST の個別と一覧は同じ判定（器の対照）");
        return (visible, list.Count);
    }

    private async Task<(HashSet<string> Visible, Pb.McpToolResult List)> McpViewAsync(string user, IEnumerable<Guid> ids)
    {
        var visible = new HashSet<string>();
        foreach (var id in ids)
        {
            var r = await ExecuteAsync(Get(user, id));
            r.TotalCount.Should().Be(r.Documents.Count);
            r.Documents.Should().HaveCountLessThanOrEqualTo(1);
            visible.UnionWith(Ids(r));
        }
        return (visible, await ExecuteAsync(List(user)));
    }

    private async Task ShouldFailAsync(Pb.ExecuteMcpToolRequest request, string token, StatusCode expected, string because)
    {
        var calls = _factory.ReadScopes.CallsFor(request.User?.UserId ?? "");
        var act = async () => await Grpc().ExecuteAsync(request, Bearer(token), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected, because);
        _factory.ReadScopes.CallsFor(request.User?.UserId ?? "").Should().Be(calls,
            "拒否した呼び出しで認可サービスへ問い合わせない: " + because);
    }

    // X-56（陽性対照）: MCP サーバーの s2s（利用者名の形・`azp` だけの形）で、2 つのツールを本文の利用者として実行できる。
    // 応答は共通エンベロープ（題名と許可リストの属性。本文・参照リンクは無い）。判定点は**本文の利用者で**認可サービスへ要求につき 1 度問い、
    // 門は要求につき 1 度だけ読まれる（受け口の経路の開閉と判定点が同じ要求内固定の値を使う）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MCPサーバーのs2sなら利用者として2つのツールを実行でき判定点が利用者で問う(bool azpOnly)
    {
        var s = await SeedAsync();
        var token = azpOnly ? AzpOnlyToken(Trusted) : ServiceAccountToken(Trusted);

        var calls = _factory.ReadScopes.CallsFor(s.User);
        var reads = _factory.ContentAbacGate.Reads;
        var get = await ExecuteAsync(Get(s.User, s.Internal), token);
        _factory.ReadScopes.CallsFor(s.User).Should().Be(calls + 1, "判定点が本文の利用者名で認可サービスへ 1 度問う（属性は送らない口）");
        _factory.ContentAbacGate.Reads.Should().Be(reads + 1, "門は要求につき 1 度だけ読む（受け口と判定点で値が食い違わない）");
        var list = await ExecuteAsync(List(s.User), token);

        var doc = get.Documents.Should().ContainSingle().Which;
        doc.DocumentId.Should().Be(Seeded.Id(s.Internal));
        doc.Title.Should().Be("社内の文書");
        doc.HasBody.Should().BeFalse("台帳は本文を持たない");
        doc.HasReferenceUrl.Should().BeFalse("内部の格納先を利用者へ見せるリンクとして返さない");
        get.TotalCount.Should().Be(1);
        Ids(list).Should().Contain([Seeded.Id(s.Internal), Seeded.Id(s.OwnNote)], "有人の所有者には自分の個人資料も返る（対照）");
    }

    // 🔴 X-57（否定）: MCP サーバー以外の `platform-service` の主体（`DocumentRead:` の中継者 bff を含む）は、利用者文脈を運んでも
    // PERMISSION_DENIED。同じ文書が MCP サーバーには返ることを先に確かめる（対照）。認可の問い合わせも走らない。
    // 門が閉じていても PERMISSION_DENIED（経路の開閉より前で落ちる。門の状態を信頼しない呼び出し元へ見せない）。
    [Theory]
    [InlineData("bff")]
    [InlineData("graph-service")]
    [InlineData("retrieval-service")]
    [InlineData("aianalysis-service")]
    [InlineData("ai-stock-trading-llm-caller")]
    [InlineData("document-service")]
    public async Task MCPサーバー以外の主体からの実行はPERMISSION_DENIED(string clientId)
    {
        var s = await SeedAsync();
        Ids(await ExecuteAsync(Get(s.User, s.Internal))).Should().Contain(Seeded.Id(s.Internal), "対照: MCP サーバーには返る");

        await ShouldFailAsync(Get(s.User, s.Internal), ServiceAccountToken(clientId), StatusCode.PermissionDenied, clientId);
        await ShouldFailAsync(List(s.User), AzpOnlyToken(clientId), StatusCode.PermissionDenied, clientId + "（azp だけの形）");
        await WithGateClosedAsync(() =>
            ShouldFailAsync(Get(s.User, s.Internal), ServiceAccountToken(clientId), StatusCode.PermissionDenied, clientId + "（門が閉じている）"));
    }

    // 🔴 X-57（否定）: クライアント識別の接頭辞・大小文字の変種、`azp` の食い違い、人のトークンは信じない。
    // 資格情報なしは UNAUTHENTICATED、管理者の利用者トークンは PERMISSION_DENIED。
    [Theory]
    [InlineData("mcp-server-x")]
    [InlineData("mcp-serve")]
    [InlineData("xmcp-server")]
    [InlineData("MCP-SERVER")]
    public async Task MCPサーバーの変種や人のトークンは信じない(string variant)
    {
        var s = await SeedAsync();
        var request = () => Get(s.User, s.Internal);

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

    // 🔴 X-58（否定。門が開いた後）: 属性の合わない利用者には機密の組織文書が返らず（個別は空・一覧に現れず件数にも入らない）、
    // 他人の個人資料も返らない。所有者の分岐で自分の機密の文書は読める（陽性対照）。許可が無い（認可サービス不達の縮退も同じ値）なら
    // 1 件も返らない。見えない・無いは同じ空の応答でバイト列まで区別できない。
    [Fact]
    public async Task 門が開いた後は権限の無い文書は返らず見えない無い許可なしは区別できない()
    {
        var s = await SeedAsync();

        Ids(await ExecuteAsync(Get(s.User, s.OwnRestricted))).Should().Equal([Seeded.Id(s.OwnRestricted)], "対照: 所有者の分岐");
        var hidden = await ExecuteAsync(Get(s.User, s.Restricted));
        var otherNote = await ExecuteAsync(Get(s.User, s.OtherNote));
        var missing = await ExecuteAsync(Get(s.User, Guid.NewGuid()));
        var list = await ExecuteAsync(List(s.User));

        Ids(list).Should().Contain(Seeded.Id(s.OwnRestricted)).And.NotContain(Seeded.Id(s.Restricted))
            .And.NotContain(Seeded.Id(s.OtherNote));

        var nobody = Unique("nobody"); // 分岐を持たない利用者（認可サービスの答えが「読めるものは無い」）
        var denied = await ExecuteAsync(Get(nobody, s.Internal));
        var deniedList = await ExecuteAsync(List(nobody));
        deniedList.Documents.Should().BeEmpty();
        deniedList.TotalCount.Should().Be(0);

        foreach (var r in new[] { hidden, otherNote, missing, denied })
        {
            r.Documents.Should().BeEmpty();
            r.TotalCount.Should().Be(0);
            r.Truncated.Should().BeFalse();
            r.ToByteArray().Should().Equal(missing.ToByteArray(), "応答のバイト列で区別できない");
        }
    }

    // 🔴 X-59（［2026-09-28 改訂 / #1611 段 2 案 2］）: 門が開いている間、MCP 経路の個別・一覧の結果は REST の同じ利用者の結果と一致する
    // （超えない・欠けない。一覧の件数も）。属性の合わない利用者に他人の機密の組織文書は返らない。
    // 門が閉じている間は MCP 経路は結果を返さない（REST より狭い＝安全側）—— X-68 が固定する。
    [Fact]
    public async Task MCP経路の結果は門が開いている間RESTの同じ利用者の結果と一致する()
    {
        var s = await SeedAsync();

        var (rest, restCount) = await RestViewAsync(s.User, s.All);
        var (mcp, list) = await McpViewAsync(s.User, s.All);

        mcp.Should().BeEquivalentTo(rest, "個別: MCP 経路は REST の同じ利用者を超えず、欠けもしない");
        Ids(list).Where(id => s.All.Select(Seeded.Id).Contains(id)).Should().BeEquivalentTo(rest, "一覧も同じ");
        list.TotalCount.Should().Be(restCount, "一覧の件数（判定の後の全体）も REST と同じ");

        mcp.Should().NotContain(Seeded.Id(s.Restricted), "属性の合わない利用者に機密の組織文書は返らない");
        mcp.Should().Contain([Seeded.Id(s.Internal), Seeded.Id(s.OwnRestricted), Seeded.Id(s.OwnNote)], "陽性対照")
            .And.NotContain(Seeded.Id(s.OtherNote), "他人の個人資料は返らない");
    }

    // 🔴 X-68（否定。［2026-09-28 裁定 / #1611 段 2 案 2］）: 門が閉じている間は、文書の 2 ツールとも FAILED_PRECONDITION で結果を 1 件も返さない。
    // 閉じている間の REST（#1615 の閉じた枝）は機密・制限の組織文書を返すが（器の対照: シードに見えるものが在る）、MCP 経路は
    // 題名も属性も 1 件も返さない。判定点を走らせない（認可サービスへ問わない）。有人・サービスアカウントとも同じ。
    // 形の誤り（引数）は門の状態に依らず INVALID_ARGUMENT（経路の開閉は引数の検証の後）。門が開けば同じ呼び出しが通る（陽性対照）。
    [Fact]
    public async Task 門が閉じている間は文書のツールは結果を返さずFAILED_PRECONDITION()
    {
        var s = await SeedAsync();

        await WithGateClosedAsync(async () =>
        {
            var (rest, _) = await RestViewAsync(s.User, s.All);
            rest.Should().Contain([Seeded.Id(s.Restricted), Seeded.Id(s.Internal)], "器の対照: 閉じた枝の REST には他人の機密の組織文書が見える");

            foreach (var user in new[] { s.User, Agent })
            {
                foreach (var id in s.All)
                    await ShouldFailClosedAsync(Get(user, id), $"{user} get {id}");
                await ShouldFailClosedAsync(List(user), $"{user} list");
                await ShouldFailClosedAsync(As(user, McpToolExecutionGrpcService.ListDocumentsTool, "{}"), $"{user} list（既定件数）");
            }

            await ShouldFailAsync(List(s.User, 0), ServiceAccountToken(Trusted), StatusCode.InvalidArgument, "形の誤りは門に依らない");
        });

        Ids(await ExecuteAsync(Get(s.User, s.Internal))).Should().Equal([Seeded.Id(s.Internal)], "陽性対照: 門が開けば同じ呼び出しが通る");
    }

    private async Task ShouldFailClosedAsync(Pb.ExecuteMcpToolRequest request, string because)
    {
        var calls = _factory.ReadScopes.CallsFor(request.User.UserId);
        Pb.McpToolResult? result = null;
        var act = async () => result = await Grpc().ExecuteAsync(request, Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition, because);
        ex.Status.Detail.Should().Be(McpToolExecutionGrpcService.GateClosedMessage);
        result.Should().BeNull("結果（題名・属性）を 1 件も返さない: " + because);
        _factory.ReadScopes.CallsFor(request.User.UserId).Should().Be(calls, "判定点を走らせない: " + because);
    }

    // 🔴 X-60（否定）: 本文に scope を入れても効かない。旧い呼び出し元の形（番号 3。機密区分を開ける属性つき）を
    // **生のバイト列で実際にワイヤへ載せても**、引数に `scope` / `filters` / `attributes` を書いても、判定点が自分で引いた分岐のとおり。
    [Fact]
    public async Task 旧い番号3のscopeや引数のscopeは効かない()
    {
        var s = await SeedAsync();

        var body = Get(s.User, s.Restricted).ToByteArray().Concat(LegacyScopeField()).ToArray();
        Pb.ExecuteMcpToolRequest.Parser.ParseFrom(body).ToByteArray().Length.Should().Be(body.Length,
            "番号 3 が未知のフィールドとしてワイヤに乗っている（空振りの試験ではない）");

        var legacy = await RawExecuteAsync(body, ServiceAccountToken(Trusted));
        legacy.Documents.Should().BeEmpty("本文の scope が開けようとした機密区分は開かない");
        var control = await RawExecuteAsync(
            Get(s.User, s.Internal).ToByteArray().Concat(LegacyScopeField()).ToArray(), ServiceAccountToken(Trusted));
        Ids(control).Should().Equal([Seeded.Id(s.Internal)], "対照: 同じ形で実行は通る");

        const string extra = ""","scope":{"filters":[{"key":"confidentiality","allowed_values":["restricted"]}],"grants_access":true},"filters":{"confidentiality":"restricted"},"attributes":{"clearance":"restricted"}""";
        (await ExecuteAsync(Get(s.User, s.Restricted, extra))).Documents.Should().BeEmpty();
        Ids(await ExecuteAsync(List(s.User, extra: extra))).Should().Contain(Seeded.Id(s.Internal))
            .And.NotContain(Seeded.Id(s.Restricted));
    }

    // 🔴 X-61（ADR-0034 決定 9 の要求側の 1 層目）: サービスアカウント実行（`service-account-` の利用者名）は、
    // その名前が所有者・共有先でも個人資料を返さず、件数にも入れない。組織文書は返る（対照）。有人の共有先には同じ個人資料が返る（対照）。
    // 門が閉じている間はそもそも返らない（X-68）。
    [Fact]
    public async Task サービスアカウント実行は個人資料を返さない()
    {
        var s = await SeedAsync();
        GrantSeedLike(Agent, "bob"); // 組織文書の陽性対照のため internal と自分の所有・共有を読める分岐を与える（個人資料は機械なので開かない）

        (await ExecuteAsync(Get(Agent, s.AgentNote))).Documents.Should().BeEmpty("自分が所有・共有先でも個人資料は返らない");
        var list = await ExecuteAsync(List(Agent));
        Ids(list).Should().NotContain([Seeded.Id(s.AgentNote), Seeded.Id(s.OwnNote), Seeded.Id(s.OtherNote)]);
        list.Documents.Should().NotContain(d => d.Attributes.GetValueOrDefault(DocumentScopes.Key) == DocumentScopes.PrivateNote);
        Ids(list).Should().Contain(Seeded.Id(s.Internal), "対照: 組織文書は返る");
        Ids(await ExecuteAsync(Get(Agent, s.Internal))).Should().Equal([Seeded.Id(s.Internal)], "対照: 組織文書は返る");
        Ids(await ExecuteAsync(Get(s.User, s.AgentNote))).Should().Equal([Seeded.Id(s.AgentNote)], "対照: 共有先の有人には返る");
    }

    // 🔴 X-62（否定と陽性対照。#1671）: エンベロープの属性は許可リストのキーだけ。台帳の属性の所有者・部署・共有先は、有人にも
    // サービスアカウントにも、個別にも一覧にも載らない。同じ文書・同じ応答に機密区分・文書スコープ・プロジェクトは載る。
    [Theory]
    [InlineData("alice-probe")]
    [InlineData(Agent)]
    public async Task 所有者部署共有先はエンベロープの属性に載らない(string userId)
    {
        var s = await SeedAsync();
        GrantSeedLike(userId, "bob"); // internal を読める分岐（dev seed の bob と同じ形）

        foreach (var r in new[] { await ExecuteAsync(Get(userId, s.Internal)), await ExecuteAsync(List(userId)) })
        {
            var doc = r.Documents.Should().ContainSingle(d => d.DocumentId == Seeded.Id(s.Internal), "対照: 文書そのものは見える").Which;
            doc.Attributes.Should().Contain("confidentiality", "internal")
                .And.Contain(DocumentScopes.Key, DocumentScopes.Organization)
                .And.Contain(RestrictedProject.DocumentKey, "alpha");
            doc.Attributes.Should().NotContainKey("owner").And.NotContainKey("dept").And.NotContainKey(AttributeValueKeys.SharedWith);
            r.Documents.Should().OnlyContain(d => d.Attributes.Keys.All(McpEnvelopeAttributes.IsCarried));
        }
    }

    // X-63: 一覧は更新の新しい順の先頭 `limit` 件。件数は判定の後の全体件数で、`limit` を超えたら打ち切りの印を立てる。
    [Fact]
    public async Task 一覧はlimitで打ち切り件数は全体を返す()
    {
        var s = await SeedAsync();

        var one = await ExecuteAsync(List(s.User, 1));
        var all = await ExecuteAsync(List(s.User));

        one.Documents.Should().ContainSingle();
        one.Truncated.Should().BeTrue();
        one.TotalCount.Should().Be(all.TotalCount).And.BeGreaterThan(1);
        Ids(one).Should().Equal([Ids(all).First()], "先頭（更新の新しい順）");
        var byDefault = await ExecuteAsync(As(s.User, McpToolExecutionGrpcService.ListDocumentsTool, "{}"));
        byDefault.Documents.Should().HaveCount(Math.Min(McpToolExecutionGrpcService.DefaultLimit, all.TotalCount), "省略は既定の件数");
        byDefault.Truncated.Should().Be(all.TotalCount > McpToolExecutionGrpcService.DefaultLimit);
    }

    // X-64: 本文の `action` は受け口が決めた操作（read）と突き合わせるだけ。違えば INVALID_ARGUMENT で、判定を問わない。
    [Theory]
    [InlineData("write")]
    [InlineData("")]
    [InlineData("READ")]
    public async Task 操作がツールの要する操作と違えばINVALID_ARGUMENT(string action)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(As(s.User, McpToolExecutionGrpcService.GetDocumentTool, $$"""{"document_id":"{{s.Internal}}"}""", action),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, action);
    }

    // X-64: 利用者文脈が無い・空は INVALID_ARGUMENT（機械の主体へ読み替えない）。自分の申告に無い名前（他のサービスのツール・
    // 申告から落とした個人資料の一覧・公開名・空白や大小文字の変種・空）は NOT_FOUND。
    [Fact]
    public async Task 利用者文脈が無い実行はINVALID_ARGUMENT()
    {
        var s = await SeedAsync();
        var noUser = new Pb.ExecuteMcpToolRequest
        {
            Tool = McpToolExecutionGrpcService.GetDocumentTool,
            ArgumentsJson = $$"""{"document_id":"{{s.Internal}}"}""",
        };

        var act = async () => await Grpc().ExecuteAsync(noUser, Bearer(ServiceAccountToken(Trusted)), cancellationToken: Ct);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        await ShouldFailAsync(As("  ", McpToolExecutionGrpcService.GetDocumentTool, $$"""{"document_id":"{{s.Internal}}"}"""),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, "空白の user_id");
    }

    [Theory]
    [InlineData("retrieval.search_documents")]
    [InlineData("graph.traverse")]
    [InlineData("document.list_private_notes")]
    [InlineData("get_document")]
    [InlineData("document.get_document ")]
    [InlineData("DOCUMENT.GET_DOCUMENT")]
    [InlineData("")]
    public async Task 自分の申告に無いツールはNOT_FOUND(string tool)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(As(s.User, tool, $$"""{"document_id":"{{s.Internal}}"}"""),
            ServiceAccountToken(Trusted), StatusCode.NotFound, tool);
    }

    // X-65: 引数は申告の `input_schema` のとおりに検証し、丸めない（INVALID_ARGUMENT。判定を問わない）。
    [Theory]
    [InlineData("document.get_document", """{}""")]
    [InlineData("document.get_document", """{"document_id":""}""")]
    [InlineData("document.get_document", """{"document_id":"not-a-guid"}""")]
    [InlineData("document.get_document", """{"document_id":7}""")]
    [InlineData("document.get_document", """["DOC"]""")]
    [InlineData("document.list_documents", """{"limit":0}""")]
    [InlineData("document.list_documents", """{"limit":101}""")]
    [InlineData("document.list_documents", """{"limit":2.5}""")]
    [InlineData("document.list_documents", """{"limit":"5"}""")]
    [InlineData("document.list_documents", """not json""")]
    public async Task 引数が申告の形でなければINVALID_ARGUMENT(string tool, string args)
    {
        var s = await SeedAsync();

        await ShouldFailAsync(As(s.User, tool, args.Replace("DOC", s.Internal.ToString())),
            ServiceAccountToken(Trusted), StatusCode.InvalidArgument, tool + " " + args);
    }

    // X-65（対照）: 件数の境界ちょうどは通る。
    [Theory]
    [InlineData(McpToolExecutionGrpcService.MinLimit)]
    [InlineData(McpToolExecutionGrpcService.MaxLimit)]
    public async Task 件数の境界は通る(int limit)
    {
        var s = await SeedAsync();

        var result = await ExecuteAsync(List(s.User, limit));

        result.Documents.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(limit);
    }

    // 構造の門: 面は ServiceCaller を宣言している（外れると上の門の試験が落ちるが、どの層で外れたかを名指しする）。
    [Fact]
    public void 実行口はServiceCallerを要求する()
    {
        typeof(McpToolExecutionGrpcService).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().Should().ContainSingle()
            .Which.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // X-66: 受け口が実行するツールと件数の上限・下限・既定は、申告（`McpToolDeclarationSource`）と同じである（2 か所に持つ値の一致）。
    [Fact]
    public void 実行するツールと件数の上限既定は申告と一致する()
    {
        var declared = McpToolDeclarationSource.Declare().Tools;

        declared.Select(t => t.Name).Should().BeEquivalentTo(
            [McpToolExecutionGrpcService.GetDocumentTool, McpToolExecutionGrpcService.ListDocumentsTool]);
        declared.Single(t => t.Name == McpToolExecutionGrpcService.GetDocumentTool).InputSchema
            .Should().Contain("\"required\":[\"document_id\"]");
        declared.Single(t => t.Name == McpToolExecutionGrpcService.ListDocumentsTool).InputSchema
            .Should().Contain($"\"minimum\":{McpToolExecutionGrpcService.MinLimit},\"maximum\":{McpToolExecutionGrpcService.MaxLimit},\"default\":{McpToolExecutionGrpcService.DefaultLimit}");
        declared.Should().OnlyContain(t => !t.Description.Contains("本文"), "応答は本文も参照リンクも持たない（返らないものを約束しない）");
    }

    // 旧い `ExecuteMcpToolRequest.scope`（番号 3）の形を手で組む: subject_id=1・subject_attributes=3（map）・
    // exclude_private_note=4・required_scope=5。機密区分を開ける属性を載せる。
    private static byte[] LegacyScopeField()
    {
        using var inner = new MemoryStream();
        var io = new CodedOutputStream(inner);
        io.WriteTag(1, WireFormat.WireType.LengthDelimited);
        io.WriteString("admin");
        foreach (var (key, value) in new[] { ("clearance", "restricted"), ("confidentiality", "restricted") })
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
        io.WriteString("document:read");
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
