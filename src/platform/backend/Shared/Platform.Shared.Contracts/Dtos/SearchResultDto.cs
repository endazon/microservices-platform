namespace Platform.Shared.Contracts.Dtos;

// FR-03, FR-04: 検索結果の1件（チャンク単位）
public record SearchResultDto(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    string Text,
    float Score,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags);

// FR-04: 出典（番号付き＋元文書へのリンク）
// AI 回答中の [1][2] と対応する根拠。利用者は SourceUri から元文書へ辿れる。
public record CitationDto(
    int Number,
    Guid DocumentId,
    string DocumentTitle,
    Guid ChunkId,
    string? SourceUri,
    float Score,
    string Snippet);

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
