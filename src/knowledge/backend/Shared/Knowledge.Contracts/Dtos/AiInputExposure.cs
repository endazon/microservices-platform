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
    // 🔴 **［2026-09-05 / #1184］本クラスは `DocumentExposure` への別名である。**
    // 露出 3 トグルが揃ったことで、判定の実体は `DocumentExposure`（同ディレクトリ）へ移った
    // （[[IADR-0395]] 決定 2）。**ここに述語の写しを置かない** —— 同じ判定を 2 か所に書くと
    // 片方だけが改名されて静かに無効化される。既存の呼び出し面と既存テストを壊さないために
    // 型名だけを残してある。
    //
    // 値域・判定表・fail-closed の向きは `DocumentExposure` の注釈が正本である。

    // ABAC 属性辞書における「AI 入力への包含」のキー。`doc_scope` と同じスネークケース。
    //
    // 🔴 **値は `DocumentExposure` の定数を参照せず、リテラルのまま複製してある。**
    // 契約 baseline（`contract-schema`）は const の**初期化式の字面**を比較するため、
    // 参照へ置き換えると値が 1 バイトも変わらないのに `constValueChanged`（breaking）として
    // 検出され、`contract-breaking-allowlist.json` の承認が要る。**判定（述語）は 1 つに寄せ、
    // 値の一致はテストで固定する**（[[IADR-0270]] 決定 6 が `NotificationKinds` で採ったのと同じ形。
    // `DocumentExposureTests` が `AiInputExposure.* == DocumentExposure.*` を assert する）。
    public const string AttributeKey = "ai_input";

    // 明示的な opt-in / opt-out の 2 値（上と同じ理由でリテラル）。
    public const string Included = "included";
    public const string Excluded = "excluded";

    public static readonly string[] All = [Included, Excluded];

    // FR-19: トグルの真偽を属性値へ写す（供給側 = DocumentService が使う）。
    public static string FromToggle(bool includeInAi) => DocumentExposure.FromToggle(includeInAi);

    // FR-21 ⑨: **この文書属性を持つチャンクを AI の入力（RAG 回答・要約）に含めてよいか。**
    public static bool IsAllowed(IReadOnlyDictionary<string, string> attributes)
        => DocumentExposure.IsAiAllowed(attributes);
}
