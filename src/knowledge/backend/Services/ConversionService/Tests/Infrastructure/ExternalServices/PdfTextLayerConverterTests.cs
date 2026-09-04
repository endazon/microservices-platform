using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12, UC-06, SC-07, ADR-0070 決定 2・3, IADR-0356 (#1192): PDF のテキスト層抽出器のテスト。
//
// 陽性／陰性の対で固定する:
//   陽性 … テキスト層を持つ PDF → 本文あり（`BodyAbsent = false`）
//   陰性 … テキスト層を持たない PDF（描画だけのページ）→ **失敗ではなく本文なし**（`BodyAbsent = true`）
//
// 🔴 **PDF はテスト実行時に生成する。** 追跡下にバイナリを置かない（`check-nul-bytes.js` は追跡下が
// 全部テキストであることを前提にしている）。生成する PDF は ASCII だけの最小構成で、xref も計算する。
//
// 実 `pdftotext` を要するケースは `Assert.SkipUnless` で**真の Skipped** にする（`PandocConversionServiceTests`
// と同じ流儀。ソフトスキップにしない）。空判定（`ToBody`）は純関数なので pdftotext 無しで走る。
[Trait("TestKind", "Integration")]
public class PdfTextLayerConverterTests
{
    private static PdfTextLayerConverter NewConverter(
        bool allowDegraded = false, IObjectStorageClient? storage = null) =>
        new(storage ?? new UnresolvableStorage(),
            Options.Create(new ConversionOptions { AllowDegradedBodyConversion = allowDegraded }),
            NullLogger<PdfTextLayerConverter>.Instance);

    // --- 空判定（純関数。pdftotext 無しで走る） -------------------------------------------

    // ADR-0070 決定 3: 「テキスト層なし」は**抽出結果が空白のみ**であること。改頁（\f）だけでも空である。
    [Theory]
    [InlineData("")]
    [InlineData("   \n\n\t\n")]
    [InlineData("\f")]
    [InlineData("\f\n\f\n")]
    [InlineData("\r\n \r\n")]
    public void ToBody_treats_whitespace_only_output_as_body_absent(string raw)
    {
        var (markdown, bodyAbsent) = PdfTextLayerConverter.ToBody(raw);

        bodyAbsent.Should().BeTrue();
        markdown.Should().BeEmpty("本文なしのときに作った文を索引へ載せない");
    }

    // 陽性対照: 可視の文字が 1 つでもあれば本文ありである（品質の判断はしない）。
    [Theory]
    [InlineData("Quarterly report")]
    [InlineData("\f\n.\n")]
    [InlineData("四半期報告")]
    public void ToBody_keeps_any_visible_text_as_a_body(string raw)
    {
        var (markdown, bodyAbsent) = PdfTextLayerConverter.ToBody(raw);

        bodyAbsent.Should().BeFalse();
        markdown.Should().NotBeEmpty();
    }

    // 整形は改行の正規化・行末空白の除去・3 連以上の空行の畳み込みだけ。Markdown の記法は触らない。
    [Fact]
    public void ToBody_normalizes_line_endings_and_collapses_blank_runs()
    {
        var (markdown, bodyAbsent) = PdfTextLayerConverter.ToBody(
            "Title  \r\n\r\n\r\n\r\nParagraph one.\fParagraph two.\n\n# not-a-heading-escape\n\n\n");

        bodyAbsent.Should().BeFalse();
        markdown.Should().Be("Title\n\nParagraph one.\nParagraph two.\n\n# not-a-heading-escape\n");
    }

    // --- 実 pdftotext（導入環境のみ） ---------------------------------------------------

    // 陽性: テキスト層を持つ PDF から本文が取れ、`BodyAbsent` は立たない。図は抽出しない。
    [Fact]
    public async Task Extracts_the_text_layer_of_a_pdf_as_the_body()
    {
        Assert.SkipUnless(PdfToTextAvailable(), "pdftotext 未導入環境では実行できない。");

        var path = TempPdfPath();
        await File.WriteAllBytesAsync(path, MinimalPdf.WithText("Quarterly Report", "Uptime 99.95 percent"),
            TestContext.Current.CancellationToken);
        try
        {
            var result = await NewConverter().ConvertAsync(new Uri(path).AbsoluteUri, "application/pdf",
                TestContext.Current.CancellationToken);

            result.BodyAbsent.Should().BeFalse();
            result.Markdown.Should().Contain("Quarterly Report").And.Contain("Uptime 99.95 percent");
            result.Markdown.Should().NotContain("から pdftotext で抽出します", "プレースホルダではない");
            result.Figures.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 陰性: テキスト層を持たない PDF（描画だけのページ）は**例外にならず**本文なしで完了する。
    // 🔴 これが ADR-0070 決定 3 の中核である —— 従前はこの原本が `failed` / `deadLettered` になっていた。
    [Fact]
    public async Task Completes_without_a_body_when_the_pdf_has_no_text_layer()
    {
        Assert.SkipUnless(PdfToTextAvailable(), "pdftotext 未導入環境では実行できない。");

        var path = TempPdfPath();
        await File.WriteAllBytesAsync(path, MinimalPdf.ImageOnly(), TestContext.Current.CancellationToken);
        try
        {
            var result = await NewConverter().ConvertAsync(new Uri(path).AbsoluteUri, "application/pdf",
                TestContext.Current.CancellationToken);

            result.BodyAbsent.Should().BeTrue();
            result.Markdown.Should().BeEmpty();
            result.Figures.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // IADR-0320 決定 3 と同じ経路: オブジェクトストレージ上の原本を取り寄せて抽出する。
    [Fact]
    public async Task Fetches_the_pdf_from_object_storage_and_extracts_it()
    {
        Assert.SkipUnless(PdfToTextAvailable(), "pdftotext 未導入環境では実行できない。");

        const string uri = "storage://knowledge-normalized/src/fetch/raw.pdf";
        var storage = new InMemoryStorage(uri, MinimalPdf.WithText("Stored PDF body"));

        var result = await NewConverter(storage: storage).ConvertAsync(uri, "application/pdf",
            TestContext.Current.CancellationToken);

        result.BodyAbsent.Should().BeFalse();
        result.Markdown.Should().Contain("Stored PDF body");
        storage.Fetched.Should().ContainSingle().Which.Should().Be(uri);
    }

    // 本文があるのに作れない失敗は従来どおり例外（再試行 → デッドレター）。壊れた PDF は非 0 終了する。
    [Fact]
    public async Task Fails_when_pdftotext_cannot_read_the_source()
    {
        Assert.SkipUnless(PdfToTextAvailable(), "pdftotext 未導入環境では実行できない。");

        var path = TempPdfPath();
        await File.WriteAllTextAsync(path, "this is not a pdf", TestContext.Current.CancellationToken);
        try
        {
            var act = async () => await NewConverter().ConvertAsync(new Uri(path).AbsoluteUri,
                "application/pdf", TestContext.Current.CancellationToken);

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*pdftotext exited*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 原本が解決できない（オブジェクトストレージ未構成）ときも、既定では例外である（fail-closed）。
    [Fact]
    public async Task Fails_closed_when_source_not_resolvable()
    {
        Assert.SkipUnless(PdfToTextAvailable(), "pdftotext 導入済み環境でのみ意味を持つケース。");

        var act = async () => await NewConverter().ConvertAsync(
            "storage://bucket/raw/missing.pdf", "application/pdf", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BodyConversionUnavailableException>())
            .WithMessage("*missing.pdf*");
    }

    // --- 抽出器が無い環境（未導入環境のみ） -----------------------------------------------

    // 🔴 既定（fail-closed）では pdftotext 未導入は**例外**である。静かに縮退しない（IADR-0320 決定 2 の線）。
    [Fact]
    public async Task Fails_closed_when_pdftotext_unavailable()
    {
        Assert.SkipWhen(PdfToTextAvailable(), "pdftotext 導入済み環境では本分岐（未導入時の失敗）を検証できない。");

        var act = async () => await NewConverter().ConvertAsync(
            "storage://bucket/raw/report.pdf", "application/pdf", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BodyConversionUnavailableException>())
            .WithMessage("*pdftotext*");
    }

    // 縮退は dev（構成で明示的に許可した場合）だけ。プレースホルダ本文（図0件）を返す。
    [Fact]
    public async Task Degrades_to_placeholder_when_explicitly_allowed()
    {
        Assert.SkipWhen(PdfToTextAvailable(), "pdftotext 導入済み環境では本分岐（未導入時のデグレード）を検証できない。");

        var result = await NewConverter(allowDegraded: true).ConvertAsync(
            "storage://bucket/raw/report.pdf", "application/pdf", TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("report");
        result.BodyAbsent.Should().BeFalse("縮退は「本文なし」ではない。区別を潰さない");
        result.Figures.Should().BeEmpty();
    }

    // --- 実行時イメージ -----------------------------------------------------------------

    // 🔴 IADR-0356 決定 1 (#1192): **実行時イメージが poppler-utils を導入していることを機械的に確かめる。**
    // `PandocConversionServiceTests.Dockerfile_installs_pandoc_into_the_runtime_stage` と同型。
    // 「焼いたイメージに実在するか」は readiness（`PdfToTextHealthCheck`）が配備側で見る。
    [Fact]
    public void Dockerfile_installs_poppler_utils_into_the_runtime_stage()
    {
        var dockerfile = File.ReadAllText(DockerfilePath());
        var runtimeStage = dockerfile[dockerfile.LastIndexOf("FROM mcr.microsoft.com/dotnet/aspnet",
            StringComparison.Ordinal)..];

        runtimeStage.Replace("\\\r\n", " ").Replace("\\\n", " ")
            .Split('\n')
            .Where(line => line.Contains("apt-get install", StringComparison.Ordinal))
            .Should().ContainSingle(line => line.Contains("poppler-utils", StringComparison.Ordinal));
    }

    // --- 補助 ---------------------------------------------------------------------------

    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"conv-pdf-{Guid.NewGuid():N}.pdf");

    private static string DockerfilePath([CallerFilePath] string thisFile = "")
    {
        for (var dir = Directory.GetParent(thisFile); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("ConversionService.csproj").Any())
                return Path.Combine(dir.FullName, "Dockerfile");
        }
        throw new InvalidOperationException($"ConversionService.csproj を {thisFile} の上位に見つけられなかった。");
    }

    // 生産コードと同じ口で判定する（poppler は `-v` で 0、xpdf 版は 99 を返す —— 終了コードで見ると
    // 開発機の xpdf 版で全ケースが Skipped になり、実行実績が無いのに緑に見える）。
    internal static bool PdfToTextAvailable() =>
        PdfTextLayerConverter.TryGetPdfToTextVersionAsync(CancellationToken.None)
            .GetAwaiter().GetResult() is not null;

    /// <summary>
    /// テスト実行時に生成する最小の PDF（ASCII のみ・xref 計算済み）。
    /// 追跡下にバイナリを置かないための器であり、PDF の生成そのものは検証対象ではない。
    /// </summary>
    internal static class MinimalPdf
    {
        /// <summary>テキスト層を持つ 1 ページ（Helvetica で各行を描く）。</summary>
        public static byte[] WithText(params string[] lines)
        {
            var content = new StringBuilder("BT /F1 18 Tf 20 100 Td ");
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) content.Append("0 -24 Td ");
                content.Append('(').Append(Escape(lines[i])).Append(") Tj ");
            }
            content.Append("ET");
            return Build(content.ToString(), withFont: true);
        }

        /// <summary>テキスト層を持たない 1 ページ（塗りつぶした矩形だけ。スキャン画像相当）。</summary>
        public static byte[] ImageOnly() => Build("0 0 1 rg 20 20 200 100 re f", withFont: false);

        private static byte[] Build(string content, bool withFont)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                withFont
                    ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"
                    : "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R /Resources << >> >>",
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            };
            if (withFont) objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

            var sb = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
                sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
            }
            var xref = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
            sb.Append("0000000000 65535 f \n");
            foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
            sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static string Escape(string text) =>
            text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    private sealed class UnresolvableStorage : IObjectStorageClient
    {
        public Task<string> PutTextAsync(string key, string text, string contentType,
            CancellationToken ct = default) => Task.FromResult($"storage://test/{key}");

        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default) => Task.FromResult($"storage://test/{key}");

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) =>
            throw new NotSupportedException(uri);

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) =>
            throw new NotSupportedException(uri);

        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

        public bool CanResolve(string? uri) => false;

        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) =>
            throw new NotSupportedException(uri);
    }

    private sealed class InMemoryStorage(string uri, byte[] bytes) : IObjectStorageClient
    {
        internal List<string> Fetched { get; } = [];

        public Task<string> PutTextAsync(string key, string text, string contentType,
            CancellationToken ct = default) => Task.FromResult($"storage://test/{key}");

        public Task<string> PutBytesAsync(string key, byte[] b, string contentType,
            CancellationToken ct = default) => Task.FromResult($"storage://test/{key}");

        public Task<string> GetTextAsync(string u, CancellationToken ct = default) =>
            Task.FromResult(Encoding.UTF8.GetString(bytes));

        public Task<byte[]> GetBytesAsync(string u, CancellationToken ct = default)
        {
            Fetched.Add(u);
            if (!string.Equals(u, uri, StringComparison.Ordinal))
                throw new FileNotFoundException(u);
            return Task.FromResult(bytes);
        }

        public Task DeleteAsync(string u, CancellationToken ct = default) => Task.CompletedTask;

        public bool CanResolve(string? u) => string.Equals(u, uri, StringComparison.Ordinal);

        public string CreatePresignedGetUrl(string u, TimeSpan? expiry = null) =>
            throw new NotSupportedException(u);
    }
}
