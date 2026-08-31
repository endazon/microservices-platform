using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Diagnostics;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0012: pandoc を使って原本の本文を Markdown へ変換する。
// オブジェクトストレージから取得した原本を pandoc で本文 Markdown 化しつつ、
// --extract-media で図（画像）を抽出する。
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

    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Converting body {Uri} (contentType={ContentType})", storageUri, contentType);

        // 形式判定を最初に行う。pandoc が入力に取れない形式（PDF）は**取り寄せる前に拒否する** ——
        // 既定の markdown へ落として pandoc に食わせると「pandoc exited with code 1」という
        // 原因の判らない失敗になり、意味の無い再試行を 4 回繰り返す（#1097）。
        var inputFormat = PandocInputFormat(contentType, storageUri);

        // pandoc が利用可能か確認する。無いのは環境の欠陥であり、既定では失敗させる。
        if (!await CheckPandocAsync(ct))
        {
            return Degrade(storageUri,
                $"pandoc が実行時イメージに無い（{storageUri} の本文変換ができない）。"
                + "実行時イメージへ pandoc を導入すること。");
        }

        // 原本を pandoc が読めるローカルファイルへ解決する（オブジェクトストレージはここで取り寄せる）。
        var source = await ResolveSourceAsync(storageUri, contentType, ct);
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
            var markdown = await RunPandocAsync(source.Path, inputFormat, mediaDir, ct);
            var figures = ExtractFigures(mediaDir);
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
    private static IReadOnlyList<ExtractedFigure> ExtractFigures(string mediaDir)
    {
        if (!Directory.Exists(mediaDir)) return [];
        var figures = new List<ExtractedFigure>();
        var index = 0;
        foreach (var file in Directory.EnumerateFiles(mediaDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var figureContentType = ContentTypeFor(Path.GetExtension(file));
            if (figureContentType is null) continue; // 画像以外（媒体外ファイル）はスキップ。
            figures.Add(new ExtractedFigure(
                FigureId: $"fig-{++index}",
                ImageContentType: figureContentType,
                ImageBytes: File.ReadAllBytes(file))
            {
                // ファイル名をコード化ヒント（Vision 未対応時のプロンプト材料）に使う。
                Caption = Path.GetFileNameWithoutExtension(file)
            });
        }
        return figures;
    }

    // contentType（不明時は拡張子）から pandoc の入力形式を決める。
    //
    // 🔴 IADR-0320 決定 4 (#1097): **pandoc が入力に取れない形式は既定へ落とさず拒否する。**
    // PDF は `FileSystemConnector` が列挙対象に含めており（`.pdf` → `application/pdf`）、
    // 従前はどの case にも当たらず拡張子判定の既定 `markdown` へ落ちていた。
    // 実 pandoc を入れると `pandoc -f markdown foo.pdf` が非0終了し、原因の判らない失敗になる。
    internal static string PandocInputFormat(string contentType, string sourcePath)
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
                throw UnsupportedPdf(contentType, sourcePath);
        }
        // contentType 不明（text/plain 等）時は拡張子から推定し、既定は markdown。
        return Path.GetExtension(SourceFileName(sourcePath)).ToLowerInvariant() switch
        {
            ".docx" or ".doc" => "docx",
            ".html" or ".htm" => "html",
            ".rtf" => "rtf",
            ".epub" => "epub",
            ".rst" => "rst",
            ".tex" or ".latex" => "latex",
            ".org" => "org",
            ".pdf" => throw UnsupportedPdf(contentType, sourcePath),
            _ => "markdown"
        };
    }

    private static UnsupportedSourceFormatException UnsupportedPdf(string contentType, string sourcePath) =>
        new($"PDF は pandoc の入力形式にならない（contentType={contentType}, source={sourcePath}）。"
            + "本文抽出の経路が決まるまで、この原本は変換できない。");

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

    // 原本 URI をローカルの読み取り可能ファイルへ解決する。
    //
    // 🔴 IADR-0320 決定 3 (#1097): **オブジェクトストレージの原本を取り寄せる。**
    // 従前は file スキームとローカルパスしか解決できず、`DataSourceSyncService` が発行する
    // storage:// の原本は常に解決不能だった —— つまり pandoc を入れても縮退したままだった。
    private async Task<ResolvedSource?> ResolveSourceAsync(string storageUri, string contentType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageUri)) return null;

        // オブジェクトストレージ上の原本は一時ファイルへ落として pandoc に食わせる。
        if (storage.CanResolve(storageUri))
        {
            var bytes = await storage.GetBytesAsync(storageUri, ct);
            var temp = Path.Combine(Path.GetTempPath(),
                $"conv-src-{Guid.NewGuid():N}{SourceExtension(storageUri, contentType)}");
            await File.WriteAllBytesAsync(temp, bytes, ct);
            logger.LogInformation("Fetched raw source {Uri} ({Bytes} bytes) for conversion",
                storageUri, bytes.Length);
            return new ResolvedSource(temp, IsTemporary: true);
        }

        if (Uri.TryCreate(storageUri, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
                return File.Exists(uri.LocalPath) ? new ResolvedSource(uri.LocalPath, false) : null;
            // storage:// を実クライアントが解決できない（未構成）場合はここへ来る。
            return null;
        }
        // スキーム無し＝ローカルパスとして扱う。
        return File.Exists(storageUri) ? new ResolvedSource(storageUri, false) : null;
    }

    // 一時ファイルの拡張子。pandoc は `-f` を明示するので必須ではないが、
    // 失敗時のログとダンプが読めるように原本の形を残す。
    private static string SourceExtension(string storageUri, string contentType)
    {
        var fromUri = Path.GetExtension(SourceFileName(storageUri));
        if (!string.IsNullOrEmpty(fromUri)) return fromUri;
        return contentType.ToLowerInvariant() switch
        {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "text/html" or "application/xhtml+xml" => ".html",
            "application/pdf" => ".pdf",
            _ => ".bin"
        };
    }

    // storage://bucket/key/raw.docx のようにスキーム付きでも拡張子を取れるようにする
    // （Path.GetExtension は URI のクエリ・フラグメントを知らないので、パス部だけを見る）。
    private static string SourceFileName(string uriOrPath) =>
        Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.AbsolutePath
            : uriOrPath;

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

    // 解決した原本。オブジェクトストレージから取り寄せたものは変換後に消す。
    private sealed record ResolvedSource(string Path, bool IsTemporary) : IDisposable
    {
        public void Dispose()
        {
            if (!IsTemporary) return;
            try { File.Delete(Path); }
            catch (IOException) { /* 一時ファイルの後始末は best-effort。 */ }
            catch (UnauthorizedAccessException) { /* 同上。 */ }
        }
    }
}
