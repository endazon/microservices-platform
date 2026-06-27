using System.Diagnostics;

namespace ConversionService.Worker.Services;

// FR-12, ADR-0012: pandoc を使って原本を Markdown へ変換する
// 実運用ではオブジェクトストレージから原本を取得してから変換する
public class PandocConversionService(ILogger<PandocConversionService> logger) : IConversionService
{
    public async Task<string> ConvertToMarkdownAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        // プレースホルダー：実装ではオブジェクトストレージからファイルをダウンロードし
        // pandoc コマンドで変換する。変換結果のストレージ URI を返す。
        logger.LogInformation("Converting {Uri} (contentType={ContentType})", storageUri, contentType);

        // pandoc が利用可能か確認し、ない場合はパススルー（開発環境での動作保証）
        var pandocAvailable = await CheckPandocAsync(ct);
        if (!pandocAvailable)
        {
            logger.LogWarning("pandoc not found; returning placeholder markdown URI");
            return storageUri.Replace("/raw/", "/normalized/") + ".md";
        }

        // 実際の変換処理（pandoc -f docx -t markdown）はここに実装する
        return storageUri.Replace("/raw/", "/normalized/") + ".md";
    }

    private static async Task<bool> CheckPandocAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("pandoc", "--version")
            {
                RedirectStandardOutput = true, UseShellExecute = false,
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
