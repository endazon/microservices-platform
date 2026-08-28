using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Platform.Bff.Tests;

// FR-16, UC-09, SC-12, ADR-0024, #452: /bff/admin/mcp-clients が McpServer の管理 API へ
// AdminOnly で中継することを検証する。
//
// 固定する性質は 3 つで、**どれも「拒否の側だけ」では測れない**ので陽性対照と対で置く。
//   1. **ロール限定**（05_screens §共通シェル のアクセス制御の割当: 本画面はシステム管理者ロール限定）:
//      管理者は 200 / 運用者は 403 / 無認証は 401。**管理者が通ることを先に確かめる。**
//   2. **状態コードの透過**: 後段の 400（属性割当の拒否）・404（不在）・409 をそのまま返す。
//      🔴 404 を 403 や 200 へ変換すると存在秘匿が BFF 層で破れる。
//   3. **資格情報の伝播**: 後段も自分で AdminOnly を強制する二重ゲートであり、
//      伝播が切れると全部 401 になる。**スタブが観測して陽性対照にする。**
public class BffMcpClientEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffMcpClientEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // IClassFixture は共有される。観測する側が既定へ戻す。
        _factory.McpStubStatusCode = HttpStatusCode.OK;
        _factory.McpStubThrows = false;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// 管理者としての呼び出し口。
    ///
    /// 🔴 **`TestAuthHandler` が扱うのはヘッダ `X-Test-Roles` であり、後段へ伝播されるのは
    /// `Authorization` である。両者は別物なので明示的に付ける**（`BffGraphEndpointTests` と同じ作法）。
    /// 付けないと後段スタブは「資格情報が届いていない」と判定して 401 を返す ——
    /// これは実サービスの挙動そのものであり、**伝播の陽性対照が成立していることの裏返し**である。
    /// </summary>
    private HttpClient AsAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    private HttpClient AsOperator()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    // ── 1. 陽性対照: 管理者は 6 端点すべてを使える ──────────────────────

    [Fact]
    public async Task ListClients_AsAdmin_ReturnsClients()
    {
        var resp = await AsAdmin()
            .GetAsync("/bff/admin/mcp-clients", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("nightly-digest-bot");
        // 後段のパスは `/mcp-clients`（BFF の接頭辞 `/bff/admin` は剥がして中継する）。
        _factory.LastMcpPath.Should().Be("/mcp-clients");
    }

    [Fact]
    public async Task RegisterClient_AsAdmin_ForwardsBodyToDownstream()
    {
        var resp = await AsAdmin().PostAsync("/bff/admin/mcp-clients",
            Json("""{"clientId":"bot","displayName":"bot","kind":"service-account"}"""),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMcpMethod.Should().Be("POST");
        // 🔴 **要求本文が後段へ渡っていること。** 落とすと後段は常に「clientId 未指定」で 400 を返し、
        // 画面からは「登録できない BFF」になる。
        _factory.LastMcpBody.Should().Contain("service-account");
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("enable")]
    public async Task ToggleClient_AsAdmin_HitsTheDownstreamPath(string action)
    {
        var resp = await AsAdmin().PostAsync(
            $"/bff/admin/mcp-clients/nightly-digest-bot/{action}", content: null,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMcpPath.Should().Be($"/mcp-clients/nightly-digest-bot/{action}");
    }

    [Fact]
    public async Task ReplaceAttributes_AsAdmin_UsesPutAndForwardsBody()
    {
        var resp = await AsAdmin().PutAsync(
            "/bff/admin/mcp-clients/nightly-digest-bot/attributes",
            Json("""{"attributes":{"confidentiality":"internal"}}"""),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMcpMethod.Should().Be("PUT");
        _factory.LastMcpPath.Should().Be("/mcp-clients/nightly-digest-bot/attributes");
        _factory.LastMcpBody.Should().Contain("confidentiality");
    }

    // クライアント ID は任意文字列である。**経路へ差し込む前にエスケープしていること**を固定する。
    //
    // 🔴 **空白では測れない。** `HttpRequestMessage` の Uri 正規化が空白を勝手に `%20` へ直すため、
    // エスケープを外しても同じ経路になる（変異試験で実測: 落ちたテスト 0 件）。
    // **経路の構造を変える文字**（`?`）を使う —— エスケープを外すと以降がクエリ文字列になり、
    // 後段は別の資源（`/mcp-clients/a`）を指す。
    [Fact]
    public async Task ToggleClient_EscapesTheClientIdInTheDownstreamPath()
    {
        var resp = await AsAdmin().PostAsync(
            "/bff/admin/mcp-clients/a%3Fb/disable", content: null,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMcpPath.Should().Be("/mcp-clients/a%3Fb/disable");
    }

    [Fact]
    public async Task ListTools_AsAdmin_ReturnsEffectiveToolsAndDrifts()
    {
        var resp = await AsAdmin()
            .GetAsync("/bff/admin/mcp-clients/tools", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("retrieval.search_documents");
        // ADR-0024 §5: 構成ドリフトも同じ応答で返る（画面が「実効構成の参照」を出せる）。
        body.Should().Contain("UndeclaredTool");
        _factory.LastMcpPath.Should().Be("/mcp-clients/tools");
    }

    // ── 2. ロール限定（陽性対照は上の 6 件）────────────────────────────

    [Theory]
    [InlineData("/bff/admin/mcp-clients")]
    [InlineData("/bff/admin/mcp-clients/tools")]
    public async Task Reads_AsOperator_AreForbidden(string path)
    {
        // SC-12 は platform-admin のみ（運用者も不可）。
        var resp = await AsOperator().GetAsync(path, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_AsOperator_IsForbidden()
    {
        var resp = await AsOperator().PostAsync("/bff/admin/mcp-clients",
            Json("""{"clientId":"bot","displayName":"bot","kind":"interactive"}"""),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/bff/admin/mcp-clients")]
    [InlineData("/bff/admin/mcp-clients/tools")]
    public async Task Reads_WhenAnonymous_AreUnauthorized(string path)
    {
        var resp = await Anonymous().GetAsync(path, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 3. 状態コードの透過 ────────────────────────────────────────────

    // 🔴 **後段の 404 を作り替えない。** 403 へ寄せると「拒否された」と読め、200 へ寄せると
    // 不在が成功に化ける。どちらも存在秘匿の設計を BFF 層で壊す。
    [Fact]
    public async Task Disable_WhenDownstreamReturns404_Passes404Through()
    {
        _factory.McpStubStatusCode = HttpStatusCode.NotFound;

        var resp = await AsAdmin().PostAsync(
            "/bff/admin/mcp-clients/no-such-client/disable", content: null,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ADR-0034 決定 9: 無人アカウントへ個人資料を読ませる属性割当は後段が 400 で拒む。
    // **理由（本文）ごと透過する** —— 画面が拒否理由を出せなくなるため。
    [Fact]
    public async Task ReplaceAttributes_WhenDownstreamRejects_Passes400AndBodyThrough()
    {
        _factory.McpStubStatusCode = HttpStatusCode.BadRequest;

        var resp = await AsAdmin().PutAsync(
            "/bff/admin/mcp-clients/nightly-digest-bot/attributes",
            Json("""{"attributes":{"doc_scope":"private-note"}}"""),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("stub-detail");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Register_WhenDownstreamConflicts_Passes409Through()
    {
        _factory.McpStubStatusCode = HttpStatusCode.Conflict;

        var resp = await AsAdmin().PostAsync("/bff/admin/mcp-clients",
            Json("""{"clientId":"dup","displayName":"dup","kind":"interactive"}"""),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ListClients_WhenDownstreamUnreachable_Returns502()
    {
        _factory.McpStubThrows = true;

        var resp = await AsAdmin()
            .GetAsync("/bff/admin/mcp-clients", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ── 4. 資格情報の伝播（陽性対照）────────────────────────────────────

    // 🔴 スタブは Authorization が無ければ 401 を返す（後段の実挙動に合わせてある）。
    // したがって **200 が返ったこと自体が「届いた」の対照**であり、加えて値も観測する。
    [Fact]
    public async Task ListClients_ForwardsTheCallerCredentialToTheDownstream()
    {
        var resp = await AsAdmin()
            .GetAsync("/bff/admin/mcp-clients", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMcpForwardedAuthorization.Should().NotBeNullOrEmpty(
            "後段も AdminOnly を強制するため、利用者の資格情報を伝播しないと二重ゲートが成立しない");
    }
}
