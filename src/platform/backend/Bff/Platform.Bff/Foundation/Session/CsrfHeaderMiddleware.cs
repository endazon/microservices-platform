using Microsoft.AspNetCore.Http;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032 §決定, IADR-0249 決定 1, #439: CSRF 対策の 2 枚目の壁。
//
// ADR-0032 は「トークン**または** SameSite + カスタムヘッダ検証」と両論併記しており、後者を採る。
//
// **壁は 2 枚ある。**
//   1 枚目 = `SameSite=Lax` —— クロスサイトの `fetch` / `<form>` POST には Cookie が付かない
//   2 枚目 = 本ミドルウェア —— カスタムヘッダを要求すると、クロスオリジンからの呼び出しは
//            **単純リクエストでなくなり preflight が飛ぶ。BFF に CORS 許可が無いのでブラウザが遮断する**
//
// 🔴 **この方式は「SPA と BFF が同一オリジンで、BFF に CORS 許可が無い」ことの上に立っている。**
// どちらかが変わったらトークン方式へ寄せ直すこと（判断条件は IADR-0249 決定 1）。
//
// **ヘッダの値は検査しない。存在することに意味がある**（値を検査すると、その値を配る口と
// 寿命の同期という可動部が増え、トークン方式の欠点をそのまま引き受けることになる）。
public sealed class CsrfHeaderMiddleware(RequestDelegate next, BffSessionOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresHeader(context, options.CookieName)
            && !context.Request.Headers.ContainsKey(options.CsrfHeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                $"Missing {options.CsrfHeaderName} header (CSRF protection).");
            return;
        }

        await next(context);
    }

    /// <summary>
    /// **状態を変える動詞にだけ要求する。** 読み取り（GET / HEAD / OPTIONS）は `SameSite=Lax` が
    /// 守る範囲であり、ここで弾くとトップレベル遷移（ログインからの復帰など）まで壊れる。
    /// </summary>
    internal static bool RequiresHeader(HttpContext context, string cookieName)
    {
        // 🔴 **判定は「セッション Cookie を運んでいるか」であって「認証済みか」ではない。**
        //
        // CSRF が成立するのは**ブラウザが自動で付ける資格情報**（Cookie）だけである。
        // Bearer トークンは呼び出し側が明示的に付けるものなので、攻撃者のページからは付けられない
        // ＝ CSRF の対象ではない。
        //
        // ここを `User.Identity.IsAuthenticated` で判定すると、**サービス間の Bearer 呼び出しにも
        // ヘッダを要求してしまう**（第 3 段の 3a では既定スキームがまだ JwtBearer なので、
        // 既存の BFF 端点が一斉に 403 になる）。
        if (!context.Request.Cookies.ContainsKey(cookieName)) return false;

        return !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && !HttpMethods.IsTrace(context.Request.Method);
    }
}
