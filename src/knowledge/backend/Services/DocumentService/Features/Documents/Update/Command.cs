namespace DocumentService.Features.Documents.Update;

public record UpdateDocumentRequest(
    string Title,
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);
