using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Bff.Foundation.Endpoints;
using Platform.Bff.Foundation.Session;

namespace Platform.Bff.Tests;

// NFR, SC-16, ADR-0032, IADR-0250, #439 第 3 段(3a): BFF セッションの入口の性質。
public class BffAuthEndpointTests
{
    // ── オープンリダイレクト防止（否定形）と、**その陽性対照**
    //
    // 🔴 **否定形だけ置かない。** 「常に "/" を返す実装」でも下の 5 件は全部通る。
    // **正当な戻り先が保たれることを対で固定する**（そうでないと、ログイン後に必ずトップへ
    // 飛ばされる実装が「安全」として緑になる）。
    [Theory]
    [InlineData("https://evil.example/steal")]   // 絶対 URL
    [InlineData("//evil.example/steal")]         // プロトコル相対
    [InlineData("/\\evil.example")]              // バックスラッシュ経由の権限部
    [InlineData("javascript:alert(1)")]          // スキーム
    [InlineData("")]                             // 空
    [InlineData(null)]                           // 未指定
    public void Login_return_url_outside_this_site_is_dropped(string? returnUrl)
        => AuthBffEndpoints.SafeReturnUrl(returnUrl).Should().Be("/");

    // ★ 陽性対照: 自サイト内のパスは**そのまま保たれる**。
    [Theory]
    [InlineData("/ask")]
    [InlineData("/analyze?days=7")]
    [InlineData("/documents/42")]
    public void Login_return_url_inside_this_site_is_preserved(string returnUrl)
        => AuthBffEndpoints.SafeReturnUrl(returnUrl).Should().Be(returnUrl);

    // ── CSRF（IADR-0250 決定 1 の 2 枚目の壁）
    private static DefaultHttpContext Request(string method, bool withSessionCookie)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        if (withSessionCookie)
            ctx.Request.Headers.Cookie = "__Host-msp-session=abc";
        return ctx;
    }

    // ★ 状態を変える動詞 ＋ セッション Cookie → ヘッダを要求する。
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Cookie_borne_state_changing_requests_require_the_header(string method)
        => CsrfHeaderMiddleware.RequiresHeader(Request(method, withSessionCookie: true), "__Host-msp-session")
            .Should().BeTrue();

    // ★ 読み取りは要求しない。ここで弾くとログインからの復帰（トップレベル GET）まで壊れる。
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Reads_do_not_require_the_header(string method)
        => CsrfHeaderMiddleware.RequiresHeader(Request(method, withSessionCookie: true), "__Host-msp-session")
            .Should().BeFalse();

    // 🔴 ★ **Bearer 呼び出しは CSRF の対象ではない。**
    // ブラウザが自動で付ける資格情報ではないため、攻撃者のページからは付けられない。
    // ここを「認証済みか」で判定すると、**サービス間の Bearer 呼び出しが一斉に 403 になる**
    // （3a では既定スキームがまだ JwtBearer なので、既存の BFF 端点が全部落ちる）。
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void Requests_without_the_session_cookie_are_not_csrf_candidates(string method)
        => CsrfHeaderMiddleware.RequiresHeader(Request(method, withSessionCookie: false), "__Host-msp-session")
            .Should().BeFalse();
}
