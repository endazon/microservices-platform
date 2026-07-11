namespace Platform.Shared.Contracts.Dtos;

public class ChunkDto
{
    public Guid ChunkId { get; init; }
    public Guid DocumentId { get; init; }
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = string.Empty;
    public float Score { get; init; }
    public string? SourceUri { get; init; }
}
