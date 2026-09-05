using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Features.DataSources.Update;

namespace DataSourceService.Tests.Features.DataSources.Update;

// FR-01, UC-04, SC-06 / IADR-0371 決定 2 / IADR-0395: 検証を FluentValidation へ移した際、
// **HTTP の面で応答が変わっていない**ことを固定する。
//
// 🔴 **既存の `Put_OmittingReplaceableField_IsRejected` は状態コードしか見ていない。**
// 400 のまま案内文だけが変わる退行はそこでは捕まらない —— この本文は「どう直せばよいか」を
// 運用者へ伝える唯一の手段であり、失われると資格情報が古いまま放置される。
//
// 🔴 **判定の順序も固定する** —— 省略の検証は `FindAsync` より前であり、
// **対象が不存在でも 400 が返る**（404 ではない）。移送で位置が動いたらここで止まる。
[Trait("TestKind", "Integration")]
public class UpdateDataSourceResponseContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static Dictionary<string, object?> ValidBody() => new()
    {
        ["name"] = "after",
        ["sourceType"] = "filesystem",
        ["connectionUri"] = "smb://new/share",
        ["config"] = new Dictionary<string, string> { ["rootPath"] = "/new" },
        ["defaultAttributes"] = new Dictionary<string, string> { ["confidentiality"] = "internal" },
    };

    private async Task<Guid> CreateAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "before",
            sourceType = "filesystem",
            connectionUri = "smb://old/share",
            config = new Dictionary<string, string> { ["rootPath"] = "/old" },
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "confidential" },
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return created.GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("defaultAttributes")]
    public async Task OmittingReplaceableField_Returns400WithOriginalBody(string omitted)
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var body = ValidBody();
        body.Remove(omitted);

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", body,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetString()
            .Should().Be(UpdateDataSourceValidator.FullReplacementRequiredMessage);
    }

    // 🔴 **省略の検証は存在確認より前である。** 検証を `FindAsync` の後ろへ動かすと
    // ここが 404 になる（移送は振る舞いを変えない作業である）。
    [Fact]
    public async Task OmittingReplaceableField_OnUnknownId_Is400NotNotFound()
    {
        var body = ValidBody();
        body.Remove("config");

        var resp = await factory.CreateClient().PutAsJsonAsync(
            $"/datasources/{Guid.NewGuid()}", body, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "PUT は全置換なので、存在確認より先に本文の妥当性を見る");
    }

    // 陽性対照: 両方を明示した PUT は通る（「常に 400 を返す」実装で上の試験が全部緑になるのを塞ぐ）。
    [Fact]
    public async Task BothFieldsProvided_Succeeds()
    {
        var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", ValidBody(),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
