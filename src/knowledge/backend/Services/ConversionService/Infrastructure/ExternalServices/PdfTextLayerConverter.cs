using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
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
public class PdfTextLayerConverter(
    IObjectStorageClient storage,
    IOptions<ConversionOptions> options,
    ILogger<PdfTextLayerConverter> logger) : IBodyConverter
{
    private bool AllowDegraded => options.Value.AllowDegradedBodyConversion;

    private readonly RawSourceResolver _resolver = new(storage, logger);

    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Extracting PDF text layer from {Uri} (contentType={ContentType})",
            storageUri, contentType);

        // 抽出器が利用可能か確認する。無いのは環境の欠陥であり、既定では失敗させる。
        if (await TryGetPdfToTextVersionAsync(ct) is null)
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
    private static async Task<string> RunPdfToTextAsync(string sourcePath, CancellationToken ct)
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pdftotext process");

        // デッドロック回避のため、待機前に stdout/stderr の読み取りを開始する。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var text = await stdoutTask;
        var error = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"pdftotext exited with code {proc.ExitCode} for {sourcePath}: {error}");

        return text;
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
    internal static async Task<string?> TryGetPdfToTextVersionAsync(CancellationToken ct)
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
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            // 待機前に読み始める（パイプが埋まると WaitForExit が返らない）。
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var text = await stderr;
            if (string.IsNullOrWhiteSpace(text)) text = await stdout;
            var firstLine = text.Split('\n')[0].Trim();
            if (firstLine.Contains("pdftotext", StringComparison.OrdinalIgnoreCase)) return firstLine;
            if (proc.ExitCode != 0) return null;
            return firstLine.Length == 0 ? "pdftotext" : firstLine;
        }
        catch { return null; }
    }
}
