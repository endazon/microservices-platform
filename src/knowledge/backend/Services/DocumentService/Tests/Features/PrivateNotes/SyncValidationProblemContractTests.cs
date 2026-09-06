using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Push;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, FR-20, UC-11, SC-19, SC-20,
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ ADR-0037 決定 2・7・8・11・20 /
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・3・9:
// **`PrivateNotes` / `ObsidianSync` / `SyncDevices` 集約の手書きガード節 → FluentValidation の移送が、
// 応答の契約を 1 バイトも変えていないことを固定する**（#1278 PR-B）。
//
// 🔴 **これらの端点の既存の 400 の試験は 3 本しかなく、いずれも状態コードしか見ていない**
// （`ObsidianSyncMoveTests:171` と `PrivateNoteQuotaTests:255,259`）。鍵（`errors` の下の
// プロパティ名）とメッセージが変わる退行は 400 のままなので**状態コードでは捕まらない**。
// しかも画面（`apiClient.ts` の `parseProblemDetails`）は `errors` の値を鍵に関係なく平坦化するため、
// **鍵の退行は機械クライアント（Obsidian プラグイン）だけを壊し、画面では見えない**。
//
// 🔴 **本ファイルは移送の前に書き、端点を `origin/develop` へ戻した状態でも緑になることを実測した**
// （等価性の直接の証拠。PR-A と同じ作法）。
//
// 🔴 **鍵の「列」を見る**（本 PR の 7 サイトはすべて形 α ＝ ガードごとに即 `return` するため、
// 常に 1 鍵 1 件である）。端点が `FirstViolation` ではなく `ToDictionary()` を呼ぶ変更が入ると、
// 複数違反の要求で鍵が増えてここで止まる。
[Trait("TestKind", "Integration")]
public class SyncValidationProblemContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す
    // （`Features/Documents/ValidationProblemContractTests` と同じ写し方）。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private HttpClient PluginWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string User(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<string> IssueTokenAsync(string user)
    {
        var resp = await SessionAs(user).PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "contract-device" }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
    }

    private static object PushBody(string title, string path, string content,
        Guid? noteId = null, int? baseVersion = null) => new
        {
            noteId,
            vaultPath = path,
            title,
            baseVersion,
            edits = new[] { new { content } },
        };

    // ── D23: SyncDevices/Issue（鍵は `deviceName`） ──

    [Fact]
    public async Task IssueDevice_BlankName_Returns400WithDeviceNameKey()
    {
        var resp = await SessionAs(User("dev")).PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "   " }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["deviceName=端末名は必須です。"]);
    }

    // 🔴 位置（P 軸）: **401 が検証より前**である。名前を持たない主体（機械クライアント）は
    // 入力が不正でも 401 であり、400 ではない —— 無資格の呼び出しに入力の形を教えない。
    [Fact]
    public async Task IssueDevice_NoSubjectWithBlankName_Returns401()
    {
        var client = SessionAs(User("dev"));
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");

        var resp = await client.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── D20: PrivateNotes/Create（鍵は `title`） ──

    [Fact]
    public async Task CreatePrivateNote_BlankTitle_Returns400WithTitleKey()
    {
        var resp = await SessionAs(User("pn")).PostAsJsonAsync("/private-notes/",
            new { title = "   " }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["title=タイトルは必須です。"]);
    }

    [Fact]
    public async Task CreatePrivateNote_NoSubjectWithBlankTitle_Returns401()
    {
        var client = SessionAs(User("pn"));
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");

        var resp = await client.PostAsJsonAsync("/private-notes/", new { title = "" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── D21: PrivateNotes/Purge（鍵は `ids`） ──

    [Fact]
    public async Task Purge_EmptyIds_Returns400WithIdsKey()
    {
        var resp = await SessionAs(User("pg")).PostAsJsonAsync("/private-notes/purge",
            new { ids = Array.Empty<Guid>() }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["ids=完全削除する資料の ID を 1 件以上指定してください。"]);
    }

    // 🔴 述語（G 軸）: `ids` が **null**（項目ごと欠けている）でも同じ 1 件である
    // （`req.Ids is not { Count: > 0 }` は null も空も同じに扱う）。
    [Fact]
    public async Task Purge_NullIds_Returns400WithIdsKey()
    {
        var resp = await SessionAs(User("pg")).PostAsJsonAsync("/private-notes/purge",
            new { ids = (Guid[]?)null }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["ids=完全削除する資料の ID を 1 件以上指定してください。"]);
    }

    // 🔴 位置（P 軸）の逆向きの対: **ids は妥当・資料は不存在 → 404**（400 ではない）。
    // 検証を DB 照会の後ろへ動かすと壊れる。
    [Fact]
    public async Task Purge_UnknownIds_Returns404()
    {
        var resp = await SessionAs(User("pg")).PostAsJsonAsync("/private-notes/purge",
            new { ids = new[] { Guid.NewGuid() } }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── D16 / D17: ObsidianSync/Move（鍵は `vaultPath` → `version`。宣言順が契約） ──

    // 🔴 **両方欠けていても鍵は 1 つ（`vaultPath`）である。** 移送前は最初のガード節で返っていた
    // —— `ToDictionary()` で写すとここで鍵が 2 つになる（O 軸 ＋ 形 α の固定）。
    [Fact]
    public async Task Move_BlankVaultPathAndNoVersion_Returns400WithVaultPathOnly()
    {
        var token = await IssueTokenAsync(User("mv"));

        var resp = await PluginWith(token).PostAsJsonAsync(
            $"/private-notes/sync/notes/{Guid.NewGuid()}/move",
            new { vaultPath = "   ", version = (int?)null }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["vaultPath=移動先の vaultPath を指定してください。"]);
    }

    [Fact]
    public async Task Move_NoVersion_Returns400WithVersionKey()
    {
        var token = await IssueTokenAsync(User("mv"));

        var resp = await PluginWith(token).PostAsJsonAsync(
            $"/private-notes/sync/notes/{Guid.NewGuid()}/move",
            new { vaultPath = "notes/x.md", version = (int?)null },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["version=リネームには version（最後に見た版）が必須です。"]);
    }

    // 🔴 位置（P 軸）の対: **不存在の資料 ＋ 空の vaultPath は 400**（上の 2 本）、
    // **不存在の資料 ＋ 妥当な入力は 404** である。検証を `FindOwnedAsync` の後ろへ動かすと
    // 前者が 404 に化ける。
    [Fact]
    public async Task Move_UnknownNoteWithValidInput_Returns404()
    {
        var token = await IssueTokenAsync(User("mv"));

        var resp = await PluginWith(token).PostAsJsonAsync(
            $"/private-notes/sync/notes/{Guid.NewGuid()}/move",
            new { vaultPath = "notes/x.md", version = 1 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 位置（P 軸）: **401（同期トークンの解決）が検証より前**である。
    [Fact]
    public async Task Move_WithoutTokenAndBlankVaultPath_Returns401()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync(
            $"/private-notes/sync/notes/{Guid.NewGuid()}/move",
            new { vaultPath = "", version = (int?)null }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── D18: ObsidianSync/Push 入口（鍵は `errors`。述語は 1 本の 4 項 `||`） ──

    [Fact]
    public async Task Push_AllFieldsInvalid_Returns400WithErrorsKey()
    {
        var token = await IssueTokenAsync(User("ps"));

        var resp = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            new { title = "", vaultPath = "", edits = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["errors=title / vaultPath / edits（1 件以上・content 必須）を指定してください。"]);
    }

    // 🔴 述語（G 軸）: `edits` はあるが `content` が null —— これも同じ 1 件である。
    [Fact]
    public async Task Push_EditWithNullContent_Returns400WithErrorsKey()
    {
        var token = await IssueTokenAsync(User("ps"));

        var resp = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            new
            {
                title = "ok",
                vaultPath = "notes/ok.md",
                edits = new[] { new { content = (string?)null } },
            },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["errors=title / vaultPath / edits（1 件以上・content 必須）を指定してください。"]);
    }

    // 🔴 位置（P 軸）: 入口の検証は **413（本文上限）より前**である ——
    // 「題名なし ＋ 本文 1 MB 超」は **400**（413 ではない）。
    [Fact]
    public async Task Push_BlankTitleWithOversizedBody_Returns400()
    {
        var token = await IssueTokenAsync(User("ps"));

        var resp = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("", "notes/big.md", new string('あ', 1_100_000)),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["errors=title / vaultPath / edits（1 件以上・content 必須）を指定してください。"]);
    }

    // 対の側: 入力は妥当・本文が 1 MB 超 → **413**。
    [Fact]
    public async Task Push_ValidRequestWithOversizedBody_Returns413()
    {
        var token = await IssueTokenAsync(User("ps"));

        var resp = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("ok", "notes/big.md", new string('あ', 1_100_000)),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Push_WithoutTokenAndInvalidInput_Returns401()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/private-notes/sync/notes",
            new { title = "", vaultPath = "", edits = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── D19: ObsidianSync/Push の `baseVersion`（**更新分岐の 404 の後ろ**。RuleSet の位置） ──

    // 🔴 位置（P 軸）の**対の片側**: 実在する自分の資料 ＋ `baseVersion` なし → **400** `baseVersion`。
    // 端点の第 2 の `Validate(req, o => o.IncludeRuleSets(...))` を消すと、
    // `Validate(req)` は名前つき集合を走らせないため、ここが壊れる。
    [Fact]
    public async Task Push_ExistingNoteWithoutBaseVersion_Returns400WithBaseVersionKey()
    {
        var token = await IssueTokenAsync(User("bv"));
        var plugin = PluginWith(token);

        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("メモ", $"notes/{Guid.NewGuid():N}.md", "# 初版"),
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken);

        var resp = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("メモ", "notes/renamed.md", "# 第 2 版", noteId: note!.NoteId),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["baseVersion=既存資料の更新には baseVersion が必須です。"]);
    }

    // 🔴 位置（P 軸）の**対のもう片側**: **不存在の noteId ＋ `baseVersion` なし → 404**（400 ではない）。
    // `baseVersion` の規則を `RuleSet` の外（既定集合）へ出すと入口で走り、ここが 400 になって止まる。
    [Fact]
    public async Task Push_UnknownNoteWithoutBaseVersion_Returns404()
    {
        var token = await IssueTokenAsync(User("bv"));

        var resp = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("メモ", "notes/unknown.md", "# 本文", noteId: Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 器そのもの（RFC7807 の外枠）が変わっていないこと ──

    // `Results.ValidationProblem` の外枠（type / title / status）と本文を**生の JSON で**比較する
    // （**器は 1 バイトも変えていない。変えたのは辞書の生産側だけ**。[[IADR-0398]] 決定 1）。
    [Fact]
    public async Task ValidationProblem_KeepsRfc7807Envelope()
    {
        var resp = await SessionAs(User("env")).PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "" }, TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\","
            + "\"title\":\"One or more validation errors occurred.\",\"status\":400,"
            + "\"errors\":{\"deviceName\":[\"端末名は必須です。\"]}}");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
