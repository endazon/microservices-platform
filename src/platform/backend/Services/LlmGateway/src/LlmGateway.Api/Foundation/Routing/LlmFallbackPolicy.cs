namespace LlmGateway.Api.Foundation.Routing;

// FR-11, ADR-0038 決定 4 (#863), IADR-0225: 上流の失敗を「フォールバックさせるもの」と
// 「させないもの」へ分ける唯一の判定点。
//
// 計画 ADR-0038 決定 4 の条文:
//   「429（レート制限）は再試行であってフォールバックではない。フォールバックは HTTP 400 系
//    （モデル不可・コンテキスト超過等）で発火する。」
//
// ★ 429 を「モデルが使えない」と読んで別モデルへ逃がしてはならない。逃がすと、
//   docs/operations/llm-model-pin-runbook.md が定めた「ピン留めしたモデルが使えないとき
//   別のモデルへ切り替えない」という禁止を実質的に破る経路になる（同 Runbook §レート制限）。
//
// 5xx・通信断・ステータスの取れない例外はフォールバックさせない —— 決定 4 が挙げるのは 400 系だけであり、
// 呼び出し先の不調は「別モデルにすれば直る」種類の失敗ではない（従来どおり upstream_error へ縮退する）。
public static class LlmFallbackPolicy
{
    // レート制限。400 系だが、決定 4 により**フォールバックの対象から外す**。
    public const int RateLimitStatusCode = 429;

    // この例外はフォールバックを発火させるか。
    public static bool ShouldFallBack(Exception exception)
        => StatusCodeOf(exception) is { } status
           && status is >= 400 and < 500
           && status != RateLimitStatusCode;

    // 例外の連鎖から上流 HTTP ステータスを取り出す（無ければ null）。
    // Anthropic.SDK 4.0.0 は HttpRequestException.StatusCode を設定して投げ（実測。#863 の作業仕様書 §2）、
    // OpenAI 互換プロバイダ（SelfHosted / Copilot）は EnsureSuccessStatusCode() が同プロパティを設定する。
    // よって全プロバイダを 1 つの判定器で扱える。ラップされている場合に備え InnerException も辿る。
    public static int? StatusCodeOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } statusCode })
                return (int)statusCode;
        }

        return null;
    }
}
