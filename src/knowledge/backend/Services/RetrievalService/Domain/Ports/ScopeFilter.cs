using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-05, FR-19, ADR-0036, IADR-0253 決定 1（段 3・検索側の分岐対応 / #989）:
// ベクトルDBポートへ渡す ABAC 制約の運搬型。**連言（AND）と選言（OR-of-AND）の両方**を持つ。
//
// **なぜ「任意の追加引数」にしないのか。**
//   分岐を省略可能な引数にすると、渡し忘れた経路が黙って Conjunction（キー単位 union の連言）
//   だけで絞る。それは **IADR-0253 決定 2 の反例が示す「どのポリシー単独も許可しない混成」を
//   許す向き**＝**情報が漏れる向きの縮退**である。1 つの型にまとめて引数を置き換えれば、
//   新しい経路を足した人が分岐を落とすとコンパイルが通らない。**fail-closed 側へ倒すための形である。**
//
// 評価規則（AbacPageFilter / AbacNodeFilter / BffScopeResolver.Matches と一致させる）:
//   Branches が 1 件以上 → **いずれかの分岐の全条件を満たす文書が可視**（分岐内 AND・分岐間 OR）。
//                          Conjunction があればそれとも AND（利用者指定の絞り込みは narrowing）。
//   Branches が空/null   → **従来どおり Conjunction のみ**（後方互換）。
public sealed record ScopeFilter(
    IReadOnlyList<AttributeFilter> Conjunction,
    IReadOnlyList<IReadOnlyList<AttributeFilter>>? Branches = null)
{
    public static readonly ScopeFilter Empty = new([]);

    // 分岐を持つか（1 件以上）。空の Branches は「分岐なし」と同じ扱い（後方互換）。
    public bool HasBranches => Branches is { Count: > 0 };

    // 制約が 1 つも無い（全件許可）。
    public bool IsUnconstrained => Conjunction.Count == 0 && !HasBranches;

    // **既存の呼び出し面（連言のみ）をそのまま通すための暗黙変換。**
    // 「フィルタの一覧」を渡していた箇所は**分岐を持たない連言**を意味しており、
    // この変換の意味と一致する。null は「制約なし」。
    public static implicit operator ScopeFilter(List<AttributeFilter>? filters) =>
        filters is { Count: > 0 } ? new ScopeFilter(filters) : Empty;

    public static implicit operator ScopeFilter(AttributeFilter[]? filters) =>
        filters is { Length: > 0 } ? new ScopeFilter(filters) : Empty;
}
