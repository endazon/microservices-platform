using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using FeedbackService.Domain;
using FeedbackService.Features.Feedback.Submit;
using Knowledge.Contracts.Dtos;

namespace FeedbackService.Tests.Features.Feedback.Submit;

// FR-08 / IADR-0371 決定 2: 検証を FluentValidation へ移した際、**HTTP の面で応答が変わっていない**
// ことを固定する。
//
// 🔴 **既存の T-04 / T-05 / T-06 は状態コードしか見ていない。** 400 のままメッセージだけが
// 変わる退行（あるいは検証器の規則順が入れ替わって別の理由が返る退行）は、そこでは捕まらない。
// 本クラスは**本文の `error` 文字列**を端点越しに固定する。
[Trait("TestKind", "Integration")]
public class SubmitFeedbackResponseContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task EmptyAnswerId_Returns400WithOriginalBody()
        => await AssertBadRequestBody(
            new FeedbackRequest(Guid.Empty, "up"),
            SubmitFeedbackValidator.AnswerIdRequiredMessage);

    [Fact]
    public async Task InvalidRating_Returns400WithOriginalBody()
        => await AssertBadRequestBody(
            new FeedbackRequest(Guid.NewGuid(), "maybe"),
            SubmitFeedbackValidator.RatingInvalidMessage);

    [Fact]
    public async Task TooLongComment_Returns400WithOriginalBody()
        => await AssertBadRequestBody(
            new FeedbackRequest(Guid.NewGuid(), "up",
                Comment: new string('あ', AnswerFeedback.MaxCommentLength + 1)),
            SubmitFeedbackValidator.CommentTooLongMessage);

    // 複数違反しても、返るのは**最初の規則**の本文である（移送前のガード節と同じ）。
    [Fact]
    public async Task MultipleViolations_ReturnsFirstRuleBody()
        => await AssertBadRequestBody(
            new FeedbackRequest(Guid.Empty, "maybe",
                Comment: new string('あ', AnswerFeedback.MaxCommentLength + 1)),
            SubmitFeedbackValidator.AnswerIdRequiredMessage);

    private async Task AssertBadRequestBody(FeedbackRequest request, string expectedMessage)
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/feedback", request,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should().Be(expectedMessage);
    }
}
