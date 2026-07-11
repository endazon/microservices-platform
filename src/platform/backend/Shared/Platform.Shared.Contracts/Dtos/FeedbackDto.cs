namespace Platform.Shared.Contracts.Dtos;

// FR-08, UC-01: 回答へのフィードバック（👍/👎・コメント）収集用の DTO 群。

// FR-08: フィードバック送信リクエスト。
//   AnswerId — 対象 AI 回答の ID（AiAnswerDto.AnswerId）。空 Guid は不可。
//   Rating   — "up"（👍）/ "down"（👎）のいずれか。
//   Comment  — 自由記述（任意、最大 2000 文字）。
//   Question — 回答の元となった質問（品質レビュー用の文脈。任意）。
public record FeedbackRequest(
    Guid AnswerId,
    string Rating,
    string? Comment = null,
    string? Question = null);

// FR-08: 保存済みフィードバックの表現（一覧・応答用）。
public record FeedbackDto(
    Guid Id,
    Guid AnswerId,
    string Rating,
    string? Comment,
    string? Question,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// FR-08: フィードバック集計（品質改善・ダッシュボードの入力）。
//   SatisfactionRate = Up / Total（Total=0 のとき 0）。
public record FeedbackStatsDto(
    int Up,
    int Down,
    int Total,
    double SatisfactionRate);

// FR-08: 評価値の定数。
public static class FeedbackRating
{
    public const string Up = "up";
    public const string Down = "down";

    public static bool IsValid(string? rating)
        => string.Equals(rating, Up, StringComparison.OrdinalIgnoreCase)
        || string.Equals(rating, Down, StringComparison.OrdinalIgnoreCase);

    // 入力の大小揺れを正規化（保存・集計は小文字で統一する）。
    public static string Normalize(string rating) => rating.ToLowerInvariant();
}
