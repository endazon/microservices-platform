using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace DataSourceService.Tests;

// IADR-0053, claude-review #222: データソース応答は Config 内の秘密（apiToken 等）をマスクし、
// 非秘密キー（listPath 等）はそのまま返すことを検証する（Vault 移行までの API 応答からの秘匿）。
public class DataSourceSecretRedactionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetDataSource_MasksSecretConfigKeys_ButKeepsNonSecrets()
    {
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/datasources", new
        {
            name = "hr-wiki",
            sourceType = "wiki",
            connectionUri = "https://wiki.example.com",
            config = new Dictionary<string, string>
            {
                ["apiToken"] = "super-secret-token",
                ["listPath"] = "/api/pages",
            },
        }, TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("id").GetGuid();

        // 作成応答でも秘密はマスクされる。
        using (var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            created.RootElement.GetProperty("config").GetProperty("apiToken").GetString().Should().Be("***");
        }

        var get = await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken);
        get.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var config = doc.RootElement.GetProperty("config");

        config.GetProperty("apiToken").GetString().Should().Be("***", "秘密キーはマスクする");
        config.GetProperty("listPath").GetString().Should().Be("/api/pages", "非秘密キーは保持する");
    }

    [Fact]
    public async Task ListDataSources_MasksSecretConfigKeys()
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/datasources", new
        {
            name = "wiki-list",
            sourceType = "wiki",
            connectionUri = "https://wiki2.example.com",
            config = new Dictionary<string, string> { ["apiToken"] = "list-secret" },
        }, TestContext.Current.CancellationToken);

        var list = await client.GetAsync("/datasources", TestContext.Current.CancellationToken);
        list.EnsureSuccessStatusCode();
        var json = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        json.Should().NotContain("list-secret", "一覧応答に平文の秘密を含めない");
    }
}
