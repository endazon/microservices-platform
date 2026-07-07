namespace WikiService.Api.Services;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0021: Wiki.js への同期・本文取得ポート。
// 同期は GraphQL API push（IADR-0021）。認可は WikiService（ゲートウェイ）が単一真実源であり、
// Wiki.js 側には ABAC 権限を持ち込まない（本 IF は「内容の反映」と「本文の取得」のみを担う）。
public interface IWikiJsClient
{
    // 冪等 upsert: path をキーに、存在すれば update、無ければ create（再配信に対して冪等）。
    Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default);

    // 認可ゲートウェイの本文プロキシ用。ABAC 通過後にのみ呼ぶ。未存在は null。
    Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default);

    // Issue #88: アーカイブ（非公開化）の伝播。ページを unpublish + private にする。未存在は冪等に成功扱い。
    Task ArchivePageAsync(string path, CancellationToken ct = default);

    // Issue #88: 削除の伝播（実体撤去・社内文書の外部システム残存防止）。未存在は冪等に成功扱い。
    Task DeletePageAsync(string path, CancellationToken ct = default);
}

// Wiki.js へ push する正規化済みページ内容（認可属性は含めない — IADR-0021）。
//
// IsPrivate は機密区分由来の「粗粒度な表示制御」（ADR-0011: Wiki.js は表示制御に留める）。ABAC の
// 代替ではなく、ネットワーク分離（IADR-0017）が退行・誤設定された場合でも public 以外の文書が
// Wiki.js 上で無条件公開にならないための多層防御（deny-closed）。細粒度の認可判定は引き続き本システム
// が単一真実源として担う（属性集合は Wiki.js へ持ち込まない）。
public record WikiJsPage(
    string Path, string Title, string Markdown, IReadOnlyList<string> Tags, bool IsPrivate);
