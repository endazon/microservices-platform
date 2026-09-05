using Platform.Shared.Contracts.Dtos;

namespace Knowledge.Contracts.Dtos;

// 🔴 FR-19, FR-20, UC-11, ADR-0036 D-05・D-06, ADR-0061 決定 5・6, [[IADR-0394]] 決定 7 (#1184):
// **個人資料は裁量（所有者・共有先）の分岐でしか見えない。**
//
// 計画 `ADR-0061` の裁定:
//
// > 5. 判定軸は `doc_scope` / `owner` / `shared_with` / `confidentiality` / 露出トグルの投影
// > 6. 🔴 **`confidentiality` だけで判定してはならない**
//
// ## なぜ規約ではなく述語で持つのか
//
// 認可スコープの分岐（[[IADR-0253]] 決定 1）は**管理者が定義したポリシー 1 件 = 1 分岐**である。
// 計画 `read` 規則の第 1 節「静的属性ベース」（例: `confidentiality ∈ {restricted}`）は
// **文書の種別を問わない**ため、露出 ON の個人資料をそのまま許可してしまう ——
// `restricted` クリアランスを持つ他人に、他人の個人メモが見える。
//
// これを「ポリシーを正しく書く運用」で守るのは無理である。
//   - 個人資料を外すには静的分岐へ `doc_scope ∈ {organization}` を足すことになるが、
//     **既存文書は `doc_scope` を持たない**（`ADR-0054` §結果: 遡及付与しない）ので、
//     足した瞬間に**既存の組織文書が全部見えなくなる**。
//   - 逆に足さなければ、露出 ON の個人資料が静的分岐経由で漏れる。
//
// **どちらの向きにも壊れる**ため、実装側の構造で閉じる。規則は 1 つ:
//
// > **`doc_scope == private-note` の資料を許可してよいのは、`owner` または `shared_with` を
// > 条件に持つ分岐だけである。**
//
// これは `ADR-0036` D-05・D-06（所有者ベース・共有先ベースの裁量制御）の言い換えであり、
// 新しい認可規則ではない。**組織文書には一切効かない**（`doc_scope` を持たない文書は
// 集合帰属で「個人資料ではない」——[[IADR-0270]] 決定 2 の作法）。
//
// 🔴 **判定は本クラス 1 か所に置く。** 消費面は Retrieval（`InMemoryVectorStore` / `QdrantVectorStore`）
// と Graph（`AbacNodeFilter`）の 3 実装あり、同じ述語を 3 度書くと 1 つだけ改名されて
// 静かに無効化される（実際に起きている型）。
public static class PrivateNoteVisibility
{
    // ADR-0036 D-05: 所有者を表す文書属性のキー。
    public const string OwnerKey = "owner";

    // ADR-0036 D-06: 共有先を表すペイロードのリスト項目のキー。
    public const string SharedWithKey = AttributeValueKeys.SharedWith;

    // **裁量の分岐か** —— `owner` または `shared_with` を条件に持つか。
    //
    // **条件が 1 つも無い分岐（＝そのポリシーの範囲で全件許可）は裁量ではない。**
    // 「無条件で全件」に個人資料を含めてはならない、というのが本規則の要点である。
    public static bool IsDiscretionaryBranch(IReadOnlyList<AttributeFilter>? branch)
    {
        if (branch is not { Count: > 0 }) return false;

        return branch.Any(f =>
            string.Equals(f.Key, OwnerKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Key, SharedWithKey, StringComparison.OrdinalIgnoreCase));
    }

    // **この分岐は、この文書属性を持つ資料を許可してよいか。**
    // 個人資料でなければ常に true（既存の判定を 1 ビットも変えない）。
    public static bool BranchMayGrant(
        IReadOnlyDictionary<string, string> attributes, IReadOnlyList<AttributeFilter>? branch)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        return !DocumentScopes.IsPrivateNote(attributes) || IsDiscretionaryBranch(branch);
    }
}
