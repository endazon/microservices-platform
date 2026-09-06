using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Tests.Features.McpClients;

// FR-16, FR-05, UC-09, SC-12,
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ ADR-0024 / ADR-0062 決定 2・3・§結果 /
// IADR-0371 決定 2 / IADR-0395 決定 2・8 / [[IADR-0398]] 決定 1 (b)・5・8・9:
// **`RegisterClient` の手書きガード節 → FluentValidation の移送が、応答の契約を 1 バイトも
// 変えていないことを固定する**（#1278 PR-C）。
//
// 🔴 **本サービスの sink は 2 つの形を同時に運んでいる。**
// `McpClientEndpoints.Problem(IReadOnlyList<string>)` は「理由をすべて返す」器だが、
// **そこへ流れ込む各サイトの振る舞いは 2 種類ある**（`IADR-0398` コンテキスト）。
//
//   - **形 α（先頭 1 件）**: `RegisterClient` の 3 ガード（M1–M3）と重複登録（M6）。
//     どれも違反を見つけたその場で `return` し、`Problem(string)` が**要素 1 の配列**を作る。
//     → 移送後は検証器の `Errors[0].ErrorMessage` を同じ sink へ渡す。
//   - **形 β（全件）**: `RejectUnassignableAsync`（M4 / M5）。ドメイン関数が返す配列を
//     **そのまま**載せる（個人資料 ＋ 制限プロジェクトで 2 件、`clearance` ＋ タグで 2 件）。
//     → **本 PR では移さない。** 移送のついでに sink を `Errors[0]` へ丸めると、
//     ADR-0062 §結果「拒否理由を丸めない。どの値が外れたかを本文へ載せる」が黙って壊れる。
//
// 🔴 **したがって形 β の「件数と順序」をここで固定する。** 既存試験は
// `body.Should().Contain("finance")` のような部分一致だけで、**2 件が 2 件のまま返ることを
// 誰も見ていない**（1 件へ丸めても緑のままになる）。
//
// 🔴 **鍵は sink（`request`）が持ち、検証器は持たない**（`IADR-0398` 決定 1 (b)）。
// DocumentService（PR-A / PR-B）は `OverridePropertyName` で鍵を明示したが、本サービスは
// 鍵が 1 つしか無いので sink 側が正である。**鍵の正を 2 つ持たない。**
//
// 🔴 **本ファイルは移送の前に書き、端点を `origin/develop` へ戻した状態でも緑になることを実測した**
// （等価性の直接の証拠。PR-A / PR-B と同じ作法）。
[Trait("TestKind", "Integration")]
public class McpValidationProblemContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す
    // （DocumentService の契約試験と同じ写し方。鍵の列も、鍵ごとのメッセージの列も見える）。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    private HttpClient Admin() => factory.CreateClient();

    private HttpClient NonAdmin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        return client;
    }

    // 登録者が配れる集合をヘッダで注入する（`StubRegistrarAttributeResolver`）。
    private HttpClient Registrar(string? clearance = null, string? tags = null)
    {
        var client = factory.CreateClient();
        if (clearance is not null)
            client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, clearance);
        if (tags is not null)
            client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.TagsHeader, tags);
        return client;
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, RegisterMcpClientRequest req)
        => client.PostAsJsonAsync("/mcp-clients", req, TestContext.Current.CancellationToken);

    // ── 形 α: M1 / M2 / M3（移送の対象） ──

    // M1: `clientId` は必須。鍵は sink の `request` である。
    [Fact]
    public async Task RegisterClient_BlankClientId_Returns400WithRequestBucket()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest("   ", "空 ID", "interactive"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["request=clientId は必須です。"]);
    }

    // M2: `kind` の値域。**メッセージは入力値を埋め込む**（補間を含むので const にできない）。
    [Fact]
    public async Task RegisterClient_InvalidKind_Returns400WithRequestBucket()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest("agent-k1", "K1", "robot"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["request=kind の値 'robot' は不正です（interactive / service-account）。"]);
    }

    // M3: `egressTier` の値域。
    [Fact]
    public async Task RegisterClient_InvalidEgressTier_Returns400WithRequestBucket()
    {
        var resp = await RegisterAsync(Admin(),
            new RegisterMcpClientRequest("agent-t1", "T1", "interactive", null, "moon"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["request=egressTier の値 'moon' は不正です。"]);
    }

    // 🔴 O 軸（宣言順が契約）＋ 形 α の証明: 3 つとも不正でも**返るのは先頭 1 件**である。
    // 移送後に端点が `result.ToDictionary()` を呼ぶと 1 鍵 3 件になり、ここで止まる。
    [Fact]
    public async Task RegisterClient_AllThreeInvalid_ReturnsOnlyClientIdMessage()
    {
        var resp = await RegisterAsync(Admin(),
            new RegisterMcpClientRequest("", "全部不正", "robot", null, "moon"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(["request=clientId は必須です。"]);
    }

    // 🔴 G 軸（述語の粒度）: `TryParseKind` は `Trim().ToLowerInvariant()` してから比較する。
    // 検証器が自前の集合比較を持つとここで割れる（**同じ関数を共有する**ことの試験）。
    [Fact]
    public async Task RegisterClient_KindWithCaseAndSpaces_IsAccepted()
    {
        var resp = await RegisterAsync(Admin(),
            new RegisterMcpClientRequest("agent-case", "大小空白", " Service-Account "));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 🔴 G 軸: `egressTier` の**未指定は有効**（既定は最も低い保護水準）。
    // `NotEmpty()` へ置き換えると落ちる。
    [Fact]
    public async Task RegisterClient_MissingEgressTier_IsAccepted()
    {
        var resp = await RegisterAsync(Admin(),
            new RegisterMcpClientRequest("agent-tier-none", "既定ティア", "interactive"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── P 軸（判定の位置） ──

    // 🔴 **認可が検証より前**である（SC-12「管理者限定」）。無資格の呼び出しに入力の形を教えない。
    [Fact]
    public async Task RegisterClient_NonAdminWithInvalidKind_Returns403()
    {
        var resp = await RegisterAsync(NonAdmin(), new RegisterMcpClientRequest("agent-403", "X", "robot"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 🔴 検証は `RejectUnassignableAsync`（M4）より**前**である。対の陽性対照が下にある。
    [Fact]
    public async Task RegisterClient_InvalidKindWithForbiddenAttribute_ReturnsKindMessage()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest(
            "agent-pos1", "位置1", "robot",
            new Dictionary<string, string> { [DocumentScope.Key] = DocumentScope.PrivateNote }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["request=kind の値 'robot' は不正です（interactive / service-account）。"]);
    }

    // 対（上の否定形）: `kind` が妥当なら属性の判定まで進む。
    [Fact]
    public async Task RegisterClient_ValidKindWithForbiddenAttribute_ReturnsAttributeMessage()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest(
            "agent-pos2", "位置2", "service-account",
            new Dictionary<string, string> { [DocumentScope.Key] = DocumentScope.PrivateNote }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal([
            "request=サービスアカウント 'agent-pos2' へ doc_scope=private-note は割り当てられません"
            + "（個人資料を読ませる属性割当は構成上禁止）。"]);
    }

    // 🔴 検証は重複登録の照会（M6。`AnyAsync`）より**前**である。
    [Fact]
    public async Task RegisterClient_InvalidKindWithDuplicateClientId_ReturnsKindMessage()
    {
        var client = Admin();
        (await RegisterAsync(client, new RegisterMcpClientRequest("agent-dup1", "重複1", "interactive")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await RegisterAsync(client, new RegisterMcpClientRequest("agent-dup1", "重複1", "robot"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["request=kind の値 'robot' は不正です（interactive / service-account）。"]);
    }

    // 対（上の陽性対照）: 入力が妥当なら重複の照会まで進む。**このガードは移さない**（DB 照会）。
    [Fact]
    public async Task RegisterClient_ValidInputWithDuplicateClientId_ReturnsDuplicateMessage()
    {
        var client = Admin();
        (await RegisterAsync(client, new RegisterMcpClientRequest("agent-dup2", "重複2", "interactive")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await RegisterAsync(client, new RegisterMcpClientRequest("agent-dup2", "重複2", "interactive"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["request=クライアント 'agent-dup2' は既に登録されています。"]);
    }

    // ── 🔴 形 β（M4 / M5。移さない側）の件数と順序 ──

    // M4: 個人資料 ＋ 制限プロジェクトの同時割当は **2 件**返る。順序は
    // `ValidateServiceAccountAttributes` の宣言順（doc_scope → projects）である。
    // **sink を `Errors[0]` へ丸めるとここが 1 件になって赤になる。**
    [Fact]
    public async Task RegisterClient_ForbiddenScopeAndRestrictedProject_ReturnsBothInDeclarationOrder()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest(
            "sa-beta1", "β1", "service-account",
            new Dictionary<string, string>
            {
                [DocumentScope.Key] = DocumentScope.PrivateNote,
                [RestrictedProject.SubjectKey] = "ai-stock-trading",
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal([
            "request=サービスアカウント 'sa-beta1' へ doc_scope=private-note は割り当てられません"
            + "（個人資料を読ませる属性割当は構成上禁止）。"
            + " | "
            + "サービスアカウント 'sa-beta1' へ projects の値 'ai-stock-trading' は割り当てられません"
            + "（MCP から外すプロジェクトの資料を読ませる属性割当は構成上禁止）。"]);
    }

    // M5: `clearance` ＋ タグの同時逸脱も **2 件**返る（宣言順は clearance → tags）。
    [Fact]
    public async Task RegisterClient_ClearanceAndTagsOutsideRegistrar_ReturnsBothInDeclarationOrder()
    {
        var resp = await RegisterAsync(Registrar(clearance: "public", tags: "sales"),
            new RegisterMcpClientRequest("sa-beta2", "β2", "service-account",
                new Dictionary<string, string>
                {
                    ["clearance"] = "confidential",
                    ["tags"] = "finance",
                }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal([
            "request=clearance の値 'confidential' は割り当てられません（登録者が持つ機密区分は 'public' です）。"
            + " | "
            + "tags の値 'finance' は割り当てられません（登録者が持つタグは 'sales' です）。"]);
    }

    // ── 器（RFC7807 の外枠）そのもののバイト比較 ──

    [Fact]
    public async Task ValidationProblem_KeepsRfc7807Envelope()
    {
        var resp = await RegisterAsync(Admin(), new RegisterMcpClientRequest("", "器", "interactive"));

        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\","
            + "\"title\":\"One or more validation errors occurred.\",\"status\":400,"
            + "\"errors\":{\"request\":[\"clientId は必須です。\"]}}");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
