using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0012: pandoc を使って原本の本文を Markdown へ変換する。
// オブジェクトストレージから取得した原本を pandoc で本文 Markdown 化しつつ、
// --extract-media で図（画像）を抽出する。
//
// ADR-0070 決定 2 / IADR-0362 (#1192): **PDF は本クラスの担当ではない。** `FormatRoutingBodyConverter` が
// `PandocInputFormat` の戻り値（PDF は null）で `PdfTextLayerConverter` へ振り分ける。
//
// 🔴 IADR-0320 (#1097): **縮退の既定は fail-closed である。**
// 従前は pandoc 未導入・原本未解決のとき無条件にプレースホルダ本文（図0件）を返して「成功」しており、
// 実行時イメージが pandoc を持たなかったため **配備した実物でずっと縮退していた**。
// 縮退そのものは消していない（単体テストは pandoc の無い CI でも走る）が、
// ConversionOptions.AllowDegradedBodyConversion が true のときに限る。
public class PandocConversionService(
    IObjectStorageClient storage,
    IOptions<ConversionOptions> options,
    ILogger<PandocConversionService> logger) : IBodyConverter
{
    private bool AllowDegraded => options.Value.AllowDegradedBodyConversion;

    private readonly RawSourceResolver _resolver = new(storage, logger);

    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Converting body {Uri} (contentType={ContentType})", storageUri, contentType);

        // 形式判定を最初に行う。pandoc が入力に取れない未知の形式は**取り寄せる前に拒否する** ——
        // 既定の markdown へ落として pandoc に食わせると「pandoc exited with code 1」という
        // 原因の判らない失敗になり、意味の無い再試行を 4 回繰り返す（#1097）。
        // PDF（null）は `FormatRoutingBodyConverter` が抽出器へ振り分けるので、ここへ来るのは配線の誤りである。
        var inputFormat = PandocInputFormat(contentType, storageUri)
            ?? throw new InvalidOperationException(
                $"PDF は pandoc の担当ではない（{storageUri}）。FormatRoutingBodyConverter を経由すること。");

        // pandoc が利用可能か確認する。無いのは環境の欠陥であり、既定では失敗させる。
        if (!await CheckPandocAsync(ct))
        {
            return Degrade(storageUri,
                $"pandoc が実行時イメージに無い（{storageUri} の本文変換ができない）。"
                + "実行時イメージへ pandoc を導入すること。");
        }

        // 原本を pandoc が読めるローカルファイルへ解決する（オブジェクトストレージはここで取り寄せる）。
        var source = await _resolver.ResolveAsync(storageUri, contentType, ct);
        if (source is null)
        {
            return Degrade(storageUri,
                $"原本 {storageUri} を読み出せない（オブジェクトストレージ未構成、または未対応スキーム）。");
        }

        // --extract-media 用の一時ディレクトリ。pandoc が本文中の画像を取り出す。
        var mediaDir = Path.Combine(Path.GetTempPath(),
            "conv-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
        Directory.CreateDirectory(mediaDir);
        try
        {
            // 本文を GFM へ変換し、--extract-media で取り出した画像を ExtractedFigure に写す。
            var rawMarkdown = await RunPandocAsync(source.Path, inputFormat, mediaDir, ct);
            var media = ExtractMedia(mediaDir);

            // 🔴 IADR-0351 (#1120): **一時パスを本文へ残さない。**
            // pandoc は本文中の画像参照を mediaDir の絶対パスへ書き換えるが、下の finally が
            // そのディレクトリを消す —— 保管された時点で既に存在しない参照になる。
            // 図の位置を運ぶため、参照を `![fig-N](figure:fig-N)` の目印へ書き換えて返す。
            var markdown = RewriteExtractedMediaReferences(rawMarkdown, mediaDir, media, logger);
            var figures = media.Select(m => m.Figure).ToList();
            logger.LogInformation("pandoc converted {Uri}: {Chars} chars, {Figures} figures",
                storageUri, markdown.Length, figures.Count);
            return new BodyConversionResult(markdown, figures);
        }
        finally
        {
            try { Directory.Delete(mediaDir, recursive: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Failed to clean temp media dir {Dir}", mediaDir); }
            source.Dispose();
        }
    }

    // IADR-0320 決定 2: 変換器が動かせないときの分岐点。**既定は例外**である。
    // dev（AllowDegradedBodyConversion=true）だけがプレースホルダ本文へ縮退する。
    private BodyConversionResult Degrade(string storageUri, string reason)
    {
        if (!AllowDegraded) throw new BodyConversionUnavailableException(reason);

        logger.LogWarning("{Reason} Conversion:AllowDegradedBodyConversion=true のため"
            + " プレースホルダ本文へ縮退する（図0件）。", reason);
        return Placeholder(storageUri);
    }

    // pandoc 未導入／原本未解決時のデグレード本文（図0件）。**dev 限定**である（決定 2）。
    private static BodyConversionResult Placeholder(string storageUri)
    {
        var name = Path.GetFileNameWithoutExtension(storageUri);
        return new BodyConversionResult(
            $"# {name}\n\n本文は {storageUri} から pandoc で変換します。", []);
    }

    // 原本を pandoc で GitHub Flavored Markdown へ変換し、標準出力（本文）を返す。
    // 恒久失敗（非0終了）は例外を送出し、再試行→デッドレターへ委ねる（UC-06 例外フロー）。
    private async Task<string> RunPandocAsync(string sourcePath, string inputFormat, string mediaDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pandoc")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // 入力形式は contentType（不明なら拡張子）から判定し、出力は GFM。画像は --extract-media で取り出す。
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(inputFormat);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("gfm");
        psi.ArgumentList.Add("--extract-media");
        psi.ArgumentList.Add(mediaDir);
        psi.ArgumentList.Add(sourcePath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pandoc process");

        // デッドロック回避のため、待機前に stdout/stderr の読み取りを開始する。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var markdown = await stdoutTask;
        var error = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"pandoc exited with code {proc.ExitCode} for {sourcePath}: {error}");

        return markdown;
    }

    // --extract-media が書き出した画像ファイルを ExtractedFigure へ写す（決定的順序で採番）。
    //
    // IADR-0351 決定 2 (#1120): **書き出し元のパスを一緒に返す。** 従前は捨てていたため、
    // 本文中の参照（同じパスを指す）と図の対応が付かず、一時パスを目印へ書き換えられなかった。
    private static IReadOnlyList<ExtractedMedia> ExtractMedia(string mediaDir)
    {
        if (!Directory.Exists(mediaDir)) return [];
        var media = new List<ExtractedMedia>();
        var index = 0;
        foreach (var file in Directory.EnumerateFiles(mediaDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var figureContentType = ContentTypeFor(Path.GetExtension(file));
            if (figureContentType is null) continue; // 画像以外（媒体外ファイル）はスキップ。
            media.Add(new ExtractedMedia(file, new ExtractedFigure(
                FigureId: $"fig-{++index}",
                ImageContentType: figureContentType,
                ImageBytes: File.ReadAllBytes(file))
            {
                // ファイル名をコード化ヒント（Vision 未対応時のプロンプト材料）に使う。
                Caption = Path.GetFileNameWithoutExtension(file)
            }));
        }
        return media;
    }

    // --extract-media が書き出した媒体ファイルと、そこから起こした図の対。
    internal sealed record ExtractedMedia(string Path, ExtractedFigure Figure);

    // 画像構文まるごと（HTML の <img> タグ／Markdown の `![…](…)`）。
    // 🔴 **src 属性だけを差し替えない**（IADR-0351 決定 3）。`<img src="figure:fig-1" style="…">` を
    // 残すと `FigureMarkdown` の目印と一致せず、人手補正（TryReplaceImageWithCode）が空振りする。
    // docx 由来の <img> は属性が改行をまたぐ（実測）が、`[^>]` は改行にも当たるので拾える。
    private static readonly Regex ImageConstructPattern = new(
        @"<img\b[^>]*>|!\[[^\]]*\]\([^)]*\)",
        RegexOptions.IgnoreCase
        | RegexOptions.Compiled);

    // 画像構文から参照先 URL を取り出す（HTML は src 属性、Markdown は括弧の中の先頭トークン）。
    private static readonly Regex HtmlSrcPattern = new(
        @"\bsrc\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))",
        RegexOptions.IgnoreCase
        | RegexOptions.Compiled);

    private static readonly Regex MarkdownTargetPattern = new(
        @"^!\[[^\]]*\]\(\s*<?(?<u>[^)\s>]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// FR-12, UC-06, IADR-0351 (#1120): 本文中の <c>--extract-media</c> 由来の参照を、図の目印
    /// （<c>![fig-N](figure:fig-N)</c>）へ書き換える。**一時パスを 1 件も残さない**のが不変条件である。
    /// </summary>
    /// <remarks>
    /// 純関数にしてある —— pandoc を実走できない環境でも、pod で採取した**実出力**を入力に
    /// 綴りを検査できる（`PandocExtractedMediaRewriteTests` T-23〜T-28）。
    /// </remarks>
    internal static string RewriteExtractedMediaReferences(string markdown, string mediaDir,
        IReadOnlyList<ExtractedMedia> media, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        // mediaDir の綴り（区切り文字が処理系で変わる）と、媒体ファイル → figureId の写像。
        var prefixes = MediaDirPrefixes(mediaDir);
        var figureIdByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in media) figureIdByPath[NormalizeSeparators(m.Path)] = m.Figure.FigureId;

        // 1 パスで画像構文を走査する。Regex.Replace は自分の出力を再走査しないため、
        // ここで見える `figure:` は**必ず原本由来**である（IADR-0351 決定 5）。
        var rewritten = ImageConstructPattern.Replace(markdown, match =>
        {
            var url = ExtractUrl(match.Value);
            if (url is null) return match.Value;

            if (url.StartsWith(FigureMarkdown.PlaceholderScheme, StringComparison.Ordinal))
            {
                // 原本が目印の綴りを含んでいた。**受け取らない** —— 残すと正規化側が無関係の図を
                // そこへ差し込むか、解決できない参照が保管物へ残る。
                logger?.LogWarning("Dropped a source-authored figure placeholder reference {Url}", url);
                return string.Empty;
            }

            if (!PointsInto(url, prefixes)) return match.Value; // 媒体外の参照（外部 URL 等）は触らない。

            if (figureIdByPath.TryGetValue(NormalizeSeparators(url), out var figureId))
                return FigureMarkdown.PlaceholderEmbed(figureId);

            // 図として採らなかった媒体（画像でない拡張子）への参照。消える先を指すので落とす。
            logger?.LogWarning("Dropped a reference to non-figure extracted media {Url}", url);
            return string.Empty;
        });

        // IADR-0351 決定 4: 構文を認識できなかった参照の安全網。**一時パスは 1 件も残さない。**
        foreach (var prefix in prefixes)
        {
            if (!rewritten.Contains(prefix, StringComparison.Ordinal)) continue;
            logger?.LogWarning(
                "Residual --extract-media reference survived syntax rewriting in {MediaDir}", mediaDir);
            rewritten = new Regex(
                    Regex.Escape(prefix) + @"[^\s""'<>)\]]*")
                .Replace(rewritten, m =>
                    figureIdByPath.TryGetValue(NormalizeSeparators(m.Value), out var id)
                        ? FigureMarkdown.PlaceholderUri(id)
                        : string.Empty);
        }

        return rewritten;
    }

    // 画像構文（HTML / Markdown）から参照先 URL を取り出す。取れなければ null。
    private static string? ExtractUrl(string construct)
    {
        if (construct.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
        {
            var m = HtmlSrcPattern.Match(construct);
            return m.Success ? WebUtility.HtmlDecode(m.Groups["u"].Value) : null;
        }
        var md = MarkdownTargetPattern.Match(construct);
        return md.Success ? md.Groups["u"].Value : null;
    }

    // mediaDir の綴り（区切り文字違い）。Linux では 1 通りだが、Windows の開発機では両方出る。
    private static IReadOnlyList<string> MediaDirPrefixes(string mediaDir)
    {
        var trimmed = mediaDir.TrimEnd('/', '\\');
        return trimmed.Contains('\\', StringComparison.Ordinal)
            ? [trimmed, NormalizeSeparators(trimmed)]
            : [trimmed];
    }

    private static bool PointsInto(string url, IReadOnlyList<string> prefixes) =>
        prefixes.Any(p => url.StartsWith(p, StringComparison.Ordinal));

    // 写像の鍵は区切り文字を `/` へ寄せた綴りにする。
    // `Directory.EnumerateFiles` の返す綴りも pandoc の出す綴りも同じ mediaDir を前置に持つので、
    // 区切り文字さえ揃えれば一致する（絶対パス化は要らない）。
    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    // contentType（不明時は拡張子）から pandoc の入力形式を決める。**形式判定の単一情報源**である
    // （`FormatRoutingBodyConverter` も同じ関数で振り分ける。写像表を 2 箇所へ持たない）。
    //
    // 戻り値:
    //   - pandoc の `-f` 値 …… pandoc が読む形式
    //   - null …… **PDF**。pandoc は PDF を出力にはできるが入力には取れない（IADR-0320 決定 4）。
    //     ADR-0070 決定 2 / IADR-0362 (#1192) により**テキスト層の抽出器（`PdfTextLayerConverter`）へ振り分ける**。
    //     従前はここで `UnsupportedSourceFormatException` を投げて `failed` にしていたが、その裁定は覆った。
    //   - 例外 …… 🔴 **計画の対応形式表に無い未知の形式**（未知の MIME ＋未知の拡張子）。
    //     従前は既定の `markdown` へ落として pandoc に食わせていたが、対応していない形式が**静かに壊れた
    //     本文**になる（ADR-0070 決定 5「この既定に頼らない」）ため、取り寄せる前に拒否する。
    //
    // 取り込み形式の集合の正本は計画（06_technical/09_datasource-connectors §対応形式）である。
    // 表に無い形式（rtf / epub / rst / latex / org）の写像は従前からあるものを残しているだけで、
    // 取り込み側（`FileSystemConnector`）が列挙しないため実害は無い。**実装が独自に増減させない。**
    internal static string? PandocInputFormat(string contentType, string sourcePath)
    {
        switch (contentType.ToLowerInvariant())
        {
            case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
            case "application/msword":
                return "docx";
            case "text/html":
            case "application/xhtml+xml":
                return "html";
            case "text/markdown":
            case "text/x-markdown":
                return "gfm";
            case "text/plain":
                // 計画の対応形式表: `.txt` は「pandoc（markdown として読む）」。
                return "markdown";
            case "application/rtf":
            case "text/rtf":
                return "rtf";
            case "application/epub+zip":
                return "epub";
            case "text/x-rst":
                return "rst";
            case "application/x-latex":
            case "text/x-tex":
                return "latex";
            case "text/org":
                return "org";
            case "application/pdf":
            case "application/x-pdf":
                return null;
        }
        // contentType 不明（application/octet-stream 等）時は拡張子から推定する。
        var extension = Path.GetExtension(RawSourceResolver.FileName(sourcePath)).ToLowerInvariant();
        return extension switch
        {
            ".docx" or ".doc" => "docx",
            ".html" or ".htm" => "html",
            ".md" or ".markdown" => "gfm",
            ".txt" => "markdown",
            ".rtf" => "rtf",
            ".epub" => "epub",
            ".rst" => "rst",
            ".tex" or ".latex" => "latex",
            ".org" => "org",
            ".pdf" => null,
            _ => throw new UnsupportedSourceFormatException(
                $"対応形式表に無い形式である（contentType={contentType}, source={sourcePath}）。"
                + "取り込み形式の集合は計画（09_datasource-connectors §対応形式）が持つ。"
                + "広げるときは変換側の対応を同時に決めること（ADR-0070 決定 5）。")
        };
    }

    // 抽出画像の拡張子 → contentType。画像でない拡張子は null（図として扱わない）。
    private static string? ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".emf" => "image/emf",
        ".wmf" => "image/wmf",
        _ => null
    };

    // 原本の解決（オブジェクトストレージからの取り寄せ。IADR-0320 決定 3）は `RawSourceResolver` が持つ。
    // PDF の抽出器と同じ経路を使うため、本クラスから切り出した（IADR-0362）。

    private static async Task<bool> CheckPandocAsync(CancellationToken ct) =>
        await TryGetPandocVersionAsync(ct) is not null;

    // IADR-0320 決定 5 (#1097): pandoc の版（`pandoc --version` の 1 行目）。取得できなければ null
    // ＝**実行時イメージに pandoc が無い**。readiness ヘルスチェックが同じ口を使う。
    internal static async Task<string?> TryGetPandocVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("pandoc", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            // 待機前に読み始める（パイプが埋まると WaitForExit が返らない）。
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var text = await stdout;
            if (proc.ExitCode != 0) return null;
            var firstLine = text.Split('\n')[0].Trim();
            return firstLine.Length == 0 ? "pandoc" : firstLine;
        }
        catch { return null; }
    }

}
