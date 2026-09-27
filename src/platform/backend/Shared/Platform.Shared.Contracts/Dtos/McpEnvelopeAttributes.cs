namespace Platform.Shared.Contracts.Dtos;

// 🔴 FR-16, UC-08, ADR-0024 §4, ADR-0034 決定 9, ADR-0117 決定 2・3, [[IADR-0479]]（2026-09-28 追記 / #1671）:
// **MCP のツール応答の共通エンベロープ（`McpToolDocument.attributes`）に載せてよい文書属性のキーの許可リスト。**
//
// エンベロープの属性は MCP サーバーを経て**外部 LLM まで渡る**（ADR-0024 §4 の前提）。MCP サーバーが文書属性を読むのは
// 応答の統制の 3 か所だけである:
//   - `confidentiality` —— 越境判定（McpServer `EgressPolicy.ConfidentialityKey`）
//   - `doc_scope` —— 2 層目の個人資料の除外（McpServer `DocumentScope.Key`）
//   - `project` —— 2 層目の制限プロジェクトの除外（`RestrictedProject.DocumentKey`）
// それ以外（`owner`・`dept`・`shared_with` 等の ABAC 判定用の属性）は MCP サーバーが読まず、外へ出す理由が無い
// （越境する個人識別子を最小にする）。
//
// ■ 🔴 なぜ契約プロジェクトに 1 か所だけ置くのか
//   読み手（platform ユニットの McpServer）と受け口（knowledge ユニットの各サービスの実行口）が**別ユニット**に居り、
//   ユニット外参照は `Platform.Shared.{Contracts,Infrastructure,Kernel}` の 3 つしか許されない（`RestrictedProject` と同じ理由）。
//   **受け口ごとに許可リストを持つと、キーを足すとき片側だけ変わる**（読み手が読むキーが運ばれず統制が黙って効かなくなる、
//   または受け口だけ広がる）。読み手の定数は本クラスの定数を指し、受け口は <see cref="IsCarried"/> で写す。
//   McpServer の試験が「読み手のキー ⊆ 許可リスト」を、受け口の試験が「許可リストのキーが残り外のキーが消える」を固定する。
//
// ■ 大小文字は区別する（`Ordinal`）
//   MCP サーバーの受信側の辞書が `Ordinal` であり、綴りの違うキーはそもそも読まれない。
public static class McpEnvelopeAttributes
{
    /// <summary>機密区分（越境判定。計画 08_data-egress-policy）。</summary>
    public const string ConfidentialityKey = "confidentiality";

    /// <summary>文書スコープ（個人資料の判定。ADR-0054）。</summary>
    public const string DocumentScopeKey = "doc_scope";

    /// <summary>プロジェクトコード（制限プロジェクトの除外）。正本は <see cref="RestrictedProject.DocumentKey"/>。</summary>
    public const string ProjectKey = RestrictedProject.DocumentKey;

    /// <summary>エンベロープへ載せてよいキー（これ以外は運ばない）。</summary>
    public static IReadOnlySet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ConfidentialityKey,
        DocumentScopeKey,
        ProjectKey,
    };

    /// <summary>このキーをエンベロープへ運んでよいか。</summary>
    public static bool IsCarried(string key) => Keys.Contains(key);
}
