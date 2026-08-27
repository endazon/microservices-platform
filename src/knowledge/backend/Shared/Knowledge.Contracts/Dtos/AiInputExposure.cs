namespace Knowledge.Contracts.Dtos;

// FR-19, ADR-0054 決定 1・2: 文書スコープ（`doc_scope`）の属性キーと 2 値。
//
// **綴りは計画が確定させたもの（ハイフン込み）をそのまま用いる**（実装で言い換えない）。
// ここへ置くのは、**AI 入力の判定（`AiInputExposure`）が同じ語彙を要る**ためである ——
// DocumentService（`DocumentAttributes`）と 2 か所に literal を持つと、片方が古くなる。
// `DocumentAttributes` の定数は本クラスへ委譲している。
public static class DocumentScopes
{
    public const string Key = "doc_scope";

    // FR-19, ADR-0054: 個人資料。**判定は常に集合帰属で書く**（`== PrivateNote`）——
    // 否定で書くと、`doc_scope` を持たない既存の組織文書が一斉に該当してしまう
    // （[[IADR-0270]] 決定 2 と同じ作法）。
    public const string PrivateNote = "private-note";
    public const string Organization = "organization";

    // 文書属性が個人資料を指しているか。**キー欠落は「個人資料ではない」**である
    // （ADR-0054 §結果: 既存文書へ遡及付与しない）。
    public static bool IsPrivateNote(IReadOnlyDictionary<string, string> attributes) =>
        attributes.TryGetValue(Key, out var scope)
        && string.Equals(scope, PrivateNote, StringComparison.OrdinalIgnoreCase);
}

// FR-19, FR-21 受け入れ基準 ⑨, [[IADR-0283]]:
// **「AI の入力に含める」トグルを ABAC 文書属性へ写した値域と、その判定。**
//
// 計画（`02_requirements` の FR-21 受け入れ基準 ⑨）は次を要求する。
//
// > 「横断検索に含める」が ON、「AI の入力に含める」が OFF の個人資料は、
// > **検索結果に現れるが RAG 回答のコンテキストには含まれない**
//
// 分離の**構造**は `RagContextPolicy` / `RagContextSelection` が既に持っている（[[IADR-0264]]）。
// 本クラスはそこへ渡す**述語の中身**＝「どのチャンクを AI の入力から外すか」を定める。
//
// 🔴 **`includeInAi`（API の欄名）と `ai_input`（本属性キー）は別物である。**
// 前者は台帳 `PrivateNote` の状態を画面へ配る形、後者はその状態を**索引・検索の側から読める形へ
// 写した投影**であり、チャンクのペイロード（`attributes -> { k: v }`）に載って RAG 経路まで届く。
//
// 形は `ConfidentialityLevels` に倣う（キー＋正準値＋安全側への縮退を 1 か所へ寄せる）。
// enum ではなく文字列 + const で持つ（[[IADR-0131]] 決定 5 と同じ理由）。
public static class AiInputExposure
{
    // ABAC 属性辞書における「AI 入力への包含」のキー。`doc_scope` と同じスネークケース。
    public const string AttributeKey = "ai_input";

    // 明示的な opt-in / opt-out の 2 値。
    public const string Included = "included";
    public const string Excluded = "excluded";

    public static readonly string[] All = [Included, Excluded];

    // FR-19: トグルの真偽を属性値へ写す（供給側 = DocumentService が使う）。
    public static string FromToggle(bool includeInAi) => includeInAi ? Included : Excluded;

    // FR-21 ⑨: **この文書属性を持つチャンクを AI の入力（RAG 回答・要約）に含めてよいか。**
    //
    // | 条件 | 結果 | 理由 |
    // | --- | --- | --- |
    // | `ai_input == "included"` | true | 明示的な opt-in |
    // | `ai_input == "excluded"` | false | 明示的な opt-out |
    // | 欠落・空・未知値 かつ **個人資料** | **false** | 🔴 fail-closed。トグル属性が欠落したら OFF 扱い |
    // | 欠落・空・未知値 かつ それ以外 | **true** | **組織文書は従来どおり**（遡及付与しない方針を壊さない） |
    //
    // **未知値を「個人資料なら拒否・組織文書なら許可」へ倒すのは意図的である。**
    // 未知値を無条件に拒否すると、綴り間違い 1 つで組織文書が RAG から静かに落ちる
    // （**検索には出るのに回答に使われない**という、原因の見えない縮退になる）。
    // 個人資料側は逆で、迷ったら見せない側へ倒す。
    public static bool IsAllowed(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (attributes.TryGetValue(AttributeKey, out var value))
        {
            if (string.Equals(value, Included, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, Excluded, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // 値が読めないときの既定は**文書スコープで分ける**（集合帰属で書く）。
        return !DocumentScopes.IsPrivateNote(attributes);
    }
}
