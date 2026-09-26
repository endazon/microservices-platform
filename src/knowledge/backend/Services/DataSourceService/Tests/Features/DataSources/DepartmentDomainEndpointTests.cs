using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Tests.Features.DataSources;

// FR-05, UC-04, SC-06, 計画 ADR-0115 決定 1・5, ADR-0074 決定 4, [[IADR-0472]] (#1557):
// SC-06 で**明示した** `department` を、書き込み時（POST / PUT / PATCH）に値域（realm の部門グループのコード）で検証する。
//
// 受け入れ基準の写像（作業仕様書 20260926_issue-1557）:
//   T-60（AC-1）: 値域の内なら 3 口とも保存される
//   T-61（AC-2）: 値域の外は 400 で理由が返り、**1 項目も保存されない**（API 直叩きで固定する）
//   T-62（AC-3）: 値域を引けなければ 502。「値域の外」と報告しない
//   T-63（AC-4・5）: 予約値 `unassigned`・空白・未指定・`defaultAttributes` を送らない PATCH は値域を引かない
// TestServer（メモリ内）で走り、ソケットを開かない。
[Trait("TestKind", "Integration")]
public class DepartmentDomainEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private StubDepartmentDomainDirectory Domain => factory.Services.GetRequiredService<StubDepartmentDomainDirectory>();

    private static Dictionary<string, string> Attrs(string? department)
    {
        var attrs = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (department is not null) attrs["department"] = department;
        return attrs;
    }

    private static object NewSource(string? department) => new
    {
        name = "規程集",
        sourceType = "filesystem",
        connectionUri = "smb://fs01/share",
        config = new Dictionary<string, string>(),
        defaultAttributes = Attrs(department),
    };

    private static object PutBody(string? department) => new
    {
        name = "規程集",
        sourceType = "filesystem",
        connectionUri = "smb://fs01/share",
        config = new Dictionary<string, string>(),
        defaultAttributes = Attrs(department),
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string DepartmentOf(JsonElement source) =>
        source.GetProperty("defaultAttributes").GetProperty("department").GetString()!;

    private async Task<Guid> CreateAsync(HttpClient client, string department)
    {
        var resp = await client.PostAsJsonAsync("/datasources", NewSource(department),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadAsync(resp)).GetProperty("id").GetGuid();
    }

    private async Task<int> CountAsync(HttpClient client) =>
        (await ReadAsync(await client.GetAsync("/datasources", TestContext.Current.CancellationToken))).GetArrayLength();

    // ---- T-60: 値域の内 ------------------------------------------------------

    [Fact]
    public async Task InDomainDepartment_IsSaved_OnPostPutAndPatch_AndOnlyThatCodeIsQueried()
    {
        Domain.Reset();
        var client = factory.CreateClient();

        var id = await CreateAsync(client, "sales");
        Domain.LastQuery.Should().BeEquivalentTo(["sales"], "送るのは明示された 1 値だけである（一覧は引かない）");

        var put = await client.PutAsJsonAsync($"/datasources/{id}", PutBody("hr"), TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        DepartmentOf(await ReadAsync(put)).Should().Be("hr");

        var patch = await client.PatchAsJsonAsync($"/datasources/{id}",
            new { defaultAttributes = Attrs("engineering") }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await ReadAsync(await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken));
        DepartmentOf(reread).Should().Be("engineering");
    }

    // ---- T-61: 値域の外 ------------------------------------------------------

    // 打ち間違い・realm に無い部門・大小文字違い・入れ子のパス・前後空白。
    // 🔴 大小文字違いを通す変異（照合を OrdinalIgnoreCase へ）と、trim してから照会する変異
    // （空白つきの値が保存される）はいずれもここで赤になる。
    [Theory]
    [InlineData("finance")]
    [InlineData("Sales")]
    [InlineData("sales/backend")]
    [InlineData(" sales ")]
    public async Task Post_OutOfDomainDepartment_Is400_WithReason_AndNothingIsPersisted(string department)
    {
        Domain.Reset();
        var client = factory.CreateClient();
        var countBefore = await CountAsync(client);

        var resp = await client.PostAsJsonAsync("/datasources", NewSource(department),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // RFC7807 の形で返す（SPA の問題本文パーサが `errors` を読む ——「理由が表示される」）。
        var body = await ReadAsync(resp);
        body.GetProperty("errors").GetProperty("errors")[0].GetString().Should().Contain(department);
        (await CountAsync(client)).Should().Be(countBefore, "値域の外の部門は保存しない（計画 ADR-0115 決定 5）");
    }

    [Fact]
    public async Task PutAndPatch_OutOfDomainDepartment_Are400_AndStoredValueIsUnchanged()
    {
        Domain.Reset();
        var client = factory.CreateClient();
        var id = await CreateAsync(client, "sales");

        var put = await client.PutAsJsonAsync($"/datasources/{id}", PutBody("finance"),
            TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var patch = await client.PatchAsJsonAsync($"/datasources/{id}",
            new { defaultAttributes = Attrs("finance") }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var reread = await ReadAsync(await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken));
        DepartmentOf(reread).Should().Be("sales", "拒否した書き込みは 1 項目も反映しない");
    }

    // ---- T-62: 引けない ------------------------------------------------------

    [Fact]
    public async Task WhenDomainUnavailable_ExplicitDepartment_Is502_NotBadRequest_AndNothingIsPersisted()
    {
        Domain.Reset();
        var client = factory.CreateClient();
        var countBefore = await CountAsync(client);
        Domain.Available = false;

        var resp = await client.PostAsJsonAsync("/datasources", NewSource("sales"),
            TestContext.Current.CancellationToken);

        // 🔴 `sales` は値域に在る（下の陽性対照）。引けないだけで「値域の外」と答えてはならない。
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadAsync(resp)).GetProperty("message").GetString().Should().Contain("保存していません");
        (await CountAsync(client)).Should().Be(countBefore);

        // 陽性対照: 引ければ同じ要求は通る。
        Domain.Available = true;
        var ok = await client.PostAsJsonAsync("/datasources", NewSource("sales"), TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- T-63: 照会しない場合 ------------------------------------------------

    // 予約値 `unassigned` は部門コードではなく「解決できなかった」の記録である（受け付ける・照会しない）。
    // 🔴 値域が引けない状態で通ることが「照会していない」ことの証拠になる（照会すれば 502 になる）。
    [Theory]
    [InlineData("unassigned")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UnresolvedDepartment_IsAccepted_WithoutConsultingTheDomain(string? department)
    {
        Domain.Reset();
        Domain.Available = false;
        var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/datasources", NewSource(department),
            TestContext.Current.CancellationToken);
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadAsync(post)).GetProperty("id").GetGuid();

        var put = await client.PutAsJsonAsync($"/datasources/{id}", PutBody(department),
            TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var patch = await client.PatchAsJsonAsync($"/datasources/{id}",
            new { defaultAttributes = Attrs(department) }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        Domain.CallCount.Should().Be(0);
        DepartmentOf(await ReadAsync(patch)).Should().Be("unassigned");
    }

    [Fact]
    public async Task PatchWithoutDefaultAttributes_DoesNotConsultTheDomain()
    {
        Domain.Reset();
        var client = factory.CreateClient();
        var id = await CreateAsync(client, "sales");
        var callsBefore = Domain.CallCount;
        Domain.Available = false;

        var patch = await client.PatchAsJsonAsync($"/datasources/{id}", new { name = "規程集（改）" },
            TestContext.Current.CancellationToken);

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "名前だけの変更を認可サービスの障害へ道連れにしない");
        Domain.CallCount.Should().Be(callsBefore);
        DepartmentOf(await ReadAsync(patch)).Should().Be("sales");
    }
}
