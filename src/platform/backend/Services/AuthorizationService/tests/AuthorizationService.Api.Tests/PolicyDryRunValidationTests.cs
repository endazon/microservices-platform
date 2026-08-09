using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthorizationService.Api.Tests;

// FR-05, FR-09, SC-09, UC-05, #535: ポリシーの dry-run 検証（保存せず検証だけ行う）。
//
// **計画確定（2026-08-05・裁定 Q23）**:「保存せず検証だけ行う口を定める。（中略）
// ローカルでの代用は採らない——『検証は通ったのに保存で矛盾が出る』形になり、検証ボタンへの
// 信頼が失われる。**信頼できない検証ボタンは無いより悪い**（押して安心してから壊す）。」
//
// **本テストの中心は「dry-run と保存が一致すること」である**（T-04）。
// 一致しなくなった瞬間に、この機能は「無いより悪い」ものへ変わる。
public class PolicyDryRunValidationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client => factory.CreateClient();

    private record ValidateResponse(bool Valid, List<string> Errors);
    private record PolicyDto(Guid Id, string Name, string Action, bool IsActive);

    // InMemory DB はプロセス内で名前共有のため、各テストは一意な名前を用いる。
    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static object PolicyBody(
        string name, string action = "read",
        Dictionary<string, List<string>>? user = null,
        Dictionary<string, List<string>>? doc = null)
        => new
        {
            Name = name,
            Action = action,
            UserConditions = user ?? new Dictionary<string, List<string>>(),
            DocumentConditions = doc ?? new Dictionary<string, List<string>>(),
        };

    // 受け入れ基準 1: 妥当なポリシーは `valid: true`。
    [Fact]
    public async Task Validate_ValidPolicy_ReturnsValid()
    {
        var res = await Client.PostAsJsonAsync("/authz/policies/validate",
            PolicyBody(UniqueName("妥当")));

        res.StatusCode.Should().Be(HttpStatusCode.OK, "矛盾の有無は要求の成否とは別である");
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>();
        body!.Valid.Should().BeTrue();
        body.Errors.Should().BeEmpty();
    }

    // 受け入れ基準 2: 矛盾は `valid: false` ＋ 理由。**200 で返す**——
    // 「検証した結果、矛盾が 2 件あった」は正常な応答であり、要求の失敗ではない。
    [Fact]
    public async Task Validate_InvalidPolicy_ReturnsErrorsWithOk()
    {
        var res = await Client.PostAsJsonAsync("/authz/policies/validate",
            PolicyBody(name: "", action: "未知のアクション"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>();
        body!.Valid.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
    }

    // ★ 受け入れ基準 3: **何も保存されない。**
    // dry-run が保存してしまうと、「試しに検証したら壊れた」という最悪の結果になる。
    [Fact]
    public async Task Validate_DoesNotPersistAnything()
    {
        var before = (await Client.GetFromJsonAsync<List<PolicyDto>>("/authz/policies"))!.Count;
        var name = UniqueName("保存されない");

        (await Client.PostAsJsonAsync("/authz/policies/validate", PolicyBody(name)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Client.GetFromJsonAsync<List<PolicyDto>>("/authz/policies");
        after!.Count.Should().Be(before, "dry-run は副作用を持たない");
        after.Should().NotContain(p => p.Name == name);
    }

    // ★★ 受け入れ基準 4: **dry-run と保存が同じ結果を出す。**
    //
    // **これが本 issue の中心である。** 計画は「検証は通ったのに保存で矛盾が出る」形を名指しで禁じた。
    // 実装は 3 経路（dry-run / POST / PUT）が同じ `ValidatePolicyAsync` を呼ぶことで守っているが、
    // **将来それが割れたらここが落ちる**。
    [Fact]
    public async Task Validate_AgreesWithSave_OnTheSameInput()
    {
        // 辞書に無い値を条件に置く（`ValidatePolicy` が「辞書外の値」として弾く形）。
        var attr = await Client.PostAsJsonAsync("/authz/attributes", new
        {
            Key = UniqueName("dept"),
            Label = "部門",
            AllowedValues = new[] { "hr" },
            Required = false,
            Scope = "document",
        });
        attr.StatusCode.Should().Be(HttpStatusCode.Created);
        var key = (await attr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var body = PolicyBody(UniqueName("食い違い検出"),
            doc: new Dictionary<string, List<string>> { [key] = ["辞書に無い値"] });

        // dry-run の結果
        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body);
        dryRun.StatusCode.Should().Be(HttpStatusCode.OK);
        var dryRunErrors = (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>())!.Errors;

        // 保存の結果（400 ＋ RFC7807 の errors）
        var save = await Client.PostAsJsonAsync("/authz/policies", body);
        save.StatusCode.Should().Be(HttpStatusCode.BadRequest, "保存側の契約は従来どおり 400 である");
        var problem = await save.Content.ReadFromJsonAsync<JsonElement>();
        var saveErrors = problem.GetProperty("errors").GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        dryRunErrors.Should().BeEquivalentTo(saveErrors,
            "検証は通ったのに保存で矛盾が出る、という形にしてはならない（信頼できない検証ボタンは無いより悪い）");
        dryRunErrors.Should().NotBeEmpty("この入力は実際に矛盾している（空同士の一致で通してしまわない）");
    }

    // **妥当な入力でも一致する**（矛盾側だけを見て「一致した」と言わない）。
    [Fact]
    public async Task Validate_AgreesWithSave_WhenInputIsValid()
    {
        var body = PolicyBody(UniqueName("両方通る"));

        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body);
        (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>())!.Valid.Should().BeTrue();

        var save = await Client.PostAsJsonAsync("/authz/policies", body);
        save.StatusCode.Should().Be(HttpStatusCode.Created, "dry-run が通ったなら保存も通る");
    }
}
