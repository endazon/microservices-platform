using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace DocumentService.Api.Tests;

// FR-06, FR-09, UC-03, SC-05, IADR-0044: 多層防御。文書の書き込みは BFF を迂回しても
// サービス単体で認可を要求する。読み取り（GET）は一般利用者の文書閲覧（SC-03）のため据え置き。
// （TestAuthHandler は常時認証のため 401 は対象外。）
//
// **［#629］運用者の書き込みは 403 になった。** 計画 §SC-05「管理系 3 画面の閲覧ロール」（裁定 Q19）は
// **閲覧を管理者・運用者へ開き、破壊的操作は管理者限定を維持する**と定めており、
// 実装だけが運用者にも開いたままだった。**従前ここには
// `Create_OperatorRole_IsAllowed`（運用者の登録が 201 になることを固定するテスト）が在り、
// それが計画との乖離をそのまま固定していた。**
//
// **「狭める」と「狭めすぎない」を必ず対で置く**（#628 が採った形）——
// 運用者の書き込みが 403 になることだけを固定すると、閲覧まで塞いでも緑のまま通る。
public class DocumentAuthorizationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    [Fact]
    public async Task Create_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        var resp = await client.PostAsJsonAsync("/documents", new { title = "t" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-09, IADR-0044: 全書き込みルートが admin/operator を要求することを網羅検証する。
    // 認可はモデルバインド前に働くため、非権限ロールではボディ・対象存在に関わらず 403。
    // 将来いずれかのルートだけを誤って別グループへ移した際に回帰を検知できるようにする。
    [Theory]
    [InlineData("PUT", "/documents/{id}")]
    [InlineData("PATCH", "/documents/{id}/metadata")]
    [InlineData("POST", "/documents/{id}/publish")]
    [InlineData("POST", "/documents/{id}/archive")]
    [InlineData("DELETE", "/documents/{id}")]
    public async Task Write_NonPrivilegedRole_Returns403(string method, string template)
    {
        var client = ClientAs("viewer");
        var path = template.Replace("{id}", Guid.NewGuid().ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "t" })
        };
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_NonPrivilegedRole_IsAllowed()
    {
        // 読み取りはロール不要（一般利用者の閲覧）。非権限ロールでも 200。
        var client = ClientAs("viewer");
        (await client.GetAsync("/documents")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── #629: 運用者は 6 口すべてで 403（受け入れ基準 1）────────────────────────
    //
    // **`Write_NonPrivilegedRole_Returns403` では代わりにならない。** あちらは `viewer`（ロール無し）で
    // グループ既定を検査しており、**グループ既定は据え置いた**ので `AdminOnly` を 1 つも積まなくても緑になる。
    // **運用者で引くことが、この作業が実際に効いていることの検査である。**
    //
    // **`POST /documents` は含めない。** あの口だけは admin ＋ operator のまま据え置いた
    // —— `ai-stock-trading` の KB 書き込みが BFF を経由せず直接叩いており、その service-account は
    // `platform-operator` しか持たないためである（[[IADR-0075]] が最小権限を理由に admin を却下）。
    // 計画の裁定を待つ。据え置きであることは `Create_OperatorRole_IsStillAllowed` が固定する。
    [Theory]
    [InlineData("PUT", "/documents/{id}")]
    [InlineData("PATCH", "/documents/{id}/metadata")]
    [InlineData("POST", "/documents/{id}/publish")]
    [InlineData("POST", "/documents/{id}/archive")]
    [InlineData("DELETE", "/documents/{id}")]
    public async Task Write_OperatorRole_Returns403(string method, string template)
    {
        var client = ClientAs("platform-operator");
        var path = template.Replace("{id}", Guid.NewGuid().ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "t" })
        };
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── #629: 狭めすぎていないことを対で固定する ──────────────────────────────

    // 受け入れ基準 4: 運用者の**閲覧は従来どおり**（Q19「閲覧は管理者・運用者に開く」）。
    [Theory]
    [InlineData("/documents")]
    [InlineData("/documents/{id}")]
    [InlineData("/documents/{id}/versions")]
    public async Task Read_OperatorRole_IsStillAllowed(string template)
    {
        var client = ClientAs("platform-operator");
        var resp = await client.GetAsync(template.Replace("{id}", Guid.NewGuid().ToString()));

        // 不在の id は 404 になる。**塞がれていないこと**が主張なので、403 でなければよい。
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // 受け入れ基準 3: 管理者は従来どおり登録できる（`AdminOnly` を積んで全員を塞いでいない）。
    [Fact]
    public async Task Create_AdminRole_IsStillAllowed()
    {
        var client = ClientAs("platform-admin");
        var resp = await client.PostAsJsonAsync("/documents",
            new { title = "admin-doc", attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ★ #629: **`POST /documents` だけは運用者に開いたままである**（据え置きの明示）。
    //
    // **これは「狭め忘れ」ではなく意図した据え置きである。** `ai-stock-trading` の KB 書き込み
    // （AST/FR-08・`HttpKnowledgeBaseWriter`）が **BFF を経由せず本口を直接**叩いており、
    // その service-account `ai-stock-trading-kb-writer` は **`platform-operator` しか持たない**
    // （[[IADR-0075]] が最小権限を理由に `platform-admin` の付与を明示的に却下している）。
    // ここへ `AdminOnly` を積むと **AST の KB 書き込みが 403 で止まる**。
    //
    // **このテストが無いと、次に来た人が「狭め漏れ」と読んで塞ぎ、AST を静かに壊す。**
    // 計画の裁定（機械クライアントの扱い）が出るまでこの状態を守る。
    [Fact]
    public async Task Create_OperatorRole_IsStillAllowed_ForMachineClientUntilArbitration()
    {
        var client = ClientAs("platform-operator");
        var resp = await client.PostAsJsonAsync("/documents",
            new { title = "kb-writer-doc", attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "AST の KB 書き込みが operator ロールで本口を直接叩いている（IADR-0075）。裁定まで据え置く");
    }
}
