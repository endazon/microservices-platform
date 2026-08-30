namespace IngestionService.Domain.Ports;

// FR-02: テキストチャンク化ポート
public interface IChunkingService
{
    List<string> Chunk(string markdownText, int maxTokens = 512, int overlap = 50);
}
