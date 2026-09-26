using AuthorizationService.Domain;
using AuthorizationService.Features.Authz;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Authz;

// FR-05, FR-09, UC-05, SC-09, SC-17, 計画 ADR-0116 決定 3, ADR-0115 決定 1, [[IADR-0476]] (#1609):
// **属性辞書の `department` の許可値は realm の部門グループから導く。**
//
// 身元プロバイダは in-memory の偽物（部門グループは開発用 realm export と同じ engineering / sales / hr）。
// realm を読めない形は `TestIdentityDirectory.GroupFailure`（グループの読み取りが例外になる）で作る。
//
// T-59 辞書の部門の値が realm の部門グループと一致する（両スコープ・保存済みの旧い値は置き換わる・ポリシー検証も同じ集合）/
// T-60 realm を読めないときは「不明」と示し、既存の値を消さない / T-61 手で足す・消す要求は拒む（空は realm から導く）。
[Trait("TestKind", "Integration")]
public class DepartmentDictionaryFromRealmTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] RealmDepartments = ["engineering", "hr", "sales"];

    // 各試験の前に辞書を空にし、realm を読める状態へ戻す（本クラスは器と DB を共有する）。
    private async Task<HttpClient> FreshAsync()
    {
        factory.Identity.Reset();
        var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        db.AttributeDefinitions.RemoveRange(await db.AttributeDefinitions.ToListAsync(Ct));
        db.Policies.RemoveRange(await db.Policies.ToListAsync(Ct));
        await db.SaveChangesAsync(Ct);
        return client;
    }

    // seed の旧い固定値（finance / legal）を API を通さずに保存する（移行前の稼働 DB と同じ形）。
    private async Task SeedStaleAsync(params string[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        foreach (var s in scopes)
            db.AttributeDefinitions.Add(AttributeDefinition.Create(
                "department", "部門", ["engineering", "sales", "hr", "finance", "legal"], false, s));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> StoredDepartmentValuesAsync(string scopeName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        return (await db.AttributeDefinitions.AsNoTracking().SingleAsync(
            d => d.Key == "department" && d.Scope == scopeName, Ct)).AllowedValues;
    }

    private static async Task<List<AttributeDefinitionView>> ListAsync(HttpClient client)
        => (await client.GetFromJsonAsync<List<AttributeDefinitionView>>("/authz/attributes", Ct))!;

    // T-59: 🔴 一覧の部門の値は realm の部門グループのコードそのもの（両スコープ）。seed の旧い値（finance / legal）は
    // 消え、出所は `realm`。導いた値は保存し直される（最後に確かめた値として残る）。
    [Fact]
    public async Task The_department_values_match_the_realm_department_groups_in_both_scopes()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.User, AttributeScope.Document);

        var departments = (await ListAsync(client)).Where(a => a.Key == "department").ToList();

        departments.Should().HaveCount(2);
        departments.Should().OnlyContain(a => a.AllowedValues.SequenceEqual(RealmDepartments)
                                              && a.AllowedValuesSource == DepartmentDictionaryValues.SourceRealm);
        (await StoredDepartmentValuesAsync(AttributeScope.User)).Should().Equal(RealmDepartments,
            "realm から導いた値を保存し直す（realm を読めなくなったときの『最後に確かめた値』になる）");
    }

    // T-59: 手で持つキーの出所は null（従来どおり）。個別取得も一覧と同じ値を返す。
    [Fact]
    public async Task Hand_held_keys_keep_their_values_and_have_no_source()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.User);
        (await client.PostAsJsonAsync("/authz/attributes",
                new { Key = "clearance", Label = "取扱", AllowedValues = new[] { "public", "internal" }, Required = false, Scope = "user" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await ListAsync(client);
        var clearance = list.Single(a => a.Key == "clearance");
        clearance.AllowedValues.Should().Equal("public", "internal");
        clearance.AllowedValuesSource.Should().BeNull();

        var department = list.Single(a => a.Key == "department");
        var single = await client.GetFromJsonAsync<AttributeDefinitionView>($"/authz/attributes/{department.Id}", Ct);
        single!.AllowedValues.Should().Equal(RealmDepartments);
        single.AllowedValuesSource.Should().Be(DepartmentDictionaryValues.SourceRealm);
    }

    // T-60: 🔴 realm を読めないときは出所を「不明」（realm-unavailable）として示し、**保存済みの値を消さない**。
    // 旧い値しか無い DB でも空へ倒さない（読めなかったことは「部門が無い」ではない）。
    [Fact]
    public async Task An_unreadable_realm_is_shown_as_unknown_and_wipes_nothing()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.User);
        factory.Identity.GroupFailure = new HttpRequestException("Keycloak のグループ照会へ届かない（試験）");

        var department = (await ListAsync(client)).Single(a => a.Key == "department");

        department.AllowedValuesSource.Should().Be(DepartmentDictionaryValues.SourceRealmUnavailable);
        department.AllowedValues.Should().Equal("engineering", "sales", "hr", "finance", "legal");
        (await StoredDepartmentValuesAsync(AttributeScope.User)).Should().Equal("engineering", "sales", "hr", "finance", "legal");
    }

    // T-60: 一度 realm から導いた後に realm が読めなくなったら、**最後に確かめた値**（旧い seed の値ではない）を示し続ける。
    [Fact]
    public async Task After_a_successful_read_the_last_confirmed_values_survive_an_outage()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.User);
        await ListAsync(client); // realm を読めた ＝ 保存し直される

        factory.Identity.GroupFailure = new HttpRequestException("障害（試験）");
        var department = (await ListAsync(client)).Single(a => a.Key == "department");

        department.AllowedValues.Should().Equal(RealmDepartments);
        department.AllowedValuesSource.Should().Be(DepartmentDictionaryValues.SourceRealmUnavailable);
    }

    // T-61: 🔴 部門の値を手で足す要求は 400（部門の追加・削除は realm の部門グループで行う）。空は realm から導いて登録し、
    // realm と同じ集合（並び違い）も受け付ける。
    [Fact]
    public async Task Department_values_cannot_be_held_by_hand()
    {
        var client = await FreshAsync();

        var byHand = await client.PostAsJsonAsync("/authz/attributes",
            new { Key = "department", Label = "部門", AllowedValues = new[] { "engineering", "finance" }, Required = false, Scope = "user" }, Ct);
        byHand.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await byHand.Content.ReadAsStringAsync(Ct)).Should().Contain("realm の部門グループ");

        var derived = await client.PostAsJsonAsync("/authz/attributes",
            new { Key = "department", Label = "部門", AllowedValues = Array.Empty<string>(), Required = false, Scope = "user" }, Ct);
        derived.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await derived.Content.ReadFromJsonAsync<AttributeDefinitionView>(Ct);
        created!.AllowedValues.Should().Equal(RealmDepartments);
        created.AllowedValuesSource.Should().Be(DepartmentDictionaryValues.SourceRealm);

        // 更新: 同じ集合（並び違い）でラベルだけ変えるのは通る。1 つ消すのは拒む。
        (await client.PutAsJsonAsync($"/authz/attributes/{created.Id}",
                new { Label = "所属部門", AllowedValues = new[] { "sales", "hr", "engineering" }, Required = false }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/authz/attributes/{created.Id}",
                new { Label = "所属部門", AllowedValues = new[] { "engineering", "hr" }, Required = false }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredDepartmentValuesAsync(AttributeScope.User)).Should().Equal(RealmDepartments);
    }

    // T-61 / T-60: realm を読めないとき、保存済みの値のまま送る更新（ラベルの変更）は通り、値は変わらない。
    // 値を変える更新は確かめられないので拒む。
    [Fact]
    public async Task While_the_realm_is_unreadable_only_unchanged_values_are_accepted()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.User);
        factory.Identity.GroupFailure = new HttpRequestException("障害（試験）");
        var department = (await ListAsync(client)).Single(a => a.Key == "department");

        (await client.PutAsJsonAsync($"/authz/attributes/{department.Id}",
                new { Label = "所属部門", AllowedValues = department.AllowedValues, Required = false }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/authz/attributes/{department.Id}",
                new { Label = "所属部門", AllowedValues = new[] { "engineering" }, Required = false }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredDepartmentValuesAsync(AttributeScope.User)).Should().Equal("engineering", "sales", "hr", "finance", "legal");
    }

    // T-59: 🔴 ポリシーの許容値も同じ集合で検証する（辞書の画面に出る値と保存の検証が食い違わない）。
    // 保存済みの旧い値（finance）を条件に持つポリシーは、realm を読める限り 400（dry-run も同じ）。
    [Fact]
    public async Task Policy_validation_uses_the_realm_department_values()
    {
        var client = await FreshAsync();
        await SeedStaleAsync(AttributeScope.Document);

        var stale = new
        {
            Name = "finance の資料",
            Action = "read",
            UserConditions = new Dictionary<string, List<string>>(),
            DocumentConditions = new Dictionary<string, List<string>> { ["department"] = ["finance"] },
        };
        (await client.PostAsJsonAsync("/authz/policies", stale, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var current = stale with { Name = "sales の資料" };
        current.DocumentConditions["department"] = ["sales"];
        (await client.PostAsJsonAsync("/authz/policies", current, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
