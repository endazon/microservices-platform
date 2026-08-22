namespace McpServer.Api.Foundation.Services;

// FR-16, FR-19, ADR-0054 決定 1・2: 個人資料かどうかを判定する軸。
// 属性キー・値の綴りは計画 ADR-0054 が確定させたものをそのまま用いる（実装で言い換えない）。
// WikiService の DocumentSyncConsumer と同じ定数・同じ判定の向きにしてある。
public static class DocumentScope
{
    public const string Key = "doc_scope";
    public const string PrivateNote = "private-note";
    public const string Organization = "organization";

    // 🔴 **集合帰属で判定する。「organization でない」で書いてはならない。**
    //
    // doc_scope は 2026-08-22 新設で実データ 0 件であり、既存文書へ遡及付与しない方針
    // （ADR-0054 §結果）。否定で書くと属性を持たない既存文書がすべて該当し、**組織文書が
    // 一斉に落ちる**。ADR-0036 D-04 が評価の性質を「集合帰属」と定めているのと同じ理由であり、
    // WikiService（ADR-0046 D-01 の実装）も同じ向きで書いている。
    //
    // **2 つの書き方は「個人資料を除外する」という点では動作で見分けがつかない。**
    // 分けられるのは「doc_scope を持たない文書は除外されない」という陽性対照テストだけである
    // （ServiceAccountDocumentFilterTests）。
    public static bool IsPrivateNote(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(Key, out var scope)
            && string.Equals(scope, PrivateNote, StringComparison.OrdinalIgnoreCase);
}
