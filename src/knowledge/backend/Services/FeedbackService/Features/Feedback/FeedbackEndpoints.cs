using FeedbackService.Domain;
using FeedbackService.Features.Feedback.List;
using FeedbackService.Features.Feedback.Stats;
using FeedbackService.Features.Feedback.Submit;
using Knowledge.Contracts.Dtos;

namespace FeedbackService.Features.Feedback;

// FR-08, UC-01: 回答へのフィードバック（👍/👎・コメント）集約の登録表（ADR-0068 決定 1）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/Feedback/<操作>/` に居る（ADR-0065 決定 2）。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group と、
// 投稿・一覧が共有する DTO 変換。
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/feedback").WithTags("Feedback");

        SubmitFeedbackEndpoint.Map(g);
        ListFeedbackEndpoint.Map(g);
        FeedbackStatsEndpoint.Map(g);

        return app;
    }

    // **投稿と一覧の 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static FeedbackDto ToDto(AnswerFeedback f)
        => new(f.Id, f.AnswerId, f.Rating, f.Comment, f.Question, f.UserId, f.CreatedAt, f.UpdatedAt);
}
