using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-06, FR-09, FR-19, FR-20, FR-21, UC-03, SC-05, SC-09,
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / [[IADR-0398]] 決定 1・3・9:
// **手書きガード節 → FluentValidation の移送が、応答の契約を 1 バイトも変えていないことを固定する。**
//
// 🔴 **既存の 400 の試験はほぼ状態コードしか見ていない。** 鍵（`errors` の下のプロパティ名）と
// メッセージが変わる退行は 400 のままなので**状態コードでは捕まらない**。しかも画面
// （`apiClient.ts` の `parseProblemDetails`）は `errors` の値を鍵に関係なく平坦化して出すため、
// **鍵の退行は機械クライアントだけを壊し、画面では見えない**。だからここで鍵を列挙順に読む。
//
// 🔴 **鍵の「列」を見る**（`Errors[0]` を採る形 = 形 α なので、常に 1 鍵 1 件である）。
// 端点が `FirstViolation` ではなく `ToDictionary()` を呼ぶ変更が入ると、複数違反の要求で
// 鍵が増えてここで止まる。
[Trait("TestKind", "Integration")]
public class ValidationProblemContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す。
    // 文字列へ畳むのは、鍵の列・各鍵のメッセージ列・その順序を **1 つの比較**で見るためである
    // （タプル ＋ 配列のままだと配列が参照比較になり、値が同じでも落ちる）。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    // ── D2 / D6 / D10: 題名と属性（形 α・鍵は PropertyName） ──

    // 🔴 **複数違反でも鍵は 1 つである**（題名も機密区分も欠けている）。
    // 移送前は最初のガード節で返っていた —— `ToDictionary()` で写すとここで鍵が 2 つになる。
    [Fact]
    public async Task Create_EmptyTitleAndMissingConfidentiality_Returns400WithTitleOnly()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "", attributes = new Dictionary<string, string> { ["dept"] = "sales" } },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["title=タイトルは必須です。"]);
    }

    [Fact]
    public async Task Create_MissingConfidentiality_Returns400WithConfidentialityKey()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "ok", attributes = new Dictionary<string, string> { ["dept"] = "sales" } },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["confidentiality=機密区分（confidentiality）は必須です。"]);
    }

    [Fact]
    public async Task Update_EmptyTitle_Returns400WithTitleKey()
    {
        var resp = await Client().PutAsJsonAsync($"/documents/{Guid.NewGuid()}",
            new { title = "", attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["title=タイトルは必須です。"]);
    }

    // 🔴 **位置（P 軸）。** 検証は `FindAsync` より前である ——
    // **不存在の文書 ID ＋ 空題名は 400 であり 404 ではない**（上の試験がまさにその形）。
    // 逆向きの対: ID は不存在だが入力は妥当 → 404。検証を取得の後ろへ動かすと**両方**が壊れる。
    [Fact]
    public async Task Update_UnknownIdWithValidInput_Returns404()
    {
        var resp = await Client().PutAsJsonAsync($"/documents/{Guid.NewGuid()}",
            new { title = "ok", attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateMetadata_UnknownDocScope_Returns400WithDocScopeKey()
    {
        var resp = await Client().PatchAsJsonAsync($"/documents/{Guid.NewGuid()}/metadata",
            new
            {
                attributes = new Dictionary<string, string>
                {
                    ["confidentiality"] = "internal",
                    ["doc_scope"] = "team",
                },
            },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["doc_scope=文書スコープ（doc_scope）の値 'team' は不正です。"
             + "許容値: private-note / organization。"]);
    }

    // ── D3–D5: `RuleSet` の位置（決定 3）。**413 と 400 の対**である ──

    // 🔴 題名あり・本文 1 MB 超・機密区分なし → **413**（400 ではない）。
    // 属性規則を `RuleSet` の外（既定集合）へ出すと入口で走り、ここが 400 になって止まる。
    [Fact]
    public async Task Create_OversizedBodyWithMissingConfidentiality_Returns413()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new
            {
                title = "ok",
                attributes = new Dictionary<string, string> { ["dept"] = "sales" },
                body = new string('あ', 1_100_000),
            },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // 🔴 対の側: 題名あり・本文適正・機密区分なし → **400**。
    // 端点の第 2 の `Validate(req, o => o.IncludeRuleSets(...))` を消すと
    // `Validate(req)` は名前つき集合を走らせないため、ここが **201** になって止まる。
    [Fact]
    public async Task Create_NormalBodyWithMissingConfidentiality_Returns400()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new
            {
                title = "ok",
                attributes = new Dictionary<string, string> { ["dept"] = "sales" },
                body = "# 小さい本文",
            },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should()
            .Equal(["confidentiality=機密区分（confidentiality）は必須です。"]);
    }

    // FR-19: 一般経路での個人資料の作成は拒否（鍵は doc_scope）。
    [Fact]
    public async Task Create_PrivateNoteScope_Returns400WithDocScopeKey()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new
            {
                title = "ok",
                attributes = new Dictionary<string, string>
                {
                    ["confidentiality"] = "internal",
                    ["doc_scope"] = "private-note",
                },
            },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["doc_scope=個人資料（doc_scope=private-note）はこの経路では作成できません。"
             + "/private-notes（SC-19）または Obsidian 同期から作成してください。"]);
    }

    // ── D13: GrantShare（鍵は `errors`。述語 1 本） ──

    [Fact]
    public async Task GrantShare_InvalidSubject_Returns400WithErrorsKey()
    {
        var resp = await Client().PostAsJsonAsync($"/documents/{Guid.NewGuid()}/shares",
            new { subjectType = "team", subjectId = "" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["errors=subjectType は user / group のいずれか、"
             + "subjectId は非空である必要があります。"]);
    }

    // ── D14: PutBody（鍵は `body`。空文字は有効） ──

    [Fact]
    public async Task PutBody_NullBody_Returns400WithBodyKey()
    {
        var resp = await Client().PutAsJsonAsync($"/documents/{Guid.NewGuid()}/body",
            new { body = (string?)null }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["body=本文は必須です。"]);
    }

    // 🔴 位置（P 軸）: 空文字の本文は検証を通り、**不存在 ID なので 404** になる（400 ではない）。
    [Fact]
    public async Task PutBody_EmptyBodyOnUnknownId_Returns404()
    {
        var resp = await Client().PutAsJsonAsync($"/documents/{Guid.NewGuid()}/body",
            new { body = "" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── D1: AddTag（検証は取得・認可・辞書照合より前） ──

    [Fact]
    public async Task AddTag_EmptyName_Returns400BeforeLookup()
    {
        var resp = await Client().PostAsJsonAsync($"/documents/{Guid.NewGuid()}/tags",
            new { name = "   " }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["name=タグ名は必須です。"]);
    }

    // ── D24 / D25: タグ辞書（鍵は `name`） ──

    [Fact]
    public async Task CreateTag_EmptyName_Returns400WithNameKey()
    {
        var resp = await Client().PostAsJsonAsync("/tags", new { name = "   " },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["name=タグ名は必須です。"]);
    }

    // 🔴 位置（P 軸）: **不存在のタグ ID への空名改名は 400 であり 404 ではない。**
    // GraphService の `RenameEdgeType` は逆（取得の後ろ）なので、揃えるとここで止まる。
    [Fact]
    public async Task RenameTag_UnknownIdWithEmptyName_Returns400()
    {
        var resp = await Client().PutAsJsonAsync($"/tags/{Guid.NewGuid()}", new { name = "" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["name=タグ名は必須です。"]);
    }

    // 逆向きの対: 名前は妥当・ID は不存在 → 404。
    [Fact]
    public async Task RenameTag_UnknownIdWithValidName_Returns404()
    {
        var resp = await Client().PutAsJsonAsync($"/tags/{Guid.NewGuid()}", new { name = "契約" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 器そのもの（RFC7807 の外枠）が変わっていないこと ──

    // `Results.ValidationProblem` の外枠（type / title / status）は移送前と同じである
    // （**器は 1 バイトも変えていない。変えたのは辞書の生産側だけ**。[[IADR-0398]] 決定 1）。
    [Fact]
    public async Task ValidationProblem_KeepsRfc7807Envelope()
    {
        var resp = await Client().PostAsJsonAsync("/tags", new { name = "" },
            TestContext.Current.CancellationToken);

        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\","
            + "\"title\":\"One or more validation errors occurred.\",\"status\":400,"
            + "\"errors\":{\"name\":[\"タグ名は必須です。\"]}}");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
