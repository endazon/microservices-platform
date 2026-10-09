using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServer.Tests.Features.McpClients;

// C-75〜C-81（AC1 / FU4）: secret は登録（無人）と再発行の応答にだけ載る。登録簿・一覧には無い。
[Trait("TestKind", "Integration")]
public class McpClientSecretEndpointTests(AuditCapturingFactory factory) : IClassFixture<AuditCapturingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // C-75: 無人の登録の 201 は IdP の現在の secret をそのまま返し、キャッシュさせない。
    [Fact]
    public async Task 無人の登録の応答はIdPのsecretを一度だけ返す()
    {
        var response = await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("sec-sa"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("応答は client secret を含む");
        var view = await response.Content.ReadFromJsonAsync<McpClientRegistrationView>(Ct);
        view!.ClientSecret.Should().NotBeNullOrEmpty().And.Be(factory.Idp().CurrentSecretOf("sec-sa"));
        view.Kind.Should().Be("service-account");
    }

    // C-76: 有人の 201 は secret を持たない（公開クライアント）。
    [Fact]
    public async Task 有人の登録の応答はsecretを持たない()
    {
        var response = await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.Interactive("sec-human"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<McpClientRegistrationView>(Ct))!.ClientSecret.Should().BeNull();
    }

    // C-77（否定形）: 一覧・登録簿には secret が無い（再表示の手段を持たない。プラットフォームは値を保存しない）。
    [Fact]
    public async Task 一覧と登録簿にはsecretが無い()
    {
        var created = await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("sec-store"), Ct);
        var secret = (await created.Content.ReadFromJsonAsync<McpClientRegistrationView>(Ct))!.ClientSecret!;

        var list = await factory.Registrar().GetStringAsync("/mcp-clients", Ct);
        list.Should().NotContain(secret).And.NotContain("clientSecret");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        var row = await db.Clients.AsNoTracking().SingleAsync(c => c.ClientId == "sec-store", Ct);
        JsonSerializer.Serialize(row).Should().NotContain(secret);
    }

    // C-78: 再発行は IdP で再生成した新しい値を返し、旧い値は IdP に残らない。登録簿は書かない（updatedAt も動かない）。
    [Fact]
    public async Task 再発行は新しいsecretを一度だけ返し登録簿を書かない()
    {
        var created = await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("sec-re"), Ct);
        var first = (await created.Content.ReadFromJsonAsync<McpClientRegistrationView>(Ct))!;

        var response = await factory.Registrar().PostAsync("/mcp-clients/sec-re/reissue-secret", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var reissued = (await response.Content.ReadFromJsonAsync<McpClientSecretView>(Ct))!;
        reissued.ClientId.Should().Be("sec-re");
        reissued.ClientSecret.Should().NotBe(first.ClientSecret).And.Be(factory.Idp().CurrentSecretOf("sec-re"));
        var list = await factory.Registrar().GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct);
        list!.Single(c => c.ClientId == "sec-re").UpdatedAt.Should().Be(first.UpdatedAt, "登録簿は書かない");
    }

    // C-79（否定形）: 有人・不在・IdP に無い行・入口が作っていないクライアントは再発行しない。
    [Fact]
    public async Task 再発行できない行は拒む()
    {
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.Interactive("sec-re-human"), Ct);
        await factory.AddRegistryRow("sec-re-legacy", McpClientKind.ServiceAccount, Ct);
        await factory.AddRegistryRow("sec-re-seeded", McpClientKind.ServiceAccount, Ct);
        factory.Idp().Seed("sec-re-seeded");

        (await factory.Registrar().PostAsync("/mcp-clients/sec-re-human/reissue-secret", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "有人は公開クライアントで secret を持たない");
        (await factory.Registrar().PostAsync("/mcp-clients/sec-re-none/reissue-secret", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.Registrar().PostAsync("/mcp-clients/sec-re-legacy/reissue-secret", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "IdP に無い（入口ができる前の行）");
        (await factory.Registrar().PostAsync("/mcp-clients/sec-re-seeded/reissue-secret", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "入口が作っていないクライアントの secret は回さない");
        factory.Idp().CurrentSecretOf("sec-re-seeded").Should().BeNull("何も書かない");
    }

    // C-80: 表示と再発行はシステム管理者に限る（決定 2 の 4）。運用者は 403 で、IdP の値は変わらない。
    [Fact]
    public async Task 再発行は管理者に限る()
    {
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("sec-role"), Ct);
        var before = factory.Idp().CurrentSecretOf("sec-role");

        var response = await factory.Registrar(roles: "platform-operator").PostAsync("/mcp-clients/sec-role/reissue-secret", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Idp().CurrentSecretOf("sec-role").Should().Be(before);
    }

    // C-81: 登録の直後に secret を読めなければ、作ったクライアントを消して 502（登録簿にも書かない。既存の補償に乗る）。
    [Fact]
    public async Task secretを読めない登録は作ったクライアントを消して502()
    {
        factory.Idp().FailSecretReads = true;
        try
        {
            var response = await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("sec-fail"), Ct);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            factory.Idp().Snapshot().Should().NotContainKey("sec-fail", "補償で消す");
            var list = await factory.Registrar().GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct);
            list.Should().NotContain(c => c.ClientId == "sec-fail");
        }
        finally
        {
            factory.Idp().FailSecretReads = false;
        }
    }
}

// C-87: 書き込み口が未構成の配備では、再発行は 503（何も書かない）で、監査は unavailable を残す。
[Trait("TestKind", "Integration")]
public class UnconfiguredReissueSecretEndpointTests(UnconfiguredIdpProvisioningEndpointTests.Factory factory)
    : IClassFixture<UnconfiguredIdpProvisioningEndpointTests.Factory>
{
    [Fact]
    public async Task 未構成の再発行は503()
    {
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRegistryRow("unconf-sa", McpClientKind.ServiceAccount, ct);

        var response = await factory.CreateClient().PostAsync("/mcp-clients/unconf-sa/reissue-secret", null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
