using AwesomeAssertions;
using Knowledge.Bff.Endpoints;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// NFR-09, #656: **エッジ（BFF）で OIDC/JWT を担保する**（計画 `02_requirements` の暫定運用）。
//
// 従前 9 端点に認可が無く、無認証で到達できた。ABAC が fail-closed で空応答へ縮退するため
// 情報漏洩は起きていなかったが、**その安全は「利用者条件が空のポリシーが 1 件も無いこと」だけに
// 支えられていた**（`AbacEvaluator.MatchesUserConditions` は条件が空なら全利用者にマッチする）。
// 端点側に門を置き、防御を 2 枚にする（[[IADR-0044]]）。
//
// **拒否の側だけでは足りない。** 「全部拒否」でも赤くならないので、**通る側と対で固定する**。
public class BffEndpointAuthenticationTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    private HttpClient Anonymous()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    private HttpClient WithRoles(string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    // ── T-19（#656）: 無認証は 401 ────────────────────────────────

    [Theory]
    // SC-01 / SC-02: 横断検索。
    [InlineData("/bff/search")]
    // SC-01 / SC-08: 対象範囲フィルタの候補（ADR-0043）。
    [InlineData("/bff/attribute-values")]
    // SC-01: RAG 回答。
    [InlineData("/bff/analysis/ask")]
    // SC-08: 分析・比較・抽出。
    [InlineData("/bff/analysis/analyze")]
    // SC-01: RAG 回答の SSE。
    [InlineData("/bff/analysis/ask/stream")]
    public async Task PostEndpoints_WhenAnonymous_Return401(string path)
    {
        var resp = await Anonymous().PostAsJsonAsync(path, new { query = "q", key = "tags", question = "q" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "NFR-09 の暫定運用はエッジで認証を担保する");
    }

    [Theory]
    // SC-05: 文書一覧（管理者・運用者）。
    [InlineData("/bff/documents")]
    // SC-03: 文書詳細・本文・版履歴（認証のみ）。
    [InlineData("/bff/documents/11111111-1111-1111-1111-111111111111")]
    [InlineData("/bff/documents/11111111-1111-1111-1111-111111111111/content")]
    [InlineData("/bff/documents/11111111-1111-1111-1111-111111111111/versions")]
    public async Task DocumentReadEndpoints_WhenAnonymous_Return401(string path)
    {
        var resp = await Anonymous().GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T-20（#656）: 認証済みの一般利用者は従来どおり使える（狭めすぎていない側）──

    // 計画 `05_screens` は利用者グループ（SC-01〜04・SC-08）を「ABAC の権限内で全利用者が
    // 利用できる」と定めている。**ロールを要求してはならない。**
    [Fact]
    public async Task Search_AsNonPrivilegedRole_IsAllowed()
    {
        var resp = await WithRoles("viewer").PostAsJsonAsync("/bff/search", new SearchRequest("q"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AttributeValues_AsNonPrivilegedRole_IsAllowed()
    {
        var resp = await WithRoles("viewer")
            .PostAsJsonAsync("/bff/attribute-values", new AttributeValuesRequest(AttributeValueKeys.Tags));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnalysisAsk_AsNonPrivilegedRole_IsAllowed()
    {
        var resp = await WithRoles("viewer")
            .PostAsJsonAsync("/bff/analysis/ask", new AnalysisRequest("問い"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-03: 文書詳細は**一般利用者が SC-01 の出典クリックから遷移する**（計画 `05_screens` SC-01 §アクション）。
    // ここへロールを足すと出典が開けなくなる。
    //
    // 当初は「403 でも 401 でもない」という弱い主張にしていたが、**それでは足りない** ——
    // スコープ外の 404 と混ざるため、ロールを足す変異でも 404 のままなら緑になりうる。
    // テスト基盤は `/authz/scope` を `Granted=true`・フィルタ空（＝全件許可）で、
    // `/documents/{id}` を文書ありでスタブしているので、**200 を直接主張できる**（実測）。
    [Theory]
    [InlineData("")]
    [InlineData("/content")]
    [InlineData("/versions")]
    public async Task DocumentReadEndpoints_AsNonPrivilegedRole_AreAllowed(string suffix)
    {
        var resp = await WithRoles("viewer")
            .GetAsync($"/bff/documents/11111111-1111-1111-1111-111111111111{suffix}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "SC-03 は一般利用者が出典から遷移する");
    }

    // ── T-21（#656）: 文書一覧だけ SC-05 の閲覧ロールに絞る ──────────

    // 計画 `05_screens`（2026-08-05 の裁定）「SC-05/06/07 = 閲覧は管理者・運用者」。
    // 呼び出し元は SC-05 の管理画面ただ 1 つである（実測）。
    [Fact]
    public async Task DocumentList_AsNonPrivilegedRole_IsForbidden()
    {
        var resp = await WithRoles("viewer").GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 狭めすぎていない側。**運用者を落とすと SC-10 で異常に気づいた運用者が原因画面へ入れない**
    // （計画 `05_screens:125` が閲覧を開いた理由そのもの）。
    [Fact]
    public async Task DocumentList_AsOperator_IsAllowed()
    {
        var resp = await WithRoles("platform-operator").GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DocumentList_AsAdmin_IsAllowed()
    {
        var resp = await WithRoles("platform-admin").GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
