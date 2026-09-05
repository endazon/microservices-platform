using FluentValidation;
using GraphService.Domain;

namespace GraphService.Features.Graph.Neighbors;

// FR-17, UC-10, SC-18, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 近傍探索のクエリ引数の入力規則。従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文を返すため:
//   1. **規則の宣言順を元のガード節の順に揃える**（hops → types）。呼び出し側が `Errors[0]` を
//      採るので、順序を入れ替えると両方違反したときの本文が変わる。
//   2. **本文が 2 欄（`error` ＋ `message`）である。** 移送先の 8 箇所（1 欄）と違い、
//      機械語と説明文の両方を運ぶ必要がある。**`WithErrorCode` を機械語（`error`）に、
//      `WithMessage` を説明文（`message`）に割り当てる**（IADR-0395 決定 4）。
//      🔴 **この 2 欄の規約を 1 欄の端点へ広げない** —— 広げると `error` の値の出どころが
//      波 1 の 6 サービス（`Error.Message` 由来）と割れる。
//
// **`types` は形式だけを見る。** 解析（`HashSet<Guid>` の構築）は端点に残す（IADR-0395 決定 3）
// —— `ValidationResult` には副産物を返す口が無く、`RootContextData` へ詰めると規則が副作用を持つ。
internal sealed class NeighborsQueryValidator : AbstractValidator<NeighborsQuery>
{
    // FR-17: 元のガード節が返していた本文の文字列。**この 4 本が応答の契約である。**
    internal const string HopsOutOfRangeCode = "hops_out_of_range";
    internal const string EdgeTypeFilterInvalidCode = "edge_type_filter_invalid";

    // 🔴 `const` にできない（`int` の補間は定数式にならない。CS0133）。移送前と同じ文字列を
    // **同じ式から**作るために `static readonly` にする。数値を直書きすると、上限を変えたときに
    // メッセージだけが古いまま残る。
    internal static readonly string HopsOutOfRangeMessage =
        $"hops は 1〜{GraphTraversal.MaxHops} で指定する（既定 {GraphTraversal.DefaultHops}）。";

    internal const string EdgeTypeFilterInvalidMessage =
        "types は辺の型 ID（GUID）のカンマ区切りで指定する。";

    // FR-17, SC-18 (#917): `types` の区切り方。**端点の解析と同じ指定を使う**
    // （片方だけ変えると「検証は通るが解析で落ちる」形になる）。
    internal const StringSplitOptions TypesSplitOptions =
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

    public NeighborsQueryValidator()
    {
        // FR-17, ADR-0034 決定 3: hops 上限の超過は 400 で拒否する。**黙って切り詰めない。**
        // **未指定は既定値へ縮退してから判定する**（移送前と同じ式）。
        RuleFor(q => q.Hops)
            .Must(h =>
            {
                var requested = h ?? GraphTraversal.DefaultHops;
                return requested >= 1 && requested <= GraphTraversal.MaxHops;
            })
            .WithErrorCode(HopsOutOfRangeCode)
            .WithMessage(HopsOutOfRangeMessage);

        // FR-17, SC-18 (#917): 辺の型フィルタ。**形式不正（GUID として読めない要素）だけを拒む。**
        // **実在しない型 ID は拒まない** —— 辺の型辞書は認証のみで全利用者へ公開済みの語彙であり
        // （#962）、実在の有無は秘匿対象ではなく、単に 1 本も一致しないだけである。
        // 空・空白は「絞らない」であって不正ではない（移送前と同じ）。
        RuleFor(q => q.Types)
            .Must(t => string.IsNullOrWhiteSpace(t)
                || t.Split(',', TypesSplitOptions).All(part => Guid.TryParse(part, out _)))
            .WithErrorCode(EdgeTypeFilterInvalidCode)
            .WithMessage(EdgeTypeFilterInvalidMessage);
    }
}
