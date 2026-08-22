namespace Knowledge.Contracts.Dtos;

// FR-17, UC-10, ADR-0034: グラフ読み取りの BFF 契約（#916a）。
//
// 🔴 **GraphService 側の応答型（`AuthorizedGraphView` / `GraphViewResponse`）とは別物である。**
// あちらは `Seal` 以外から構築できない型ゲート（`IADR-0242` 決定 2）で、**未フィルタの結果を
// 直列化させない**ために存在する。ゲートは GraphService の出口を守るものであり、
// BFF はその**既に封をされた JSON** を受け取って中継するだけなので、ここは素の記録型でよい。
//
// 契約をここへ置く理由: BFF は可変ユニットのサービス実装（`GraphService.Api`）を参照できない
// （`CLAUDE.md` の依存規則。ユニット外参照は `platform/backend/Shared/` のみ）。

// FR-17: グラフのノード（文書単位）。
public record GraphNodeItemDto(Guid DocumentId, string Title);

// FR-17, ADR-0033 決定 4: グラフの辺。
// **辺の型は識別子で返す**（表示名は辞書側で解決する。改名に追随するため）。
public record GraphEdgeItemDto(
    Guid Id,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    Guid EdgeTypeId,
    string Provenance);

// FR-17, UC-10, ADR-0034 決定 4: 近傍グラフ。
//
// `Truncated` は表示上限（ノード 200 / 辺 500）で打ち切ったかを表す。
// **上限の計数は権限判定を通過した品目に対してのみ行われる**ため、この値が権限外文書の
// 存在を漏らすことはない（GraphService 側で保証。`IADR-0242` 決定 4）。
public record GraphViewDto(
    List<GraphNodeItemDto> Nodes,
    List<GraphEdgeItemDto> Edges,
    bool Truncated);
