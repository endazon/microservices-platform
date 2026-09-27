using AuthorizationService.Domain.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Users.Department;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0116 決定 1, [[IADR-0473]] (#1610):
// SC-17 の部門欄 ＝ **部門グループの所属の変更**（`GET` / `PUT /authz/users/{id}/department`）。IdP は偽物（実 realm へは触れない）。
//
// 受け入れ基準: T-62 保存で所属が変わる（部門なしを含む）／T-64 属性 `department` を書く要求が出ない（否定の試験）／
// T-65 2 個以上の部門グループの人は変えない／T-66 途中の失敗は補償し、黙って 0 個にしない／値域・不在・realm を読めないとき。
//
// 偽物の realm（`InMemoryIdentityAdminClient`）: 部門グループ engineering / sales / hr。佐藤（u-sato）は `/teams/knowledge` と
// `engineering`、鈴木（u-suzuki）は `/teams/knowledge` だけ、高橋（u-takahashi）は `/teams` だけに属する。
// 🔴 **所属は器（クラス）ごとの偽物に残る**ので、各試験は自分の利用者を使い、最後に元へ戻す。
[Trait("TestKind", "Integration")]
public class UserDepartmentEndpointTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly TestWebApplicationFactory _factory;

    public UserDepartmentEndpointTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _ = factory.Services.GetRequiredService<IIdentityAdminClient>();
        factory.Identity.Reset();
    }

    public void Dispose() => _factory.Identity.Reset();

    private HttpClient Client => _factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record DepartmentDto(List<string> DepartmentGroups, string? DepartmentAttribute, List<string> Choices);

    private IIdentityAdminClient Inner => _factory.Identity.Inner;

    private async Task<string[]> GroupPathsAsync(string userId)
        => [.. (await Inner.GetUserGroupsAsync(userId, Ct)).Select(g => g.Path).Order(StringComparer.Ordinal)];

    private async Task<string?> AttributeAsync(string userId)
        => (await Inner.FindByIdAsync(userId, Ct))!.Attributes.GetValueOrDefault("department");

    // SC-17: 部門欄の選択肢は realm の部門グループのコード。いまの所属と属性を返す。
    [Fact]
    public async Task Reading_returns_the_realm_department_groups_as_choices_with_the_membership_and_attribute()
    {
        var dto = await Client.GetFromJsonAsync<DepartmentDto>("/authz/users/u-sato/department", Ct);

        dto!.Choices.Should().Equal("engineering", "hr", "sales");
        dto.DepartmentGroups.Should().Equal("engineering");
        dto.DepartmentAttribute.Should().Be("engineering");
    }

    // T-62: 🔴 保存すると、選んだ部門グループへ入り、いまの部門グループから外れる。部門の木の外（`/teams/knowledge`）は触らない。
    // 先に入れてから外す（途中で止まっても 0 個にならない順）。「部門なし」はすべての部門グループから外す。
    [Fact]
    public async Task Saving_moves_the_user_to_the_chosen_department_group_and_none_leaves_them_all()
    {
        var moved = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct);

        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await moved.Content.ReadFromJsonAsync<DepartmentDto>(Ct))!.DepartmentGroups.Should().Equal("sales");
        (await GroupPathsAsync("u-sato")).Should().Equal("/department/sales", "/teams/knowledge");
        _factory.Identity.MembershipWrites.Should().Equal(
            ("join", "u-sato", "g-sales"), ("leave", "u-sato", "g-engineering"));

        var none = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = (string?)null }, Ct);

        none.StatusCode.Should().Be(HttpStatusCode.OK);
        (await none.Content.ReadFromJsonAsync<DepartmentDto>(Ct))!.DepartmentGroups.Should().BeEmpty();
        (await GroupPathsAsync("u-sato")).Should().Equal("/teams/knowledge");

        // 元へ戻す（陽性対照を兼ねる: 0 個から 1 つへ入れられる）。
        (await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "engineering" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await GroupPathsAsync("u-sato")).Should().Equal("/department/engineering", "/teams/knowledge");
    }

    // T-62: 同じ部門を選び直しても何も書かない（冪等）。
    [Fact]
    public async Task Saving_the_current_department_writes_nothing()
    {
        (await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "engineering" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Identity.MembershipWrites.Should().BeEmpty();
    }

    // T-64: 🔴 **否定の試験。** 部門を変えても、属性 `department` を書く要求（差し替え・部門の書き込み・消去）は 1 つも出ない。
    // 属性は変更前の値のまま（部門の同期が追いつく。T-63）。陽性対照として、所属は実際に変わっている。
    [Fact]
    public async Task Changing_the_department_never_writes_the_department_attribute()
    {
        var before = await AttributeAsync("u-takahashi");

        var res = await Client.PutAsJsonAsync("/authz/users/u-takahashi/department", new { Department = "sales" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GroupPathsAsync("u-takahashi")).Should().Contain("/department/sales", "陽性対照: 所属は変わった");
        _factory.Identity.AttributeWrites.Should().BeEmpty("SC-17 の部門の経路から属性を書かない");
        (await AttributeAsync("u-takahashi")).Should().Be(before);
        (await res.Content.ReadFromJsonAsync<DepartmentDto>(Ct))!.DepartmentAttribute.Should().Be(before,
            "応答の属性は同期が追いつくまで元の値である（画面が「未反映」を示す）");

        await Client.PutAsJsonAsync("/authz/users/u-takahashi/department", new { Department = (string?)null }, Ct);
    }

    // T-65: 🔴 2 個以上の部門グループに属する人は変えない（409・理由つき）。何も書かない。
    [Fact]
    public async Task A_user_in_several_department_groups_is_refused_and_nothing_is_written()
    {
        await Inner.JoinGroupAsync("u-suzuki", "g-sales", Ct);
        await Inner.JoinGroupAsync("u-suzuki", "g-hr", Ct);
        try
        {
            var read = await Client.GetFromJsonAsync<DepartmentDto>("/authz/users/u-suzuki/department", Ct);
            read!.DepartmentGroups.Should().Equal("hr", "sales");

            var res = await Client.PutAsJsonAsync("/authz/users/u-suzuki/department", new { Department = "engineering" }, Ct);

            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("複数の部門グループ");
            _factory.Identity.MembershipWrites.Should().BeEmpty();
            (await GroupPathsAsync("u-suzuki")).Should().Equal("/department/hr", "/department/sales", "/teams/knowledge");
        }
        finally
        {
            await Inner.LeaveGroupAsync("u-suzuki", "g-sales", Ct);
            await Inner.LeaveGroupAsync("u-suzuki", "g-hr", Ct);
        }
    }

    // T-66: 🔴 入れた後に外せなかったら、元に戻す（目的のグループから外し、元のグループに残す）。502 と「元に戻しました」。
    [Fact]
    public async Task A_failed_removal_is_compensated_back_to_the_original_department()
    {
        _factory.Identity.FailLeave = groupId => groupId == "g-engineering";

        var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "hr" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("元に戻しました").And.Contain("engineering");
        (await GroupPathsAsync("u-sato")).Should().Equal("/department/engineering", "/teams/knowledge");
        _factory.Identity.MembershipWrites.Should().Equal(
            ("join", "u-sato", "g-hr"), ("leave", "u-sato", "g-engineering"),
            ("join", "u-sato", "g-engineering"), ("leave", "u-sato", "g-hr"));
    }

    // T-66: 🔴 補償まで失敗しても、部門グループ 0 個にはしない（目的のグループを残す）。502 と「元に戻せませんでした」、いまの所属。
    [Fact]
    public async Task When_compensation_also_fails_the_user_is_never_left_without_a_department_group()
    {
        _factory.Identity.FailLeave = _ => true;
        try
        {
            var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct);

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadAsStringAsync(Ct);
            body.Should().Contain("元に戻せませんでした").And.Contain("engineering, sales");
            (await GroupPathsAsync("u-sato")).Should().Contain(p => p.StartsWith("/department/", StringComparison.Ordinal),
                "黙って部門グループ 0 個にしない");
        }
        finally
        {
            _factory.Identity.FailLeave = null;
            await Inner.LeaveGroupAsync("u-sato", "g-sales", Ct);
        }
    }

    // T-66: 🔴 **元のグループからの外しは反映された（応答だけが時間切れ）のに、補償の入れ直しが失敗した**ときも、
    // 目的のグループから外さない（外すと部門グループ 0 個になる）。502 は読み直した実際の所属（sales）を示す。
    // 「入れ直しがすべて成功したときだけ目的のグループから外す」の条件を外す変異はここで落ちる。
    [Fact]
    public async Task When_the_rejoin_fails_after_the_removal_took_effect_the_new_group_is_kept()
    {
        _factory.Identity.FailLeaveAfterApplying = groupId => groupId == "g-engineering";
        _factory.Identity.FailJoin = groupId => groupId == "g-engineering";
        try
        {
            var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct);

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadAsStringAsync(Ct);
            (await GroupPathsAsync("u-sato")).Should().Equal(["/department/sales", "/teams/knowledge"],
                "元の所属は消え入れ直しも失敗したので、目的のグループを残す（部門グループ 0 個にしない）");
            body.Should().Contain("元に戻せませんでした").And.Contain("いまの部門グループ: sales")
                .And.Contain("部門グループを 1 つも持たない状態にはしていません");
            _factory.Identity.MembershipWrites.Should().NotContain(("leave", "u-sato", "g-sales"),
                "入れ直しが失敗したのに目的のグループから外してはならない");
        }
        finally
        {
            _factory.Identity.FailLeaveAfterApplying = null;
            _factory.Identity.FailJoin = null;
            await Inner.LeaveGroupAsync("u-sato", "g-sales", Ct);
            await Inner.JoinGroupAsync("u-sato", "g-engineering", Ct);
        }
    }

    // T-66: 🔴 読み直した所属が 0 個のとき（部門なしを選んだ人の途中失敗）は、「部門グループを 1 つも持たない状態には
    // していません」と**言わない**（嘘になる）。いまの所属は「なし」と示す。
    [Fact]
    public async Task The_failure_message_does_not_claim_a_group_remains_when_the_read_back_membership_is_empty()
    {
        _factory.Identity.FailLeaveAfterApplying = groupId => groupId == "g-engineering";
        _factory.Identity.FailJoin = groupId => groupId == "g-engineering";
        try
        {
            var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = (string?)null }, Ct);

            res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            var body = await res.Content.ReadAsStringAsync(Ct);
            (await GroupPathsAsync("u-sato")).Should().Equal("/teams/knowledge");
            body.Should().Contain("元に戻せませんでした").And.Contain("いまの部門グループ: なし");
            body.Should().NotContain("1 つも持たない状態にはしていません");
        }
        finally
        {
            _factory.Identity.FailLeaveAfterApplying = null;
            _factory.Identity.FailJoin = null;
            await Inner.JoinGroupAsync("u-sato", "g-engineering", Ct);
        }
    }

    // T-66: 入れる段で失敗したら何も外さない（元の所属のまま）。502。
    [Fact]
    public async Task A_failed_join_removes_nothing()
    {
        _factory.Identity.FailJoin = groupId => groupId == "g-sales";

        var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await GroupPathsAsync("u-sato")).Should().Equal("/department/engineering", "/teams/knowledge");
        _factory.Identity.MembershipWrites.Should().NotContain(w => w.Op == "leave" && w.GroupId == "g-engineering");
    }

    // T-59（#1609 から移した）: 部門の値域は realm の部門グループのコード。realm に無い `finance`（seed の旧い固定値・
    // `/teams/finance` の名前）は 400 で、何も書かない。
    [Fact]
    public async Task A_code_outside_the_realm_department_groups_is_rejected()
    {
        var res = await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "finance" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("finance");
        _factory.Identity.MembershipWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_user_is_404()
    {
        (await Client.GetAsync("/authz/users/u-nobody/department", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PutAsJsonAsync("/authz/users/u-nobody/department", new { Department = "sales" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // realm を読めないときは選択肢を推測で出さず、何も変えない（503）。
    [Fact]
    public async Task When_the_realm_cannot_be_read_nothing_changes()
    {
        _factory.Identity.GroupFailure = new HttpRequestException("Keycloak へ届かない（偽）");

        (await Client.GetAsync("/authz/users/u-sato/department", Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.Identity.MembershipWrites.Should().BeEmpty();
    }

    // 05_screens §共通シェル「SC-17 = システム管理者」。運用者は読み取りも変更も 403。
    [Fact]
    public async Task Non_admin_roles_are_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        (await client.GetAsync("/authz/users/u-sato/department", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync("/authz/users/u-sato/department", new { Department = "sales" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Identity.MembershipWrites.Should().BeEmpty();
    }
}
