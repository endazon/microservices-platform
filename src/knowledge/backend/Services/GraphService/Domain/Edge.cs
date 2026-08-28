namespace GraphService.Domain;

// FR-17, UC-10, ADR-0033 決定 4・5・6, IADR-0242 決定 9: 文書間の辺。
//
// **常に 1 行である。** 方向型（cites / supersedes / derived-from / embeds ほか）は
// SourceDocumentId → TargetDocumentId が意味方向そのもの。対称型（related）は書き込み時に
// 文書 ID の昇順へ正規化して重複を防ぐ（一意制約が効く）。**バックリンク（FR-17）は行を増やさず**
// TargetDocumentId の索引の逆引きで実現する。
//
// **辺自身は機密属性を持たない**（IADR-0242 決定 6）。辺の機微性は「両端文書の存在と関係を明かす
// こと」に由来し、両端点の認可判定の連言で完全に覆われる。辺に独自の ACL 軸を足すのは計画に無い
// 抽象化である。Provenance は認可軸ではなくメタデータである。
//
// **最新版のみ保持する**（ADR-0033 決定 6）。文書の版ごとには持たず、差分更新する（#912）。
public class Edge
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // 対称型では正規化順の小さい側。
    public Guid SourceDocumentId { get; private set; }

    // 対称型では正規化順の大きい側。
    public Guid TargetDocumentId { get; private set; }

    // ADR-0033 決定 3・9: 型辞書への参照。**識別子で参照するため改名に自動追随する。**
    // DB 側は ON DELETE RESTRICT で、参照が 1 件でもある型の削除を拒否する（削除ガードの最後の防壁）。
    public Guid EdgeTypeId { get; private set; }

    // ADR-0033 決定 4: 出所を区別する（自動抽出／利用者付与／AI 提案〔承認済み〕）。
    public string Provenance { get; private set; } = EdgeProvenance.Auto;

    // ADR-0033 決定 5: **アンカー粒度は文書単位**とする。ただし Phase 3（トランスクルージョン）で
    // チャンク単位へ拡張できるよう**欄を予約する**。
    //
    // **空文字が「文書単位」を表す。null は使わない** —— 一意制約に参加する列を NULL 可にすると、
    // PostgreSQL の既定では NULL 同士が相異なるものとして扱われ、**同一の (source, target, type) を
    // 何行でも入れられてしまう**。予約列のせいで重複防止が壊れるのは本末転倒である
    // （GraphDbContext の ux_edges を参照）。
    public string SourceAnchor { get; private set; } = string.Empty;
    public string TargetAnchor { get; private set; } = string.Empty;

    // FR-17, ADR-0033 決定 6, IADR-0281 (#912): **自動抽出の起点となった文書**。
    // provenance = auto の辺にのみ入り、利用者付与・AI 承認済みの辺では null である。
    //
    // 🔴 **Source 列では代用できない。** 対称型（related）は書き込み時に (min, max) へ正規化される
    // ため（IADR-0242 決定 9）、Source は「小さい方の文書 ID」であって抽出の起点ではない。決定 6 の
    // 「**当該文書を起点とする**自動抽出の辺を作り直す」を Source で実装すると、**他文書の本文から
    // 抽出した related 辺まで巻き込んで消す**。
    //
    // **正規化で入れ替えない**（Edge.Create 参照）—— 起点は端点の並びとは独立である。
    public Guid? ExtractedFrom { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Edge() { }

    // FR-17, IADR-0242 決定 9: 辺を作る。
    // **対称型は (min, max) へ正規化する** —— 正規化しないと A→B と B→A が別行として入り、
    // 一意制約が重複を防げなくなる。
    public static Edge Create(
        Guid sourceDocumentId,
        Guid targetDocumentId,
        Guid edgeTypeId,
        bool isSymmetric,
        string provenance,
        string? sourceAnchor = null,
        string? targetAnchor = null,
        Guid? extractedFrom = null)
    {
        // null は「文書単位」＝空文字へ正規化する（上の予約欄の注記を参照）。
        var src = sourceAnchor ?? string.Empty;
        var tgt = targetAnchor ?? string.Empty;

        // 対称型は端点とアンカーを**組で**入れ替える。アンカーだけ据え置くと
        // 「A の見出し X から B へ」が「B の見出し X から A へ」に化ける。
        var (source, target, srcAnchor, tgtAnchor) =
            isSymmetric && sourceDocumentId.CompareTo(targetDocumentId) > 0
                ? (targetDocumentId, sourceDocumentId, tgt, src)
                : (sourceDocumentId, targetDocumentId, src, tgt);

        return new Edge
        {
            SourceDocumentId = source,
            TargetDocumentId = target,
            EdgeTypeId = edgeTypeId,
            Provenance = provenance,
            SourceAnchor = srcAnchor,
            TargetAnchor = tgtAnchor,
            // 🔴 上の (source, target) の入れ替えに**追随させない**。抽出の起点は端点の並びと独立で
            // あり、入れ替えると対称型で起点が相手文書に化ける（差分の母集合が壊れる）。
            ExtractedFrom = extractedFrom,
        };
    }

    // UC-10: フロンティア側でない方の端点を返す（探索で隣接ノードを引くのに使う）。
    public Guid OtherEnd(Guid knownEnd)
        => SourceDocumentId == knownEnd ? TargetDocumentId : SourceDocumentId;
}

// ADR-0033 決定 4: 辺の出所。**計画側が値集合を固定しているため定数で持つ**
// （「コード定義にしない」の制約がかかるのは*辺の型*だけである）。
public static class EdgeProvenance
{
    // 正規化 Markdown からの自動抽出（#912）。
    public const string Auto = "auto";
    // 利用者が明示的に付与（#913）。
    public const string User = "user";
    // AI 提案のうち人間が承認したもの（#914）。**未承認は辺にならない。**
    public const string AiApproved = "ai-approved";

    public static bool IsValid(string value)
        => value is Auto or User or AiApproved;
}
