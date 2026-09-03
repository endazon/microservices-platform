using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Tests.Features.DataSources;

// FR-05, UC-04, SC-06, ADR-0036, ADR-0074 決定 1・4 (#1194): `owner` の写像表の登録・更新と実在検証。
//
// 受け入れ基準の写像（#1194）:
//   - 実在する写像先を入れると保存され、再読込しても残る
//   - 実在しない写像先は**保存されず**理由が返る（**API 直叩き**で固定する ——
//     画面だけの検証にすると API を直接叩いた経路で偽の所有者が入る）
//   - 写像表と既定属性は**片方だけ PATCH してももう片方が消えない**
//   - 運用者は更新できない（管理者限定。既定属性 3 つと同じ権限）
public class OwnerMappingEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private StubPlatformUserDirectory Directory => factory.Services.GetRequiredService<StubPlatformUserDirectory>();

    private static object NewSource(object? ownerMappings = null) => new
    {
        name = "規程集",
        sourceType = "db",
        connectionUri = "postgres://db.example.test/records",
        config = new Dictionary<string, string>(),
        defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ownerMappings,
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    // ---- 登録 -------------------------------------------------------------

    [Fact]
    public async Task Post_WithExistingTarget_SavesMappingAndSurvivesReload()
    {
        var client = factory.CreateClient();
        Directory.Available = true;

        var resp = await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["hr_system\\tanaka"] = "alice" }),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadAsync(resp);
        created.GetProperty("ownerMappings").GetProperty("hr_system\\tanaka").GetString().Should().Be("alice");

        // 再読込しても残る（受け入れ基準 1）。
        var id = created.GetProperty("id").GetGuid();
        var reread = await ReadAsync(await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken));
        reread.GetProperty("ownerMappings").GetProperty("hr_system\\tanaka").GetString().Should().Be("alice");
    }

    [Fact]
    public async Task Post_WithUnknownTarget_IsRejected_AndNothingIsPersisted()
    {
        var client = factory.CreateClient();
        Directory.Available = true;
        var before = await ReadAsync(await client.GetAsync("/datasources", TestContext.Current.CancellationToken));
        var countBefore = before.GetArrayLength();

        var resp = await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["hr_system\\ghost"] = "nobody" }),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "実在しない写像先は偽の所有者を作り、ADR-0036 の裁量制御が意図しない相手に開く");
        // 🔴 **RFC7807 の形で返す**（`{ error }` は SPA の問題本文パーサが読まないため、
        // 画面に理由が出ない ——「保存されず**理由が表示される**」を満たせない）。
        var body = await ReadAsync(resp);
        body.GetProperty("errors").GetProperty("errors")[0].GetString().Should().Contain("nobody",
            "SC-06 は管理者限定の面であり、その管理者は SC-17 で利用者一覧を丸ごと見られる。理由を伏せても隠せる情報が無い");

        // 🔴 **「保存しない」を件数で固定する。** 400 を返しても行が増えていたら要件を満たさない。
        var after = await ReadAsync(await client.GetAsync("/datasources", TestContext.Current.CancellationToken));
        after.GetArrayLength().Should().Be(countBefore, "検証に通らない対は保存しない（ADR-0074 決定 4）");
    }

    [Fact]
    public async Task Post_WithBlankTarget_IsRejectedWithoutConsultingTheDirectory()
    {
        var client = factory.CreateClient();
        Directory.Available = true;
        var callsBefore = Directory.CallCount;

        var resp = await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["hr_system\\tanaka"] = "   " }),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.CallCount.Should().Be(callsBefore, "書式違反は後段へ問い合わせるまでもない");
    }

    [Fact]
    public async Task Post_WhenDirectoryUnavailable_Returns502_NotBadRequest()
    {
        var client = factory.CreateClient();
        Directory.Available = false;
        try
        {
            var resp = await client.PostAsJsonAsync("/datasources",
                NewSource(new Dictionary<string, string> { ["hr_system\\tanaka"] = "alice" }),
                TestContext.Current.CancellationToken);

            // 🔴 **「確かめられなかった」を「存在しない」と報告するのは嘘である。**
            // alice は実在する（下の陽性対照）のに、名簿が引けないだけで 400 を返してはならない。
            resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            Directory.Available = true;
        }

        // 陽性対照: 名簿が引ければ同じ要求は通る。
        var ok = await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["hr_system\\tanaka"] = "alice" }),
            TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Post_WithoutOwnerMappings_DoesNotConsultTheDirectory()
    {
        var client = factory.CreateClient();
        var callsBefore = Directory.CallCount;

        var resp = await client.PostAsJsonAsync("/datasources", NewSource(),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync(resp)).GetProperty("ownerMappings").EnumerateObject().Should().BeEmpty();
        Directory.CallCount.Should().Be(callsBefore,
            "写像表を触らない操作を、認可サービスの障害へ道連れにしない");
    }

    // ---- 更新（PATCH / PUT） ---------------------------------------------

    [Fact]
    public async Task Patch_OwnerMappingsOnly_KeepsDefaultAttributes_AndViceVersa()
    {
        var client = factory.CreateClient();
        var created = await ReadAsync(await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["src\\a"] = "alice" }),
            TestContext.Current.CancellationToken));
        var id = created.GetProperty("id").GetGuid();

        // 既定属性だけを送る → 写像表は消えない。
        var patched = await ReadAsync(await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "restricted" },
        }, TestContext.Current.CancellationToken));
        patched.GetProperty("defaultAttributes").GetProperty("confidentiality").GetString().Should().Be("restricted");
        patched.GetProperty("ownerMappings").GetProperty("src\\a").GetString().Should().Be("alice",
            "器が別なので、既定属性の全置換は写像表を巻き込まない");

        // 写像表だけを送る → 既定属性は消えない。
        var patched2 = await ReadAsync(await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            ownerMappings = new Dictionary<string, string> { ["src\\b"] = "bob" },
        }, TestContext.Current.CancellationToken));
        patched2.GetProperty("defaultAttributes").GetProperty("confidentiality").GetString().Should().Be("restricted");
        patched2.GetProperty("ownerMappings").GetProperty("src\\b").GetString().Should().Be("bob");
        patched2.GetProperty("ownerMappings").TryGetProperty("src\\a", out _).Should().BeFalse(
            "写像表そのものは全置換である（送った地図が丸ごと新しい表になる）");
    }

    [Fact]
    public async Task Patch_WithUnknownTarget_IsRejected_AndStoredMappingIsUnchanged()
    {
        var client = factory.CreateClient();
        var created = await ReadAsync(await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["src\\a"] = "alice" }),
            TestContext.Current.CancellationToken));
        var id = created.GetProperty("id").GetGuid();

        var resp = await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            ownerMappings = new Dictionary<string, string> { ["src\\a"] = "ghost" },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reread = await ReadAsync(await client.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken));
        reread.GetProperty("ownerMappings").GetProperty("src\\a").GetString().Should().Be("alice",
            "拒否した更新が既存の表を壊してはならない");
    }

    [Fact]
    public async Task Put_WithoutOwnerMappings_KeepsThem_AndEmptyObjectClearsThem()
    {
        var client = factory.CreateClient();
        var created = await ReadAsync(await client.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["src\\a"] = "alice" }),
            TestContext.Current.CancellationToken));
        var id = created.GetProperty("id").GetGuid();

        // 🔴 PUT は全置換だが、**後から足した `ownerMappings` の省略は現状維持である**
        // （必須にすると既存の PUT クライアントが一斉に 400 になる＝契約の破壊）。
        var kept = await ReadAsync(await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "規程集2",
            sourceType = "db",
            connectionUri = "postgres://db.example.test/records",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        }, TestContext.Current.CancellationToken));
        kept.GetProperty("ownerMappings").GetProperty("src\\a").GetString().Should().Be("alice");

        // 消したいときは {} を明示する。
        var cleared = await ReadAsync(await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "規程集2",
            sourceType = "db",
            connectionUri = "postgres://db.example.test/records",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ownerMappings = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken));
        cleared.GetProperty("ownerMappings").EnumerateObject().Should().BeEmpty();
    }

    // ---- 認可 -------------------------------------------------------------

    [Fact]
    public async Task Operator_CanReadMappings_ButCannotWriteThem()
    {
        var admin = factory.CreateClient();
        var created = await ReadAsync(await admin.PostAsJsonAsync("/datasources",
            NewSource(new Dictionary<string, string> { ["src\\a"] = "alice" }),
            TestContext.Current.CancellationToken));
        var id = created.GetProperty("id").GetGuid();

        var op = factory.CreateClient();
        op.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        // 閲覧はできる（既定属性 3 つと同じ権限。ADR-0074 決定 1）。
        var read = await op.GetAsync($"/datasources/{id}", TestContext.Current.CancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(read)).GetProperty("ownerMappings").GetProperty("src\\a").GetString().Should().Be("alice");

        // 更新はできない。
        var write = await op.PatchAsJsonAsync($"/datasources/{id}", new
        {
            ownerMappings = new Dictionary<string, string> { ["src\\a"] = "bob" },
        }, TestContext.Current.CancellationToken);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
