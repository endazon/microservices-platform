namespace DocumentService.Features.Documents.GrantShare;

// FR-20, ADR-0036 D-06: 共有の付与（個人／グループの 2 種別）。
public record CreateShareRequest(string SubjectType, string SubjectId);
