using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

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

        var result = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

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

        var result = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

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

        var result = await svc.NormalizeAsync(Raw("restricted"), TestContext.Current.CancellationToken);

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

        var r1 = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);
        var r2 = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

        r1.DocumentId.Should().Be(r2.DocumentId);
        r1.DocumentId.Should().Be(
            DeterministicGuid.ForDocument(Raw().SourceId, "/docs/design.docx"));
    }

    // --- 図の位置（T-29〜T-32 / #1120 / IADR-0351） ---------------------------------------
    //
    // 🔴 従前は図を**無条件に末尾へ append** していた。pandoc の `--extract-media` は本文中の画像参照を
    // 一時ディレクトリの絶対パスへ書き換えるので、**本文には消えたパスへの壊れた参照が残ったまま、
    // 同じ図が末尾にも出ていた**（#1097 で pandoc を実走させて初めて観測された）。
    // 変換器が置いた目印（`![fig-N](figure:fig-N)`）を、ここで最終の埋め込みへ置換する。

    // T-29: 画像保持へ縮退した図は、目印の位置（＝本文中の元の位置）へ埋め込まれる。
    [Fact]
    public async Task Embeds_retained_image_at_the_placeholder_position()
    {
        var store = new RecordingObjectStore();
        var body = "# 本文\n\n段落。\n\n" + FigureMarkdown.PlaceholderEmbed("fig-1") + "\n\n## まとめ\n";
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult(body, [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("not-codeable")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

        store.LastMarkdown.Should().Contain(
            "段落。\n\n" + FigureMarkdown.ImageEmbed("fig-1", result.AssetUris[0]) + "\n\n## まとめ");
        store.LastMarkdown.Should().NotContain(FigureMarkdown.PlaceholderScheme,
            "目印が残ると解決できない参照が保管物に入る");
        store.LastMarkdown.Should().EndWith("## まとめ\n", "末尾へ append し直していない");
    }

    // T-30: コード化に成功した図も、目印の位置へ埋め込まれる。
    [Fact]
    public async Task Embeds_coded_diagram_at_the_placeholder_position()
    {
        var store = new RecordingObjectStore();
        var body = "# 本文\n\n段落。\n\n" + FigureMarkdown.PlaceholderEmbed("fig-1") + "\n\n## まとめ\n";
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult(body, [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Success("mermaid", "graph TD; A-->B")),
            store,
            NullLogger<NormalizationService>.Instance);

        await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

        store.LastMarkdown.Should().Contain(
            "段落。\n\n```mermaid\ngraph TD; A-->B\n```\n\n## まとめ");
        store.LastMarkdown.Should().NotContain(FigureMarkdown.PlaceholderScheme);
        store.LastMarkdown.Should().EndWith("## まとめ\n");
    }

    // T-31: 目印を持たない本文（縮退プレースホルダ・変換器の差し替え）では**従来どおり末尾へ append**
    // する。図が本文からまったく参照できなくなるほうが悪い（IADR-0351 決定 6）。
    // 綴りは従前とバイト等価であり、目印を含まないゴールデンは動かない。
    [Fact]
    public async Task Appends_at_the_end_when_the_body_carries_no_placeholder()
    {
        var store = new RecordingObjectStore();
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult("# 本文\n", [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("not-codeable")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

        store.LastMarkdown.Should().Be(
            "# 本文\n\n\n" + FigureMarkdown.ImageEmbed("fig-1", result.AssetUris[0]) + "\n");
    }

    // T-32: 受け入れ基準 3 —— **人手補正が空振りしない。**
    // 位置を本文中へ戻しても、埋め込みの綴りは `FigureMarkdown.ImageEmbed` のままである
    // （IADR-0154 決定 3 の目印。`src` 属性だけ差し替えていたらここが false になる）。
    [Fact]
    public async Task Keeps_the_embed_form_the_manual_correction_replaces()
    {
        var store = new RecordingObjectStore();
        var body = "段落。\n\n" + FigureMarkdown.PlaceholderEmbed("fig-1") + "\n\n続き。\n";
        var svc = new NormalizationService(
            new FakeBodyConverter(new BodyConversionResult(body, [Figure()])),
            new FakeDiagramCoder(DiagramCodingResult.Retain("not-codeable")),
            store,
            NullLogger<NormalizationService>.Instance);

        var result = await svc.NormalizeAsync(Raw(), TestContext.Current.CancellationToken);

        FigureMarkdown.TryReplaceImageWithCode(store.LastMarkdown, "fig-1", result.AssetUris[0],
                "mermaid", "graph TD; A-->B", out var corrected)
            .Should().BeTrue("補正の置換が空振りすると、補正だけ保存されて本文に出ない");
        corrected.Should().Contain("段落。\n\n```mermaid\ngraph TD; A-->B\n```\n\n続き。");
    }

    // --- ゴールデンファイル（T-14〜T-18 / #447 退行防止 / IADR-0298） ---------------------
    //
    // 上の 4 件は部分一致（`Should().Contain(...)`）であり、**出力の形が変わっても緑のまま通る**。
    // 形には人手補正（`FigureMarkdown.TryReplaceImageWithCode` の文字列一致）と削除伝播
    // （`DocumentObjectPurger` が逆引きする資産キー）が依存しているため、**全体をスナップショットで
    // 固定する**。器・固定範囲・更新手順は `Golden/NormalizationGolden.cs` を参照。
    //
    // 🔴 **pandoc は実走していない。** 入力は変換器出力を模した Markdown であり、
    // docx / PDF / HTML の原本は 1 バイトも読んでいない（IADR-0298 決定 2 / N-1・N-2）。

    public static TheoryData<string> GoldenCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in NormalizationGolden.CaseNames()) data.Add(name);
        return data;
    }

    // FR-12, UC-06: 代表的文書形式の正規化結果が golden と一致する（本文全文・資産キー・
    // 決定的 DocumentId・件数・図の記録・機密区分の受け渡し）。
    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task Normalization_matches_golden(string caseName)
    {
        NormalizationGolden.Verify(caseName, await NormalizationGolden.RenderAsync(caseName));
    }

    // 器そのものの fail-closed（IADR-0298 決定 5）。走査が空振りしたまま緑にならないこと、
    // case を消したのに golden が残る（孤児）状態を検出することを固定する。
    // **これが無いと、`Cases/` を丸ごと消しても Theory が 0 件になるだけで気付けない。**
    [Fact]
    public void Golden_case_set_is_closed()
    {
        var cases = NormalizationGolden.CaseNames();
        var goldens = NormalizationGolden.GoldenNames();

        cases.Should().NotBeEmpty("case が 0 件なら走査が空振りしている");
        cases.Should().Contain(["markdown-plain", "html-article", "office-docx-report", "pdf-report"],
            "代表 4 形式は #447 の退行防止項目が名指ししている");
        // ADR-0070 決定 2・3 / IADR-0356 (#1192): PDF はテキスト層の有無で終端が分かれる。両方を固定する。
        cases.Should().Contain(["pdf-text-layer", "pdf-no-text-layer"],
            "テキスト層あり（本文あり）／なし（本文なしで完了）の対が要る");
        goldens.Should().BeEquivalentTo(cases,
            "golden と case は 1 対 1 である（孤児 golden は case の削除漏れ）");
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
