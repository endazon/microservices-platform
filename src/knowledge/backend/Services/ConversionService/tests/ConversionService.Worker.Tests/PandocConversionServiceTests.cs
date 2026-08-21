using ConversionService.Worker.Composable.Adapters;
using System.Diagnostics;
using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Services;
using ConversionService.Worker.Foundation.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Worker.Tests;

// FR-12, ADR-0012: pandoc 本文変換のテスト。pandoc の実行と図抽出（--extract-media）、
// および原本がローカル解決不能／pandoc 未導入時のグレースフルデグレードを検証する。
// pandoc の導入有無は環境依存のため、各ケースは前提を満たさない場合はスキップする。
// #455 A-2: 従前は `if (cond) return;` の**ソフトスキップ**だった（Xunit.SkippableFact を
// 導入しない方針のため）。🔴 これは走らなかったケースを **Passed として報告する** —— 実行実績が
// 無いのに緑に見える。xUnit v3 へ移り `Assert.Skip` が標準で使えるようになったため、
// 追加パッケージゼロで**真の Skipped** へ改めた。
public class PandocConversionServiceTests
{
    private static PandocConversionService NewService() =>
        new(NullLogger<PandocConversionService>.Instance);

    // pandoc 未導入環境ではプレースホルダ本文（図0件）へデグレードする。
    [Fact]
    public async Task Degrades_to_placeholder_when_pandoc_unavailable()
    {
        Assert.SkipWhen(PandocAvailable(), "pandoc 導入済み環境では本分岐（未導入時のデグレード）を検証できない。");

        var result = await NewService().ConvertAsync(
            "storage://bucket/raw/design.docx", "application/msword");

        result.Markdown.Should().Contain("design");
        result.Figures.Should().BeEmpty();
    }

    // 実オブジェクトストレージ URI（storage://）はローカル解決できないためデグレードする。
    [Fact]
    public async Task Degrades_when_source_not_locally_readable()
    {
        Assert.SkipUnless(PandocAvailable(), "pandoc 導入済み環境でのみ意味を持つケース。");

        var result = await NewService().ConvertAsync(
            "storage://bucket/raw/missing.md", "text/markdown");

        // pandoc は使えるが原本がローカルに無い → プレースホルダへデグレード（図0件）。
        result.Figures.Should().BeEmpty();
        result.Markdown.Should().Contain("missing");
    }

    // pandoc 導入済み環境では、ローカルの Markdown 原本を実際に変換して本文を返す。
    [Fact]
    public async Task Runs_pandoc_on_local_markdown_source()
    {
        Assert.SkipUnless(PandocAvailable(), "pandoc 未導入環境では実行できない。");

        var path = Path.Combine(Path.GetTempPath(), $"conv-src-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "# タイトル\n\n本文テスト。\n");
        try
        {
            var result = await NewService().ConvertAsync(new Uri(path).AbsoluteUri, "text/markdown");

            result.Markdown.Should().Contain("タイトル");
            result.Figures.Should().BeEmpty(); // 画像を含まない原本のため図0件。
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool PandocAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("pandoc", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
