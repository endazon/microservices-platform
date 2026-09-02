using System.Security.Claims;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Infrastructure.Persistence;
using McpServer.Features.McpClients;
using McpServer.Features.Tools;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Features.Tools;

// FR-16: ツール呼び出しの単一経路の統制（登録確認 → 公開確認 → 個人資料の除外 → 越境 → 監査）。
// 計画 ADR-0024 / ADR-0034 決定 9、UC-08（外部 AI エージェント連携）の基本・代替・例外フロー。
public class ToolInvocationServiceTests
{
    // 下流サービスの代わり。**要求側の制約を意図的に無視して**個人資料を返し、
    // 応答側フィルタ（2 層目）が効いていることを確かめられるようにしてある。
    private sealed class FakeInvoker : IToolInvoker
    {
        public ToolInvocationScope? LastScope { get; private set; }
        public required McpToolResult Result { get; init; }

        public Task<McpToolResult> InvokeAsync(
            PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct)
        {
            LastScope = scope;
            return Task.FromResult(Result);
        }
    }

    private const string PrivateId = "doc-private";
    private const string OrgId = "doc-org";

    // 3 経路すべてを同じ表で回す（計画: 探索系に限らず検索系・文書取得系にも同様に適用する）。
    public static TheoryData<string> AllRoutes => new()
    {
        "retrieval.search_documents",
        "document.get_document",
        "graph.traverse"
    };

    private static McpToolResult MixedResult() => new(
    [
        new McpToolDocument(PrivateId, "個人メモ", new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note", ["owner"] = "alice", ["confidentiality"] = "internal"
        }, Body: "個人資料の本文"),
        new McpToolDocument(OrgId, "組織文書", new Dictionary<string, string>
        {
            ["doc_scope"] = "organization", ["confidentiality"] = "public"
        }, Body: "組織文書の本文")
    ], TotalCount: 2);

    private static ToolPublicationConfig ConfigFor(string toolName) => new("test",
        [new ToolPublicationEntry(toolName, ServiceOf(toolName))]);

    private static string ServiceOf(string toolName) => toolName.Split('.')[0];

    private static IReadOnlyList<ServiceToolDeclarations> DeclarationsFor(string toolName) =>
    [
        new ServiceToolDeclarations(ServiceOf(toolName),
        [
            new McpToolDeclaration(toolName, "テスト用", """{"type":"object"}""",
                "http://svc/internal/mcp/exec", "read", "internal")
        ])
    ];

    private static ClaimsPrincipal PrincipalFor(string clientId, string? userId = null)
    {
        var claims = new List<Claim> { new("azp", clientId) };
        if (userId is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static McpDbContext NewDb() => new(new DbContextOptionsBuilder<McpDbContext>()
        .UseInMemoryDatabase($"McpInvoke_{Guid.NewGuid()}").Options);

    private static (ToolInvocationService Service, FakeInvoker Invoker, McpDbContext Db) Build(
        string toolName, McpClient client, McpToolResult? downstream = null)
    {
        var db = NewDb();
        db.Clients.Add(client);
        db.SaveChanges();

        var catalog = new ToolCatalog(NullLogger<ToolCatalog>.Instance);
        catalog.Refresh(ConfigFor(toolName), DeclarationsFor(toolName));

        var invoker = new FakeInvoker { Result = downstream ?? MixedResult() };
        var service = new ToolInvocationService(
            new McpSubjectResolver(db),
            catalog,
            invoker,
            new ServiceAccountDocumentFilter(NullLogger<ServiceAccountDocumentFilter>.Instance),
            new EgressPolicy(),
            NullLogger<ToolInvocationService>.Instance);
        return (service, invoker, db);
    }

    private static McpClient ServiceAccountClient(string clientId = "batch-agent") =>
        McpClient.Register(clientId, "無人エージェント", McpClientKind.ServiceAccount,
            null, EgressTier.SelfHosted, DateTimeOffset.UtcNow);

    private static McpClient InteractiveClient(string clientId = "claude-desktop") =>
        McpClient.Register(clientId, "有人エージェント", McpClientKind.Interactive,
            null, EgressTier.SelfHosted, DateTimeOffset.UtcNow);

    // FR-16（否定形・全経路）: サービスアカウント実行では、検索・文書取得・グラフ探索の
    // **いずれの経路でも**個人資料が返らない（計画 ADR-0034 決定 9）。
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task サービスアカウント実行ではどの経路でも個人資料が返らない(string toolName)
    {
        var (service, _, _) = Build(toolName, ServiceAccountClient());

        var outcome = await service.InvokeAsync(
            PrincipalFor("batch-agent"), toolName, "{}", CancellationToken.None);

        outcome.Ok.Should().BeTrue();
        outcome.Result!.Documents.Should().ContainSingle().Which.DocumentId.Should().Be(OrgId);
        outcome.Result.Documents.Should().NotContain(d => d.DocumentId == PrivateId);
        outcome.Result.TotalCount.Should().Be(1);
    }

    // FR-16（陽性対照・全経路）: 同じツール・同じ引数でも**有人実行では個人資料が返る**。
    // 計画（UC-08 例外フロー）が「有人実行と無人実行で結果が変わる」と明記している性質である。
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task 有人実行では同じ経路で個人資料が返る(string toolName)
    {
        var (service, _, _) = Build(toolName, InteractiveClient());

        var outcome = await service.InvokeAsync(
            PrincipalFor("claude-desktop", "alice"), toolName, "{}", CancellationToken.None);

        outcome.Ok.Should().BeTrue();
        outcome.Result!.Documents.Should().HaveCount(2);
        outcome.Result.Documents.Should().Contain(d => d.DocumentId == PrivateId);
    }

    // FR-16: 要求側（1 層目）でも下流へ除外制約を渡している。
    // 応答側フィルタだけだと、下流が個人資料を検索・探索する処理を実際に行ってしまう。
    [Fact]
    public async Task サービスアカウント実行では下流へ除外制約を渡す()
    {
        var (service, invoker, _) = Build("retrieval.search_documents", ServiceAccountClient());

        await service.InvokeAsync(PrincipalFor("batch-agent"), "retrieval.search_documents", "{}",
            CancellationToken.None);

        invoker.LastScope!.ExcludePrivateNote.Should().BeTrue();
        invoker.LastScope.SubjectKind.Should().Be(nameof(McpClientKind.ServiceAccount));
    }

    // FR-16（陽性対照）: 有人実行では除外制約を立てない。
    [Fact]
    public async Task 有人実行では下流へ除外制約を渡さない()
    {
        var (service, invoker, _) = Build("retrieval.search_documents", InteractiveClient());

        await service.InvokeAsync(PrincipalFor("claude-desktop", "alice"),
            "retrieval.search_documents", "{}", CancellationToken.None);

        invoker.LastScope!.ExcludePrivateNote.Should().BeFalse();
    }

    // FR-16: 公開許可リスト外のツールは実行できない（既定は非公開。計画 ADR-0024 §決定）。
    // **「権限がありません」ではなく「不明なツール」**として返す（存在秘匿）。
    [Fact]
    public async Task 公開構成に無いツールは不明なツールとして拒否される()
    {
        var (service, _, _) = Build("retrieval.search_documents", InteractiveClient());

        var outcome = await service.InvokeAsync(
            PrincipalFor("claude-desktop", "alice"), "ai.summarize", "{}", CancellationToken.None);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("不明なツール");
        outcome.Error.Should().NotContain("権限");
    }

    // FR-16: 無効化したクライアントは**次の呼び出しから即座に**拒否される（計画 UC-09 例外フロー）。
    [Fact]
    public async Task 無効化したクライアントは即座に拒否される()
    {
        var client = InteractiveClient();
        var (service, _, db) = Build("retrieval.search_documents", client);
        var principal = PrincipalFor("claude-desktop", "alice");

        (await service.InvokeAsync(principal, "retrieval.search_documents", "{}",
            CancellationToken.None)).Ok.Should().BeTrue();

        var stored = await db.Clients.FirstAsync(c => c.ClientId == "claude-desktop", TestContext.Current.CancellationToken);
        stored.SetEnabled(false, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var after = await service.InvokeAsync(principal, "retrieval.search_documents", "{}",
            CancellationToken.None);
        after.Ok.Should().BeFalse();
    }

    // FR-16: 未登録クライアントは実行を拒否される（計画 UC-08 例外フロー）。
    [Fact]
    public async Task 未登録クライアントは拒否される()
    {
        var (service, _, _) = Build("retrieval.search_documents", InteractiveClient());

        var outcome = await service.InvokeAsync(
            PrincipalFor("unknown-agent"), "retrieval.search_documents", "{}", CancellationToken.None);

        outcome.Ok.Should().BeFalse();
    }

    // FR-16: 未登録・無効化されたクライアントには公開ツール一覧すら見せない。
    [Fact]
    public async Task 未登録クライアントには公開ツール一覧を返さない()
    {
        var (service, _, _) = Build("retrieval.search_documents", InteractiveClient());

        (await service.ListToolsAsync(PrincipalFor("unknown-agent"), CancellationToken.None))
            .Should().BeEmpty();
        (await service.ListToolsAsync(PrincipalFor("claude-desktop", "alice"), CancellationToken.None))
            .Should().ContainSingle();
    }

    // FR-16: 越境不可の文書は本文を落とし参照リンクのみへ縮退する（計画 ADR-0024 §4・UC-08 代替フロー）。
    [Fact]
    public async Task 越境不可の文書は本文を落として参照リンクのみになる()
    {
        var downstream = new McpToolResult(
        [
            new McpToolDocument("c1", "機密文書", new Dictionary<string, string>
            {
                ["doc_scope"] = "organization", ["confidentiality"] = "confidential"
            }, Body: "機密本文", ReferenceUrl: "https://wiki.internal/c1"),
            new McpToolDocument("p1", "公開文書", new Dictionary<string, string>
            {
                ["doc_scope"] = "organization", ["confidentiality"] = "public"
            }, Body: "公開本文", ReferenceUrl: "https://wiki.internal/p1")
        ], TotalCount: 2);

        // ティアC（標準外部 API）のクライアント。
        var client = McpClient.Register("standard-agent", "標準外部", McpClientKind.Interactive,
            null, EgressTier.StandardExternal, DateTimeOffset.UtcNow);
        var (service, _, _) = Build("document.get_document", client, downstream);

        var outcome = await service.InvokeAsync(
            PrincipalFor("standard-agent", "alice"), "document.get_document", "{}",
            CancellationToken.None);

        var confidential = outcome.Result!.Documents.Single(d => d.DocumentId == "c1");
        confidential.Body.Should().BeNull();
        confidential.ReferenceUrl.Should().Be("https://wiki.internal/c1");

        outcome.Result.Documents.Single(d => d.DocumentId == "p1").Body.Should().Be("公開本文");
    }
}
