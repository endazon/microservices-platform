using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Diagnostics;
using System.Text;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0070 決定 2・3, IADR-0356 (#1192): PDF の本文を**テキスト層の抽出器**で取り出す。
//
// pandoc は PDF を出力にはできるが入力には取れない（IADR-0320 決定 4）。ADR-0070 決定 2 は
// 「PDF はテキスト層の抽出器で本文を取り出し、Markdown 本文とする（poppler の pdftotext 相当）」と
// 裁定した。本クラスは poppler-utils の `pdftotext` を **pandoc と同じ型（外部プロセス）**で起動する。
// NuGet は足さない。抽出はコンテナ内でローカル完結し、外部送信を行わない（03_conversion-flow §補足）。
//
// 🔴 **テキスト層が無い PDF（スキャン等）は失敗ではない**（ADR-0070 決定 3）。抽出結果が空白のみで
// あることを確かめたうえで `HasBody = false` を返し、変換は「本文なし・原本参照のみ」として完了する。
// 再試行しても結果は変わらず、デッドレターに溜める価値も無い。
//
// 🔴 **fail-closed は「本文があるのに作れない」場合に限って維持する**（IADR-0320 決定 2 と同じ線）。
// pdftotext が実行時イメージに無い → 既定は `BodyConversionUnavailableException`。
// pdftotext が非 0 終了（壊れた PDF・暗号化）→ `InvalidOperationException` → 再試行 → デッドレター。
//
// 🔴 IADR-0008（2026-09-27 追記 / #1641）: pandoc と同じく**自前の期限つきで起動し、期限切れか呼び出し元の取り消しで
// プロセスツリーごと止める**（`ExternalProcess`。期限は `DiagramCodingLimits.BodyConversionTimeout`）。
public class PdfTextLayerConverter(
    IObjectStorageClient storage,
    IOptions<ConversionOptions> options,
    DiagramCodingLimits limits,
    ILogger<PdfTextLayerConverter> logger) : IBodyConverter
{
    private bool AllowDegraded => options.Value.AllowDegradedBodyConversion;

    // #1641: pdftotext 1 回の期限（`Conversion:BodyConversionTimeoutSeconds`。pandoc と同じ鍵）。
    internal TimeSpan ProcessTimeout => limits.BodyConversionTimeout;

    // 試験用の口（#1641）: 起動する命令を差し替える。本番は恒等（`pdftotext` をそのまま起動する）。
    internal Func<ProcessStartInfo, ProcessStartInfo> StartInfoFilter { get; init; } = static psi => psi;

    private readonly RawSourceResolver _resolver = new(storage, logger);

    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Extracting PDF text layer from {Uri} (contentType={ContentType})",
            storageUri, contentType);

        // 抽出器が利用可能か確認する。無いのは環境の欠陥であり、既定では失敗させる。
        if (await TryGetPdfToTextVersionAsync(ct, StartInfoFilter, logger) is null)
        {
            return Degrade(storageUri,
                $"pdftotext が実行時イメージに無い（{storageUri} の本文抽出ができない）。"
                + "実行時イメージへ poppler-utils を導入すること。");
        }

        var source = await _resolver.ResolveAsync(storageUri, contentType, ct);
        if (source is null)
        {
            return Degrade(storageUri,
                $"原本 {storageUri} を読み出せない（オブジェクトストレージ未構成、または未対応スキーム）。");
        }

        try
        {
            var raw = await RunPdfToTextAsync(source.Path, ct);
            var (markdown, hasBody) = ToBody(raw);
            if (!hasBody)
            {
                // ADR-0070 決定 3: テキスト層が無い。失敗として溜めず「本文なし」で完了させる。
                logger.LogInformation("pdftotext found no text layer in {Uri}: completing without a body",
                    storageUri);
            }
            else
            {
                logger.LogInformation("pdftotext extracted {Uri}: {Chars} chars", storageUri, markdown.Length);
            }
            // 図は抽出しない（PDF 内画像の図抽出は計画に無い）。
            return new BodyConversionResult(markdown, []) { HasBody = hasBody };
        }
        finally
        {
            source.Dispose();
        }
    }

    // IADR-0320 決定 2 と同じ分岐点。**既定は例外**である。
    // dev（AllowDegradedBodyConversion=true）だけがプレースホルダ本文へ縮退する。
    private BodyConversionResult Degrade(string storageUri, string reason)
    {
        if (!AllowDegraded) throw new BodyConversionUnavailableException(reason);

        logger.LogWarning("{Reason} Conversion:AllowDegradedBodyConversion=true のため"
            + " プレースホルダ本文へ縮退する（図0件）。", reason);
        var name = Path.GetFileNameWithoutExtension(RawSourceResolver.FileName(storageUri));
        return new BodyConversionResult(
            $"# {name}\n\n本文は {storageUri} から pdftotext で抽出します。", []);
    }

    // 原本を pdftotext でプレーンテキストへ落とし、標準出力を返す。
    // `-enc UTF-8` で符号化を固定し、`-nopgbrk` で改頁（\f）を出さない。出力先 `-` は標準出力。
    // 恒久失敗（非 0 終了）は例外を送出し、再試行→デッドレターへ委ねる（UC-06 例外フロー）。
    // #1641: 期限（`ProcessTimeout`）切れは `BodyConversionTimeoutException`、呼び出し元の取り消しは
    // `OperationCanceledException`。どちらもプロセスツリーを止めてから投げる（`ExternalProcess`）。
    private async Task<string> RunPdfToTextAsync(string sourcePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pdftotext")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-enc");
        psi.ArgumentList.Add("UTF-8");
        psi.ArgumentList.Add("-nopgbrk");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-");

        var result = await ExternalProcess.RunAsync(
            StartInfoFilter(psi), "pdftotext", ProcessTimeout, logger, ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"pdftotext exited with code {result.ExitCode} for {sourcePath}: {result.StandardError}");

        return result.StandardOutput;
    }

    /// <summary>
    /// FR-12, ADR-0070 決定 3, IADR-0356 (#1192): 抽出したプレーンテキストを本文 Markdown へ整え、
    /// **テキスト層の有無**を判定する。
    /// </summary>
    /// <remarks>
    /// 純関数にしてある —— pdftotext を実走できない環境でも、空判定の綴りを検査できる。
    /// **本文なしの判定は「空白のみ」である**（改行・改頁・空白・タブしか無い）。
    /// 1 文字でも可視の文字があれば本文ありとし、品質の判断はしない（PDF の本文品質は原本に依存する。
    /// 段組み・表・脚注はテキスト層の抽出で崩れることがある —— ADR-0070 §結果）。
    /// 整形は改行の正規化・行末空白の除去・3 連以上の空行の畳み込みだけで、Markdown の記法は
    /// エスケープしない（プレーンテキストは Markdown としてそのまま読める）。
    /// </remarks>
    internal static (string Markdown, bool HasBody) ToBody(string raw)
    {
        var text = (raw ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n');
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, HasBody: false);

        var sb = new StringBuilder(text.Length);
        var blankRun = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                // 2 つ目以降の連続する空行は畳む（段落の区切りは空行 1 つで足りる）。
                if (++blankRun >= 2) continue;
            }
            else
            {
                blankRun = 0;
            }
            sb.Append(trimmed).Append('\n');
        }
        return (sb.ToString().Trim('\n') + "\n", HasBody: true);
    }

    // IADR-0356 決定 7 / IADR-0320 決定 5 と同型: pdftotext の版（`pdftotext -v` の 1 行目）。
    // 取得できなければ null ＝**実行時イメージに pdftotext が無い**。readiness ヘルスチェックが同じ口を使う。
    //
    // 版の出力は**標準エラー**へ出る（標準出力は空）ので、両方読んで空でない側を採る。
    // 🔴 終了コードでは判定しない —— poppler の `pdftotext -v` は 0 で終わるが、同名の xpdf 版は 99 で終わる
    // （開発機で実測）。「版の行が出た」ことを在る証拠とし、終了コードは版の行が無いときだけ見る。
    //
    // #1641: 版の確かめも期限（`ExternalProcess.VersionProbeTimeout`）つきで起動し、期限切れ・取り消しではツリーごと止める。
    internal static async Task<string?> TryGetPdfToTextVersionAsync(CancellationToken ct,
        Func<ProcessStartInfo, ProcessStartInfo>? startInfoFilter = null, ILogger? logger = null)
    {
        try
        {
            var psi = new ProcessStartInfo("pdftotext", "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var result = await ExternalProcess.RunAsync(startInfoFilter?.Invoke(psi) ?? psi, "pdftotext",
                ExternalProcess.VersionProbeTimeout, logger ?? NullLogger.Instance, ct);
            var text = result.StandardError;
            if (string.IsNullOrWhiteSpace(text)) text = result.StandardOutput;
            var firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Contains("pdftotext", StringComparison.OrdinalIgnoreCase)) return firstLine;
            if (result.ExitCode != 0) return null;
            return firstLine.Length == 0 ? "pdftotext" : firstLine;
        }
        // #1641: 呼び出し元の取り消しは「pdftotext が無い」へ畳まずに外へ出す。
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
