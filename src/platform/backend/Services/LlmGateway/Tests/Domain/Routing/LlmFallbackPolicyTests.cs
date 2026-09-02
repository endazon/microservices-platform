using System.Net;
using AwesomeAssertions;
using LlmGateway.Domain.Routing;

namespace LlmGateway.Tests.Domain.Routing;

// T-25, FR-11, ADR-0038 決定 4 (#863), IADR-0225:
// 「フォールバックは HTTP 400 系で発火する」「429（レート制限）は再試行であってフォールバックではない」
// という条文を、判定器の単体テストとして固定する。
//
// ★ 429 の行が本テストの要点である。429 を「モデルが使えない」と読んで別モデルへ逃がすと、
//   docs/operations/llm-model-pin-runbook.md の禁止（利用不能時に別モデルへ切り替えない）を
//   実質的に破る経路になる。**429 で落ちないことが、機構が空振りしていないことと同じくらい重要である。**
public class LlmFallbackPolicyTests
{
    // ADR-0038 決定 4: 400 系（モデル不可・コンテキスト超過等）はフォールバックさせる。
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]          // 400: モデル不可・パラメータ不正
    [InlineData(HttpStatusCode.Forbidden)]           // 403
    [InlineData(HttpStatusCode.NotFound)]            // 404: モデル ID が存在しない
    [InlineData(HttpStatusCode.RequestEntityTooLarge)] // 413: コンテキスト超過
    [InlineData(HttpStatusCode.UnprocessableEntity)] // 422
    public void ShouldFallBack_On4xxOtherThanRateLimit(HttpStatusCode status)
        => LlmFallbackPolicy.ShouldFallBack(Upstream(status)).Should().BeTrue();

    // ★ ADR-0038 決定 4 の核心: 429 は**再試行**であってフォールバックではない。
    [Fact]
    public void ShouldNotFallBack_OnRateLimit429()
    {
        var exception = Upstream(HttpStatusCode.TooManyRequests);

        LlmFallbackPolicy.StatusCodeOf(exception).Should().Be(LlmFallbackPolicy.RateLimitStatusCode);
        LlmFallbackPolicy.ShouldFallBack(exception).Should().BeFalse(
            "429 はレート制限であり、別モデルへ逃がすと ADR-0038 決定 4 とピン Runbook の禁止を破る");
    }

    // 5xx は「別モデルにすれば直る」種類の失敗ではない（従来どおり upstream_error へ縮退する）。
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ShouldNotFallBack_On5xx(HttpStatusCode status)
        => LlmFallbackPolicy.ShouldFallBack(Upstream(status)).Should().BeFalse();

    // ステータスの取れない失敗（通信断・SDK 内部の例外）は発火させない（安全側）。
    [Fact]
    public void ShouldNotFallBack_WhenStatusUnknown()
    {
        LlmFallbackPolicy.ShouldFallBack(new HttpRequestException("connection refused")).Should().BeFalse();
        LlmFallbackPolicy.ShouldFallBack(new InvalidOperationException("BaseUrl 未設定")).Should().BeFalse();
        LlmFallbackPolicy.StatusCodeOf(new InvalidOperationException("x")).Should().BeNull();
    }

    // ラップされていても上流ステータスを見失わない（プロバイダが例外を包み直す実装に耐える）。
    [Fact]
    public void StatusCodeOf_LooksIntoInnerExceptions()
    {
        var wrapped = new InvalidOperationException("provider failed", Upstream(HttpStatusCode.BadRequest));

        LlmFallbackPolicy.StatusCodeOf(wrapped).Should().Be(400);
        LlmFallbackPolicy.ShouldFallBack(wrapped).Should().BeTrue();
    }

    // 実測（#863 の作業仕様書 §2）: Anthropic.SDK 4.0.0 も EnsureSuccessStatusCode() も
    // HttpRequestException.StatusCode を設定して投げる。判定器はその形だけを見る。
    private static HttpRequestException Upstream(HttpStatusCode status)
        => new("upstream rejected the request", null, status);
}
