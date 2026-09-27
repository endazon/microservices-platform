using ConversionService.Domain.Ports;
using System.Diagnostics;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0012, IADR-0008（2026-09-27 追記 / #1641）: 本文変換の外部プロセス（pandoc・pdftotext）を
// **自前の期限つきで**起動し、期限切れか呼び出し元の取り消しで**プロセスツリーごと止める**。
//
// 🔴 従前の 4 か所（pandoc の変換・版、pdftotext の抽出・版）は `WaitForExitAsync(ct)` で待つだけだった。ct が立つと
// 待ちを抜けるが、`using` の `Dispose` はハンドルを閉じるだけで**プロセスを止めない** —— 止まった pandoc は受け口の外で
// 走り続け、再試行の pandoc と並ぶ。期限も受け口の実行期限（300 秒・#1624）任せで、Inline の受け口
// （1 通ずつ処理）を 1 通で最大 4 × 300 ＋ 42 ≒ 1242 秒ふさいだ（#1641）。
internal static class ExternalProcess
{
    // 版の確かめ（`pandoc --version` / `pdftotext -v`）の期限。これで返らない環境は「無い」と同じに扱う。
    internal static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    // 止めたプロセスの刈り取りを待つ上限。越えたら警告ログを出して先へ進む（受け口をここで止めない）。
    internal static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(10);

    internal sealed record Result(int ExitCode, string StandardOutput, string StandardError);

    // psi は標準出力・標準エラーをリダイレクトしていること（`UseShellExecute = false`）。
    //
    // 例外:
    //   - `ct` が立った → `OperationCanceledException`（その `ct` を運ぶ）。**呼び出し元の取り消しを優先する**
    //     （期限と同時に立っても取り消しとして外へ出す。#1604 / #1621 と同じ境界）。
    //   - 期限が来た → `BodyConversionTimeoutException`（`TimeoutException` の派生。受け口が失敗として記録し、再試行へ委ねる）。
    // どちらの場合もプロセスツリーを止めて刈り取ってから投げる。
    // tool は例外・ログに出す道具名（pandoc / pdftotext）。試験が起動する命令を差し替えても名前は変わらない。
    internal static async Task<Result> RunAsync(ProcessStartInfo psi, string tool, TimeSpan timeout, ILogger logger,
        CancellationToken ct)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {tool} process");

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // デッドロック回避のため、待機前に stdout/stderr の読み取りを開始する。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new Result(proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await KillTreeAndReapAsync(proc, tool, logger);
            // 止めたプロセスの出力は使わない。子孫がパイプを握っていても待たないよう、観測だけして捨てる。
            Observe(stdoutTask);
            Observe(stderrTask);

            ct.ThrowIfCancellationRequested();
            throw new BodyConversionTimeoutException(tool, timeout);
        }
    }

    private static async Task KillTreeAndReapAsync(Process proc, string tool, ILogger logger)
    {
        try
        {
            // 🔴 **ツリーごと止める。** pandoc は補助プロセス（図の変換器等）を起動し得るうえ、親だけ止めると
            // 標準出力のパイプを握った子孫が残り、読み取りが終わらない。既に終わったプロセスには何もしない。
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            logger.LogWarning(ex, "Failed to kill {Tool} process tree", tool);
        }

        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(ReapTimeout);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Tool} process did not exit within {ReapTimeout} after kill", tool, ReapTimeout);
        }
    }

    private static void Observe(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
