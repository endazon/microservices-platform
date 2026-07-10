namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-12, UC-06, SC-07: 変換ジョブの状況 DTO（BFF ↔ SPA 契約）。ConversionService が保持する変換
// 読み取りモデルを表す。Status は queued / processing / succeeded / failed。失敗ジョブは Error を持つ。
public record ConversionJobDto(
    Guid Id,
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string Status,
    string? Error,
    Guid? DocumentId,
    string? MarkdownUri,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// FR-12, UC-06, SC-07: 変換ジョブの状態値。
public static class ConversionJobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}
