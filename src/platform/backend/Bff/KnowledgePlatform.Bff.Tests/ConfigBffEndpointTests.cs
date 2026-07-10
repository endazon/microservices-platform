using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Tests;

// FR-15, ADR-0018: 構成情報 API（/bff/admin/config）の読み取り専用・アクセス制御・監査・ドリフトを検証する。
public class ConfigBffEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    private static EffectiveCollection SampleEffective()
    {
        var wiki = new ServiceIntrospectionDto(
            "wiki-service",
            [new StepIntrospectionDto("wiki-sync",
                "WikiService.Api.Composable.Steps.DocumentSyncConsumer",
                "DocumentUpdated", [], true)],
            [new PortSelectionDto("wiki-sync", "WikiJsGraphQlClient", "http://wiki-js:3000")],
            []);
        return new EffectiveCollection([wiki], new HashSet<string> { "wiki-service" }, new HashSet<string>());
    }

    // 実効構成を読み取り専用で返す。構成バージョン（Git コミット）を含む。
    [Fact]
    public async Task GetConfig_AsAdmin_ReturnsEffectiveConfigWithVersion()
    {
        factory.StubEffective = SampleEffective();

        var resp = await factory.CreateClient().GetAsync("/bff/admin/config");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var config = await resp.Content.ReadFromJsonAsync<EffectiveConfigDto>();
        config.Should().NotBeNull();
        config!.Version.GitCommit.Should().Be("abc1234");
        config.Version.AppliedBy.Should().Be("argocd");
        config.Pipeline.Should().ContainSingle(s => s.Name == "wiki-sync" && s.Enabled);
        config.Ports.Should().Contain(p => p.Implementation == "WikiJsGraphQlClient");
    }

    // 運用者ロールでも閲覧できる（管理者・運用者に限定）。
    [Fact]
    public async Task GetConfig_AsOperator_ReturnsOk()
    {
        factory.StubEffective = SampleEffective();

        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/config");
        req.Headers.Add(TestAuthHandler.RolesHeader, KnowledgePlatform.Shared.Infrastructure
            .Foundation.Extensions.KnowledgePlatformAuthPolicies.OperatorRole);

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 管理者・運用者以外は 404 で応答自体を秘匿する（存在秘匿）。
    [Fact]
    public async Task GetConfig_AsNonPrivileged_Returns404AndAuditsDenied()
    {
        factory.RecordedAudits.Clear();

        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/config");
        req.Headers.Add(TestAuthHandler.RolesHeader, "platform-viewer");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.RecordedAudits.Should().Contain(a =>
            a.Action == "config.read" && a.Outcome == "denied");
    }

    // 無認証（JWT を持たない一般利用者）も 404 で応答自体を秘匿し、拒否を監査する。
    // RequireAuthorization による 401 短絡で存在が漏れないことを保証する（FR-15 / IADR-0029）。
    [Fact]
    public async Task GetConfig_AsAnonymous_Returns404AndAuditsDenied()
    {
        factory.RecordedAudits.Clear();

        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/config");
        req.Headers.Add(TestAuthHandler.AnonymousHeader, "true");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.RecordedAudits.Should().Contain(a =>
            a.Action == "config.read" && a.Outcome == "denied");
    }

    // ドリフト取得も無認証は 404 で秘匿する。
    [Fact]
    public async Task GetDrift_AsAnonymous_Returns404()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/config/drift");
        req.Headers.Add(TestAuthHandler.AnonymousHeader, "true");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-15 (#139), IADR-0046: 構成バージョン履歴を返す。履歴未注入のテスト環境では現在バージョン
    // （Config:GitCommit 等）へ縮退した単一エントリを返す。取得は許可を監査する。
    [Fact]
    public async Task GetHistory_AsAdmin_ReturnsCurrentVersionEntryAndAuditsGranted()
    {
        factory.StubEffective = SampleEffective();
        factory.RecordedAudits.Clear();

        var resp = await factory.CreateClient().GetAsync("/bff/admin/config/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await resp.Content.ReadFromJsonAsync<List<ConfigVersionEntryDto>>();
        history.Should().NotBeNull();
        history!.Should().ContainSingle();
        history[0].GitCommit.Should().Be("abc1234");
        history[0].AppliedBy.Should().Be("argocd");
        factory.RecordedAudits.Should().Contain(a =>
            a.Action == "config.history.read" && a.Outcome == "granted");
    }

    // 履歴取得も無認証は 404 で秘匿し、拒否を監査する（存在秘匿・IADR-0009）。
    [Fact]
    public async Task GetHistory_AsAnonymous_Returns404AndAuditsDenied()
    {
        factory.RecordedAudits.Clear();

        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/admin/config/history");
        req.Headers.Add(TestAuthHandler.AnonymousHeader, "true");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.RecordedAudits.Should().Contain(a =>
            a.Action == "config.history.read" && a.Outcome == "denied");
    }

    // 取得操作（許可）が監査ログに残る。
    [Fact]
    public async Task GetConfig_AsAdmin_AuditsGranted()
    {
        factory.StubEffective = SampleEffective();
        factory.RecordedAudits.Clear();

        await factory.CreateClient().GetAsync("/bff/admin/config");

        factory.RecordedAudits.Should().Contain(a =>
            a.Action == "config.read" && a.Outcome == "granted");
    }

    // ドリフト検出: 宣言に無い購読が実効に存在すれば不一致を返す。
    [Fact]
    public async Task GetDrift_WhenUndeclaredSubscription_ReportsDrift()
    {
        // BFF テストでは宣言（pipeline）は空のため、実効の有効段は「宣言に無い購読」になる。
        factory.StubEffective = SampleEffective();

        var drift = await factory.CreateClient()
            .GetFromJsonAsync<DriftReportDto>("/bff/admin/config/drift");

        drift.Should().NotBeNull();
        drift!.HasDrift.Should().BeTrue();
        drift.Findings.Should().Contain(f => f.Kind == DriftDetector.UndeclaredSubscription);
    }

    // FR-15 (#145): 適用直後の即時ドリフト検出トリガ。メッシュ内部限定・無認証で 202 を返し、
    // 構成情報は本文に含めない（存在秘匿）。ArgoCD PostSync フックが叩く経路。
    [Fact]
    public async Task PostDriftRun_ReturnsAcceptedWithoutBody()
    {
        factory.StubEffective = SampleEffective();

        var resp = await factory.CreateClient().PostAsync("/internal/config/drift-run", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    // FR-15 (#145): 受け入れ基準「不一致があれば適用直後にアラートが発火する」。
    // 不一致（宣言に無い購読）を含む実効構成でトリガを叩くと、IDriftAlertSink が発火することを検証する。
    [Fact]
    public async Task PostDriftRun_WhenDriftPresent_FiresAlert()
    {
        factory.AlertedReports.Clear();
        factory.StubEffective = SampleEffective(); // 宣言に無い購読 → HasDrift

        var resp = await factory.CreateClient().PostAsync("/internal/config/drift-run", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        factory.AlertedReports.Should().ContainSingle()
            .Which.HasDrift.Should().BeTrue();
    }
}
