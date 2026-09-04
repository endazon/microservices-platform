
namespace McpServer.Domain;

// FR-16, UC-08 例外フロー, ADR-0024（2026-08-02 注記）, ADR-0034 決定 9:
// **サービスアカウント（無人エージェント）による実行では、個人資料（private-note）を一律で
// 対象外とする。所有者の個人資料であっても含めない。**
//
// 🔴 **ツール名で分岐しない。** 計画（11_mcp-server-integration §6）は「探索系ツールに限らず、
// 検索・文書取得系（retrieval.* / document.*）にも同様に適用する。経路によって扱いが変わると、
// 除外の意味が失われる」と定めている。したがって本フィルタは**全ツール共通の後段**に置く。
//
// 🔴 **要求側の制約（ToolInvocationScope.ExcludePrivateNote）と二重に持つ。** 要求側だけでは
// 下流サービスの実装を信用することになり、応答側だけでは下流が無駄な処理をする。**どちらか一方に
// しない** —— 下流はまだ 1 つも実装されておらず、信用する相手が存在しない（fail-closed）。
//
// ■ 🔴 除外の軸は 2 つある（#1190 / AST/ADR-0032 決定 2・決定 3）
//   個人資料（`doc_scope=private-note`）に加え、**MCP から外すプロジェクトの文書
//   （`project=ai-stock-trading`）** も同じ後段で落とす。
//   **属性を付けるだけでは弾かれない** —— 基盤の ABAC の具体判定規則に `project` は登場せず
//   （06_technical/07_abac-attribute-model §ポリシー評価モデル）、サービスアカウントへの
//   割当を禁止しても到達可否は 1 件も変わらない。`private-note` が実際に効いているのは
//   ABAC ではなく**この一律除外**であり（ADR-0034 決定 9）、AST/ADR-0032 決定 3 の但し書きも
//   「同じ形を採れば成立する」と述べている。**割当禁止（構成検証）だけでは統制は働かない。**
public sealed class ServiceAccountDocumentFilter(ILogger<ServiceAccountDocumentFilter> logger)
{
    // 応答から個人資料を除き、**件数からも外した**結果を返す。
    //
    // ADR-0034 決定 2・4: 件数は「認可判定を通したあとの件数」でなければならない。
    // 除外した文書を件数に残すと、その数字自体が「見えない何かがある」という漏洩になる。
    public McpToolResult Apply(McpSubject subject, McpToolResult result)
    {
        if (!subject.IsServiceAccount) return result;

        var kept = result.Documents.Where(d => !IsExcluded(d.Attributes)).ToList();
        var removed = result.Documents.Count - kept.Count;
        if (removed == 0) return result;

        logger.LogInformation(
            "Excluded {Removed} document(s) (private-note / restricted project) for service account {ClientId}"
            + " (ADR-0034 決定 9, AST/ADR-0032 決定 2)",
            removed, subject.ClientId);

        // TotalCount からも同数を引く。下流が全体件数として大きい値を返していても、
        // **除外した分は必ず減らす**（下限は残存件数）。
        var total = Math.Max(kept.Count, result.TotalCount - removed);
        return result with { Documents = kept, TotalCount = total };
    }

    // 🔴 **どちらも集合帰属で判定する。** 否定（「organization でない」「制限値でない」）で書くと、
    // 属性を持たない既存文書がすべて該当して一斉に落ちる。**2 つの書き方は動作で見分けがつかず、
    // 分けられるのは「属性を持たない文書が残る」という陽性対照テストだけである。**
    private static bool IsExcluded(IReadOnlyDictionary<string, string> attributes)
        => DocumentScope.IsPrivateNote(attributes) || RestrictedProject.IsRestricted(attributes);
}
