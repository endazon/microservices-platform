namespace GraphService.Domain;

// FR-17, SC-18, ADR-0054, IADR-0274 決定 3 (#917): 文書スコープ（個人資料か組織文書か）の判定。
//
// 🔴 **集合帰属で判定する。「organization でない」で書いてはならない。**
// `doc_scope` は 2026-08-22 新設で実データ 0 件・既存文書へ遡及付与しない方針（ADR-0054 §結果）で
// あり、否定で書くと属性を持たない既存文書がすべて「個人資料」に倒れ、**組織文書が角丸四角＋👤 で
// 描かれる**。**値が無い文書は組織文書として扱う** —— これは暫定の埋め合わせではなく、
// ADR-0054 決定 5（システム投入経路の既定は organization・取り込み経路が個人資料を作ることはない）
// を根拠とする明示的な決定である（IADR-0274 決定 3）。
//
// platform ユニットの McpServer に同形の判定（`DocumentScope.cs`）があるが、可変ユニットから
// 参照できない（ユニット外参照は platform/backend/Shared の 3 プロジェクトのみ）ため、
// bool 1 個の導出を共有化せずここに持つ（IADR-0274 §検討した選択肢）。
internal static class GraphDocumentScope
{
    public const string Key = "doc_scope";
    public const string PrivateNote = "private-note";

    public static bool IsPrivateNote(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(Key, out var scope)
            && string.Equals(scope, PrivateNote, StringComparison.OrdinalIgnoreCase);
}
