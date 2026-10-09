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

    // FR-17, ADR-0033 決定 4・6, [[IADR-0522]] (#1396): **自動抽出の辺が何から作られたか**（`EdgeAutoSource`）。
    // provenance = auto の辺にだけ入る（利用者付与・AI 承認済みでは null）。
    //
    // 🔴 **出所（Provenance）の値は増やさない。** 計画の出所は 3 値で固定されており（ADR-0033 決定 4）、
    // 共有タグの辺は「自動抽出」に属する。ここは自動抽出の**内訳**であり、差分更新の母集合を分けるためにある
    // —— 本文のリンクの差分（`LinkEdgeSynchronizer`）が共有タグの辺を消さず、共有タグの差分が
    // 本文のリンクの辺を消さないこと。`ExtractedFrom` が null かどうかで代用しない（暗黙の符号化は後から割れる）。
    public string? AutoSource { get; private set; }

    // 共有タグから作った辺か。
    public bool IsTagDerived => Provenance == EdgeProvenance.Auto && AutoSource == EdgeAutoSource.Tag;

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
        Guid? extractedFrom = null,
        string? autoSource = null)
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
            // [[IADR-0522]]: 自動抽出で内訳の指定が無ければ本文のリンク（従前の唯一の自動抽出）。
            AutoSource = provenance == EdgeProvenance.Auto ? autoSource ?? EdgeAutoSource.Link : null,
        };
    }

    // [[IADR-0522]] (#1396): 共有タグの辺を、同じ 5 つ組の**本文のリンクの辺として引き取る**。
    //
    // 🔴 **消して入れ直さない。** 同じ保存の中で同じ 5 つ組の行を削除して挿入すると、PostgreSQL の一意索引
    // `ux_edges` は文の順序次第で衝突する。行はそのまま、内訳と起点だけを書き換える。
    public void ClaimAsLink(Guid extractedFrom)
    {
        if (!IsTagDerived)
            throw new InvalidOperationException("only a tag-derived edge can be claimed as a link edge");
        AutoSource = EdgeAutoSource.Link;
        ExtractedFrom = extractedFrom;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // [[IADR-0522]] (#1396): 本文のリンクの辺（同じ保存で削除予定のもの）を**共有タグの辺として残す**。
    // 理由は `ClaimAsLink` と同じ（削除と挿入の組を作らない）。
    public void ConvertToTagDerived()
    {
        if (Provenance != EdgeProvenance.Auto)
            throw new InvalidOperationException("only an auto-extracted edge can become tag-derived");
        AutoSource = EdgeAutoSource.Tag;
        ExtractedFrom = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // [[IADR-0522]] (#1396), ADR-0033 決定 6: 利用者付与・AI 承認が共有タグの辺と同じ関係を張るとき、
    // その行を引き取る。**以後は再取り込み（タグの付け外し）で消えない。**
    public void AdoptAs(string provenance)
    {
        if (!IsTagDerived)
            throw new InvalidOperationException("only a tag-derived edge can be adopted");
        if (provenance is not (EdgeProvenance.User or EdgeProvenance.AiApproved))
            throw new ArgumentException("adoption is for user-asserted or AI-approved edges", nameof(provenance));
        Provenance = provenance;
        AutoSource = null;
        ExtractedFrom = null;
        UpdatedAt = DateTimeOffset.UtcNow;
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

// FR-17, ADR-0033 決定 4, [[IADR-0522]] (#1396): 自動抽出の辺の内訳。
public static class EdgeAutoSource
{
    // 本文のリンク（Obsidian・Wiki・標準 Markdown・フロントマターの明示指定）。#912。
    public const string Link = "link";
    // 同じタグを持つ文書の組（利用者裁定 2026-10-09）。
    public const string Tag = "tag";
}
