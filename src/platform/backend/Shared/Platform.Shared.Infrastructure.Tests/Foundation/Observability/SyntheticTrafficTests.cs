using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Observability;

// NFR-02, ADR-0044, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **合成監視の標識の判定規則。** 決定 4 は「識別できる標識を持ち」まで定め、形は実装に委ねている。
// ここで固定するのは**偽装できないこと**と**fail-closed であること**の 2 点である。
[Trait("TestKind", "Unit")]
public class SyntheticTrafficTests
{
    private const string Subject = "synthetic-monitor";

    private static SyntheticMonitoringOptions Options(params string[] subjects)
        => new() { Subjects = subjects };

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Test"));

    // ★ 陽性: `azp`（Keycloak のサービスアカウントが名乗るクライアント識別子）で判定できる。
    [Fact]
    public void 主体のazpが許可集合にあれば合成と判定する()
        => SyntheticTraffic.IsSyntheticPrincipal(
                Authenticated(new Claim("azp", Subject)), Options(Subject))
            .Should().BeTrue();

    // ★ 陽性: `preferred_username`（`service-account-<clientId>`）でも書ける。
    [Fact]
    public void 主体のpreferred_usernameが許可集合にあれば合成と判定する()
        => SyntheticTraffic.IsSyntheticPrincipal(
                Authenticated(new Claim("preferred_username", "service-account-synthetic-monitor")),
                Options("service-account-synthetic-monitor"))
            .Should().BeTrue();

    // ★ 陰性対照: 別の主体は合成ではない（除外が過剰に効かない）。
    [Fact]
    public void 別の主体は合成と判定しない()
        => SyntheticTraffic.IsSyntheticPrincipal(
                Authenticated(new Claim("azp", "platform-spa")), Options(Subject))
            .Should().BeFalse();

    // 🔴 ★ **fail-closed**: 許可集合が空なら**何も合成と見なさない**。
    // 逆向き（空＝全部が合成）に倒すと、設定漏れで**実利用がまるごと費用計上から消える。**
    [Fact]
    public void 許可集合が空なら何も合成と判定しない()
        => SyntheticTraffic.IsSyntheticPrincipal(
                Authenticated(new Claim("azp", Subject)), Options())
            .Should().BeFalse();

    // ★ 未認証は合成ではない（標識は**検証済みの**主体でしか立たない）。
    [Fact]
    public void 未認証の主体は合成と判定しない()
        => SyntheticTraffic.IsSyntheticPrincipal(new ClaimsPrincipal(new ClaimsIdentity()), Options(Subject))
            .Should().BeFalse();

    // ★ 構成由来の値が前後空白を連れてきても一致する（ConfigMap / 環境変数で静かに外れない）。
    [Fact]
    public void 許可集合の値の前後空白は無視する()
        => SyntheticTraffic.IsSyntheticPrincipal(
                Authenticated(new Claim("azp", Subject)), Options("  synthetic-monitor  "))
            .Should().BeTrue();

    // 🔴 ★ **内周の判定は主体を見ない**（ヘッダだけ）。外周と内周で材料が違うことを型で示す。
    [Fact]
    public void 内周の判定はヘッダの有無で決まる()
    {
        var request = new DefaultHttpContext().Request;
        SyntheticTraffic.IsSyntheticInternalRequest(request).Should().BeFalse();

        request.Headers[SyntheticTraffic.HeaderName] = SyntheticTraffic.HeaderValue;
        SyntheticTraffic.IsSyntheticInternalRequest(request).Should().BeTrue();
    }

    // ★ 空値のヘッダは標識にならない（`X-Synthetic-Traffic:` だけ付いた要求を合成にしない）。
    [Fact]
    public void 空値のヘッダは内周の標識にならない()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[SyntheticTraffic.HeaderName] = "";
        SyntheticTraffic.IsSyntheticInternalRequest(request).Should().BeFalse();
    }

    // ★ 伝播は合成のときだけ（常に付けると内周の陽性が「常に真」になる）。
    [Fact]
    public void 伝播は合成のときだけ行う()
    {
        using var synthetic = new HttpRequestMessage();
        SyntheticTraffic.PropagateTo(synthetic, isSynthetic: true);
        synthetic.Headers.Contains(SyntheticTraffic.HeaderName).Should().BeTrue();

        using var ordinary = new HttpRequestMessage();
        SyntheticTraffic.PropagateTo(ordinary, isSynthetic: false);
        ordinary.Headers.Contains(SyntheticTraffic.HeaderName).Should().BeFalse();
    }

    // 🔴 ★ **既定では LLM を呼ばせない。** ADR-0076 §残るもの が費用の上限を未定と残しているため、
    // 既定値が `true` へ倒れると**上限の無い恒常的な費用**が実装裁量で発生することになる。
    [Fact]
    public void LLM送信の既定は許可しない()
        => new SyntheticMonitoringOptions().AllowLlmEgress.Should().BeFalse();

    // ── NFR-02, ADR-0079 決定 1・2, [[IADR-0654]] (#2335): 合成の 2 段（常時 60 秒／SLO 評価 60 分）を要求ごとに区別する ──

    private const string SloSubject = "synthetic-monitor-slo";

    private static SyntheticMonitoringOptions TwoTier()
        => new() { Subjects = [Subject, SloSubject], LlmSubjects = [SloSubject] };

    // ★ 外周の分類: SLO の主体は SloProbe、常時の主体は Synthetic、別の主体は None。
    [Fact]
    public void 外周はLlmSubjectsの主体をSLOとそれ以外の合成を常時と分類する()
    {
        SyntheticTraffic.Classify(Authenticated(new Claim("azp", SloSubject)), TwoTier())
            .Should().Be(SyntheticTrafficKind.SloProbe);
        SyntheticTraffic.Classify(Authenticated(new Claim("azp", Subject)), TwoTier())
            .Should().Be(SyntheticTrafficKind.Synthetic);
        SyntheticTraffic.Classify(Authenticated(new Claim("azp", "platform-spa")), TwoTier())
            .Should().Be(SyntheticTrafficKind.None);
    }

    // 🔴 ★ fail-closed: LlmSubjects が空なら SLO は 1 件も存在しない（常時の合成のまま）。
    [Fact]
    public void LlmSubjectsが空ならSLOと分類しない()
        => SyntheticTraffic.Classify(Authenticated(new Claim("azp", SloSubject)),
                new SyntheticMonitoringOptions { Subjects = [Subject, SloSubject] })
            .Should().Be(SyntheticTrafficKind.Synthetic);

    // 🔴 ★ SLO であることは合成であることを含む（Subjects に無くても LlmSubjects に在れば除外の対象）。
    // 逆に倒すと、LLM を呼ぶ要求が費用計上と SC-10 の集計へ実利用として混ざる（ADR-0076 決定 4）。
    [Fact]
    public void LlmSubjectsの主体は合成としても判定する()
        => SyntheticTraffic.IsSyntheticPrincipal(Authenticated(new Claim("azp", SloSubject)),
                new SyntheticMonitoringOptions { LlmSubjects = [SloSubject] })
            .Should().BeTrue();

    // ★ 伝播の値: SLO は `slo`、常時は従前の `1`、合成でなければ付けない。
    [Fact]
    public void 伝播の値は分類ごとに違い合成でなければ付けない()
    {
        using var slo = new HttpRequestMessage();
        SyntheticTraffic.PropagateTo(slo, SyntheticTrafficKind.SloProbe);
        slo.Headers.GetValues(SyntheticTraffic.HeaderName).Should().Equal([SyntheticTraffic.SloHeaderValue]);

        using var always = new HttpRequestMessage();
        SyntheticTraffic.PropagateTo(always, SyntheticTrafficKind.Synthetic);
        always.Headers.GetValues(SyntheticTraffic.HeaderName).Should().Equal([SyntheticTraffic.HeaderValue]);

        using var none = new HttpRequestMessage();
        SyntheticTraffic.PropagateTo(none, SyntheticTrafficKind.None);
        none.Headers.Contains(SyntheticTraffic.HeaderName).Should().BeFalse();
    }

    // ★ 内周の SLO の判定は値 `slo` だけ（大文字小文字は問わない）。`1`・ヘッダなしは SLO ではない。
    // 合成の判定（値が空でないこと）は従前のまま —— SLO の要求も合成として費用計上から外れる。
    [Fact]
    public void 内周のSLOの判定はヘッダの値がsloのときだけ真になる()
    {
        var request = new DefaultHttpContext().Request;
        SyntheticTraffic.IsSloProbeInternalRequest(request).Should().BeFalse();

        request.Headers[SyntheticTraffic.HeaderName] = SyntheticTraffic.HeaderValue;
        SyntheticTraffic.IsSloProbeInternalRequest(request).Should().BeFalse("常時の合成は LLM を呼ばない");

        request.Headers[SyntheticTraffic.HeaderName] = "SLO";
        SyntheticTraffic.IsSloProbeInternalRequest(request).Should().BeTrue();
        SyntheticTraffic.IsSyntheticInternalRequest(request).Should().BeTrue("SLO の要求も合成である");
    }
}
