using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Tests.Features.DataSources;

// NFR-18, FR-01, SC-06, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 2 (#458 段 S4): 移送期間の旗（`ConnectorCredentials:PlaintextAllowed`）。
//   - 旗が真（既定）: 従前どおり平文を保存できる（移送期間）。
//   - 旗が偽: SC-06 の登録・更新・部分更新で、秘密のキーへの平文を 400 で拒む（参照・空・マスクの書き戻しは通す）。
//   - 旗が偽なのに平文の行が残っていれば、起動しない（旗を偽にできるのは平文の行が 0 件のときだけ）。
[Trait("TestKind", "Integration")]
public sealed class DataSourcePlaintextCredentialTests
{
    private const string Plain = "plaintext-write-marker-93ab";

    private static WebApplicationFactory<Program> WithFlag(TestWebApplicationFactory factory, bool allowed) =>
        factory.WithWebHostBuilder(b => b.UseSetting("ConnectorCredentials:PlaintextAllowed", allowed ? "true" : "false"));

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Dictionary<string, string> config) =>
        client.PostAsJsonAsync("/datasources", new
        {
            name = $"wiki-{Guid.NewGuid():N}",
            sourceType = "wiki",
            connectionUri = "https://wiki.example.test",
            config,
        }, TestContext.Current.CancellationToken);

    private static async Task<Guid> IdOf(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

    [Fact]
    public async Task While_the_flag_is_true_plaintext_is_still_accepted()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await CreateAsync(factory.CreateClient(), new() { ["apiToken"] = Plain });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "移送期間は従前どおり（既存の運用を止めない）");
    }

    [Fact]
    public async Task After_the_flag_is_false_create_rejects_plaintext_but_accepts_references_and_empty()
    {
        using var factory = new TestWebApplicationFactory();
        var client = WithFlag(factory, allowed: false).CreateClient();

        var rejected = await CreateAsync(client, new() { ["apiToken"] = Plain, ["spaceKey"] = "DOCS" });
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var body = JsonDocument.Parse(raw))
        {
            body.RootElement.GetProperty("error").GetString().Should().Be("plaintext-credential-rejected");
            body.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).Should().Equal(["apiToken"]);
        }
        raw.Should().NotContain(Plain, "拒んだ値を応答へ返さない");

        (await CreateAsync(client, new() { ["apiToken"] = "vault:datasource/x#apiToken" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await CreateAsync(client, new() { ["apiToken"] = "  " })).StatusCode.Should().Be(HttpStatusCode.Created, "空白は値なし");
        (await CreateAsync(client, new() { ["spaceKey"] = "DOCS" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task After_the_flag_is_false_update_and_patch_reject_plaintext_but_keep_masked_round_trips()
    {
        using var factory = new TestWebApplicationFactory();
        var client = WithFlag(factory, allowed: false).CreateClient();
        var id = await IdOf(await CreateAsync(client, new() { ["apiToken"] = "vault:datasource/y#apiToken" }));

        var put = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "wiki-put",
            sourceType = "wiki",
            connectionUri = "https://wiki.example.test",
            config = new Dictionary<string, string> { ["apiToken"] = Plain },
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("plaintext-credential-rejected");

        var patch = await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            config = new Dictionary<string, string> { ["apiToken"] = Plain },
        }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await patch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("plaintext-credential-rejected");

        // 応答のマスク（***）の書き戻しは「既存値を保つ」印であり、平文ではない。
        var masked = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "wiki-put",
            sourceType = "wiki",
            connectionUri = "https://wiki.example.test",
            config = new Dictionary<string, string> { ["apiToken"] = SecretMask.Placeholder },
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);
        masked.StatusCode.Should().Be(HttpStatusCode.OK);
        var nameOnly = await client.PatchAsJsonAsync($"/datasources/{id}", new { name = "wiki-renamed" }, TestContext.Current.CancellationToken);
        nameOnly.StatusCode.Should().Be(HttpStatusCode.OK, "Config を送らない PATCH は現状維持");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        db.DataSources.Find(id)!.Config["apiToken"].Should().Be("vault:datasource/y#apiToken");
    }

    [Fact]
    public async Task The_service_refuses_to_start_with_the_flag_false_while_a_plaintext_row_remains()
    {
        using var factory = new TestWebApplicationFactory();
        (await CreateAsync(factory.CreateClient(), new() { ["apiToken"] = Plain })).StatusCode.Should().Be(HttpStatusCode.Created);

        // 同じ InMemory の DB を共有する派生のホストを、旗を偽にして起こす。
        var act = () => WithFlag(factory, allowed: false).CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*平文の資格情報を持つデータソースが 1 件*")
            .Which.Message.Should().NotContain(Plain);
    }

    [Fact]
    public async Task The_service_starts_with_the_flag_false_once_no_plaintext_row_remains()
    {
        using var factory = new TestWebApplicationFactory();
        var id = await IdOf(await CreateAsync(factory.CreateClient(), new() { ["apiToken"] = Plain }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
            db.DataSources.Find(id)!.ReplacePlaintextWithReference("apiToken", Plain).Should().BeTrue();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var health = await WithFlag(factory, allowed: false).CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        health.IsSuccessStatusCode.Should().BeTrue("平文の行が 0 件なら旗を偽にして起動できる");
    }
}
