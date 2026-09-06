using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Domain;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, FR-21, UC-05, ADR-0004, ADR-0036 D-07,
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1 (b)・8・9:
// **`ResolveScope` の手書きガード節 → FluentValidation の移送が、応答の契約を 1 バイトも
// 変えていないことを固定する**（#1278 PR-C）。
//
// 🔴 **鍵は sink（`AuthzEndpoints.ValidationProblem` の `errors`）が持ち、検証器は持たない**
// （`IADR-0398` 決定 1 (b)）。`errors` バケットは 8 サイトが共有しており、そのうち 6 サイトは
// **全件を載せる形 β**（`AbacValidation` / `UserAssignmentValidation` の配列）である。
// 本 PR が移すのは**形 α の 1 サイト（`ResolveScope`）だけ**であり、
// **sink は形 β のまま残さなければならない**。
//
// 🔴 **既存の `AccessScopeContractTests.ResolveScopeEndpoint_UnknownAction_Returns400` は
// 状態コードしか見ていない。** 鍵（`errors`）とメッセージが変わる退行は 400 のままなので
// 状態コードでは捕まらない —— この端点は SPA ではなく**サービス間の呼び出し先**であり、
// 画面越しに気づく経路が無い。
//
// 🔴 **本ファイルは移送の前に書き、端点を `origin/develop` へ戻した状態でも緑になることを実測した**
// （等価性の直接の証拠。PR-A / PR-B と同じ作法）。
[Trait("TestKind", "Integration")]
public class AuthzValidationProblemContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 応答本文の `errors` を**列挙順のまま** `"<鍵>=<メッセージ列を | で連結>"` へ写す。
    private static async Task<List<string>> ErrorsOf(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("errors").EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(" | ", p.Value.EnumerateArray().Select(v => v.GetString()))}")];
    }

    private Task<HttpResponseMessage> ResolveAsync(string action)
        => factory.CreateClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest($"contract-{Guid.NewGuid():N}"[..24], new Dictionary<string, string>(), action),
            TestContext.Current.CancellationToken);

    // A4: 値域外の action は `errors` バケットの 1 件で返る（形 α）。
    // **リテラルと `PolicyAction.All` から組み立てた文字列の両方**へ当てる ——
    // どちらか片方だけだと、値域が増えたときにメッセージだけが黙って古くなる。
    [Fact]
    public async Task ResolveScope_UnknownAction_Returns400WithErrorsBucket()
    {
        var resp = await ResolveAsync("delete");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            ["errors=action は read / analyze / manage / write のいずれかである必要があります。"]);
        (await ErrorsOf(resp)).Should().Equal(
            [$"errors=action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。"]);
    }

    // 空文字も値域外である（`PolicyAction.IsValid("")` は false）。**述語を写す**証拠。
    [Fact]
    public async Task ResolveScope_EmptyAction_Returns400WithErrorsBucket()
    {
        var resp = await ResolveAsync("");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().Equal(
            [$"errors=action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。"]);
    }

    // 🔴 G 軸（述語の粒度）: `PolicyAction.IsValid` は**完全一致**である（`Trim` も小文字化もしない）。
    // 検証器が `TryParse` 風の寛容な述語を持つとここで割れる。
    [Theory]
    [InlineData("Read")]
    [InlineData(" read ")]
    public async Task ResolveScope_ActionWithCaseOrSpaces_Returns400(string action)
    {
        var resp = await ResolveAsync(action);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(resp)).Should().HaveCount(1);
    }

    // 陽性対照（値域内は通る）。**値は主張しない** —— 共有 InMemory DB のポリシー状態に依存する。
    [Theory]
    [InlineData("read")]
    [InlineData("analyze")]
    [InlineData("manage")]
    [InlineData("write")]
    public async Task ResolveScope_KnownAction_Returns200(string action)
    {
        var resp = await ResolveAsync(action);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 器（RFC7807 の外枠）そのもののバイト比較。
    [Fact]
    public async Task ValidationProblem_KeepsRfc7807Envelope()
    {
        var resp = await ResolveAsync("delete");

        var json = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Be(
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.1\","
            + "\"title\":\"One or more validation errors occurred.\",\"status\":400,"
            + "\"errors\":{\"errors\":"
            + "[\"action は read / analyze / manage / write のいずれかである必要があります。\"]}}");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
