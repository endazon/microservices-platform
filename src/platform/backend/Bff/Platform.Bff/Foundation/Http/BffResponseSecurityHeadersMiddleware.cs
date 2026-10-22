using Microsoft.AspNetCore.Http;

namespace Platform.Bff.Foundation.Http;

// NFR / IADR-0581（Refs GHSA-5h4q-4m2j-rjhm）: BFF の全応答に付ける応答ヘッダ（多層防御）。
//
// BFF の応答は SPA が fetch で読む API・画像（図・CAPTCHA）・リダイレクト（ログイン・ログアウトの往復）だけであり、
// **文書として描かれることを前提にした応答は無い**（HTML を返す口は 0 件）。よって、応答がブラウザに文書として
// 開かれても何も実行・埋め込みできない値を既定にする。
//   - X-Content-Type-Options: nosniff —— 宣言した Content-Type 以外として解釈させない
//   - Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; sandbox
//       —— 文書として開かれても、スクリプト・資産の取得・埋め込み・同一オリジンの権限を持たない。
//          fetch で読む側（SPA）・`<img>` で読む側には効かない（CSP は文書にだけ掛かる）。
//   - X-Frame-Options: DENY —— frame-ancestors を解さない古いブラウザ向け
//   - Cross-Origin-Resource-Policy: same-origin —— 別オリジンのページから資源として読ませない
//     （SPA と BFF は同一オリジン。CsrfHeaderMiddleware の前提と同じ）
//
// 🔴 **既に値がある場合は上書きしない**（中継の口が後段の値を運ぶ余地を残す。現状その口は無い）。
// 値は応答の開始時（OnStarting）に付ける —— 後段の例外・短絡（401 / 403 / 404）の応答にも付く。
public sealed class BffResponseSecurityHeadersMiddleware(RequestDelegate next)
{
    internal static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; sandbox",
        ["X-Frame-Options"] = "DENY",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
    };

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            foreach (var (name, value) in Headers)
            {
                if (!response.Headers.ContainsKey(name))
                    response.Headers[name] = value;
            }
            return Task.CompletedTask;
        }, context.Response);

        return next(context);
    }
}
