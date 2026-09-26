using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Users.DepartmentSync;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;

namespace AuthorizationService.Tests.Features.Users.DepartmentSync;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573):
// 利用者属性 `department` を部門グループの所属へ合わせる同期（IdP は偽物。**実 realm へは触れない**）。
//
// 受け入れ基準（#1573）: 1 食い違いの検知 / 2 属性をグループ側へ直す（グループは変えない）/
// 3 複数は上書きしない / 4 冪等 / 5 既定で無効。
// ［2026-09-27 / #1609・計画 ADR-0116 決定 2］T-55 部門グループ 0 個の人の属性は消す / T-56 全利用者の列挙が途中で
// 失敗・打ち切られた周期は誰も消さない（否定の試験）/ T-57 1 つは直る・2 個以上とサービスアカウントは触らない。
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
        realm.User("u-none", "sales", "g-teams-sales");         // 部門グループなし（別の木の同名）→ #1609: 属性を消す
        realm.User("u-plain", null);                            // 部門グループなし・属性なし → 計画に現れない（書かない）
        realm.User("svc-ast", null);                            // サービスアカウント（所属なし）→ 現れない
        // #1609: 部門グループなしで属性を持つサービスアカウント（開発用 realm の service-account-abac-seeder と同じ形）→ 消さない
        realm.NamedUser("svc-seeder", "engineering", "service-account-abac-seeder");
        return realm;
    }

    private static readonly IMeterFactory Meters =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    private static DepartmentAttributeSync Sync(FakeDepartmentIdentity realm)
        => new(realm, new DepartmentAttributeSyncMetrics(Meters), NullLogger<DepartmentAttributeSync>.Instance);

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
        outcome.Should().BeEquivalentTo(new
        {
            RootFound = true, Corrected = 3, Mismatched = 3, InSync = 2, Unresolved = 1, Orphaned = 1, Cleared = 1,
            EnumerationComplete = true,
        });
    }

    // 受け入れ基準 3 / T-57: 🔴 2 部門・サービスアカウントの属性は変えない（消しもしない）。1 つ・一致の人にも書かない。
    [Fact]
    public async Task Several_department_groups_and_service_accounts_are_left_untouched()
    {
        var realm = Realm();

        await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Department("u-two").Should().Be("sales");
        realm.Department("svc-ast").Should().BeNull();
        realm.Department("svc-seeder").Should().Be("engineering",
            "部門グループに属さないサービスアカウントの属性は所属から何も言えない（機械の主体は消さない）");
        realm.Writes.Select(w => w.UserId).Should().NotContain(["u-two", "u-none", "svc-ast", "u-ok", "u-nested-same"]);
        realm.Clears.Should().Equal(["u-none"], "消すのは部門グループ 0 個で属性を持つ人間の利用者だけ");
    }

    // T-55（#1609・計画 ADR-0116 決定 2）: 🔴 部門グループに 1 つも属さない人の属性は、同期の後に消える。
    // `/teams/sales` は部門グループではない（名前ではなくパスで判定する）。グループの所属は変えない。
    [Fact]
    public async Task Fix_clears_the_attribute_of_a_user_in_no_department_group()
    {
        var realm = Realm();
        var membershipsBefore = realm.MembershipSnapshot();
        using var listener = OutcomeCounter("cleared", out var cleared);

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Department("u-none").Should().BeNull("部門グループ 0 個 ＝ 部門なし");
        realm.HasAttribute("u-none", "clearance").Should().BeTrue("消すのは department の 1 キーだけ");
        outcome.Cleared.Should().Be(1);
        cleared().Should().Be(1, "計器 department_sync.users.total{outcome=cleared} に出る");
        realm.MembershipSnapshot().Should().BeEquivalentTo(membershipsBefore);
    }

    // T-56（#1609）: 🔴 **否定の試験**。全利用者の列挙が途中で失敗した・打ち切られた周期は、**誰の属性も消さない**。
    // 1 つ属する人の是正は続け、未完了は計器 department_sync.enumeration_incomplete.total{reason} に出る。
    // 打ち切りの偽物は「読めた分」に u-none を**含めて**返す —— 部分的な列挙から 0 個を推定する変異
    // （Complete を見ずに読めた分で判定する）はここで赤になる。
    [Theory]
    [InlineData("page_failed")]
    [InlineData("truncated")]
    public async Task An_enumeration_that_does_not_finish_clears_nobody(string reason)
    {
        var realm = Realm();
        if (reason == "page_failed") realm.FailEnumeration = true;
        else realm.TruncateEnumeration = true;
        using var listener = Listen(
            DepartmentAttributeSyncMetrics.EnumerationIncompleteCounterName, DepartmentAttributeSyncMetrics.ReasonTag, reason,
            out var incomplete);

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Clears.Should().BeEmpty("読み切れなかった周期は 0 個と言えない（原則 A）");
        realm.Department("u-none").Should().Be("sales");
        outcome.EnumerationComplete.Should().BeFalse();
        outcome.Orphaned.Should().Be(0);
        outcome.Corrected.Should().Be(3, "1 つ属する人の是正は列挙に依らず続ける");
        incomplete().Should().Be(1);
    }

    // T-55 の歯止め（#1609）: 所属者の一覧（ページ送り）で飛んだ人を 0 個と読まない。消す直前の個別の所属の読み直しで
    // 部門グループが見つかれば消さず、見送り（skipped_changed）として数える。
    [Fact]
    public async Task A_user_found_in_a_department_group_just_before_clearing_is_not_cleared()
    {
        var realm = Realm();
        realm.HiddenFromMemberLists.Add("u-ok"); // 所属者の一覧からだけ漏れる（本当は /department/engineering に属する）

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        realm.Department("u-ok").Should().Be("engineering");
        realm.Clears.Should().NotContain("u-ok");
        outcome.SkippedChanged.Should().Be(1);
        outcome.Cleared.Should().Be(1, "本当に 0 個の u-none は消える");
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
        realm.Clears.Should().Equal(["u-none"], "1 周目に消しただけで、2 周目は消さない");
        second.Mismatched.Should().Be(0);
        second.Orphaned.Should().Be(0);
        second.Corrected.Should().Be(0);
        second.Cleared.Should().Be(0);
    }

    // Report は検知するが書かない（稼働 realm で先に食い違いを見るための段）。
    [Fact]
    public async Task Report_detects_mismatches_without_writing()
    {
        var realm = Realm();

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Report, Ct);

        outcome.Mismatched.Should().Be(3);
        outcome.Orphaned.Should().Be(1);
        outcome.Corrected.Should().Be(0);
        outcome.Cleared.Should().Be(0);
        realm.Writes.Should().BeEmpty();
        realm.Clears.Should().BeEmpty();
        realm.Department("u-wrong").Should().Be("engineering");
        realm.Department("u-none").Should().Be("sales");
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
    [InlineData("Fix", "60")]        // 🔴 TimeSpan.TryParse なら 60 日になる値（#1573 監査）
    [InlineData("Fix", "00:00:30")]  // 下限（1 分）未満
    [InlineData("Fix", "1.00:00:00")] // hh:mm:ss 以外の書式
    [InlineData("Fix", "24:00:00")]   // 🔴 TryParse なら 24 日（時 ≥ 24 は日として読まれる）
    [InlineData("Fix", "99:00:00")]   // 🔴 TryParse なら 99 日 → PeriodicTimer が起動後に落ちる
    public void Options_reject_undeclared_values(string mode, string? interval)
    {
        var act = () => DepartmentAttributeSyncOptions.FromConfiguration(Config(mode, interval));

        act.Should().Throw<InvalidOperationException>().WithMessage("*DepartmentAttributeSync:*");
    }

    [Theory]
    [InlineData("00:01:00", 1)]
    [InlineData("00:15:00", 15)]
    [InlineData(" 01:30:00 ", 90)]
    [InlineData("23:59:00", 1439)]
    public void Options_accept_hh_mm_ss_intervals_of_at_least_a_minute(string declared, int minutes)
        => DepartmentAttributeSyncOptions.FromConfiguration(Config("Fix", declared))
            .Interval.Should().Be(TimeSpan.FromMinutes(minutes));

    // ── #1573 監査: 1 人の失敗で周期を止めない ──────────────────────────────

    // 🔴 1 人の書き込みが例外でも、他の人は直り、失敗は数えられて計器に出る。
    [Fact]
    public async Task A_failing_user_is_counted_and_the_others_are_still_corrected()
    {
        var realm = Realm();
        realm.FailWritesFor.Add("u-wrong");
        using var listener = FailedCounter(out var failures);

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        outcome.Failed.Should().Be(1);
        outcome.Corrected.Should().Be(2);
        realm.Department("u-missing").Should().Be("hr", "失敗した人の後ろの人も直る");
        realm.Department("u-nested").Should().Be("engineering");
        realm.Department("u-wrong").Should().Be("engineering", "失敗した人の属性は変わっていない");
        failures().Should().Be(1, "失敗は計器 department_sync.users.total{outcome=failed} に出る");
    }

    // ── #1573 監査: SC-17 の操作との競合 ─────────────────────────────────

    // 🔴 計画の読み取り後に有効状態が変わった（SC-17 の無効化）人は書かない。**無効化を取り消さない。**
    [Fact]
    public async Task A_user_changed_after_the_read_is_skipped_not_overwritten()
    {
        var realm = Realm();
        realm.BeforeWrite = userId =>
        {
            if (userId == "u-wrong") realm.Disable("u-wrong", "2026-09-26T00:00:00Z");
        };

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        outcome.SkippedChanged.Should().Be(1);
        realm.Writes.Select(w => w.UserId).Should().NotContain("u-wrong");
        realm.IsEnabled("u-wrong").Should().BeFalse("無効化は残る");
        realm.Department("u-wrong").Should().Be("engineering");
        outcome.Corrected.Should().Be(2, "他の人は直る");
    }

    // ── #1573 差分監査 ──────────────────────────────────────────

    // F2: 🔴 ホストの停止（取り消し）が周期の途中で来たら、**周期ごと中断**して例外を上げる。
    // 利用者ごとの失敗として数えて次の人へ進む変異（取り消しも catch する）はここで赤になる。
    [Fact]
    public async Task Host_cancellation_mid_cycle_aborts_without_counting_user_failures()
    {
        var realm = Realm();
        using var cts = new CancellationTokenSource();
        realm.BeforeWrite = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        using var listener = OutcomeCounter("failed", out var failures);

        var act = async () => await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        failures().Should().Be(0, "取り消しは利用者の失敗ではない");
        realm.Writes.Should().BeEmpty();
    }

    // F3: 🔴 直そうとした全員が「変わった」で見送られた周期は、別の結末（all_skipped_changed）として出す。
    // 計画の読み取りと書く直前の読み直しで属性の見え方が違う realm では、Fix が黙って誰も直さなくなるため。
    [Fact]
    public async Task A_cycle_where_every_correction_was_skipped_is_reported_as_such()
    {
        var realm = Realm();
        realm.BeforeWrite = userId => realm.AddAttribute(userId, "only_on_user_endpoint", "x");
        using var listener = CycleCounter("all_skipped_changed", out var cycles);

        var outcome = await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        outcome.SkippedChanged.Should().Be(4, "直す 3 人と消す 1 人（#1609）");
        outcome.Corrected.Should().Be(0);
        cycles().Should().Be(1);
    }

    // 陰性対照: 一部だけ見送られた周期は all_skipped_changed ではない。
    [Fact]
    public async Task A_cycle_with_some_corrections_is_not_reported_as_all_skipped()
    {
        var realm = Realm();
        realm.BeforeWrite = userId =>
        {
            if (userId == "u-wrong") realm.AddAttribute(userId, "only_on_user_endpoint", "x");
        };
        using var listener = CycleCounter("all_skipped_changed", out var cycles);

        await Sync(realm).RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        cycles().Should().Be(0);
    }

    // F4: 🔴 周期ごとの失敗（木の読み取りが落ちた等）も計器 cycles.total{outcome=aborted} に出る。
    [Fact]
    public async Task Hosted_service_counts_a_failed_cycle_as_aborted()
    {
        var realm = Realm();
        realm.FailTreeReads = true;
        var services = new ServiceCollection();
        services.AddSingleton<IIdentityAdminClient>(realm);
        services.AddSingleton(new DepartmentAttributeSyncMetrics(Meters));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<DepartmentAttributeSync>();
        using var provider = services.BuildServiceProvider();
        using var listener = CycleCounter("aborted", out var aborted);

        using var service = new DepartmentAttributeSyncHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new DepartmentAttributeSyncOptions(DepartmentAttributeSyncMode.Fix, TimeSpan.FromHours(1)),
            provider.GetRequiredService<DepartmentAttributeSyncMetrics>(),
            NullLogger<DepartmentAttributeSyncHostedService>.Instance);
        await service.StartAsync(Ct);
        for (var i = 0; i < 100 && aborted() == 0; i++) await Task.Delay(20, Ct);
        await service.StopAsync(Ct);

        aborted().Should().Be(1, "1 周目が例外で中断した");
    }

    private static MeterListener OutcomeCounter(string outcome, out Func<long> read)
        => Listen(DepartmentAttributeSyncMetrics.OutcomeCounterName, outcome, out read);

    private static MeterListener CycleCounter(string outcome, out Func<long> read)
        => Listen(DepartmentAttributeSyncMetrics.CycleCounterName, outcome, out read);

    private static MeterListener Listen(string counter, string outcome, out Func<long> read)
        => Listen(counter, DepartmentAttributeSyncMetrics.OutcomeTag, outcome, out read);

    private static MeterListener Listen(string counter, string tagKey, string tagValue, out Func<long> read)
    {
        long total = 0;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DepartmentAttributeSyncMetrics.MeterName && instrument.Name == counter)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == tagKey && (string?)tag.Value == tagValue)
                    Interlocked.Add(ref total, value);
        });
        listener.Start();
        read = () => Interlocked.Read(ref total);
        return listener;
    }

    private static MeterListener FailedCounter(out Func<long> read)
    {
        long total = 0;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DepartmentAttributeSyncMetrics.MeterName
                    && instrument.Name == DepartmentAttributeSyncMetrics.OutcomeCounterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == DepartmentAttributeSyncMetrics.OutcomeTag && (string?)tag.Value == "failed")
                    Interlocked.Add(ref total, value);
        });
        listener.Start();
        read = () => Interlocked.Read(ref total);
        return listener;
    }

    // 🔴 Off のときは器が同期を**解決すらしない**（スコープを作らない）。
    [Fact]
    public async Task Hosted_service_in_off_mode_never_runs_the_sync()
    {
        var factory = new ThrowingScopeFactory();
        using var service = new DepartmentAttributeSyncHostedService(
            factory, new DepartmentAttributeSyncOptions(DepartmentAttributeSyncMode.Off, TimeSpan.FromMilliseconds(10)),
            new DepartmentAttributeSyncMetrics(Meters), NullLogger<DepartmentAttributeSyncHostedService>.Instance);

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
        private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);

        public HashSet<string> FailWritesFor { get; } = new(StringComparer.Ordinal);

        public bool FailTreeReads { get; set; }

        public void AddAttribute(string userId, string key, string value) => _attributes[userId][key] = value;

        // 計画の読み取りと書き込みの間に割り込む操作（SC-17 の無効化などを模す）。
        public Action<string>? BeforeWrite { get; set; }

        public void Disable(string userId, string anchor)
        {
            _enabled[userId] = false;
            _attributes[userId]["account_disabled_at"] = anchor;
        }

        public bool IsEnabled(string userId) => _enabled[userId];

        public List<(string UserId, string Department)> Writes { get; } = [];
        public int Calls { get; private set; }

        // #1609: 全利用者の列挙・部門の消去の偽物の状態。
        private readonly List<string> _userOrder = [];
        private readonly Dictionary<string, string> _usernames = new(StringComparer.Ordinal);

        /// <summary>消した利用者（`ClearDepartmentAttributeAsync` が実際に書いた順）。</summary>
        public List<string> Clears { get; } = [];

        /// <summary>全利用者の列挙がページの途中で失敗する（例外）。</summary>
        public bool FailEnumeration { get; set; }

        /// <summary>全利用者の列挙が打ち切られる（読めた分 ＝ 全員を返すが Complete = false）。</summary>
        public bool TruncateEnumeration { get; set; }

        /// <summary>所属者の一覧（`ListGroupMembersAsync`）からだけ漏れる利用者（ページ送りで飛んだ形を模す）。</summary>
        public HashSet<string> HiddenFromMemberLists { get; } = new(StringComparer.Ordinal);

        public void Group(string id, string path) => _groups.Add(new IdentityGroup(id, path[(path.LastIndexOf('/') + 1)..], path));

        public void User(string id, string? department, params string[] groupIds) => AddUser(id, department, null, groupIds);

        // 利用者名が内部 ID と違う利用者（サービスアカウントの形など）。部門グループには入れない。
        public void NamedUser(string id, string? department, string username) => AddUser(id, department, username, []);

        private void AddUser(string id, string? department, string? username, string[] groupIds)
        {
            _attributes[id] = new Dictionary<string, string>(StringComparer.Ordinal) { ["clearance"] = "internal" };
            _enabled[id] = true;
            _userOrder.Add(id);
            _usernames[id] = username ?? id;
            if (department is not null) _attributes[id]["department"] = department;
            foreach (var g in groupIds)
            {
                if (!_members.TryGetValue(g, out var list)) _members[g] = list = [];
                list.Add(id);
            }
        }

        public string? Department(string userId) => _attributes[userId].GetValueOrDefault("department");

        public bool HasAttribute(string userId, string key) => _attributes[userId].ContainsKey(key);

        public Dictionary<string, string[]> MembershipSnapshot()
            => _members.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);

        private IdentityUser Snapshot(string id)
            => new(id, _usernames[id], id, _enabled[id], [],
                new Dictionary<string, string>(_attributes[id], StringComparer.Ordinal));

        // #1609: 全利用者（本物と同じく属性つき・ロールなし）。偽物は利用者名の接頭辞でサービスアカウントを除かない ——
        // 除くのは同期の側でも確かめる（本物の実装が除き損ねても消さない）。
        public Task<UserEnumeration> ListAllUsersAsync(CancellationToken ct)
        {
            Calls++;
            if (FailEnumeration) throw new HttpRequestException("Keycloak の利用者一覧の 2 ページ目が 500 を返した（偽）");
            return Task.FromResult(new UserEnumeration([.. _userOrder.Select(Snapshot)], Complete: !TruncateEnumeration));
        }

        // #1609: 消す直前の所属の読み直し（本物と同じく直接の所属だけ。親へ遡らない）。
        public Task<IReadOnlyList<IdentityGroup>> GetUserGroupsAsync(string userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IdentityGroup>>(
                [.. _groups.Where(g => _members.GetValueOrDefault(g.Id, []).Contains(userId))]);
        }

        // #1609: 本物と同じ意味論（読み取りから変わっていれば書かない）で department だけを消す。
        public Task<DepartmentWriteResult> ClearDepartmentAttributeAsync(
            string userId, IdentityUser observed, CancellationToken ct)
        {
            Calls++;
            BeforeWrite?.Invoke(userId);
            if (FailWritesFor.Contains(userId)) throw new HttpRequestException("Keycloak が 500 を返した（偽）");
            if (!_attributes.ContainsKey(userId)) return Task.FromResult(DepartmentWriteResult.NotFound);
            if (!DepartmentWriteResult.SameExceptDepartment(observed, Snapshot(userId)))
                return Task.FromResult(DepartmentWriteResult.Changed);
            Clears.Add(userId);
            _attributes[userId].Remove("department");
            return Task.FromResult(DepartmentWriteResult.Applied(Snapshot(userId)));
        }

        public Task<IdentityGroup?> FindGroupByPathAsync(string path, CancellationToken ct)
        {
            Calls++;
            if (FailTreeReads) throw new HttpRequestException("Keycloak のグループ照会へ届かない（偽）");
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
                [.. _members.GetValueOrDefault(groupId, []).Where(id => !HiddenFromMemberLists.Contains(id)).Select(Snapshot)]);
        }

        // 本物と同じ意味論: 書く直前の像が計画の読み取り（observed）と有効状態・部門以外の属性で違えば書かない。
        public Task<DepartmentWriteResult> SetDepartmentAttributeAsync(
            string userId, string department, IdentityUser observed, CancellationToken ct)
        {
            Calls++;
            BeforeWrite?.Invoke(userId);
            if (FailWritesFor.Contains(userId)) throw new HttpRequestException("Keycloak が 500 を返した（偽）");
            if (!_attributes.ContainsKey(userId)) return Task.FromResult(DepartmentWriteResult.NotFound);
            if (!DepartmentWriteResult.SameExceptDepartment(observed, Snapshot(userId)))
                return Task.FromResult(DepartmentWriteResult.Changed);
            Writes.Add((userId, department));
            _attributes[userId]["department"] = department;
            return Task.FromResult(DepartmentWriteResult.Applied(Snapshot(userId)));
        }

        public Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct) => throw Untouchable();
        public Task<IdentityUser?> FindByUsernameAsync(string username, CancellationToken ct) => throw Untouchable();
        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string query, int max, CancellationToken ct) => throw Untouchable();
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
