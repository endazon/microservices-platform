namespace WikiService.Domain.Ports;

// UC-07 基本フロー 1「**検索する**」, FR-13, ADR-0011, IADR-0020, IADR-0334:
// Wiki.js への全文検索の委譲口。**本文は前段が持たない**（IADR-0020: ゲートウェイは本文を自前で
// 保持しない）ため、当たりを引けるのは Wiki.js だけである。
//
// 🔴 **戻り値はまだ誰にも見せてよい集合ではない。** ADR-0011 は「Wiki.js 側のページ／グループ権限を
// 属性ベース細粒度判定の代替としない」と定めており、**呼び出し側が前段の ABAC で必ず絞り直す**。
//
// **同期の口（`IWikiJsClient`）と分けてある。** 検索は読み取り経路の関心であり、同期・削除の面を
// 一緒に背負わせる理由が無い —— 分けたことで、検索だけを差し替えるテストが同期側のスタブに
// 触らずに済む（[[IADR-0334]] 決定 3）。実装クラスは `WikiJsGraphQlClient` で共通である。
public interface IWikiJsSearchClient
{
    Task<IReadOnlyList<WikiJsSearchHit>> SearchAsync(string query, CancellationToken ct = default);
}

// Wiki.js の全文検索が返した 1 件（**未認可**の生のヒット）。
// 前段が必要とするのは `Path` だけである（`doc/<documentId>` から台帳の行を引き当てる）。
// 表題は Wiki.js 側の写しなので**応答には使わない** —— 応答の表題は台帳（`WikiPage`）を正とする。
// 順位は列の並びが表す（Wiki.js の関連度順を保つ）。
public record WikiJsSearchHit(string Path, string Title);
