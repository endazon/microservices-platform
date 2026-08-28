using System.Net;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knowledge.IntegrationTests.McpTools;

// 🔴 FR-16, UC-08, ADR-0024 §2・§5 (#1020): **実効カタログが空でないことを、実物で固定する。**
//
// #445 は収集機構（`HttpToolDeclarationSource` → `ToolCatalog`）を作ったが、
// `/internal/mcp-tools` を実装したサービスが 0 件だったため**集める対象が存在せず**、
// 単体試験はすべてスタブの申告に対して緑だった。本試験は
// **実サービス 3 本を in-process で起こし、実際の HTTP 応答を収集して**突合まで通す。
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
            tool.Declaration.Endpoint.Should().StartWith("http://");
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
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{HttpToolDeclarationSource.ServicesSection}:document-service"] = "http://document-service:8080",
                [$"{HttpToolDeclarationSource.ServicesSection}:retrieval-service"] = "http://retrieval-service:8080",
                [$"{HttpToolDeclarationSource.ServicesSection}:graph-service"] = "http://graph-service:8080",
            }).Build();

        var source = new HttpToolDeclarationSource(
            new MeshHttpClientFactory(_document, _retrieval, _graph),
            configuration,
            NullLogger<HttpToolDeclarationSource>.Instance);

        var declarations = await source.CollectAsync(TestContext.Current.CancellationToken);
        declarations.Should().HaveCount(3, "3 サービスすべてから申告を集められること");

        var catalog = new ToolCatalog(NullLogger<ToolCatalog>.Instance);
        catalog.Refresh(config, declarations);
        return catalog;
    }

    // 収集側が組み立てる絶対 URL（`http://<mesh-host>:8080/internal/mcp-tools`）を、
    // host 名で in-process のテストサーバーへ振り分ける。**実 DNS は引かない。**
    private sealed class MeshHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, HttpMessageInvoker> _byHost;

        public MeshHttpClientFactory(params McpToolDeclarationHostAccessor[] hosts)
            => _byHost = hosts.ToDictionary(h => h.MeshHost, h => h.Invoker, StringComparer.Ordinal);

        public HttpClient CreateClient(string name) => new(new RoutingHandler(_byHost), disposeHandler: false);

        private sealed class RoutingHandler(Dictionary<string, HttpMessageInvoker> byHost) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => byHost.TryGetValue(request.RequestUri!.Host, out var invoker)
                    ? invoker.SendAsync(request, cancellationToken)
                    // 到達できないサービスは「申告なし」になる（収集側の縮退）。
                    // ここへ落ちたら振り分けの設定漏れであり、黙って空にしない。
                    : throw new InvalidOperationException(
                        $"未登録のメッシュ host: {request.RequestUri.Host}");
        }
    }
}

// テストサーバーの handler を host 名つきで渡すための小さな受け皿。
internal readonly record struct McpToolDeclarationHostAccessor(string MeshHost, HttpMessageInvoker Invoker)
{
    public static implicit operator McpToolDeclarationHostAccessor(DocumentServiceDeclarationHost host)
        => new(host.MeshHost, new HttpMessageInvoker(host.Server.CreateHandler()));

    public static implicit operator McpToolDeclarationHostAccessor(RetrievalServiceDeclarationHost host)
        => new(host.MeshHost, new HttpMessageInvoker(host.Server.CreateHandler()));

    public static implicit operator McpToolDeclarationHostAccessor(GraphServiceDeclarationHost host)
        => new(host.MeshHost, new HttpMessageInvoker(host.Server.CreateHandler()));
}
