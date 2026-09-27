using ConversionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;

namespace ConversionService.Infrastructure.Configuration;

// FR-12, UC-06, ADR-0012, ADR-0029, IADR-0008（2026-09-27 追記 / #1621）: 図のコード化の時間の上限。
//
// 🔴 **変換の受け口（`RawDocumentFetchedConsumer`）の ct は Wolverine が 1 通ごとに張る実行期限を含む。**
// Wolverine は受け口へ渡す ct を「実行期限の CTS」と「停止要求」の連結で作る（`DefaultExecutionTimeout` 既定 60 秒）。
// 図のコード化に呼び出しごとの期限が無いと、応答しない LLM ゲートウェイは **いつも受け口の ct が先に立つ形で**終わり、
// 「呼び出し元の取り消しだけを外へ出す」絞り（`!ct.IsCancellationRequested`）の縮退の枝には届かない —— ジョブは失敗し、
// 再試行を使い切ってデッドレターへ行く（#1621 の監査で実測）。そこで 3 つの上限を**この順に内側から**持つ:
//
//   1. **1 回の呼び出しの期限**（`Conversion:DiagramCodingTimeoutSeconds`・既定 20 秒）。REST は名前付きクライアントの
//      `HttpClient.Timeout`、gRPC は呼び出しの `Deadline` に**同じ値**を与える（輸送ごとに値を書き写さない）。
//   2. **1 文書あたりの図のコード化の総枠**（`Conversion:DiagramCodingBudgetSeconds`・既定 120 秒）。使い切ったら残りの図は
//      ゲートウェイを呼ばずに画像として残す。枠の判定は呼び出しの**前**に行うので、超過は最後の 1 回の期限（1.）までに収まる。
//   3. **受け口の実行期限**（`Conversion:HandlerTimeoutSeconds`・既定 300 秒）。Wolverine の `HandlerChain.ExecutionTimeoutInSeconds`
//      へ与える（`RawDocumentFetchedTimeoutPolicy`）。**2.＋1. を超えていなければ起動を止める** —— 図のコード化が受け口の期限を
//      食い尽くす構成を、実行時の縮退の失敗として初めて知ることにしない。既定では本文変換・保管に 300−(120＋20)＝160 秒が残る。
//
// いずれも 1 未満は 1 に丸める（#1604 の `Mcp:DeclarationTimeoutSeconds` と同じ扱い）。
//
// ［2026-09-27 追記 / #1641］4 つ目の上限として**本文変換の外部プロセス（pandoc・pdftotext）の期限**
// （`Conversion:BodyConversionTimeoutSeconds`・既定 90 秒）を持つ（`BodyConversionTimeout`）。期限で**プロセスツリーごと止める**
// （`ExternalProcess`）。起動時の検査は「受け口 ＞ 本文変換 ＋ 総枠 ＋ 1 回」へ広げる（従前の「受け口 ＞ 総枠 ＋ 1 回」を包含する）。
// 既定では 300 −（90 ＋ 120 ＋ 20）＝ 70 秒が残る。#1654 D でそのうち版の確認と刈り取りの上限（計 20 秒）も式へ入れた（下の `BodyConversionWorstCase`）ので、取り寄せ・保管・発行の余裕は 50 秒である。
// 型の名前は #1621 のまま残す（位置引数も変えない）—— 受け口の時間の上限を 1 か所で検査するための record である。
public sealed record DiagramCodingLimits(TimeSpan CallTimeout, TimeSpan Budget, TimeSpan HandlerTimeout)
{
    public const string CallTimeoutKey = "Conversion:DiagramCodingTimeoutSeconds";
    public const string BudgetKey = "Conversion:DiagramCodingBudgetSeconds";
    public const string HandlerTimeoutKey = "Conversion:HandlerTimeoutSeconds";
    public const string BodyConversionTimeoutKey = "Conversion:BodyConversionTimeoutSeconds";

    public const int DefaultCallTimeoutSeconds = 20;
    public const int DefaultBudgetSeconds = 120;
    public const int DefaultHandlerTimeoutSeconds = 300;
    public const int DefaultBodyConversionTimeoutSeconds = 90;

    // #1641: 本文変換の外部プロセス（pandoc・pdftotext）1 回の期限。1 文書は形式でどちらか一方だけを通る
    // （`FormatRoutingBodyConverter`）ので、鍵は 1 つで足りる。
    public TimeSpan BodyConversionTimeout { get; init; } = TimeSpan.FromSeconds(DefaultBodyConversionTimeoutSeconds);

    // ［2026-09-27 追記 / #1654 D］本文変換 1 回が受け口の時間を使い得る最悪。版の確認（固定 10 秒）→ 変換（期限）→
    // 刈り取り（上限 10 秒）の和である。版の確認が止まったときは 10 ＋ 10 秒で「無い」になり変換へ進まないので、この和を超えない。
    // 既定: 90 ＋ 10 ＋ 10 ＝ 110 秒 → 起動時の式は 300 ＞ 110 ＋ 120 ＋ 20 ＝ 250（余裕 50 秒）。
    public TimeSpan BodyConversionWorstCase =>
        BodyConversionTimeout + ExternalProcess.VersionProbeTimeout + ExternalProcess.ReapTimeout;

    public static DiagramCodingLimits Default { get; } = new(
        TimeSpan.FromSeconds(DefaultCallTimeoutSeconds),
        TimeSpan.FromSeconds(DefaultBudgetSeconds),
        TimeSpan.FromSeconds(DefaultHandlerTimeoutSeconds));

    // 構成から読む。受け口の期限が「本文変換の最悪（期限＋版の確認＋刈り取り）＋総枠＋1 回の期限」を超えていなければ
    // InvalidOperationException（起動失敗）。
    public static DiagramCodingLimits From(IConfiguration configuration)
    {
        var limits = new DiagramCodingLimits(
            Seconds(configuration, CallTimeoutKey, DefaultCallTimeoutSeconds),
            Seconds(configuration, BudgetKey, DefaultBudgetSeconds),
            Seconds(configuration, HandlerTimeoutKey, DefaultHandlerTimeoutSeconds))
        {
            BodyConversionTimeout = Seconds(configuration, BodyConversionTimeoutKey, DefaultBodyConversionTimeoutSeconds),
        };

        if (limits.HandlerTimeout <= limits.BodyConversionWorstCase + limits.Budget + limits.CallTimeout)
            throw new InvalidOperationException(
                $"{HandlerTimeoutKey}（{limits.HandlerTimeout.TotalSeconds} 秒）は "
                + $"{BodyConversionTimeoutKey}（{limits.BodyConversionTimeout.TotalSeconds} 秒）"
                + $"＋ 版の確認（{ExternalProcess.VersionProbeTimeout.TotalSeconds} 秒）"
                + $"＋ 刈り取りの上限（{ExternalProcess.ReapTimeout.TotalSeconds} 秒）"
                + $"＋ {BudgetKey}（{limits.Budget.TotalSeconds} 秒）"
                + $"＋ {CallTimeoutKey}（{limits.CallTimeout.TotalSeconds} 秒）より長くなければならない。"
                + " 本文変換と図のコード化が受け口の実行期限を食い尽くすと、止まった pandoc や応答しない LLM ゲートウェイが"
                + "自前の期限ではなく受け口の取り消しとして終わり、変換ジョブの失敗理由が失われる。");

        return limits;
    }

    private static TimeSpan Seconds(IConfiguration configuration, string key, int defaultSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>(key) ?? defaultSeconds));
}
