using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, IADR-0041: /bff/documents の書き込み（作成・更新・公開・アーカイブ・削除）が
// **管理者限定**であること、スコープ外文書の変更は 404 秘匿されること、検証 400・楽観ロック競合 409 を
// 透過することを検証する。各テストはスタブ状態を変えるため直列（共有 fixture を汚さない）。
//
// **［#629］「管理者・運用者」から「管理者限定」へ狭めた。** 計画 §SC-05（裁定 Q19）が
// **閲覧は管理者・運用者／破壊的操作は管理者限定**と定めており、実装だけが運用者にも開いていた。
// **後段（`DocumentEndpoints`）にも同じ制限がある**（[[IADR-0044]] の多層防御）ので、
// **両側を別々のテストが押さえる** —— 片側だけ直すと BFF 迂回で通るか、画面だけ 403 になる。
public class BffDocumentWriteEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentWriteEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        // #1010: write スコープは read と独立（既定は許可・条件なし）。
        _factory.WriteScopeGranted = true;
        _factory.WriteScopeFilters = [];
        _factory.ScopeActionsRequested.Clear();
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentWriteStatusCode = HttpStatusCode.OK;
    }

    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents",
            new { title = "新規文書", attributes = new { confidentiality = "internal" }, tags = new[] { "hr" } }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_AsNonPrivilegedRole_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        var resp = await client.PostAsJsonAsync("/bff/documents", new { title = "x" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── #629: 運用者は 5 口すべてで 403（受け入れ基準 2）───────────────────────
    //
    // **`Create_AsNonPrivilegedRole_IsForbidden` では代わりにならない** ——
    // あちらは `viewer` でグループ既定を検査しており、**グループ既定は据え置いた**ので
    // `AdminOnly` を 1 つも積まなくても緑になる。**運用者で引くことがこの作業の検査である。**
    [Theory]
    [InlineData("POST", "/bff/documents")]
    [InlineData("PUT", "/bff/documents/{id}")]
    [InlineData("POST", "/bff/documents/{id}/publish")]
    [InlineData("POST", "/bff/documents/{id}/archive")]
    [InlineData("DELETE", "/bff/documents/{id}")]
    public async Task Write_AsOperator_IsForbidden(string method, string template)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var path = template.Replace("{id}", BffTestFactory.StubDocumentId.ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "x" })
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 受け入れ基準 4（対）: 運用者の**閲覧は従来どおり**塞がれていない（Q19）。
    // これが無いと、読み取りグループまで狭めてしまっても上のテストは緑のまま通る。
    [Theory]
    [InlineData("/bff/documents")]
    [InlineData("/bff/documents/{id}")]
    [InlineData("/bff/documents/{id}/versions")]
    [InlineData("/bff/documents/{id}/content")]
    public async Task Read_AsOperator_IsStillAllowed(string template)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await client.GetAsync(template.Replace("{id}", BffTestFactory.StubDocumentId.ToString()), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.PostAsJsonAsync("/bff/documents", new { title = "x" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WhenScopeNotGranted_IsForbidden_DenyByDefault()
    {
        // #1010: 作成は write スコープで判定する（read の可否は効かない）。
        _factory.WriteScopeGranted = false;
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents", new { title = "x" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── #1010: 否定形と陽性対照の対（FR-05/FR-06・ADR-0036 D-07） ──────────────
    //
    // **read ポリシーしか持たない主体（read=許可・write=不許可）は文書を作成できない。**
    // 従前は action 省略＝read で解決していたため、この主体が作成できた（#993 と同型）。
    // read を明示的に「許可」へ立てるのが要点 —— read の可否で 403 になっているなら
    // このテストは欠陥を検出できない。
    [Fact]
    public async Task Create_WhenSubjectHasOnlyReadPolicy_IsForbidden()
    {
        _factory.SearchScopeGranted = true;   // read は許可されている
        _factory.WriteScopeGranted = false;   // write ポリシーは 1 件も無い
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents",
            new { title = "read しか持たない主体の作成" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 陽性対照: write スコープが許可なら作成は通り、/authz/scope へは write が発行されている。
    // （403 が「常に 403」で緑になっていないことと、action の書き分けそのものを固定する。）
    [Fact]
    public async Task Create_WhenWriteGranted_Succeeds_AndResolvesWriteAction()
    {
        _factory.ScopeActionsRequested.Clear();
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents",
            new { title = "write を持つ主体の作成", attributes = new { confidentiality = "internal" } }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.ScopeActionsRequested.Should().Equal("write");
    }

    // #1010: 読み取り経路は従来どおり read を発行する（対。書き分けが片側だけ効く実装を落とす）。
    [Fact]
    public async Task Detail_ResolvesReadAction()
    {
        _factory.ScopeActionsRequested.Clear();
        var resp = await _factory.CreateClient().GetAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ScopeActionsRequested.Should().Equal("read");
    }

    // #1010: 既存文書への write グループも write スコープで判定する（read しか持たない主体は
    // 404 秘匿 —— 既存のステータス形〔スコープ外=404〕を保つ）。
    [Theory]
    [InlineData("PUT", "/bff/documents/{id}")]
    [InlineData("POST", "/bff/documents/{id}/publish")]
    [InlineData("POST", "/bff/documents/{id}/archive")]
    [InlineData("DELETE", "/bff/documents/{id}")]
    public async Task Write_WhenSubjectHasOnlyReadPolicy_Returns404(string method, string template)
    {
        _factory.SearchScopeGranted = true;
        _factory.WriteScopeGranted = false;

        var path = template.Replace("{id}", BffTestFactory.StubDocumentId.ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "x" })
        };
        var resp = await _factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_WhenTitleMissing_Passes400Through()
    {
        _factory.DocumentWriteStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents", new { title = "" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_AsAdminInScope_Returns200()
    {
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath,
            new { title = "改訂", attributes = new { confidentiality = "internal" }, tags = Array.Empty<string>(), expectedVersion = 3 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WhenOutOfScope_Returns404()
    {
        // write 許可は secret のみ。対象文書は internal → write スコープ外 → 変更不可（存在秘匿）。
        // #1010: 「Granted だけを見ない」の固定 —— write スコープの文書条件まで適用される。
        _factory.WriteScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath, new { title = "改訂" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_WhenVersionConflict_Passes409Through()
    {
        _factory.DocumentWriteStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath,
            new { title = "改訂", expectedVersion = 1 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_AsAdminInScope_Returns200()
    {
        var resp = await _factory.CreateClient().PostAsync($"{DetailPath}/publish", content: null, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_AsAdminInScope_Returns204()
    {
        var resp = await _factory.CreateClient().DeleteAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WhenOutOfScope_Returns404()
    {
        // #1010: 削除も write スコープの文書条件で判定する。
        _factory.WriteScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().DeleteAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
