using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ConversionService.Domain.Ports;
using ConversionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12, ADR-0012: pandoc 本文変換のテスト。pandoc の実行と図抽出（--extract-media）、
// オブジェクトストレージ上の原本の取り寄せ、pandoc が入力に取れない形式の拒否、
// および pandoc 未導入時の**縮退の可否**（既定 fail-closed）を検証する。
// pandoc の導入有無は環境依存のため、実 pandoc を要するケースはスキップする。
// #455 A-2: 従前は `if (cond) return;` の**ソフトスキップ**だった（Xunit.SkippableFact を
// 導入しない方針のため）。🔴 これは走らなかったケースを **Passed として報告する** —— 実行実績が
// 無いのに緑に見える。xUnit v3 へ移り `Assert.Skip` が標準で使えるようになったため、
// 追加パッケージゼロで**真の Skipped** へ改めた。
//
// 🔴 IADR-0320 (#1097): 従前このクラスは「pandoc が無ければプレースホルダへ縮退する」ことを
// **正常な振る舞いとして固定していた**。ところが実行時イメージが pandoc を持っておらず、
// 配備した実物がその縮退のまま「成功」を返し続けていた。既定を fail-closed へ改め、
// 縮退は `Conversion:AllowDegradedBodyConversion=true` のときだけに限る。
public class PandocConversionServiceTests
{
    private static PandocConversionService NewService(
        bool allowDegraded = false, IObjectStorageClient? storage = null) =>
        new(storage ?? new UnresolvableStorage(),
            Options.Create(new ConversionOptions { AllowDegradedBodyConversion = allowDegraded }),
            NullLogger<PandocConversionService>.Instance);

    // 🔴 既定（fail-closed）では pandoc 未導入は**例外**である。静かに縮退しない。
    [Fact]
    public async Task Fails_closed_when_pandoc_unavailable()
    {
        Assert.SkipWhen(PandocAvailable(), "pandoc 導入済み環境では本分岐（未導入時の失敗）を検証できない。");

        var act = async () => await NewService().ConvertAsync(
            "storage://bucket/raw/design.docx", "application/msword", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BodyConversionUnavailableException>())
            .WithMessage("*pandoc*");
    }

    // 縮退は消していない。dev（構成で明示的に許可した場合）だけプレースホルダ本文（図0件）を返す。
    [Fact]
    public async Task Degrades_to_placeholder_when_explicitly_allowed()
    {
        Assert.SkipWhen(PandocAvailable(), "pandoc 導入済み環境では本分岐（未導入時のデグレード）を検証できない。");

        var result = await NewService(allowDegraded: true).ConvertAsync(
            "storage://bucket/raw/design.docx", "application/msword", TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("design");
        result.Figures.Should().BeEmpty();
    }

    // 原本が解決できない（オブジェクトストレージ未構成）ときも、既定では例外である。
    [Fact]
    public async Task Fails_closed_when_source_not_resolvable()
    {
        Assert.SkipUnless(PandocAvailable(), "pandoc 導入済み環境でのみ意味を持つケース。");

        var act = async () => await NewService().ConvertAsync(
            "storage://bucket/raw/missing.md", "text/markdown", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BodyConversionUnavailableException>())
            .WithMessage("*missing.md*");
    }

    // pandoc 導入済み環境では、ローカルの Markdown 原本を実際に変換して本文を返す。
    [Fact]
    public async Task Runs_pandoc_on_local_markdown_source()
    {
        Assert.SkipUnless(PandocAvailable(), "pandoc 未導入環境では実行できない。");

        var path = Path.Combine(Path.GetTempPath(), $"conv-src-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "# タイトル\n\n本文テスト。\n", TestContext.Current.CancellationToken);
        try
        {
            var result = await NewService().ConvertAsync(new Uri(path).AbsoluteUri, "text/markdown",
                TestContext.Current.CancellationToken);

            result.Markdown.Should().Contain("タイトル");
            result.Figures.Should().BeEmpty(); // 画像を含まない原本のため図0件。
        }
        finally
        {
            File.Delete(path);
        }
    }

    // IADR-0320 決定 3: オブジェクトストレージ上の原本を取り寄せて変換する。
    // 🔴 これが無いと、配備した実物では **pandoc があっても** 原本が解決できず縮退したままになる
    // （`DataSourceSyncService` が発行する StorageUri は常にオブジェクトストレージの参照である）。
    [Fact]
    public async Task Fetches_source_from_object_storage_and_converts_it()
    {
        Assert.SkipUnless(PandocAvailable(), "pandoc 未導入環境では実行できない。");

        const string uri = "storage://knowledge-normalized/src/fetch/raw.md";
        var storage = new InMemoryStorage(uri, Encoding.UTF8.GetBytes("# ストレージ本文\n\n段落。\n"));

        var result = await NewService(storage: storage).ConvertAsync(uri, "text/markdown",
            TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("ストレージ本文");
        result.Markdown.Should().NotContain("から pandoc で変換します"); // プレースホルダではない。
        storage.Fetched.Should().ContainSingle().Which.Should().Be(uri);
    }

    // IADR-0320 決定 4: PDF は pandoc の入力形式にならない。**既定の markdown へ落とさない。**
    [Theory]
    [InlineData("application/pdf", "storage://bucket/raw/report.pdf")]
    [InlineData("application/pdf", "storage://bucket/raw/no-extension")]
    [InlineData("application/octet-stream", "storage://bucket/raw/report.pdf")]
    public async Task Rejects_pdf_instead_of_falling_back_to_markdown(string contentType, string uri)
    {
        var act = async () => await NewService().ConvertAsync(uri, contentType,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<UnsupportedSourceFormatException>())
            .WithMessage("*PDF*");
    }

    // 形式判定そのものの回帰。PDF 以外は従来どおりの写像である。
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "a.docx", "docx")]
    [InlineData("text/html", "a.html", "html")]
    [InlineData("text/markdown", "a.md", "gfm")]
    [InlineData("text/plain", "a.txt", "markdown")]
    [InlineData("application/octet-stream", "storage://b/k/raw.docx", "docx")]
    public void Maps_content_type_to_pandoc_input_format(string contentType, string path, string expected) =>
        PandocConversionService.PandocInputFormat(contentType, path).Should().Be(expected);

    // 🔴 IADR-0320 決定 1 (#1097): **実行時イメージが pandoc を導入していることを機械的に確かめる。**
    // 本サービスは pandoc を外部プロセスとして起動する。Dockerfile から導入行が消えると、
    // 変換は例外にならずプレースホルダ本文へ落ち、変換ジョブは成功として並ぶ（実際にそうなっていた）。
    // 単体テストで捕まえられるのは「Dockerfile に書いてあるか」までであり、
    // 「焼いたイメージに実在するか」は readiness（PandocHealthCheck）が配備側で見る。
    [Fact]
    public void Dockerfile_installs_pandoc_into_the_runtime_stage()
    {
        var dockerfile = File.ReadAllText(DockerfilePath());

        // runtime 段（aspnet ベース）以降に pandoc の導入があること。build 段に書いても実行時には残らない。
        var runtimeStage = dockerfile[dockerfile.LastIndexOf("FROM mcr.microsoft.com/dotnet/aspnet",
            StringComparison.Ordinal)..];

        runtimeStage.Should().Contain("apt-get install");
        // 導入行そのもの（コメント中の言及では通らないよう、apt-get install と同じ RUN 命令内で見る）。
        runtimeStage.Replace("\\\r\n", " ").Replace("\\\n", " ")
            .Split('\n')
            .Where(line => line.Contains("apt-get install", StringComparison.Ordinal))
            .Should().ContainSingle(line => line.Contains("pandoc", StringComparison.Ordinal));
    }

    // 🔴 段数を数えて遡らない (#1063)。このファイルは Tests/ 直下から
    // Tests/Infrastructure/ExternalServices/ へ移送されており、固定段数の遡上
    // (`GetDirectoryName` 2 回) は移送で静かに壊れた（Tests/Dockerfile を読みに行き
    // FileNotFound で落ちる）。サービス直下は「ConversionService.csproj がある階層」
    // として引き当てる —— 移送先の深さに依存しない。
    private static string DockerfilePath([CallerFilePath] string thisFile = "")
    {
        for (var dir = Directory.GetParent(thisFile); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("ConversionService.csproj").Any())
            {
                return Path.Combine(dir.FullName, "Dockerfile");
            }
        }

        throw new InvalidOperationException(
            $"ConversionService.csproj を {thisFile} の上位に見つけられなかった。");
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

    // 何も解決できないストレージ（`NullObjectStorageClient` 相当。dev の縮退クライアント）。
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

    // 1 件だけ持つストレージ。取り寄せが実際に起きたかを記録する。
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
