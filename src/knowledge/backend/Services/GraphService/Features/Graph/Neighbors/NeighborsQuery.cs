namespace GraphService.Features.Graph.Neighbors;

// FR-17, UC-10, ADR-0068 決定 2 / IADR-0395: 近傍探索の**検証対象**を表す要求モデル。
//
// `AbstractValidator<T>` は型に対して規則を宣言するので、クエリ引数を検証するには器が要る。
// **この 1 操作でしか使わない**ので 3 段目（`Features/Graph/Neighbors/`）に置く。
//
// 🔴 **端点の引数一覧の複製ではない。** 検証しない引数（`documentId` / `by`）は載せない ——
// 載せると「検証されているように見えるが規則が無い」欄ができる。
//
// **端点の署名は変えない**（`[AsParameters]` へ束ねると OpenAPI の生成が変わり得るので、
// 振る舞いを変えない制約から出る）。端点が受け取った引数をここへ詰め替えてから検証する。
internal sealed record NeighborsQuery(int? Hops, string? Types);
