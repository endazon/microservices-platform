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

// FR-17, SC-19, ADR-0034 決定 2・8・9, [[IADR-0655]] (#2343): リンク作成の補完候補の 1 件。
// **文書 ID・表題・個人資料かどうかだけ**を運ぶ。件数・総数・打ち切りの印を運ぶ器は持たない（応答は配列そのもの）。
// 候補は辺の作成と同じ read スコープ・同じ述語で絞ったグラフのノードで、個人資料は呼び出し主体が所有者のものだけである。
public record GraphLinkCandidateDto(
    Guid DocumentId,
    string Title,
    bool IsPrivateNote);
