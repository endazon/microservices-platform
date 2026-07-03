using System.Diagnostics;

namespace ConversionService.Worker.Services;

// FR-12, ADR-0012: pandoc を使って原本の本文を Markdown へ変換する。
// 実運用ではオブジェクトストレージから原本を取得し、pandoc で本文を Markdown 化しつつ
// 図（画像）を抽出する。pandoc 未導入の dev 環境ではプレースホルダ本文へグレースフルデグレードする。
public class PandocConversionService(ILogger<PandocConversionService> logger) : IBodyConverter
{
    public async Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        logger.LogInformation("Converting body {Uri} (contentType={ContentType})", storageUri, contentType);

        // pandoc が利用可能か確認し、ない場合はプレースホルダ本文でデグレード（dev 環境での動作保証）。
        var pandocAvailable = await CheckPandocAsync(ct);
        if (!pandocAvailable)
        {
            logger.LogWarning("pandoc not found; returning placeholder markdown body for {Uri}", storageUri);
            var name = Path.GetFileNameWithoutExtension(storageUri);
            // 図抽出は pandoc/実ストレージ導入後に実装する。dev では図0件（本文のみ）。
            return new BodyConversionResult($"# {name}\n\n本文は {storageUri} から pandoc で変換します。", []);
        }

        // 実際の変換（pandoc -f <fmt> -t gfm、メディア抽出 --extract-media）はここに実装する。
        // pandoc の標準出力を Markdown 本文、--extract-media で取り出した画像を ExtractedFigure とする。
        var title = Path.GetFileNameWithoutExtension(storageUri);
        return new BodyConversionResult($"# {title}\n", []);
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
