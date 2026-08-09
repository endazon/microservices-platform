using FluentAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Platform.Bff.Tests;

// FR-09, SC-09, UC-05, #640: `/bff/tags`（タグ辞書の管理）を検証する。
//
// **#634 / #635 は口を DocumentService 側にだけ置いたので、画面は辞書を読めても書けなかった。**
// 本テストは (1) 4 口が BFF を通ること、(2) **書き込みが管理者限定**であること（[[IADR-0044]] の
// 多層防御の BFF 側）、(3) **削除拒否の 409 が使用件数つきで透過される**ことを押さえる。
//
// 各テストはスタブ状態を変えるため、**観測する前に既定へ戻す**（共有 fixture を汚さない）。
public class BffTagDictionaryEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffTagDictionaryEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.TagDictionaryStatusCode = HttpStatusCode.OK;
        _factory.TagWriteStatusCode = HttpStatusCode.OK;
        _factory.StubDeleteUsageCount = 0;
        _factory.StubRenameRepublished = 2;
    }

    private static string DetailPath => $"/bff/tags/{BffTestFactory.StubCreatedTagId}";

    private HttpClient ClientAs(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);
        return client;
    }

    // ── 通ること ─────────────────────────────────────────────

    [Fact]
    public async Task List_AsAdmin_ReturnsDictionaryWithUsageCounts()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/tags");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TagDictionaryResponse>();
        body!.Tags.Should().NotBeEmpty();
        body.Tags[0].UsageCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/tags", new { name = "新規タグ" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // FR-09, SC-09, #640: 改名の応答は **`RepublishedDocuments` ごと**通す。
    // **[[IADR-0153]] 決定 3** —— 射影（Qdrant / Wiki.js）の反映は非同期なので、
    // 件数が無いと管理者は「0 件だった」と「まだ届いていない」を切り分けられない。
    [Fact]
    public async Task Rename_AsAdmin_RelaysRepublishedCount()
    {
        _factory.StubRenameRepublished = 7;

        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath, new { name = "改名後" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<RenameTagResponse>();
        body!.RepublishedDocuments.Should().Be(7, "改名が下流へ何件波及したかを画面が示せる必要がある");
    }

    [Fact]
    public async Task Delete_AsAdmin_WhenUnused_Returns204()
    {
        _factory.StubDeleteUsageCount = 0;

        var resp = await _factory.CreateClient().DeleteAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── ★ 削除拒否の 409 は**使用件数つきで**透過する（受け入れ基準 3）──────────
    //
    // **SC-09 は「削除前に使用件数を示す」と定めている。** BFF が本文を詰め替えると
    // 件数が画面へ届かず、管理者は「まず 3 件を外してから消す」と行動できない。
    //
    // **BFF で数え直さないことも同時に固定している** —— 後段の値がそのまま出ることを見ており、
    // 数え方を 2 つ持てば一覧と削除拒否で件数が割れる（[[IADR-0153]] 決定 6 の趣旨）。
    [Fact]
    public async Task Delete_AsAdmin_WhenInUse_Returns409WithUsageCount()
    {
        _factory.StubDeleteUsageCount = 3;

        var resp = await _factory.CreateClient().DeleteAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("usageCount").GetInt32().Should().Be(3);
        json.GetProperty("error").GetString().Should().Be("tag_in_use");
    }

    // 追加・改名の 409（名前の重複）も透過する。SC-09「新しい名前は既存値と重複しない」。
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Write_WhenNameDuplicated_Relays409(string method)
    {
        _factory.TagWriteStatusCode = HttpStatusCode.Conflict;
        var client = _factory.CreateClient();

        var resp = method == "POST"
            ? await client.PostAsJsonAsync("/bff/tags", new { name = "経理" })
            : await client.PutAsJsonAsync(DetailPath, new { name = "経理" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── ★ 書き込みは管理者限定（受け入れ基準 4 の BFF 側）────────────────────
    //
    // **`viewer` で引くテストでは代わりにならない。** 読み取り群は admin ＋ operator なので、
    // **`AdminOnly` を 1 つも積まなくても `viewer` は 403 になる**（#629 で同じ穴を実測した）。
    // **運用者で引くことがこの作業の検査である。**
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Write_AsOperator_IsForbidden(string method)
    {
        var client = ClientAs("platform-operator");

        var resp = method switch
        {
            "POST" => await client.PostAsJsonAsync("/bff/tags", new { name = "x" }),
            "PUT" => await client.PutAsJsonAsync(DetailPath, new { name = "x" }),
            _ => await client.DeleteAsync(DetailPath),
        };

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "SC-09 のアクセス制御はシステム管理者ロール限定である");
    }

    // **狭めすぎない。** 読み取りは運用者に開いたままである（裁定 Q18。[[IADR-0152]] 決定 5）。
    // これが無いと、書き込みを狭めるついでに読み取りまで塞いでも誰も気づかない。
    [Fact]
    public async Task List_AsOperator_IsStillAllowed()
    {
        var resp = await ClientAs("platform-operator").GetAsync("/bff/tags");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 一般利用者は読み取りも含めて 403 である（辞書は管理面。ADR-0043 決定 1 が
    // 一般利用者へ辞書を丸ごと返すことを禁じている）。
    [Fact]
    public async Task List_AsViewer_IsForbidden()
    {
        var resp = await ClientAs("viewer").GetAsync("/bff/tags");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
