namespace Knowledge.Contracts.Dtos;

// FR-19, FR-21 受け入れ基準 ⑨, ADR-0061 決定 1〜6, [[IADR-0394]] (#1184):
// **個人資料の露出 3 トグルを ABAC 文書属性へ写した値域と、その判定の単一情報源。**
//
// 計画 `ADR-0061`（planning#492）は次を裁定した。
//
// > 1. 露出 3 トグルのうち **1 つでも ON なら索引へ載せる**
// > 2. **3 つとも OFF なら載せない**（既定は「索引に存在しない」ことで構造的に守る）
// > 3. **用途の別は索引を分けずに文書属性で表す**
// > 4. **ON → OFF の切り替えは索引からの削除まで及ぶ**
// > 5. 判定軸は `doc_scope` / `owner` / `shared_with` / `confidentiality` / 露出トグルの投影
// > 6. 🔴 **`confidentiality` だけで判定してはならない**
//
// 🔴 **本クラスが唯一の述語である。生産側（発行・索引）と消費側（検索・グラフ・RAG）が
// 同じ関数を呼ぶ。** 同じ判定を 2 か所へ書くと、片方だけが改名・改修されて**静かに無効化される**
// （実際に起きている型。`AiInputExposure` が `ConfidentialityLevels` に倣ったのと同じ理由）。
//
// **`IsIndexable` は定義そのものが 3 つの選言である** —— 「1 つでも ON」を別の場所で
// 書き下さない。軸を足す・外すときは本クラスだけを直せば全消費面が追随する。
//
// 🔴 **API の欄名（`includeInSearch` 等）と属性キー（`search_exposure` 等）は別物である。**
// 前者は台帳 `PrivateNote` の状態を画面へ配る形、後者は**索引・検索の側から読める形へ写した投影**で
// あり、チャンクのペイロード（`attributes -> { k: v }`）に載って各消費経路まで届く。
//
// enum ではなく文字列 + const で持つ（[[IADR-0131]] 決定 5 と同じ理由）。
public static class DocumentExposure
{
    // FR-19「横断検索に含める」の投影。
    public const string SearchKey = "search_exposure";

    // FR-19「ナレッジグラフに表示」の投影。
    public const string GraphKey = "graph_exposure";

    // FR-19「AI の入力に含める」の投影。**綴りは [[IADR-0283]] が置いた `ai_input` のままである。**
    // 3 者で語尾が揃わないのは承知のうえで改名しない —— 既に作成済みの個人資料の
    // `Document.Attributes` に書かれた値であり、改名は移行を伴う（[[IADR-0394]] 決定 1）。
    public const string AiKey = "ai_input";

    // 明示的な opt-in / opt-out の 2 値。**否定形の名前を新たに持ち込まない**
    // （`bodyAbsent` → `hasBody` で寄せた向きと同じ。[[IADR-0388]]）。
    public const string Included = "included";
    public const string Excluded = "excluded";

    public static readonly string[] AllValues = [Included, Excluded];

    // 露出の軸（投影キー）の全体。**順序は FR-19 の記載順**（横断検索 / グラフ / AI 入力）。
    public static readonly string[] AllKeys = [SearchKey, GraphKey, AiKey];

    // FR-19: トグルの真偽を属性値へ写す（供給側 = DocumentService が使う）。
    public static string FromToggle(bool included) => included ? Included : Excluded;

    // FR-19, ADR-0061 決定 3: 3 トグルを**まとめて**属性の形へ投影する。
    // **1 つでも書き漏らすと fail-closed 側（見えない側）に倒れる**ので、3 つ同時に置く口を
    // 1 つだけ用意し、呼び出し側でキーを並べさせない。
    public static Dictionary<string, string> Project(
        bool includeInSearch, bool includeInGraph, bool includeInAi) => new()
        {
            [SearchKey] = FromToggle(includeInSearch),
            [GraphKey] = FromToggle(includeInGraph),
            [AiKey] = FromToggle(includeInAi),
        };

    // **この文書属性を持つチャンクを、当該の用途に露出してよいか。**
    //
    // | 条件 | 結果 | 理由 |
    // | --- | --- | --- |
    // | `<key> == "included"` | true | 明示的な opt-in |
    // | `<key> == "excluded"` | false | 明示的な opt-out |
    // | 欠落・空・未知値 **かつ 個人資料** | **false** | 🔴 fail-closed。トグル属性が欠落したら OFF 扱い |
    // | 欠落・空・未知値 **かつ それ以外** | **true** | **組織文書は従来どおり**（遡及付与しない方針を壊さない） |
    //
    // **判定は集合帰属で書く**（`doc_scope == "private-note"`）。否定で書くと `doc_scope` を持たない
    // 既存の組織文書が一斉に該当する（[[IADR-0270]] 決定 2 と同じ作法）。
    //
    // **未知値を無条件に拒否しない。** 綴り間違い 1 つで組織文書が静かに落ち、
    // 原因の見えない縮退になる。個人資料側は逆に倒す（[[IADR-0283]] 決定 2）。
    public static bool IsAllowed(IReadOnlyDictionary<string, string> attributes, string key)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (attributes.TryGetValue(key, out var value))
        {
            if (string.Equals(value, Included, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, Excluded, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return !DocumentScopes.IsPrivateNote(attributes);
    }

    // FR-19: 横断検索（Retrieval）に露出してよいか。
    public static bool IsSearchAllowed(IReadOnlyDictionary<string, string> attributes)
        => IsAllowed(attributes, SearchKey);

    // FR-19, FR-17: ナレッジグラフ（Graph）に露出してよいか。
    public static bool IsGraphAllowed(IReadOnlyDictionary<string, string> attributes)
        => IsAllowed(attributes, GraphKey);

    // FR-19, FR-21 ⑨: AI の入力（RAG 回答・要約）に含めてよいか。
    public static bool IsAiAllowed(IReadOnlyDictionary<string, string> attributes)
        => IsAllowed(attributes, AiKey);

    // 🔴 ADR-0061 決定 1・2: **1 つでも ON なら索引へ載せる。3 つとも OFF なら載せない。**
    //
    // **生産側（発行の門・索引の門）が呼ぶのはこれ 1 つである。** 「1 つでも ON」を
    // 呼び出し側で書き下すと、軸が増えたときに片方だけ古くなる ——
    // ここが 3 つの `IsXxxAllowed` の選言そのものであることが、その事故を構造で塞ぐ。
    //
    // **組織文書は常に true である**（全キーが欠落 → 各軸が true）。既存の取り込み経路の
    // 挙動は 1 ビットも変わらない。
    public static bool IsIndexable(IReadOnlyDictionary<string, string> attributes)
        => IsSearchAllowed(attributes)
           || IsGraphAllowed(attributes)
           || IsAiAllowed(attributes);
}
