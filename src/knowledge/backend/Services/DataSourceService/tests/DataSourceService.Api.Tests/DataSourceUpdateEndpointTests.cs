using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Api.Tests;

// FR-01, UC-04, SC-06（Q16 / issue #534）: データソース更新 API（PUT 全置換 / PATCH 部分更新）。
//
// 受け入れ基準の写像:
//   - 更新しても Id / CreatedAt / LastSyncedAt が変わらない（＝ ID と履歴を切らない。Q16 の目的）
//   - PATCH で省略した項目は現状維持
//   - 更新で confidentiality を欠落させられない（FR-05 フェイルセーフ）
//   - 存在しない ID は 404
//   - 更新は管理者限定（運用者は 403）は DataSourceAuthorizationTests 側で検証する
public class DataSourceUpdateEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<JsonElement> CreateAsync(HttpClient client, string name = "before")
    {
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name,
            sourceType = "filesystem",
            connectionUri = "smb://old/share",
            config = new Dictionary<string, string> { ["rootPath"] = "/old", ["apiToken"] = "s3cret" },
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "confidential" },
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Put_ReplacesFields_ButKeepsIdentityAndHistory()
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();
        var createdAt = created.GetProperty("createdAt").GetDateTimeOffset();

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "after",
            sourceType = "wiki",
            connectionUri = "https://wiki.example.test",
            config = new Dictionary<string, string> { ["listPath"] = "/pages" },
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("name").GetString().Should().Be("after");
        body.GetProperty("sourceType").GetString().Should().Be("wiki");
        body.GetProperty("connectionUri").GetString().Should().Be("https://wiki.example.test");
        // Q16 の目的そのもの: 更新は ID と履歴を切らない。
        body.GetProperty("id").GetGuid().Should().Be(id, "更新は ID を切らない（削除→再登録との違い）");
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(createdAt, "登録時刻は更新で動かない");
        body.GetProperty("lastSyncedAt").ValueKind.Should().Be(JsonValueKind.Null, "更新で watermark を巻き戻さない");
    }

    [Fact]
    public async Task Put_EmptyAttributes_IsFailsafedToInternal()
    {
        // FR-05, IADR-0019: 更新で機密区分を空にできると fail-closed 検索（IADR-0012）から文書が落ちる。
        // **空辞書を明示した場合**でもフェイルセーフが働くことを固定する（省略は 400 で拒否されるので、
        // 「属性を全部消す」意図はこの形でしか表現できない。AI レビュー 🟡 / #627）。
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "no-attrs",
            sourceType = "filesystem",
            connectionUri = "smb://new/share",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("defaultAttributes").GetProperty("confidentiality").GetString()
            .Should().Be("internal", "属性を省略した更新でも機密区分はフェイルセーフで補完される");
    }

    [Fact]
    public async Task Patch_OmittedFields_AreLeftUnchanged()
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client, "keep-me");
        var id = created.GetProperty("id").GetGuid();

        // 接続先だけを差し替える（日常運用）。他項目は送らない。
        var resp = await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            connectionUri = "smb://relocated/share",
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("connectionUri").GetString().Should().Be("smb://relocated/share");
        body.GetProperty("name").GetString().Should().Be("keep-me", "省略した項目は現状維持");
        body.GetProperty("sourceType").GetString().Should().Be("filesystem", "省略した項目は現状維持");
        body.GetProperty("defaultAttributes").GetProperty("confidentiality").GetString()
            .Should().Be("confidential", "省略した属性は現状維持（フェイルセーフで上書きしない）");
    }

    [Fact]
    public async Task Patch_OmittingConfig_DoesNotWriteBackMaskedSecret()
    {
        // IADR-0053: 応答の Config は秘密がマスクされる（apiToken → ***）。PATCH で Config を省略したとき、
        // マスク済みの値が書き戻されて秘密が破壊されないことを固定する（読んで書き戻す往復事故の防止）。
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("config").GetProperty("apiToken").GetString().Should().Be("***");

        var resp = await client.PatchAsJsonAsync($"/datasources/{id}", new { name = "renamed" }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 保存された実体が *** で潰れていないことを、DB から直接読んで確かめる（応答は常にマスクされるため）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Foundation.Persistence.DataSourceDbContext>();
        var entity = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        entity!.Config["apiToken"].Should().Be("s3cret", "Config を送らない PATCH は秘密を書き換えない");
    }

    // IADR-0053 / IADR-0148 決定 6（AI レビュー 🟡・#627）: **読んで書き戻す往復が秘密を壊さない。**
    // 応答は秘密キーを *** でマスクするため、GET の結果をそのまま編集して送り返すと、
    // マスク値が本物の資格情報を上書きしてしまう。**マスク値は保存せず、既存値を保つ。**
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Update_WritingBackMaskedSecret_KeepsStoredValue(string method)
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();

        // GET の応答をそのまま部分編集して送り返す（API を直接叩く運用者・将来の編集フォームの経路）。
        using var req = new HttpRequestMessage(new HttpMethod(method), $"/datasources/{id}")
        {
            Content = JsonContent.Create(new
            {
                name = "round-tripped",
                sourceType = "filesystem",
                connectionUri = "smb://old/share",
                config = new Dictionary<string, string> { ["rootPath"] = "/new", ["apiToken"] = "***" },
                defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            }),
        };
        (await client.SendAsync(req, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Foundation.Persistence.DataSourceDbContext>();
        var entity = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        entity!.Config["apiToken"].Should().Be("s3cret", "マスク値は本物の秘密を上書きしない");
        entity.Config["rootPath"].Should().Be("/new", "秘密でない項目は要求どおり更新される");
    }

    // 過剰に守らない: **秘密キーでなければ *** は普通の値**である（マスクされて返らないため、
    // 利用者が意図して入れた文字列と区別できる）。
    [Fact]
    public async Task Patch_NonSecretKeyWithPlaceholderValue_IsStoredVerbatim()
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();

        await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            config = new Dictionary<string, string> { ["rootPath"] = "***" },
        }, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Foundation.Persistence.DataSourceDbContext>();
        var entity = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        entity!.Config["rootPath"].Should().Be("***");
    }

    // AI レビュー 🟡（#627・`2b7ddc9` 時点）: **PUT で `config` を省略すると秘密が黙って消えていた。**
    // PUT は全置換なので「省略 ＝ 空で置換」は筋が通るが、**契約が省略を許していた**ため、
    // 事故が「うっかり」で起きる形になっていた（PATCH は省略＝現状維持なので挙動が非対称でもあった）。
    //
    // **是正の方向は「PUT の意味論を曲げる」ではなく「省略を許さない」である** ——
    // null を現状維持にすると PUT と PATCH の区別が消える。消したいなら `{}` と明示させる。
    [Theory]
    [InlineData("config")]
    [InlineData("defaultAttributes")]
    public async Task Put_OmittingReplaceableField_IsRejected(string omitted)
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();

        var body = new Dictionary<string, object?>
        {
            ["name"] = "after",
            ["sourceType"] = "filesystem",
            ["connectionUri"] = "smb://new/share",
            ["config"] = new Dictionary<string, string> { ["rootPath"] = "/new" },
            ["defaultAttributes"] = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        };
        body.Remove(omitted);

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", body, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "PUT は全置換なので、消えては困る項目の省略を受理しない（消すなら {} と明示させる）");

        // 拒否されたので実体は無傷である（ここが本質——秘密が残っていること）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Foundation.Persistence.DataSourceDbContext>();
        var entity = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        entity!.Config["apiToken"].Should().Be("s3cret", "拒否された更新は秘密を壊さない");
    }

    // **空辞書は「消す」という明示なので受理する。** 過剰に守らない。
    [Fact]
    public async Task Put_ExplicitEmptyConfig_ClearsIt()
    {
        var client = factory.CreateClient();
        var created = await CreateAsync(client);
        var id = created.GetProperty("id").GetGuid();

        var resp = await client.PutAsJsonAsync($"/datasources/{id}", new
        {
            name = "cleared",
            sourceType = "filesystem",
            connectionUri = "smb://new/share",
            config = new Dictionary<string, string>(),
            defaultAttributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Foundation.Persistence.DataSourceDbContext>();
        var entity = await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
        entity!.Config.Should().BeEmpty("空辞書は「消す」という明示である");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Update_UnknownId_Returns404(string method)
    {
        var client = factory.CreateClient();
        using var req = new HttpRequestMessage(new HttpMethod(method), $"/datasources/{Guid.NewGuid()}")
        {
            // PUT は全置換なので、存在確認より先に本文の妥当性を見る（省略は 400）。
            // ここでは「存在しない ID」だけを見たいので、本文は妥当な形で送る。
            Content = JsonContent.Create(new
            {
                name = "x",
                sourceType = "filesystem",
                connectionUri = "smb://x",
                config = new Dictionary<string, string>(),
                defaultAttributes = new Dictionary<string, string>(),
            }),
        };
        (await client.SendAsync(req, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_DisabledSource_IsUpdatable()
    {
        // 無効化は論理削除であり、認証情報のローテーションは無効中にも起こる。
        var client = factory.CreateClient();
        var created = await CreateAsync(client, "to-disable");
        var id = created.GetProperty("id").GetGuid();
        (await client.DeleteAsync($"/datasources/{id}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var resp = await client.PatchAsJsonAsync($"/datasources/{id}", new
        {
            connectionUri = "smb://rotated/share",
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetString().Should().Be("disabled", "更新は無効状態を変えない");
        body.GetProperty("connectionUri").GetString().Should().Be("smb://rotated/share");
    }
}
