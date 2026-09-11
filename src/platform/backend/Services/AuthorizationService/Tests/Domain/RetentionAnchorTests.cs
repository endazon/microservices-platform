using AuthorizationService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AuthorizationService.Tests.Domain;

// FR-19, SC-17, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5・フォローアップ 3, [[IADR-0428]] (#1392):
// 退職時に個人資料へ掛かる 30 日窓の**起点**（retention anchor）の解決と判定。
//
// 🔴 **陰性対照（起点なし → 数えない）だけでは、「常に対象外」を返す実装と区別できない。**
// 陽性対照（起点あり → 30 日後に対象）を同じ表に並べる。
[Trait("TestKind", "Unit")]
public class RetentionAnchorTests
{
    private static readonly DateTimeOffset DisabledAt = new(2026, 8, 1, 3, 0, 0, TimeSpan.Zero);

    private static RetentionAnchorOptions Options(
        RetentionAnchorSource source = RetentionAnchorSource.AccountDisabledAt)
        => new() { Source = source };

    private static Dictionary<string, string> Attributes(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ---- 解決 ----

    // FR-19, ADR-0082 決定 5: 陽性対照。無効化日が属性に載っていれば起点として解決する。
    [Fact]
    public void 無効化日が載っていれば起点として解決する()
    {
        var anchor = RetentionAnchorPolicy.Resolve(
            Attributes((RetentionAnchorAttributes.AccountDisabledAtKey, "2026-08-01T03:00:00Z")),
            Options());

        anchor.State.Should().Be(RetentionAnchorState.Supplied);
        anchor.At.Should().Be(DisabledAt);
    }

    // FR-19, ADR-0082 決定 5: 🔴 **陰性対照。起点が未供給なら「未供給」である**
    //（既定値・エポックへ倒さない）。
    [Fact]
    public void 属性が無ければ未供給である()
        => RetentionAnchorPolicy.Resolve(Attributes(("department", "hr")), Options())
            .State.Should().Be(RetentionAnchorState.NotSupplied);

    // FR-19: 🔴 **「0」と「未供給」を分ける**（#1392 の射程）。**`"0"` はエポックではない。**
    // 空白だけの値も「載っていない」と同じに扱う（Keycloak は空の属性を残せる）。
    [Theory]
    [InlineData("0", RetentionAnchorState.Unreadable)]
    [InlineData("2026/08/01", RetentionAnchorState.Unreadable)]
    [InlineData("yesterday", RetentionAnchorState.Unreadable)]
    [InlineData("", RetentionAnchorState.NotSupplied)]
    [InlineData("   ", RetentionAnchorState.NotSupplied)]
    public void 日付として読めない値は起点にならない(string raw, RetentionAnchorState expected)
        => RetentionAnchorPolicy.Resolve(
                Attributes((RetentionAnchorAttributes.AccountDisabledAtKey, raw)), Options())
            .State.Should().Be(expected);

    // FR-19, ADR-0082 決定 5: 人事由来の退職日は**日付だけ**で来ることを見込む。
    [Fact]
    public void 日付だけの値も起点として読む()
    {
        var anchor = RetentionAnchorPolicy.Resolve(
            Attributes((RetentionAnchorAttributes.HrLeaveDateKey, "2026-08-01")),
            Options(RetentionAnchorSource.HrLeaveDate));

        anchor.State.Should().Be(RetentionAnchorState.Supplied);
        anchor.At.Should().Be(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    }

    // FR-19, ADR-0082 フォローアップ 3: 🔴 **構成点が効く。** 出所を持ち替えると読む属性が変わる。
    // 人事側が未供給のままなら **fail-safe（数えない）へ倒れる** —— 人事連携は未実装である。
    [Fact]
    public void 出所を人事由来へ持ち替えると読む属性が変わる()
    {
        var attributes = Attributes(
            (RetentionAnchorAttributes.AccountDisabledAtKey, "2026-08-01T03:00:00Z"));

        RetentionAnchorPolicy.Resolve(attributes, Options(RetentionAnchorSource.HrLeaveDate))
            .State.Should().Be(RetentionAnchorState.NotSupplied,
                "人事由来へ持ち替えたのに無効化日を読み続けたら、切替えが効いていない");

        RetentionAnchorPolicy.Resolve(
                Attributes((RetentionAnchorAttributes.HrLeaveDateKey, "2026-08-01")),
                Options(RetentionAnchorSource.HrLeaveDate))
            .State.Should().Be(RetentionAnchorState.Supplied);
    }

    // ---- 判定 ----

    // FR-19, SC-19, 計画 ADR-0036 D-09: 🔴 **陽性対照。起点から 30 日で対象になる。**
    [Theory]
    [InlineData(30, RetentionEligibility.Elapsed)]
    [InlineData(31, RetentionEligibility.Elapsed)]
    [InlineData(29, RetentionEligibility.WithinWindow)]
    [InlineData(0, RetentionEligibility.WithinWindow)]
    public void 起点があれば30日で対象になる(int elapsedDays, RetentionEligibility expected)
    {
        var anchor = new RetentionAnchor(RetentionAnchorState.Supplied, DisabledAt, "2026-08-01T03:00:00Z");

        RetentionAnchorPolicy.Evaluate(anchor, DisabledAt.AddDays(elapsedDays)).Should().Be(expected);
    }

    // FR-19: 🔴 **陰性対照。起点が無ければ、どれだけ時間が経っても対象にしない**（fail-safe）。
    // **これが「削除しない」の実体である** —— 起点の不在を「経過した」へ倒す経路は存在しない。
    [Theory]
    [InlineData(RetentionAnchorState.NotSupplied)]
    [InlineData(RetentionAnchorState.Unreadable)]
    public void 起点が無ければ何年経っても対象にしない(RetentionAnchorState state)
    {
        var anchor = new RetentionAnchor(state, null, null);

        RetentionAnchorPolicy.Evaluate(anchor, DisabledAt.AddYears(10))
            .Should().Be(RetentionEligibility.NotEvaluable);
    }

    // FR-19, 計画 ADR-0036 D-09 / ADR-0082 決定 5:「**期間（30 日）は変えない**」。
    // 🔴 構成に出していないことを値で固定する（`PrivateNote` の 90 日と混ざっていないこと）。
    [Fact]
    public void 窓は30日である()
        => RetentionAnchorPolicy.RetentionDays.Should().Be(30);

    // 書いた値をそのまま読み戻せる（刻印と解決が同じ書式を使う）。
    [Fact]
    public void 書いた起点はそのまま読み戻せる()
    {
        var written = RetentionAnchorPolicy.Format(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(9)));

        written.Should().Be("2026-08-01T03:00:00Z");
        RetentionAnchorPolicy.Resolve(
                Attributes((RetentionAnchorAttributes.AccountDisabledAtKey, written)), Options())
            .At.Should().Be(DisabledAt);
    }

    // ---- 予約キーの扱い ----

    // FR-19, SC-17, [[IADR-0428]]: 応答 DTO 側の像から予約キーだけが落ちる（ABAC 属性は残る）。
    [Fact]
    public void 予約キーだけを落とした像を返す()
    {
        var without = RetentionAnchorAttributes.WithoutReserved(Attributes(
            ("department", "hr"),
            (RetentionAnchorAttributes.AccountDisabledAtKey, "2026-08-01T03:00:00Z"),
            (RetentionAnchorAttributes.HrLeaveDateKey, "2026-08-01")));

        without.Should().ContainKey("department").And.HaveCount(1);
    }

    // FR-19, SC-17, [[IADR-0428]]: 🔴 **ABAC 属性の差し替えで起点が消えない。**
    // 要求側に予約キーが混ざっていても採らない（書き手は 1 つだけである）。
    [Fact]
    public void 差し替えでは予約キーを持ち越し要求側の値は採らない()
    {
        var merged = RetentionAnchorAttributes.PreserveReserved(
            current: Attributes(
                ("department", "finance"),
                (RetentionAnchorAttributes.AccountDisabledAtKey, "2026-08-01T03:00:00Z")),
            requested: Attributes(
                ("department", "hr"),
                (RetentionAnchorAttributes.AccountDisabledAtKey, "1999-01-01T00:00:00Z")));

        merged["department"].Should().Be("hr");
        merged[RetentionAnchorAttributes.AccountDisabledAtKey].Should().Be("2026-08-01T03:00:00Z");
    }

    // ---- 構成 ----

    // FR-19, ADR-0082 決定 5: 未宣言は暫定側（アカウント無効化日）。
    [Fact]
    public void 未宣言なら暫定側の起点を採る()
        => RetentionAnchorOptions.FromConfiguration(Config(new()))
            .Source.Should().Be(RetentionAnchorSource.AccountDisabledAt);

    // FR-19, ADR-0082 フォローアップ 3: 宣言で持ち替えられる（構成点）。
    [Theory]
    [InlineData("account-disabled-at", RetentionAnchorSource.AccountDisabledAt)]
    [InlineData("hr-leave-date", RetentionAnchorSource.HrLeaveDate)]
    public void 宣言した出所を採る(string declared, RetentionAnchorSource expected)
        => RetentionAnchorOptions.FromConfiguration(
                Config(new() { [RetentionAnchorOptions.SourceKey] = declared }))
            .Source.Should().Be(expected);

    // 🔴 値域外は起動時に落とす。既定へ倒すと、綴りを間違えた配備が
    // 「どの日付を起点にしているか誰も答えられない」まま動き続ける。
    [Fact]
    public void 値域外の宣言は起動時に落ちる()
    {
        var act = () => RetentionAnchorOptions.FromConfiguration(
            Config(new() { [RetentionAnchorOptions.SourceKey] = "termination-date" }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*termination-date*");
    }

    private static IConfiguration Config(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
