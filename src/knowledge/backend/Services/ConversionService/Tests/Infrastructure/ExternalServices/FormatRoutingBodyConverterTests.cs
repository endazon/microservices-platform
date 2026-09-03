using AwesomeAssertions;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0070 決定 2・5, IADR-0356 決定 2 (#1192): 本文変換器の振り分け。
//
// 外部プロセス（pandoc / pdftotext）の有無に依存せずに**どちらへ振り分けたか**を測るため、
// 縮退を明示的に許可（`AllowDegradedBodyConversion=true`）し、原本を解決できないストレージを渡す。
// すると両変換器は**必ず自分のプレースホルダ本文**を返す（pandoc は「から pandoc で変換します」、
// 抽出器は「から pdftotext で抽出します」）。その綴りが「どちらが走ったか」の観測点である。
// 生産コードへテスト用の接ぎ目を足していない。
public class FormatRoutingBodyConverterTests
{
    private static FormatRoutingBodyConverter NewRouter()
    {
        var storage = new UnresolvableStorage();
        var options = Options.Create(new ConversionOptions { AllowDegradedBodyConversion = true });
        return new FormatRoutingBodyConverter(
            new PandocConversionService(storage, options, NullLogger<PandocConversionService>.Instance),
            new PdfTextLayerConverter(storage, options, NullLogger<PdfTextLayerConverter>.Instance));
    }

    // ADR-0070 決定 2: PDF はテキスト層の抽出器へ。MIME からでも拡張子からでも判る。
    [Theory]
    [InlineData("application/pdf", "storage://bucket/raw/report.pdf")]
    [InlineData("application/x-pdf", "storage://bucket/raw/no-extension")]
    [InlineData("application/octet-stream", "storage://bucket/raw/report.pdf")]
    public async Task Routes_pdf_to_the_text_layer_extractor(string contentType, string uri)
    {
        var result = await NewRouter().ConvertAsync(uri, contentType, TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("から pdftotext で抽出します");
    }

    // 陽性対照: PDF 以外は従来どおり pandoc（ADR-0012 の「本文は pandoc」は PDF 以外でそのまま有効）。
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "storage://b/raw/a.docx")]
    [InlineData("text/html", "storage://b/raw/a.html")]
    [InlineData("text/markdown", "storage://b/raw/a.md")]
    [InlineData("text/plain", "storage://b/raw/a.txt")]
    public async Task Routes_everything_else_to_pandoc(string contentType, string uri)
    {
        var result = await NewRouter().ConvertAsync(uri, contentType, TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("から pandoc で変換します");
    }

    // ADR-0070 決定 5: 対応形式表に無い未知の形式は**取り寄せる前に**拒否する（どちらの変換器も走らない）。
    [Theory]
    [InlineData("application/vnd.ms-excel", "storage://b/raw/sheet.xls")]
    [InlineData("application/octet-stream", "storage://b/raw/no-extension")]
    public async Task Rejects_unknown_formats_before_touching_either_converter(string contentType, string uri)
    {
        var act = async () => await NewRouter().ConvertAsync(uri, contentType,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<UnsupportedSourceFormatException>()).WithMessage("*対応形式表*");
    }

    // 何も解決できないストレージ。両変換器を「原本未解決 → 縮退プレースホルダ」の分岐へ確実に落とす。
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
}
