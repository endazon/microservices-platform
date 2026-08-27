using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-06, UC-01/UC-03/UC-07, SC-03: /bff/documents が ABAC スコープ解決 → 文書取得を集約し、
// スコープ外・不在をいずれも 404 で秘匿すること（存在秘匿・IADR-0009/IADR-0038）、本文を
// ストレージ縮退込みで返すことを検証する。各テストはスタブ状態を変えるため直列（共有 fixture を汚さない）。
public class BffDocumentEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // 既定状態へ戻す（テスト間の状態リーク防止）。
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    // ── #989 段 3（FR-19, IADR-0253 決定 1）: BFF の実 HTTP 経路でも分岐（Branches）が効く ──
    //
    // 応答の AllowedFilters（従来の連言）は secret のみ＝対象文書（internal）を許可しないが、
    // 分岐「組織文書」（confidentiality ∈ {internal}）が許可する → 200。
    // **分岐を無視して従来評価へ戻す退行はこのテストが落とす**（従来評価なら 404 になる）。
    [Fact]
    public async Task GetDetail_WhenBranchAllowsButLegacyFiltersDeny_ReturnsDocument()
    {
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        _factory.ScopeBranches =
        [
            new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["tester"])]),
            new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
        ];

        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 陰性対照: 分岐がどれも合致しなければ、AllowedFilters（空＝全件許可の形）でも 404。
    // **分岐がある応答では分岐が正であり、従来値へフォールバックしない**ことを固定する。
    [Fact]
    public async Task GetDetail_WhenNoBranchMatches_Returns404_EvenIfLegacyFiltersWouldAllow()
    {
        _factory.ScopeFilters = []; // 従来評価なら「条件なしで全件許可」
        _factory.ScopeBranches =
            [new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["tester"])])];

        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";

    [Fact]
    public async Task GetDetail_WhenAuthorizedAndAttributesInScope_ReturnsDocument()
    {
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentDto>();
        body!.Title.Should().Be("経費規程 2025");
        body.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetDetail_WhenScopeNotGranted_Returns404_DenyByDefault()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound); // 権限外は存在秘匿
    }

    // スコープは許可されていても、文書属性が許可フィルタに合致しなければ 404（存在秘匿）。
    [Fact]
    public async Task GetDetail_WhenAttributesOutOfScope_Returns404()
    {
        // 許可は confidentiality=secret のみ。文書は internal → 不一致。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 不在（DocumentService が 404）とスコープ外を区別しない（IADR-0009）。
    [Fact]
    public async Task GetDetail_WhenDocumentNotFound_Returns404()
    {
        _factory.DocumentStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // IADR-0038: 後段（DocumentService）不調はいずれも 404 へ縮退する（5xx も存在秘匿・区別しない）。
    [Fact]
    public async Task GetDetail_WhenDocumentServiceFails_Returns404()
    {
        _factory.DocumentStatusCode = HttpStatusCode.InternalServerError;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetList_ReturnsOnlyInScopeDocuments()
    {
        // internal のみ許可 → secret 文書は列挙されない（権限内のみ）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];
        var resp = await _factory.CreateClient().GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentDto>>();
        body.Should().ContainSingle(d => d.Title == "経費規程 2025");
        body.Should().NotContain(d => d.Title == "取締役会議事録");
    }

    [Fact]
    public async Task GetList_WhenScopeNotGranted_ReturnsEmpty()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentDto>>();
        body!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetContent_WhenAuthorized_ReturnsMarkdownAndSourceUri()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/content");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentContentDto>();
        // ストレージ未配備（Null クライアント）のため本文はプレースホルダへ縮退し、タイトルを含む。
        body!.Markdown.Should().Contain("経費規程 2025");
        body.SourceUri.Should().Be("storage://bucket/expense.md");
    }

    [Fact]
    public async Task GetContent_WhenScopeNotGranted_Returns404()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/content");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVersions_WhenAuthorized_ReturnsHistory()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentVersionDto>>();
        body!.Should().HaveCount(2);
        body[0].Version.Should().Be(3);
    }

    [Fact]
    public async Task GetVersions_WhenScopeNotGranted_Returns404()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- FR-06, UC-03, SC-03（#449）: 特定版の取得 ----
    //
    // 計画 FR-06 の射程は「版の作成・一覧・**取得**」まで（［2026-08-23 明確化］・環流 planning#473）。
    // サービス側には端点が在ったが **BFF が露出しておらず、利用者経路から到達できなかった**。

    [Fact]
    public async Task GetVersion_WhenAuthorized_ReturnsSnapshot()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions/2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentVersionDto>();
        body!.Version.Should().Be(2);
        body.DocumentId.Should().Be(BffTestFactory.StubDocumentId);
    }

    // 🔴 存在秘匿: スコープ外は 404。**後段を引く前に落ちる**ことが要点である ——
    // 判定せずに後段を引くと、閲覧できない文書の版メタデータが漏れる。
    [Fact]
    public async Task GetVersion_WhenScopeNotGranted_Returns404()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions/2");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 当該版が無い場合も 404（後段の 404 を透過する）。
    [Fact]
    public async Task GetVersion_WhenVersionMissing_Returns404()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions/99");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 一覧（/versions）と取得（/versions/{n}）が**別の口として振り分く**ことを固定する。
    // ルート順によっては取得が一覧に食われる（あるいは詳細に落ちる）ため、形の違いで見る。
    [Fact]
    public async Task GetVersion_IsRoutedSeparatelyFromTheVersionList()
    {
        var single = await _factory.CreateClient()
            .GetFromJsonAsync<DocumentVersionDto>($"{DetailPath}/versions/3");
        var list = await _factory.CreateClient()
            .GetFromJsonAsync<List<DocumentVersionDto>>($"{DetailPath}/versions");

        single!.Version.Should().Be(3);
        list!.Should().HaveCount(2);
    }
}
