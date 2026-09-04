using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Authz;

// FR-09, UC-05, ADR-0004: 属性辞書・ABAC ポリシー管理エンドポイントの結合テスト。
// ※ InMemory DB はプロセス内で名前共有のため、各テストは衝突回避に一意なキー／名前を用いる。
[Trait("TestKind", "Integration")]
public class AuthzManagementEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client => factory.CreateClient();

    private record AttributeDto(Guid Id, string Key, string Label, List<string> AllowedValues, bool Required, string Scope);
    private record PolicyDto(Guid Id, string Name, string Action, bool IsActive);
    private record ValidateResponse(bool Valid, List<string> Errors);

    private static object AttributeBody(string key, List<string> allowed, bool required = false, string scope = "document")
        => new { Key = key, Label = $"{key}-label", AllowedValues = allowed, Required = required, Scope = scope };

    // ---- 属性辞書 ----

    // FR-09: 属性辞書の登録→個別取得
    [Fact]
    public async Task CreateThenGetAttribute_Roundtrips()
    {
        var create = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_create_get", ["hr", "eng"]), TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<AttributeDto>(TestContext.Current.CancellationToken);
        created!.AllowedValues.Should().BeEquivalentTo("hr", "eng");

        var get = await Client.GetAsync($"/authz/attributes/{created.Id}", TestContext.Current.CancellationToken);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-09: 許可値が空なら 400
    [Fact]
    public async Task CreateAttribute_EmptyAllowedValues_Returns400()
    {
        var res = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_empty", []), TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-09: 同一スコープでキー重複なら 400
    [Fact]
    public async Task CreateAttribute_DuplicateKey_Returns400()
    {
        var first = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_dup", ["a"]), TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_dup", ["b"]), TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-09: 属性辞書の更新（許可値の差し替え）
    [Fact]
    public async Task UpdateAttribute_ChangesAllowedValues()
    {
        var create = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_update", ["a"]), TestContext.Current.CancellationToken);
        var created = await create.Content.ReadFromJsonAsync<AttributeDto>(TestContext.Current.CancellationToken);

        var update = await Client.PutAsJsonAsync($"/authz/attributes/{created!.Id}",
            new { Label = "更新後", AllowedValues = new[] { "a", "b", "c" }, Required = true }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<AttributeDto>(TestContext.Current.CancellationToken);
        updated!.AllowedValues.Should().BeEquivalentTo("a", "b", "c");
        updated.Required.Should().BeTrue();

        // 後片付け: 本クラスは InMemory DB を共有するため、Required=true な属性を残すと
        // 他テスト（文書属性検証）の必須充足判定を汚染する。削除して分離する。
        await Client.DeleteAsync($"/authz/attributes/{created.Id}", TestContext.Current.CancellationToken);
    }

    // FR-09: 属性辞書の削除→取得で 404
    [Fact]
    public async Task DeleteAttribute_ThenGet_Returns404()
    {
        var create = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("dept_delete", ["a"]), TestContext.Current.CancellationToken);
        var created = await create.Content.ReadFromJsonAsync<AttributeDto>(TestContext.Current.CancellationToken);

        var del = await Client.DeleteAsync($"/authz/attributes/{created!.Id}", TestContext.Current.CancellationToken);
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await Client.GetAsync($"/authz/attributes/{created.Id}", TestContext.Current.CancellationToken);
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- ポリシー ----

    // FR-09: ポリシー登録→更新→無効化→削除
    [Fact]
    public async Task PolicyLifecycle_CreateUpdateDeactivateDelete()
    {
        var create = await Client.PostAsJsonAsync("/authz/policies", new
        {
            Name = "policy_lifecycle",
            Action = "read",
            UserConditions = new Dictionary<string, string[]> { ["role"] = ["admin"] },
            DocumentConditions = new Dictionary<string, string[]>()
        }, TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<PolicyDto>(TestContext.Current.CancellationToken);
        created!.IsActive.Should().BeTrue();

        var update = await Client.PutAsJsonAsync($"/authz/policies/{created.Id}", new
        {
            Name = "policy_lifecycle_v2",
            Action = "analyze",
            UserConditions = new Dictionary<string, string[]> { ["role"] = ["admin"] },
            DocumentConditions = new Dictionary<string, string[]>()
        }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<PolicyDto>(TestContext.Current.CancellationToken))!.Action.Should().Be("analyze");

        var patch = await Client.PatchAsJsonAsync($"/authz/policies/{created.Id}/active",
            new { IsActive = false }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<PolicyDto>(TestContext.Current.CancellationToken))!.IsActive.Should().BeFalse();

        var del = await Client.DeleteAsync($"/authz/policies/{created.Id}", TestContext.Current.CancellationToken);
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // FR-09: 存在しないポリシー ID への取得／更新／無効化／削除は 404
    [Fact]
    public async Task PolicyOperations_NonExistentId_Return404()
    {
        var id = Guid.NewGuid();

        (await Client.GetAsync($"/authz/policies/{id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var put = await Client.PutAsJsonAsync($"/authz/policies/{id}", new
        {
            Name = "ghost",
            Action = "read",
            UserConditions = new Dictionary<string, string[]>(),
            DocumentConditions = new Dictionary<string, string[]>()
        }, TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var patch = await Client.PatchAsJsonAsync($"/authz/policies/{id}/active",
            new { IsActive = false }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Client.DeleteAsync($"/authz/policies/{id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-09, FR-05: 条件を省略（null）したポリシーでも保存でき、/scope が 500 で落ちない。
    // 省略された条件は「条件なし」（空辞書）として扱われる（NRE 回帰防止）。
    [Fact]
    public async Task CreatePolicy_OmittedConditions_ScopeDoesNotCrash()
    {
        var create = await Client.PostAsJsonAsync("/authz/policies", new
        {
            Name = "policy_no_conditions",
            Action = "read"
            // UserConditions / DocumentConditions を意図的に省略
        }, TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var scope = await Client.PostAsJsonAsync("/authz/scope", new
        {
            UserId = "u-omit",
            UserAttributes = new Dictionary<string, string> { ["role"] = "member" }
        }, TestContext.Current.CancellationToken);
        scope.StatusCode.Should().Be(HttpStatusCode.OK);

        // 後片付け: 条件なしの有効ポリシーは全利用者にマッチするため削除しておく。
        var created = await create.Content.ReadFromJsonAsync<PolicyDto>(TestContext.Current.CancellationToken);
        await Client.DeleteAsync($"/authz/policies/{created!.Id}", TestContext.Current.CancellationToken);
    }

    // FR-09: 管理者ロールを持たない呼び出しは管理系エンドポイントで 403
    [Fact]
    public async Task ManagementEndpoint_WithoutAdminRole_Returns403()
    {
        // 管理者以外のロールで認証させる（空ヘッダ値は HTTP で送信されず既定 admin に戻るため使わない）。
        var req = new HttpRequestMessage(HttpMethod.Get, "/authz/policies");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");
        var res = await Client.SendAsync(req, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-09, IADR-0006: ポリシーが参照中の属性辞書は削除できない（409）
    [Fact]
    public async Task DeleteAttribute_ReferencedByPolicy_Returns409()
    {
        var attr = await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("conf_del_ref", ["public", "internal"]), TestContext.Current.CancellationToken);
        var created = await attr.Content.ReadFromJsonAsync<AttributeDto>(TestContext.Current.CancellationToken);

        var policy = await Client.PostAsJsonAsync("/authz/policies", new
        {
            Name = "policy_ref_attr",
            Action = "read",
            UserConditions = new Dictionary<string, string[]>(),
            DocumentConditions = new Dictionary<string, string[]> { ["conf_del_ref"] = ["public"] }
        }, TestContext.Current.CancellationToken);
        policy.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdPolicy = await policy.Content.ReadFromJsonAsync<PolicyDto>(TestContext.Current.CancellationToken);

        var del = await Client.DeleteAsync($"/authz/attributes/{created!.Id}", TestContext.Current.CancellationToken);
        del.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 後片付け: 参照ポリシーを消してから属性も削除する（他テストへの汚染回避）。
        await Client.DeleteAsync($"/authz/policies/{createdPolicy!.Id}", TestContext.Current.CancellationToken);
        await Client.DeleteAsync($"/authz/attributes/{created.Id}", TestContext.Current.CancellationToken);
    }

    // FR-09: 不正なアクションのポリシーは 400
    [Fact]
    public async Task CreatePolicy_InvalidAction_Returns400()
    {
        var res = await Client.PostAsJsonAsync("/authz/policies", new
        {
            Name = "bad_action",
            Action = "destroy",
            UserConditions = new Dictionary<string, string[]>(),
            DocumentConditions = new Dictionary<string, string[]>()
        }, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-09, UC-05: 辞書外の文書属性値を含むポリシーは 400（矛盾検証）
    [Fact]
    public async Task CreatePolicy_DocValueOutsideDictionary_Returns400()
    {
        // 先に辞書を定義
        await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("conf_policy", ["public", "internal"]), TestContext.Current.CancellationToken);

        var res = await Client.PostAsJsonAsync("/authz/policies", new
        {
            Name = "conflicting_policy",
            Action = "read",
            UserConditions = new Dictionary<string, string[]>(),
            DocumentConditions = new Dictionary<string, string[]> { ["conf_policy"] = ["secret"] }
        }, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 文書属性検証 ----

    // FR-09: 文書属性が辞書に整合すれば valid=true
    //   ※ InMemory DB は本クラス内で共有されるため、必須属性の累積で結果が揺れないよう
    //     許可値整合（required=false）で検証する。必須欠落の網羅は AbacValidationTests が担う。
    [Fact]
    public async Task ValidateDocumentAttributes_Valid_ReturnsTrue()
    {
        await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("conf_validate_ok", ["public", "internal"], required: false), TestContext.Current.CancellationToken);

        var res = await Client.PostAsJsonAsync("/authz/attributes/validate", new
        {
            Attributes = new Dictionary<string, string> { ["conf_validate_ok"] = "internal" }
        }, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken);
        body!.Valid.Should().BeTrue();
    }

    // FR-09: 辞書外の値は valid=false＋エラー
    [Fact]
    public async Task ValidateDocumentAttributes_ValueOutsideDictionary_ReturnsFalse()
    {
        await Client.PostAsJsonAsync("/authz/attributes",
            AttributeBody("conf_validate_ng", ["public", "internal"], required: false), TestContext.Current.CancellationToken);

        var res = await Client.PostAsJsonAsync("/authz/attributes/validate", new
        {
            Attributes = new Dictionary<string, string> { ["conf_validate_ng"] = "secret" }
        }, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ValidateResponse>(TestContext.Current.CancellationToken);
        body!.Valid.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
    }
}
