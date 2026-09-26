using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Users.DepartmentSync;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthorizationService.Tests.Features.Users.DepartmentSync;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573):
// 利用者属性 `department` を部門グループの所属へ合わせる同期（IdP は偽物。**実 realm へは触れない**）。
//
// 受け入れ基準（#1573）: 1 食い違いの検知 / 2 属性をグループ側へ直す（グループは変えない）/
// 3 0 個・複数は上書きしない / 4 冪等 / 5 既定で無効。
[Trait("TestKind", "Unit")]
public class DepartmentAttributeSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 開発用 realm に近い木（`/department/{engineering,sales,hr}`）に入れ子と別の木を足したもの。
    private static FakeDepartmentIdentity Realm()
    {
        var realm = new FakeDepartmentIdentity();
        realm.Group("g-dept", "/department");
        realm.Group("g-eng", "/department/engineering");
        realm.Group("g-backend", "/department/engineering/backend");
        realm.Group("g-sales", "/department/sales");
        realm.Group("g-hr", "/department/hr");
        realm.Group("g-teams", "/teams");
        realm.Group("g-teams-sales", "/teams/sales");

        realm.User("u-ok", "engineering", "g-eng");             // 一致
        realm.User("u-wrong", "engineering", "g-sales");        // 食い違い → sales へ直す
        realm.User("u-missing", null, "g-hr");                  // 属性なし → hr へ直す
        realm.User("u-nested", "sales", "g-backend");           // 入れ子 → engineering へ直す
        realm.User("u-nested-same", "engineering", "g-eng", "g-backend"); // 同じ部門の入れ子は 1 つ
        realm.User("u-two", "sales", "g-sales", "g-hr");        // 2 部門 → 上書きしない（先頭の hr へ寄せる変異を捕まえる値）
        realm.User("u-none", "sales", "g-teams-sales");         // 部門グループなし（別の木の同名）→ 上書きしない
        realm.User("svc-ast", null);                            // サービスアカウント（所属なし）→ 現れない
        return realm;
    }

    private static DepartmentAttributeSync Sync(FakeDepartmentIdentity realm)
        => new(realm, NullLogger<DepartmentAttributeSync>.Instance);

    // 受け入れ基準 1・2: Fix は食い違いを**グループのコード**へ直す。グループの所属は変えない。
    [Fact]
    public async Task Fix_corrects_the_attribute_towards_the_group_and_never_touches_groups()
    {
        var realm = Realm();
        var membershipsBefore = realm.MembershipSnapshot();

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Writes.Should().BeEquivalentTo(new[]
        {
            ("u-missing", "hr"), ("u-nested", "engineering"), ("u-wrong", "sales"),
        });
        realm.Department("u-wrong").Should().Be("sales");
        realm.Department("u-missing").Should().Be("hr");
        realm.Department("u-nested").Should().Be("engineering", "入れ子は上位のコードに畳む");
        realm.MembershipSnapshot().Should().BeEquivalentTo(membershipsBefore, "グループは正本であり、逆向きに直さない");
        outcome.Should().BeEquivalentTo(new { RootFound = true, Corrected = 3, Mismatched = 3, InSync = 2, Unresolved = 1 });
    }

    // 受け入れ基準 3: 🔴 2 部門・部門なし・サービスアカウントの属性は変えない（消しもしない）。
    [Fact]
    public async Task Zero_or_several_department_groups_are_left_untouched()
    {
        var realm = Realm();

        await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Department("u-two").Should().Be("sales");
        realm.Department("u-none").Should().Be("sales", "/teams/sales は部門グループではない（名前ではなくパスで判定する）");
        realm.Department("svc-ast").Should().BeNull();
        realm.Writes.Select(w => w.UserId).Should().NotContain(["u-two", "u-none", "svc-ast", "u-ok", "u-nested-same"]);
    }

    // 受け入れ基準 4: 冪等 —— 2 周目は書き込み 0 件。
    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        var realm = Realm();
        await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);
        realm.Writes.Clear();

        var second = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Writes.Should().BeEmpty();
        second.Mismatched.Should().Be(0);
        second.Corrected.Should().Be(0);
    }

    // Report は検知するが書かない（稼働 realm で先に食い違いを見るための段）。
    [Fact]
    public async Task Report_detects_mismatches_without_writing()
    {
        var realm = Realm();

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Report, Ct);

        outcome.Mismatched.Should().Be(3);
        outcome.Corrected.Should().Be(0);
        realm.Writes.Should().BeEmpty();
        realm.Department("u-wrong").Should().Be("engineering");
    }

    // 受け入れ基準 5: Off は IdP へ 1 回も問い合わせない。
    [Fact]
    public async Task Off_does_not_call_the_identity_provider_at_all()
    {
        var realm = Realm();

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Off, Ct);

        realm.Calls.Should().Be(0);
        outcome.RootFound.Should().BeFalse();
    }

    // `/department` グループが無い realm では何もしない（書かない）。
    [Fact]
    public async Task Without_a_department_root_nothing_is_written()
    {
        var realm = new FakeDepartmentIdentity();
        realm.Group("g-teams", "/teams");
        realm.User("u1", "sales", "g-teams");

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        outcome.RootFound.Should().BeFalse();
        realm.Writes.Should().BeEmpty();
    }

    // 受け入れ基準 5（構成）: 既定は Off。値域外は起動時に落とす（打ち間違いを黙って Off にしない）。
    [Theory]
    [InlineData(null, DepartmentAttributeSyncMode.Off)]
    [InlineData("", DepartmentAttributeSyncMode.Off)]
    [InlineData("off", DepartmentAttributeSyncMode.Off)]
    [InlineData("Report", DepartmentAttributeSyncMode.Report)]
    [InlineData(" FIX ", DepartmentAttributeSyncMode.Fix)]
    public void Options_default_to_off_and_parse_the_declared_mode(string? declared, DepartmentAttributeSyncMode expected)
    {
        var options = DepartmentAttributeSyncOptions.FromConfiguration(Config(declared, null));

        options.Mode.Should().Be(expected);
        options.Interval.Should().Be(DepartmentAttributeSyncOptions.DefaultInterval);
    }

    [Theory]
    [InlineData("fixx", null)]
    [InlineData("2", null)]
    [InlineData("Fix", "0")]
    [InlineData("Fix", "soon")]
    public void Options_reject_undeclared_values(string mode, string? interval)
    {
        var act = () => DepartmentAttributeSyncOptions.FromConfiguration(Config(mode, interval));

        act.Should().Throw<InvalidOperationException>().WithMessage("*DepartmentAttributeSync:*");
    }

    // 🔴 Off のときは器が同期を**解決すらしない**（スコープを作らない）。
    [Fact]
    public async Task Hosted_service_in_off_mode_never_runs_the_sync()
    {
        var factory = new ThrowingScopeFactory();
        using var service = new DepartmentAttributeSyncHostedService(
            factory, new DepartmentAttributeSyncOptions(DepartmentAttributeSyncMode.Off, TimeSpan.FromMilliseconds(10)),
            NullLogger<DepartmentAttributeSyncHostedService>.Instance);

        await service.StartAsync(Ct);
        await (service.ExecuteTask ?? Task.CompletedTask);
        await service.StopAsync(Ct);

        factory.Created.Should().Be(0);
    }

    private static IConfiguration Config(string? mode, string? interval)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values[DepartmentAttributeSyncOptions.ModeKey] = mode;
        if (interval is not null) values[DepartmentAttributeSyncOptions.IntervalKey] = interval;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            throw new InvalidOperationException("Off のときはスコープを作らない");
        }
    }

    // 部門の同期が使う 4 つの口だけを持つ IdP の偽物。**それ以外の口は呼ばれたら落ちる**（同期が触れてはならない）。
    private sealed class FakeDepartmentIdentity : IIdentityAdminClient
    {
        private readonly List<IdentityGroup> _groups = [];
        private readonly Dictionary<string, Dictionary<string, string>> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);

        public List<(string UserId, string Department)> Writes { get; } = [];
        public int Calls { get; private set; }

        public void Group(string id, string path) => _groups.Add(new IdentityGroup(id, path[(path.LastIndexOf('/') + 1)..], path));

        public void User(string id, string? department, params string[] groupIds)
        {
            _attributes[id] = new Dictionary<string, string>(StringComparer.Ordinal) { ["clearance"] = "internal" };
            if (department is not null) _attributes[id]["department"] = department;
            foreach (var g in groupIds)
            {
                if (!_members.TryGetValue(g, out var list)) _members[g] = list = [];
                list.Add(id);
            }
        }

        public string? Department(string userId) => _attributes[userId].GetValueOrDefault("department");

        public Dictionary<string, string[]> MembershipSnapshot()
            => _members.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);

        private IdentityUser Snapshot(string id)
            => new(id, id, id, true, [], new Dictionary<string, string>(_attributes[id], StringComparer.Ordinal));

        public Task<IdentityGroup?> FindGroupByPathAsync(string path, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_groups.FirstOrDefault(g => string.Equals(g.Path, path, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<IdentityGroup>> ListSubGroupsAsync(string groupId, CancellationToken ct)
        {
            Calls++;
            var parent = _groups.Single(g => g.Id == groupId);
            return Task.FromResult<IReadOnlyList<IdentityGroup>>(
            [
                .. _groups.Where(g => g.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal)
                                      && !g.Path[(parent.Path.Length + 1)..].Contains('/'))
            ]);
        }

        public Task<IReadOnlyList<IdentityUser>> ListGroupMembersAsync(string groupId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IdentityUser>>(
                [.. _members.GetValueOrDefault(groupId, []).Select(Snapshot)]);
        }

        public Task<IdentityUser?> SetDepartmentAttributeAsync(string userId, string department, CancellationToken ct)
        {
            Calls++;
            if (!_attributes.ContainsKey(userId)) return Task.FromResult<IdentityUser?>(null);
            Writes.Add((userId, department));
            _attributes[userId]["department"] = department;
            return Task.FromResult<IdentityUser?>(Snapshot(userId));
        }

        public Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> FindByUsernameAsync(string username, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string query, int max, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<IdentityGroup>> GetUserGroupsAsync(string userId, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string query, int max, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<IdentityGroup>> GetGroupsByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> ReplaceAttributesAsync(string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> SetRetentionAnchorAsync(string userId, string attributeKey, DateTimeOffset? anchorAt, CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> ReplaceRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> SetEnabledAsync(string userId, bool enabled, CancellationToken ct) => throw Untouchable();
        public Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct) => throw Untouchable();

        private static InvalidOperationException Untouchable()
            => new("部門の同期はこの口を使わない（属性の全置換・ロール・有効状態・セッションに触れてはならない）");
    }
}
