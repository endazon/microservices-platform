using Grpc.Net.Client;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace Knowledge.IntegrationTests.McpTools;

// 🔴 FR-16, UC-08, ADR-0024 §2・§5 (#1020): **実効カタログが空でないことを、実物で固定する。**
//
// #445 は収集機構（当時は REST の `HttpToolDeclarationSource` → `ToolCatalog`）を作ったが、
// 申告を実装したサービスが 0 件だったため**集める対象が存在せず**、
// 単体試験はすべてスタブの申告に対して緑だった。本試験は
// **実サービス 3 本を in-process で起こし、実際の gRPC 応答（`McpToolDeclarations/Declare`）を収集して**突合まで通す。
//
// ［2026-10-10 / #1517］[[IADR-0533]] 決定 3: REST の申告口と収集（`HttpToolDeclarationSource`）を撤去したので、
// 収集を gRPC へ移した。応答の写しは本番の収集器と同じ `GrpcToolDeclarationCollector.ToDto` を通す。
// チャネルは in-process のテストサーバーへ向ける（実 DNS・実ポートを使わない）。s2s の認可（ServiceCaller）は
// 各サービスの `GrpcMcpToolDeclarationTests` が実 Kestrel の h2c で測るので、本器では認可の評価を通過させる。
//
// 空でないことだけでなく、**申告した個々のツールが載ること**を測る ——
// 「1 件でも載れば緑」にすると、6 件のうち 5 件が落ちても気づけない。
public sealed class McpToolCatalogIntegrationTests : IAsyncLifetime
{
    private readonly DocumentServiceDeclarationHost _document = new();
    private readonly RetrievalServiceDeclarationHost _retrieval = new();
    private readonly GraphServiceDeclarationHost _graph = new();

    // 公開構成（許可リスト）。**McpServer が出荷する `Configuration/mcp-publication.json` と同じ 6 件。**
    private static readonly ToolPublicationEntry[] Published =
    [
        new("retrieval.search_documents", "retrieval-service"),
        new("document.get_document", "document-service"),
        new("document.list_documents", "document-service"),
        new("graph.get_backlinks", "graph-service"),
        new("graph.get_links", "graph-service"),
        new("graph.traverse", "graph-service"),
    ];

    public ValueTask InitializeAsync()
    {
        // WebApplicationFactory はホストを遅延生成する。CreateClient() で起動を確定させる。
        _document.CreateClient().Dispose();
        _retrieval.CreateClient().Dispose();
        _graph.CreateClient().Dispose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _document.DisposeAsync();
        await _retrieval.DisposeAsync();
        await _graph.DisposeAsync();
    }

    // 🔴 FR-16（陽性対照）: 公開構成に載せた **6 ツールが個々に**実効カタログへ現れる。
    [Fact]
    public async Task 実サービスの申告が実効カタログへ載る()
    {
        var catalog = await RefreshAsync(new ToolPublicationConfig("test", Published));

        catalog.PublishedTools.Select(t => t.PublishedName).Should()
            .BeEquivalentTo(Published.Select(e => e.Name));

        // 申告の中身も運ばれている（名前だけの空殻ではない）。
        foreach (var tool in catalog.PublishedTools)
        {
            tool.Declaration.Description.Should().NotBeNullOrWhiteSpace();
            tool.Declaration.InputSchema.Should().NotBeNullOrWhiteSpace();
            tool.Declaration.RequiredScope.Should().NotBeNullOrWhiteSpace();
            tool.Declaration.EgressClass.Should().NotBeNullOrWhiteSpace();
        }
    }

    // FR-16, ADR-0024 §5: 申告のある公開宣言では**ドリフトが沈黙する**。
    [Fact]
    public async Task 申告のある公開宣言ではドリフトが出ない()
    {
        var catalog = await RefreshAsync(new ToolPublicationConfig("test", Published));

        catalog.Drifts.Should().BeEmpty();
    }

    // 🔴 FR-16, ADR-0024 §5（否定形）: 申告の無い公開宣言は**ドリフトとして発火し、公開されない**。
    // 実装後もドリフト検出が効いていることの確認である（沈黙と発火の対で測る）。
    [Fact]
    public async Task 申告の無い公開宣言はドリフトとして発火する()
    {
        var config = new ToolPublicationConfig("test",
            [.. Published, new ToolPublicationEntry("document.get_ghost", "document-service")]);

        var catalog = await RefreshAsync(config);

        catalog.Drifts.Should().ContainSingle()
            .Which.Should().Match<ToolCatalogDrift>(d =>
                d.Kind == "missing-declaration" && d.Target == "document.get_ghost");
        catalog.Find("document.get_ghost").Should().BeNull();
        catalog.PublishedTools.Should().HaveCount(Published.Length);
    }

    // 🔴 FR-19, ADR-0034 決定 9（否定形）: **個人資料のツールは実効カタログにも載らない。**
    //
    // 公開構成が要求しても、DocumentService が申告しない以上は公開できない（許可リストと
    // 自己申告の**両方**が要る）。除外が申告する側で効いていることが、ここで実物として測られる。
    [Fact]
    public async Task 個人資料のツールは公開構成が要求しても載らない()
    {
        var config = new ToolPublicationConfig("test",
            [.. Published, new ToolPublicationEntry("document.list_private_notes", "document-service")]);

        var catalog = await RefreshAsync(config);

        catalog.Find("document.list_private_notes").Should().BeNull();
        catalog.Drifts.Should().ContainSingle()
            .Which.Target.Should().Be("document.list_private_notes");
    }

    private async Task<ToolCatalog> RefreshAsync(ToolPublicationConfig config)
    {
        var declarations = new List<ServiceToolDeclarations>();
        foreach (McpToolDeclarationHostAccessor host in new McpToolDeclarationHostAccessor[] { _document, _retrieval, _graph })
        {
            using var channel = GrpcChannel.ForAddress($"http://{host.MeshHost}",
                new GrpcChannelOptions { HttpHandler = host.Handler });
            var declared = await new Pb.McpToolDeclarations.McpToolDeclarationsClient(channel)
                .DeclareAsync(new Pb.DeclareMcpToolsRequest(), cancellationToken: TestContext.Current.CancellationToken);
            declarations.Add(GrpcToolDeclarationCollector.ToDto(declared));
        }

        declarations.Should().HaveCount(3, "3 サービスすべてから申告を集められること");
        declarations.Select(d => d.Service).Should().BeEquivalentTo(["document-service", "retrieval-service", "graph-service"]);

        var catalog = new ToolCatalog(NullLogger<ToolCatalog>.Instance);
        catalog.Refresh(config, declarations);
        return catalog;
    }
}

// テストサーバーの handler を host 名つきで渡すための小さな受け皿。
internal readonly record struct McpToolDeclarationHostAccessor(string MeshHost, HttpMessageHandler Handler)
{
    public static implicit operator McpToolDeclarationHostAccessor(DocumentServiceDeclarationHost host)
        => new(host.MeshHost, host.Server.CreateHandler());

    public static implicit operator McpToolDeclarationHostAccessor(RetrievalServiceDeclarationHost host)
        => new(host.MeshHost, host.Server.CreateHandler());

    public static implicit operator McpToolDeclarationHostAccessor(GraphServiceDeclarationHost host)
        => new(host.MeshHost, host.Server.CreateHandler());
}
