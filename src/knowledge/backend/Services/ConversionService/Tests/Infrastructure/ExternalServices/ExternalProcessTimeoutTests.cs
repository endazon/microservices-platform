using System.Diagnostics;
using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12 テスト仕様 T-50 (#1641) —— UC-06, ADR-0012, IADR-0008（2026-09-27 追記 / #1641）:
// 本文変換の外部プロセス（pandoc・pdftotext）は**自前の期限つきで**起動し、期限切れか呼び出し元の取り消しで
// **プロセスツリーごと止める**。止めたあとに子孫が 1 つも残らないことを、プロセス番号で確かめる。
//
// 🔴 pandoc も pdftotext も要らない（CI には無い）。変換器の試験用の口（`StartInfoFilter`）で、起動する命令を
// 差し替える: 版の確かめは版の行を出して終わる命令、変換は**子孫を持って止まる**命令（Linux: `sh` ＋ `sleep`、
// Windows: `powershell` ＋ `ping 127.0.0.1`）。止まる命令は自分と子のプロセス番号をファイルへ書く。待ち受けはしない。
//
// 🔴 NFR, #1686（IADR-0490）: 試験は**入れ子のクラスへ分けてある**。xUnit は 1 クラス＝1 テストコレクションで、
// コレクションの中は直列に走る。この試験群は実時間の期限（HangTimeout・刈り取りの上限）を待つのが本体なので、
// 1 クラスのままだと待ちが足し算になり（実測 約 29 秒）、ConversionService.Tests 全体の律速になっていた。
// 入れ子のクラスは別のコレクションになり、互いに並列に走る。各試験の中身（期限・待ち・検査）は分ける前と同一である。
// 共有の状態は無い（プロセス番号のファイル・原本は試験ごとに GUID の別名。補助は状態を持たない static）。
public static class ExternalProcessTimeoutTests
{
    // 止まる命令の起動（Windows の powershell は起動に数秒かかることがある）が期限の内に番号を書けるだけの長さ。
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 5 : 2);

    // 試験そのものの上限。期限を外した変異（M2）はここで止まり、`BodyConversionTimeoutException` にならない。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    // 止めたプロセスが消えるまで待つ上限（刈り取りは実行器の中で済んでいる。孫は init が刈る）。
    private static readonly TimeSpan GoneWithin = TimeSpan.FromSeconds(10);

    private static DiagramCodingLimits Limits(TimeSpan bodyTimeout) =>
        DiagramCodingLimits.Default with { BodyConversionTimeout = bodyTimeout };

    private static PandocConversionService Pandoc(TimeSpan bodyTimeout, Func<ProcessStartInfo> conversion) =>
        new(new NoStorage(), Options.Create(new ConversionOptions()), Limits(bodyTimeout),
            NullLogger<PandocConversionService>.Instance)
        {
            StartInfoFilter = psi => IsVersionProbe(psi) ? Stub.Print("pandoc 3.1.1") : conversion()
        };

    private static PdfTextLayerConverter PdfToText(TimeSpan bodyTimeout, Func<ProcessStartInfo> conversion) =>
        new(new NoStorage(), Options.Create(new ConversionOptions()), Limits(bodyTimeout),
            NullLogger<PdfTextLayerConverter>.Instance)
        {
            StartInfoFilter = psi => IsVersionProbe(psi) ? Stub.Print("pdftotext version 24.02.0") : conversion()
        };

    // 版の確かめ（`pandoc --version` / `pdftotext -v`）は引数の文字列で起動している。
    private static bool IsVersionProbe(ProcessStartInfo psi) => psi.Arguments is "--version" or "-v";

    // T-50 (1)〜(3): 止まった変換器を期限・呼び出し元の取り消しで子孫ごと止める。
    [Trait("TestKind", "Integration")]
    public sealed class HungConverter
    {
        // T-50 (1): 止まった pandoc は期限で止められ、子孫ごと消える。例外は期限切れ（取り消しではない）。
        [Fact]
        public async Task Hung_pandoc_is_killed_with_its_process_tree_at_the_timeout()
        {
            using var source = new TempSource(".md");
            using var pids = new PidFile();
            var service = Pandoc(HangTimeout, () => Stub.HangWithChild(pids.Path));

            var started = Stopwatch.GetTimestamp();
            var act = () => service.ConvertAsync(source.Uri, "text/markdown", TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            var thrown = (await act.Should().ThrowExactlyAsync<BodyConversionTimeoutException>()).Which;
            var elapsed = Stopwatch.GetElapsedTime(started);

            thrown.Tool.Should().Be("pandoc");
            thrown.Timeout.Should().Be(HangTimeout);
            thrown.Message.Should().Contain("pandoc").And.Contain(DiagramCodingLimits.BodyConversionTimeoutKey);
            elapsed.Should().BeGreaterThanOrEqualTo(HangTimeout - TimeSpan.FromMilliseconds(100));
            await pids.ShouldAllBeGoneAsync();
        }

        // T-50 (2): pdftotext も同じ実行器を通る（ADR-0070 決定 2 の外部プロセス）。
        [Fact]
        public async Task Hung_pdftotext_is_killed_with_its_process_tree_at_the_timeout()
        {
            using var source = new TempSource(".pdf");
            using var pids = new PidFile();
            var converter = PdfToText(HangTimeout, () => Stub.HangWithChild(pids.Path));

            var act = () => converter.ConvertAsync(source.Uri, "application/pdf", TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            (await act.Should().ThrowExactlyAsync<BodyConversionTimeoutException>()).Which.Tool.Should().Be("pdftotext");
            await pids.ShouldAllBeGoneAsync();
        }

        // T-50 (3)（(1) の対照）: 呼び出し元（受け口の ct）の取り消しでも子孫ごと止め、**取り消しとして**外へ出す
        // （期限切れへ読み替えない）。期限（60 秒）は取り消しより十分遅い。
        [Fact]
        public async Task Caller_cancellation_kills_the_pandoc_process_tree_and_propagates()
        {
            using var source = new TempSource(".md");
            using var pids = new PidFile();
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var service = Pandoc(TimeSpan.FromSeconds(60), () => Stub.HangWithChild(pids.Path));

            var conversion = service.ConvertAsync(source.Uri, "text/markdown", caller.Token);
            await pids.WaitForBothAsync(Guard);
            var cancelled = Stopwatch.GetTimestamp();
            await caller.CancelAsync();

            var act = () => conversion.WaitAsync(Guard, TestContext.Current.CancellationToken);

            var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
            thrown.CancellationToken.Should().Be(caller.Token);
            Stopwatch.GetElapsedTime(cancelled).Should().BeLessThan(TimeSpan.FromSeconds(20));
            await pids.ShouldAllBeGoneAsync();
        }
    }

    // T-50 (4): 期限切れのジョブを失敗として記録する。
    [Trait("TestKind", "Integration")]
    public sealed class TimedOutJob
    {
        // T-50 (4): 期限切れのジョブは失敗として記録され、理由（道具名・構成鍵）が変換ジョブの失敗理由に残る。
        // 例外は再送出される（再試行 → デッドレターの扱いは不変。UC-06 例外フロー）。受け口から本文変換までは本物である。
        [Fact]
        public async Task Timed_out_conversion_is_recorded_as_a_failed_job_with_the_reason()
        {
            using var source = new TempSource(".md");
            using var pids = new PidFile();
            var normalizer = new NormalizationService(
                Pandoc(HangTimeout, () => Stub.HangWithChild(pids.Path)), new NeverCalledCoder(), new NeverCalledStore(),
                NullLogger<NormalizationService>.Instance, Limits(HangTimeout));
            var options = new DbContextOptionsBuilder<ConversionJobDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            var ev = new RawDocumentFetched(Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/hung.md", source.Uri,
                "text/markdown", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

            await using (var db = new ConversionJobDbContext(options))
            {
                var consumer = new RawDocumentFetchedConsumer(normalizer, new RecordingDocumentNormalizedPublisher(),
                    new EfConversionJobStore(db), NullLogger<RawDocumentFetchedConsumer>.Instance);
                var act = () => consumer.Handle(ev, new Envelope { Attempts = 1 }, TestContext.Current.CancellationToken)
                    .WaitAsync(Guard, TestContext.Current.CancellationToken);

                await act.Should().ThrowExactlyAsync<BodyConversionTimeoutException>();
            }

            await using var readDb = new ConversionJobDbContext(options);
            var job = (await new EfConversionJobStore(readDb).GetAsync(ev.FetchId, TestContext.Current.CancellationToken))!;
            job.Status.Should().Be(ConversionJobStatus.Failed);
            job.Error.Should().Contain("pandoc").And.Contain(DiagramCodingLimits.BodyConversionTimeoutKey);
            job.DeadLettered.Should().BeFalse("初回の試行の後には再試行が残っている");
            await pids.ShouldAllBeGoneAsync();
        }
    }

    // T-50 (5)〜(6): 正常系・非 0 終了は不変。
    [Trait("TestKind", "Integration")]
    public sealed class NormalExit
    {
        // T-50 (5): 正常系は不変 —— 0 終了なら標準出力がそのまま本文になる（期限の内に終わる命令）。
        [Fact]
        public async Task Normal_pandoc_output_becomes_the_body()
        {
            using var source = new TempSource(".md");
            var service = Pandoc(TimeSpan.FromSeconds(30), () => Stub.Print("# stub-body"));

            var result = await service.ConvertAsync(source.Uri, "text/markdown", TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            result.Markdown.Should().Contain("# stub-body");
            result.Figures.Should().BeEmpty();
        }

        // T-50 (6): 非 0 終了は従前どおり `InvalidOperationException`（終了コードと標準エラーを含む）。期限切れと混ぜない。
        [Fact]
        public async Task Non_zero_exit_keeps_the_existing_failure()
        {
            using var source = new TempSource(".md");
            var service = Pandoc(TimeSpan.FromSeconds(30), Stub.FailWithCode3);

            var act = () => service.ConvertAsync(source.Uri, "text/markdown", TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            (await act.Should().ThrowExactlyAsync<InvalidOperationException>())
                .WithMessage("pandoc exited with code 3 for *boom*");
        }
    }

    // T-50 (7): 版の確認の時間切れ。
    [Trait("TestKind", "Integration")]
    public sealed class VersionProbeTimeout
    {
        // T-50 (7) #1654 B: 版の確認の時間切れは Warning を残して「無い」（null）に倒す。止まっているだけの道具を、
        // ログ無しで「実行時イメージに無い」と報告しない。止めたプロセスは子孫ごと残らない。
        [Fact]
        public async Task Version_probe_timeout_is_logged_as_a_warning_for_pandoc()
        {
            using var pids = new PidFile();
            var logger = new RecordingLogger();

            var version = await PandocConversionService.TryGetPandocVersionAsync(TestContext.Current.CancellationToken,
                    _ => Stub.HangWithChild(pids.Path), logger, HangTimeout)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            version.Should().BeNull();
            logger.Warnings.Should().ContainSingle().Which.Should().Contain("pandoc").And.Contain("hung");
            await pids.ShouldAllBeGoneAsync();
        }

        [Fact]
        public async Task Version_probe_timeout_is_logged_as_a_warning_for_pdftotext()
        {
            using var pids = new PidFile();
            var logger = new RecordingLogger();

            var version = await PdfTextLayerConverter.TryGetPdfToTextVersionAsync(TestContext.Current.CancellationToken,
                    _ => Stub.HangWithChild(pids.Path), logger, HangTimeout)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            version.Should().BeNull();
            logger.Warnings.Should().ContainSingle().Which.Should().Contain("pdftotext").And.Contain("hung");
            await pids.ShouldAllBeGoneAsync();
        }
    }

    // T-50 (8): 自分で終わったプロセスの出力を、孫が握っていても捨てない。
    [Trait("TestKind", "Integration")]
    public sealed class ExitedBeforeTimeout
    {
        // T-50 (8) #1654 C（終了と時間切れの競合）: プロセスは期限の前に**自分で**終わったが、ツリーの外の孫が標準出力を
        // 期限の少し後まで握っていた。読み取りの完了が期限を跨いでも、正しい出力を捨てず結果として返す（kill しない）。
        [Fact]
        public async Task Output_is_kept_when_the_process_exited_but_a_grandchild_briefly_held_stdout()
        {
            using var pids = new PidFile();
            // 期限は止まる命令の起動（Windows の powershell は負荷下で 1 秒を超える）より長くとる。期限の時点で親がまだ走っていると
            // kill の枝へ入り、この試験が測りたい「自分で終わっていた」枝を通らない（1 秒の期限で全体実行の負荷下に実測した揺らぎ）。
            // 孫は期限の 3 秒後まで握る（刈り取りの上限 10 秒の内）。
            var timeout = HangTimeout;
            var holdSeconds = (int)timeout.TotalSeconds + 3;
            var started = Stopwatch.GetTimestamp();

            var result = await ExternalProcess.RunAsync(Stub.PrintAndDetachChild("stub-out", holdSeconds, pids.Path), "pandoc",
                    timeout, NullLogger.Instance, TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Contain("stub-out");
            // 孫が握っている間は読み取りが終わらない —— 期限を跨いでから返ったことを確かめる。
            Stopwatch.GetElapsedTime(started).Should().BeGreaterThan(timeout);
        }
    }

    // T-50 (9): 孤児の孫は刈り取りの上限で諦める。
    // 本ファイルで最も長く実時間を待つ（期限 ＋ 刈り取りの上限）。最初に起動させる（#1686。`StartsFirstCollectionOrderer`）。
    [Trait("TestKind", "Integration")]
    [Collection(DetachedGrandchildCollection.Name)]
    public sealed class DetachedGrandchild
    {
        // T-50 (9) #1654 C（孤児の孫）: 親が終わった後、ツリーの外の孫が標準出力を握り続ける。孫は `Kill(true)` では止まらないので、
        // 期限 ＋ 刈り取りの上限で時間切れとして終わる（待ち続けない）。孫は試験の後始末で止める。
        [Fact]
        public async Task Detached_grandchild_holding_stdout_times_out_within_the_reap_cap()
        {
            using var pids = new PidFile();
            var started = Stopwatch.GetTimestamp();

            var act = () => ExternalProcess.RunAsync(Stub.PrintAndDetachChild("stub-out", 600, pids.Path), "pandoc",
                    HangTimeout, NullLogger.Instance, TestContext.Current.CancellationToken)
                .WaitAsync(Guard, TestContext.Current.CancellationToken);

            (await act.Should().ThrowExactlyAsync<BodyConversionTimeoutException>()).Which.Timeout.Should().Be(HangTimeout);
            // 刈り取りの上限まで読み取りを待ってから諦める（期限そのものでは諦めない）。諦めた後は、孫（600 秒握る）を待たずに返る
            // （#1654 L1: 諦めた読み取りは手放す。上限は負荷の揺らぎを見込んで 10 秒の余裕を置く）。
            Stopwatch.GetElapsedTime(started).Should()
                .BeGreaterThanOrEqualTo(HangTimeout + ExternalProcess.ReapTimeout - TimeSpan.FromMilliseconds(200))
                .And.BeLessThan(HangTimeout + ExternalProcess.ReapTimeout + TimeSpan.FromSeconds(10));
        }
    }

    [CollectionDefinition(Name)]
    [StartsFirst]
    public sealed class DetachedGrandchildCollection
    {
        public const string Name = "ExternalProcessTimeoutTests.DetachedGrandchild";
    }

    // 代わりに起動する命令。いずれも標準出力・標準エラーをリダイレクトする（実行器の前提）。
    private static class Stub
    {
        public static ProcessStartInfo Print(string text) => OperatingSystem.IsWindows()
            ? Start("cmd.exe", "/c", "echo " + text)
            : Start("sh", "-c", "printf '%s\\n' \"$1\"", "sh", text);

        public static ProcessStartInfo FailWithCode3() => OperatingSystem.IsWindows()
            ? Start("cmd.exe", "/c", "echo boom 1>&2 & exit /b 3")
            : Start("sh", "-c", "echo boom >&2; exit 3");

        // 子を 1 つ起動して止まる。自分と子のプロセス番号を空白区切りでファイルへ書く。
        // 子は標準出力のパイプを受け継ぐので、親だけ止めても読み取りは終わらない（実物の pandoc の補助プロセスと同じ形）。
        public static ProcessStartInfo HangWithChild(string pidFile) => OperatingSystem.IsWindows()
            ? Start("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                "$c = Start-Process -FilePath ping.exe -ArgumentList '-n','600','127.0.0.1' -NoNewWindow -PassThru; "
                + $"Set-Content -LiteralPath '{pidFile}' -Value ('{{0}} {{1}}' -f $PID, $c.Id); $c.WaitForExit()")
            : Start("sh", "-c", "sleep 600 & echo \"$$ $!\" > \"$1\"; wait", "sh", pidFile);

        // #1654 C: 孫をツリーの外へ出して（親は待たずに）すぐ終わる。孫は標準出力のパイプを受け継いで seconds 秒握る。
        // 自分と孫のプロセス番号を空白区切りでファイルへ書く。Linux はサブシェルで孫を init へ付け替える。
        public static ProcessStartInfo PrintAndDetachChild(string text, int seconds, string pidFile) =>
            OperatingSystem.IsWindows()
                ? Start("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                    $"$c = Start-Process -FilePath ping.exe -ArgumentList '-n','{seconds + 1}','127.0.0.1' -NoNewWindow -PassThru; "
                    + $"Set-Content -LiteralPath '{pidFile}' -Value ('{{0}} {{1}}' -f $PID, $c.Id); Write-Output '{text}'")
                : Start("sh", "-c", $"(sleep {seconds} & echo \"$$ $!\" > \"$1\"); echo {text}", "sh", pidFile);

        private static ProcessStartInfo Start(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return psi;
        }
    }

    // 止まる命令が書いたプロセス番号（親・子）。後始末で残っていれば止める（変異で kill を外したときに 600 秒残さない）。
    private sealed class PidFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"conv-pids-{Guid.NewGuid():N}.txt");

        public async Task<int[]> WaitForBothAsync(TimeSpan within)
        {
            var until = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
            {
                var pids = TryRead();
                if (pids.Length == 2) return pids;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException($"stub did not write its process ids to {Path} within {within}");
        }

        public async Task ShouldAllBeGoneAsync()
        {
            var pids = TryRead();
            pids.Should().HaveCount(2, "止まる命令は期限の前に自分と子の番号を書いているはずである");
            var until = Stopwatch.GetTimestamp() + (long)(GoneWithin.TotalSeconds * Stopwatch.Frequency);
            while (pids.Any(IsAlive) && Stopwatch.GetTimestamp() < until)
                await Task.Delay(100, TestContext.Current.CancellationToken);
            pids.Where(IsAlive).Should().BeEmpty("親も子も止められ、プロセスは 1 つも残らない");
        }

        private int[] TryRead()
        {
            try
            {
                var parts = File.ReadAllText(Path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b)
                    ? [a, b]
                    : [];
            }
            catch (IOException) { return []; }
        }

        public void Dispose()
        {
            foreach (var pid in TryRead().Where(IsAlive))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
                { }
            }
            try { File.Delete(Path); }
            catch (IOException) { }
        }
    }

    // 生きているか。Linux では刈り取られていない終了済み（zombie）も「残っていない」と数える
    // （孫は親が止まると init の子になり、init が刈るまで一瞬 zombie で見える）。
    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var state = stat[stat.LastIndexOf(')') + 2];
                return state is not ('Z' or 'X');
            }
            catch (IOException) { return false; }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        { return false; }
    }

    // 変換器へ渡す原本（ローカルファイル。中身は使われない —— 命令は差し替えてある）。
    private sealed class TempSource : IDisposable
    {
        private readonly string _path;

        public TempSource(string extension)
        {
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"conv-src-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(_path, "# source\n");
        }

        public string Uri => new Uri(_path).AbsoluteUri;

        public void Dispose()
        {
            try { File.Delete(_path); }
            catch (IOException) { }
        }
    }

    private sealed class NoStorage : IObjectStorageClient
    {
        public Task<string> PutTextAsync(string key, string text, string contentType,
            CancellationToken ct = default) => throw new NotSupportedException(key);

        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default) => throw new NotSupportedException(key);

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) =>
            throw new NotSupportedException(uri);

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) =>
            throw new NotSupportedException(uri);

        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

        public bool CanResolve(string? uri) => false;

        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) =>
            throw new NotSupportedException(uri);
    }

    // Warning 以上の記録だけを取る logger（#1654 B）。
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return [.. _warnings]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }

    private sealed class NeverCalledCoder : IDiagramCoder
    {
        public Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
            CancellationToken ct = default) => throw new UnreachableException("本文変換で止まるので図は無い");
    }

    private sealed class NeverCalledStore : IObjectStore
    {
        public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default) =>
            throw new UnreachableException("本文変換で止まるので保管しない");

        public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default) => throw new UnreachableException("本文変換で止まるので保管しない");

        public Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default) =>
            throw new UnreachableException("読み戻しは無い");
    }
}
