namespace DocumentService.Features.Documents.UpdateMetadata;

// FR-06, UC-03: メタデータ（属性・タグ）のみ更新するリクエスト
public record UpdateMetadataRequest(
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);
