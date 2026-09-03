namespace Knowledge.Contracts.Dtos;

// FR-03, FR-04: 検索結果の1件（チャンク単位）
public record SearchResultDto(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    float Score,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    // FR-03, SC-02, #536: 文書の更新日時（利用者裁定 Q6 / planning#197）。計画 §SC-02 主要素の
    // 結果テーブル（文書／タグ／**更新日時**）に要る。既定値を持たせて追加する（既定値の無いメンバー
    // 追加は契約上の破壊的変更。IADR-0122 決定 2）。
    //
    // **null 許容である理由は再索引にある**（IADR-0149 決定 3）。本項目は索引（Qdrant のペイロード）
    // へ日時を取り込むところから必要であり、**既に索引済みのチャンクは日時を持たない**。
    // null は「まだ索引に無い」を正しく表す —— DateTimeOffset.MinValue で埋めると
    // 「知らない」が「とても古い」に化け、並び順（#532）が嘘をつく。
    DateTimeOffset? UpdatedAt = null,
    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193: この結果が**本文由来か**（[[IADR-0354]] 決定 4）。
    // `false` は「本文を持たない文書をメタデータで索引した点」であり、**`Text` は空文字列**になる
    // （索引テキストは題名由来なので、本文の抜粋としては出さない）。SC-02 は抜粋の位置へ
    // 「本文なし（原本を参照）」を出し、**結果から除外しない**（ADR-0070 決定 4）。
    //
    // 既定値を持たせて末尾へ追加する（既定値の無いメンバー追加は契約上の破壊的変更。IADR-0122 決定 2）。
    // **既定は `true`（本文あり）** —— 本項目を持たない旧発行者の応答は従来どおり抜粋が描かれる。
    // 逆に倒すと、再索引が済むまで全文書が「本文なし」と表示される（IADR-0149 が
    // `DateTimeOffset.MinValue` で踏みかけた「知らないを別の意味へ化けさせる」と同型）。
    bool HasBody = true);

// FR-04: 出典（番号付き＋元文書へのリンク）
// AI 回答中の [1][2] と対応する根拠。利用者は SourceUri から元文書へ辿れる。
public record CitationDto(
    int Number,
    Guid DocumentId,
    string DocumentTitle,
    Guid ChunkId,
    string? SourceUri,
    float Score,
    string Snippet,
    // FR-04, SC-01, #541: この出典の文書の機密区分（ABAC 文書属性 confidentiality）。
    // 08_data-egress-policy は機密区分を軸に越境を統制しており、利用者が AI 回答を社外資料へ
    // 引用してよいか判断する最も直接的な手掛かりになる（利用者裁定 Q10 / planning#197・planning#200）。
    // 既定値を持たせて追加する（既定値の無いメンバー追加は契約上の破壊的変更。IADR-0122 決定 2）。
    // 既定は安全側（restricted）——本項目を持たない旧発行者の応答は過剰公開ではなく過剰制限へ倒す。
    string Confidentiality = ConfidentialityLevels.SafeDefault);

// FR-04, FR-05, FR-11, SC-01, #541: 機密区分（ABAC 文書属性 confidentiality）の値集合と安全側への縮退。
// enum ではなく文字列 + const で持つ（IADR-0131 決定 5。後段の値追加を SPA 側の破壊的変更にしない）。
// **表示名（公開 / 社内限 / 秘 / 取扱制限）はここに置かない。** 正は計画リポジトリの用語集
// （計画リポジトリ project-planning の docs/glossary.md）であり、実装側で再定義すると用語の正が 2 か所へ割れる。
// なお restricted の表示名は「取扱制限」であって「極秘」ではない（個人資料が既定でこの区分を持つため。
// 利用者裁定 Q30）。
public static class ConfidentialityLevels
{
    // ABAC 属性辞書における機密区分のキー。
    public const string AttributeKey = "confidentiality";

    public const string Public = "public";
    public const string Internal = "internal";
    public const string Confidential = "confidential";
    public const string Restricted = "restricted";

    // 08_data-egress-policy「既定は安全側」/ FR-05 deny-by-default:
    // 属性の欠落・空文字・未知値はすべて最も強い区分へ倒す。
    public const string SafeDefault = Restricted;

    // 機密度の昇順。越境判定は「文脈に含む文書のうち最も高い区分」で行う（FR-11）。
    public static readonly string[] All = [Public, Internal, Confidential, Restricted];

    public static bool IsValid(string? level) =>
        level is not null && All.Contains(level, StringComparer.OrdinalIgnoreCase);

    // 未知・未指定は安全側（SafeDefault）へ縮退する。呼び出し側の分岐を 1 か所に閉じるための正規化。
    public static string Normalize(string? level) =>
        IsValid(level) ? level!.ToLowerInvariant() : SafeDefault;

    // 機密度の順位（大きいほど高い）。未知値は安全側の順位を返す。
    public static int Rank(string? level) =>
        Array.IndexOf(All, Normalize(level));

    // 文書属性から機密区分を読み、安全側へ縮退させる。出典表示（FR-04）と越境判定（FR-11）が
    // 同じ規則から導かれるよう、読み取りをここへ寄せる。
    public static string FromAttributes(IReadOnlyDictionary<string, string> attributes) =>
        Normalize(attributes.TryGetValue(AttributeKey, out var level) ? level : null);
}

// FR-04: RAG 回答レスポンス（回答本文＋番号付き出典）
// FR-08: 回答を一意に識別する AnswerId を付与し、フィードバック（👍/👎・コメント）の紐付け先とする。
//        既存の位置引数コンストラクタを壊さないよう init 既定値プロパティとし、回答生成ごとに自動採番する。
public record AiAnswerDto(
    string Answer,
    List<CitationDto> Citations,
    string Model,
    int InputTokens,
    int OutputTokens)
{
    // FR-08, UC-01: この回答の識別子。利用者はこの ID を添えてフィードバックを送信する。
    // 注意: record の自動生成 Equals/GetHashCode は init プロパティも含むため、本文が同一でも
    //       AnswerId が異なる 2 つの AiAnswerDto は等価にならない（回答ごとに自動採番されるため常に別値）。
    //       スナップショット比較・キャッシュキーにレコード全体等価（Should().Be(...)）を使う場合は留意する。
    public Guid AnswerId { get; init; } = Guid.NewGuid();
}
