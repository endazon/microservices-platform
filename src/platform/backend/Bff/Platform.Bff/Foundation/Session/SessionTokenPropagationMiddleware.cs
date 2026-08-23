using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0273 決定 4, #439: セッション → 下流サービスへの資格情報の橋。
//
// BFF の各端点は `Request.Headers.Authorization` を下流へ透過する方式で書かれている
// （AuthzBffEndpoints.Proxy ほか。knowledge / AST の端点モジュールも同じ契約）。
// Cookie セッションのリクエストにはそのヘッダが**無い**ので、何もしないと下流呼び出しが
// 全部資格情報欠落になる（#948 と同型の欠陥が全端点で再発する）。
//
// ここで、セッション認証に成功したリクエストのチケットからアクセストークンを取り出し、
// **受信リクエストのヘッダへ昇格**する。端点モジュール側は 1 行も変えずに済み、
// ユニット外（knowledge / AST）のモジュールへ手を入れない。
//
// 🔴 **トークンは応答には決して載らない。** 書き換えるのは**受信リクエスト**のヘッダであり、
// ブラウザへ返る応答ではない（BffSessionFlowTests が否定形＋陽性対照で固定する）。
public sealed class SessionTokenPropagationMiddleware(RequestDelegate next, BffSessionOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // 既に Authorization を運ぶ呼び出し（サービス間の Bearer）は触らない。
        // セッション Cookie を運ばないリクエストにも用は無い。
        if (StringValues.IsNullOrEmpty(context.Request.Headers.Authorization)
            && context.Request.Cookies.ContainsKey(options.CookieName))
        {
            // 認証ミドルウェアが既に同スキームを解決していれば結果はリクエスト内でキャッシュされる。
            var result = await context.AuthenticateAsync(BffSessionExtensions.SessionScheme);
            var accessToken = result.Succeeded
                ? result.Properties?.GetTokenValue("access_token")
                : null;
            if (!string.IsNullOrEmpty(accessToken))
                context.Request.Headers.Authorization = "Bearer " + accessToken;
        }

        await next(context);
    }
}
