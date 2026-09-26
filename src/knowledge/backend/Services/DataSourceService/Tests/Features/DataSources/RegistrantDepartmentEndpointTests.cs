using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace DataSourceService.Tests.Features.DataSources;

// FR-05, UC-04, SC-06, IADR-0468 (#754): 登録（POST /datasources）で、`department` が未指定なら
// **登録した管理者の部門グループ**から既定属性を補う。更新（PUT / PATCH）では導き直さない。
//
// 登録者の所属は `TestAuthHandler` の `X-Test-GroupPaths`（クレーム `group_paths`）で与える。
// TestServer（メモリ内）で走り、ソケットを開かない。
[Trait("TestKind", "Integration")]
public class RegistrantDepartmentEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static object NewSource(Dictionary<string, string>? defaultAttributes = null) => new
    {
        name = "規程集",
        sourceType = "filesystem",
        connectionUri = "smb://fs01/share",
        config = new Dictionary<string, string>(),
        defaultAttributes = defaultAttributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" },
    };

    private HttpClient ClientAs(string? groupPaths)
    {
        var client = factory.CreateClient();
        if (groupPaths is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.GroupPathsHeader, groupPaths);
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string DepartmentOf(JsonElement source) =>
        source.GetProperty("defaultAttributes").GetProperty("department").GetString()!;

    // T-53 / T-54 / T-55: 登録者の所属と、保存される既定部門の対応（再読込しても残ることまで見る）
    [Theory]
    [InlineData("/clearance/restricted,/department/engineering", "engineering")]
    [InlineData(null, "unassigned")]
    [InlineData("/clearance/internal", "unassigned")]
    [InlineData("/department/engineering,/department/sales", "unassigned")]
    public async Task Post_WithoutDepartment_DerivesFromRegistrantGroupsOnlyWhenExactlyOne(
        string? groupPaths, string expected)
    {
        var client = ClientAs(groupPaths);

        var resp = await client.PostAsJsonAsync("/datasources", NewSource(), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadAsync(resp);
        DepartmentOf(created).Should().Be(expected);

        // 既定属性として**保存される**（SC-06 で見えて直せる。隠れた段にしない）。
        var id = created.GetProperty("id").GetGuid();
        var reread = await ReadAsync(await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken));
        DepartmentOf(reread).Should().Be(expected);
    }

    // T-56: 明示値は登録者の所属で上書きしない
    [Fact]
    public async Task Post_WithExplicitDepartment_KeepsIt()
    {
        var client = ClientAs("/department/engineering");

        var resp = await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "sales" }),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        DepartmentOf(await ReadAsync(resp)).Should().Be("sales");
    }

    // T-57: 更新（PATCH / PUT）は導き直さない —— 裁定は「**登録した**利用者」であり、
    // 更新した管理者の所属で部門が揺れてはならない。空にすれば従来どおり予約値へ倒れる。
    [Fact]
    public async Task PatchAndPut_DoNotRederiveFromTheUpdater()
    {
        var registrant = ClientAs("/department/engineering");
        var created = await ReadAsync(await registrant.PostAsJsonAsync("/datasources", NewSource(),
            TestContext.Current.CancellationToken));
        var id = created.GetProperty("id").GetGuid();
        DepartmentOf(created).Should().Be("engineering");

        var updater = ClientAs("/department/sales");

        var patched = await updater.PatchAsJsonAsync($"/datasources/{id}",
            new { defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } },
            TestContext.Current.CancellationToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        DepartmentOf(await ReadAsync(patched)).Should().Be("unassigned");

        var put = await updater.PutAsJsonAsync($"/datasources/{id}", NewSource(), TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        DepartmentOf(await ReadAsync(put)).Should().Be("unassigned");
    }
}
