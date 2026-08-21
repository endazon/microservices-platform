using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Services;
using ConversionService.Worker.Foundation.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06: 正規化オーケストレータ（本文＋図コード化/画像保持＋保管）の単体テスト。
public class NormalizationServiceTests
{
    private static RawDocumentFetched Raw(string? confidentiality = "internal") => new(
        FetchId: Guid.NewGuid(),
        SourceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceType: "filesystem",
        OriginalPath: "/docs/design.docx",
        StorageUri: "storage://bucket/raw/design.docx",
        ContentType: "application/msword",
        Attributes: confidentiality is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["confidentiality"] = confidentiality },
        Tags: ["knowledge-mgmt"],
        FetchedAt: DateTimeOffset.UtcNow);

    private static ExtractedFigure Figure() =>
        new("fig-1", "image/png", [1, 2, 3]) { Caption = "シーケンス図" };

    // 図のコード化に成功したら、本文へコードブロックを埋め込み、画像資産は生成しない。
    [Fact]
    public async Task Codes_diagram_into_markdown_when_coding_succeeds()
    {
        var store = new RecordingObjectStore();
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult("# 本文\n", [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Success("mermaid", "graph TD; A-->B")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw());

        result.DiagramsCoded.Should().Be(1);
        result.DiagramsRetained.Should().Be(0);
        result.AssetUris.Should().BeEmpty();
        store.LastMarkdown.Should().Contain("```mermaid").And.Contain("graph TD; A-->B");
        store.SavedAssets.Should().BeEmpty();
    }

    // コード化できない図は、画像としてオブジェクトストレージへ保持し、本文へ参照を埋め込む。
    [Fact]
    public async Task Retains_diagram_as_image_when_coding_not_possible()
    {
        var store = new RecordingObjectStore();
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult("# 本文\n", [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("not-codeable")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw());

        result.DiagramsCoded.Should().Be(0);
        result.DiagramsRetained.Should().Be(1);
        result.AssetUris.Should().HaveCount(1);
        store.SavedAssets.Should().HaveCount(1);
        store.LastMarkdown.Should().Contain("![fig-1](").And.Contain(result.AssetUris[0]);
    }

    // 機密区分で送信拒否（Sent=false→Retain）された図も画像保持に収束する（ADR-0012 機密制御）。
    [Fact]
    public async Task Retains_diagram_when_egress_denied()
    {
        var store = new RecordingObjectStore();
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult("# 本文\n", [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("egress-denied")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw("restricted"));

        result.DiagramsRetained.Should().Be(1);
        result.AssetUris.Should().HaveCount(1);
    }

    // 冪等性: DocumentId は SourceId＋原本パスから決定的に導出される（再変換で同一）。
    [Fact]
    public async Task Produces_deterministic_document_id()
    {
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult("# 本文\n", [])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("unused")),
            new RecordingObjectStore(),
            NullLogger<NormalizationService>.Instance);

        var r1 = await svc.NormalizeAsync(Raw());
        var r2 = await svc.NormalizeAsync(Raw());

        r1.DocumentId.Should().Be(r2.DocumentId);
        r1.DocumentId.Should().Be(
            DeterministicGuid.ForDocument(Raw().SourceId, "/docs/design.docx"));
    }

    private sealed class FakeBodyConverter(BodyConversionResult result) : IBodyConverter
    {
        public Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class FakeDiagramCoder(DiagramCodingResult result) : IDiagramCoder
    {
        public Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class RecordingObjectStore : IObjectStore
    {
        public string LastMarkdown { get; private set; } = string.Empty;
        public List<string> SavedAssets { get; } = [];

        // IADR-0154: 人手補正が本文を読み戻すため、保存した本文を URI 引きできるようにする。
        private readonly Dictionary<string, string> _markdown = [];

        public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default)
        {
            LastMarkdown = markdown;
            var uri = $"storage://normalized/{key}";
            _markdown[uri] = markdown;
            return Task.FromResult(uri);
        }

        public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default)
        {
            SavedAssets.Add(key);
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default) =>
            Task.FromResult(_markdown.TryGetValue(uri, out var md) ? md : null);
    }
}
