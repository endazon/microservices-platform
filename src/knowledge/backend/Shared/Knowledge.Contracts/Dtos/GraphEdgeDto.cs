namespace Knowledge.Contracts.Dtos;

// FR-17, ADR-0034 決定 8, IADR-0242: 利用者が明示的に辺を付与する契約（#913）。

// FR-17: 辺の作成要求。
//
// **出所は要求に含めない。** この経路で作られる辺は必ず「利用者付与」であり、
// 呼び出し側に選ばせると自動抽出や AI 承認済みを騙れる（出所は ADR-0033 決定 4 の区別の根拠である）。
//
// **アンカーも受け取らない。** アンカー粒度は文書単位（決定 5）で、チャンク粒度は Phase 3 の予約である。
// 受け取れるようにすると、予約列が「使ってよい欄」として先に埋まってしまう。
public record CreateGraphEdgeRequest(
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    Guid EdgeTypeId);

// FR-17: 作成された辺。
public record GraphEdgeCreatedDto(
    Guid Id,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    Guid EdgeTypeId,
    string Provenance);
