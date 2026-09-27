using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthorizationService.Tests.Features.Authz.ValidatePolicy;

// FR-05, FR-09, SC-09, UC-05, #535: ポリシーの dry-run 検証（保存せず検証だけ行う）。
//
// **計画確定（2026-08-05・裁定 Q23）**:「保存せず検証だけ行う口を定める。（中略）
// ローカルでの代用は採らない——『検証は通ったのに保存で矛盾が出る』形になり、検証ボタンへの
// 信頼が失われる。**信頼できない検証ボタンは無いより悪い**（押して安心してから壊す）。」
//
// **本テストの中心は「dry-run と保存が一致すること」である**（T-04）。
// 一致しなくなった瞬間に、この機能は「無いより悪い」ものへ変わる。
[Trait("TestKind", "Integration")]
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
            PolicyBody(UniqueName("妥当")), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "矛盾の有無は要求の成否とは別である");
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken);
        body!.Valid.Should().BeTrue();
        body.Errors.Should().BeEmpty();
    }

    // 受け入れ基準 2: 矛盾は `valid: false` ＋ 理由。**200 で返す**——
    // 「検証した結果、矛盾が 2 件あった」は正常な応答であり、要求の失敗ではない。
    [Fact]
    public async Task Validate_InvalidPolicy_ReturnsErrorsWithOk()
    {
        var res = await Client.PostAsJsonAsync("/authz/policies/validate",
            PolicyBody(name: "", action: "未知のアクション"), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken);
        body!.Valid.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
    }

    // ★ 受け入れ基準 3: **何も保存されない。**
    // dry-run が保存してしまうと、「試しに検証したら壊れた」という最悪の結果になる。
    [Fact]
    public async Task Validate_DoesNotPersistAnything()
    {
        var before = (await Client.GetFromJsonAsync<List<PolicyDto>>("/authz/policies", TestContext.Current.CancellationToken))!.Count;
        var name = UniqueName("保存されない");

        (await Client.PostAsJsonAsync("/authz/policies/validate", PolicyBody(name), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Client.GetFromJsonAsync<List<PolicyDto>>("/authz/policies", TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);
        attr.StatusCode.Should().Be(HttpStatusCode.Created);
        var key = (await attr.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("key").GetString()!;

        var body = PolicyBody(UniqueName("食い違い検出"),
            doc: new Dictionary<string, List<string>> { [key] = ["辞書に無い値"] });

        // dry-run の結果
        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body, TestContext.Current.CancellationToken);
        dryRun.StatusCode.Should().Be(HttpStatusCode.OK);
        var dryRunErrors = (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken))!.Errors;

        // 保存の結果（400 ＋ RFC7807 の errors）
        var save = await Client.PostAsJsonAsync("/authz/policies", body, TestContext.Current.CancellationToken);
        save.StatusCode.Should().Be(HttpStatusCode.BadRequest, "保存側の契約は従来どおり 400 である");
        var problem = await save.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
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

        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body, TestContext.Current.CancellationToken);
        (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken))!.Valid.Should().BeTrue();

        var save = await Client.PostAsJsonAsync("/authz/policies", body, TestContext.Current.CancellationToken);
        save.StatusCode.Should().Be(HttpStatusCode.Created, "dry-run が通ったなら保存も通る");
    }

    // FR-05, SC-09, ADR-0036 D-03, ADR-0121 決定 1 (#1666) T-69: **束縛の検証も dry-run と保存で一致する。**
    // SC-09 の「検証」ボタンが綴り違いの束縛を通し、保存で落ちる（または逆）形にしない。
    [Fact]
    public async Task Validate_AgreesWithSave_OnUnknownBindingVariable()
    {
        var body = PolicyBody(UniqueName("綴り違いの束縛"),
            doc: new Dictionary<string, List<string>> { ["owner"] = ["${current_usr}"] });

        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body, TestContext.Current.CancellationToken);
        var result = (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken))!;
        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("${current_usr}"));

        var save = await Client.PostAsJsonAsync("/authz/policies", body, TestContext.Current.CancellationToken);
        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-68（陽性対照）: SC-09 が作る所有者の read ポリシーは、dry-run も保存も通る（辞書に owner は無い）。
    [Fact]
    public async Task Validate_AgreesWithSave_OnOwnerReadPolicy()
    {
        var body = PolicyBody(UniqueName("所有者は自分の文書を読める"),
            doc: new Dictionary<string, List<string>> { ["owner"] = ["${current_user}"] });

        var dryRun = await Client.PostAsJsonAsync("/authz/policies/validate", body, TestContext.Current.CancellationToken);
        (await dryRun.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken))!.Valid.Should().BeTrue();

        var save = await Client.PostAsJsonAsync("/authz/policies", body, TestContext.Current.CancellationToken);
        save.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
