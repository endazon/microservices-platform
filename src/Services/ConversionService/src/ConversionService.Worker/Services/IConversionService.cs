namespace ConversionService.Worker.Services;

// FR-12, ADR-0012: ファイル変換ポート（pandoc ラッパー）
public interface IConversionService
{
    Task<string> ConvertToMarkdownAsync(string storageUri, string contentType,
        CancellationToken ct = default);
}
