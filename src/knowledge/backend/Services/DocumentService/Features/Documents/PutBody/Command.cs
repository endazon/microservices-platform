namespace DocumentService.Features.Documents.PutBody;

// FR-21, UC-03: 既存文書への本文投入リクエスト（`PUT /documents/{id}/body`）。
public record UpdateDocumentBodyRequest(string? Body);
