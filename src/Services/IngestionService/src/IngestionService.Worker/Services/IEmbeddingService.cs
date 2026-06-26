namespace IngestionService.Worker.Services;

// ADR-0013: 埋め込み生成ポート
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
