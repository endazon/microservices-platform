namespace FeedbackService.Domain;

// FR-08, UC-01: AI 回答へのフィードバック（👍/👎・コメント）エンティティ。
// 同一 (AnswerId, UserId) は 1 行に upsert する（二重計上しない。IADR-0010）。
// IADR-0280 決定 2: 8 要素配置への移送（旧 FeedbackService.Api/Foundation/Domain/。振る舞いは変えない）。
public class AnswerFeedback
{
    // FR-08: コメントの最大長（バリデーション・カラム長と一致させる）。
    public const int MaxCommentLength = 2000;
    // FR-08: 保持する質問文の最大長（品質レビュー用の文脈。超過分は切り詰める）。
    public const int MaxQuestionLength = 1000;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AnswerId { get; private set; }
    public string? Question { get; private set; }
    public string Rating { get; private set; } = string.Empty; // up|down（小文字正規化済み）
    public string? Comment { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AnswerFeedback() { }

    // FR-08: 新規フィードバック。Rating は正規化済み（up|down）を渡す。
    public static AnswerFeedback Create(Guid answerId, string userId, string rating,
        string? comment, string? question)
        => new()
        {
            AnswerId = answerId,
            UserId = userId,
            Rating = rating,
            Comment = comment,
            Question = Truncate(question, MaxQuestionLength),
        };

    // FR-08: 既存フィードバックの上書き（同一利用者の再送信）。CreatedAt は保持し UpdatedAt のみ更新する。
    public void Update(string rating, string? comment, string? question)
    {
        Rating = rating;
        Comment = comment;
        Question = Truncate(question, MaxQuestionLength);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
