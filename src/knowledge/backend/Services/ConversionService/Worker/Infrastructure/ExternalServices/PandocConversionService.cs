using ConversionService.Worker.Domain.Ports;
using System.Diagnostics;

namespace ConversionService.Worker.Infrastructure.ExternalServices;

// FR-12, ADR-0012: pandoc を使って原本の本文を Markdown へ変換する。
// 実運用ではオブジェクトストレージから取得した原本を pandoc で本文 Markdown 化しつつ、
// --extract-media で図（画像）を抽出する。pandoc 未導入／原本がローカル解決不能な dev 環境では
// プレースホルダ本文へグレースフルデグレードする（図0件）。
public class PandocConversionService(ILogger<PandocConversionService> logger) : IBodyConverter
{
    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Converting body {Uri} (contentType={ContentType})", storageUri, contentType);

        // pandoc が利用可能か確認し、ない場合はプレースホルダ本文でデグレード（dev 環境での動作保証）。
        if (!await CheckPandocAsync(ct))
        {
            logger.LogWarning("pandoc not found; returning placeholder markdown body for {Uri}", storageUri);
            return Placeholder(storageUri);
        }

        // 原本をローカルの読み取り可能なファイルへ解決する。実オブジェクトストレージ（ADR-0014）の
        // クライアントは未実装のため file:// もしくはローカルパスのみ対応し、解決不能な場合はデグレードする。
        var sourcePath = ResolveLocalSource(storageUri);
        if (sourcePath is null)
        {
            logger.LogWarning(
                "Raw source {Uri} is not locally readable (object storage client not configured); " +
                "returning placeholder markdown body", storageUri);
            return Placeholder(storageUri);
        }

        // --extract-media 用の一時ディレクトリ。pandoc が本文中の画像を取り出す。
        var mediaDir = Path.Combine(Path.GetTempPath(),
            "conv-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
        Directory.CreateDirectory(mediaDir);
        try
        {
            // 本文を GFM へ変換し、--extract-media で取り出した画像を ExtractedFigure に写す。
            var markdown = await RunPandocAsync(sourcePath, contentType, mediaDir, ct);
            var figures = ExtractFigures(mediaDir);
            logger.LogInformation("pandoc converted {Uri}: {Chars} chars, {Figures} figures",
                storageUri, markdown.Length, figures.Count);
            return new BodyConversionResult(markdown, figures);
        }
        finally
        {
            try { Directory.Delete(mediaDir, recursive: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Failed to clean temp media dir {Dir}", mediaDir); }
        }
    }

    // pandoc 未導入／原本未解決時のデグレード本文（図0件）。dev 環境での動作保証。
    private static BodyConversionResult Placeholder(string storageUri)
    {
        var name = Path.GetFileNameWithoutExtension(storageUri);
        return new BodyConversionResult(
            $"# {name}\n\n本文は {storageUri} から pandoc で変換します。", []);
    }

    // 原本を pandoc で GitHub Flavored Markdown へ変換し、標準出力（本文）を返す。
    // 恒久失敗（非0終了）は例外を送出し、MassTransit の再試行→デッドレターへ委ねる（UC-06 例外フロー）。
    private async Task<string> RunPandocAsync(string sourcePath, string contentType, string mediaDir,
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
        psi.ArgumentList.Add(PandocInputFormat(contentType, sourcePath));
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
    private static string PandocInputFormat(string contentType, string sourcePath)
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
        }
        // contentType 不明（text/plain 等）時は拡張子から推定し、既定は markdown。
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".docx" or ".doc" => "docx",
            ".html" or ".htm" => "html",
            ".rtf" => "rtf",
            ".epub" => "epub",
            ".rst" => "rst",
            ".tex" or ".latex" => "latex",
            ".org" => "org",
            _ => "markdown"
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

    // 原本 URI をローカルの読み取り可能ファイルへ解決する。file:// とローカルパスのみ対応。
    // 実オブジェクトストレージ（storage://, s3:// 等）のクライアントは未実装（ADR-0014 製品未確定）。
    private static string? ResolveLocalSource(string storageUri)
    {
        if (string.IsNullOrWhiteSpace(storageUri)) return null;
        if (Uri.TryCreate(storageUri, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
                return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            // file 以外のスキーム（storage:// 等）はクライアント未実装のため解決不能。
            return null;
        }
        // スキーム無し＝ローカルパスとして扱う。
        return File.Exists(storageUri) ? storageUri : null;
    }

    private static async Task<bool> CheckPandocAsync(CancellationToken ct)
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
            if (proc is null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
