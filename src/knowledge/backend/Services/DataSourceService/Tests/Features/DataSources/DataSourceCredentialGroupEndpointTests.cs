using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Tests.Features.DataSources;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 1・3・4, [[IADR-0501]] 決定 3 (#458 段 S2):
// SC-22 の群「データソースの資格情報」の後段 2 端点。
//   - `GET /datasources/credentials`: 有効で、資格情報を宣言する種別だけ。キーごとの供給の事実だけで、値も参照の文字列も返さない。
//   - `PUT /datasources/{id}/credentials/{key}/reference`: 管理者だけ。値なしのキーにだけ正規の参照を置き、平文は置き換えない。
[Trait("TestKind", "Integration")]
public class DataSourceCredentialGroupEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 🔴 テスト用の明白なダミー値（秘密ではない）。応答に出ないことを測る目印でもある。
    private const string PlaintextMarker = "plaintext-marker-for-credential-group-tests";

    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private async Task<Guid> CreateAsync(string sourceType, Dictionary<string, string>? config = null, string? name = null)
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/datasources", new
        {
            name = name ?? $"{sourceType}-{Guid.NewGuid():N}",
            sourceType,
            connectionUri = sourceType == "filesystem" ? "smb://share/docs" : "https://source.example.test",
            config = config ?? new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private async Task<List<JsonElement>> ListAsync(HttpClient? client = null)
    {
        var resp = await (client ?? factory.CreateClient()).GetAsync("/datasources/credentials", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).EnumerateArray()];
    }

    private static string? SupplyOf(IEnumerable<JsonElement> items, Guid id, string key) =>
        items.SingleOrDefault(i => i.GetProperty("id").GetGuid() == id) is { ValueKind: JsonValueKind.Object } item
            ? item.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == key)
                .GetProperty("supply").GetString()
            : null;

    private string? StoredConfig(Guid id, string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        return db.DataSources.Find(id)!.Config.GetValueOrDefault(key);
    }

    private Task<HttpResponseMessage> PlaceAsync(Guid id, string key, HttpClient? client = null) =>
        (client ?? factory.CreateClient()).PutAsync($"/datasources/{id}/credentials/{key}/reference", null,
            TestContext.Current.CancellationToken);

    // ── 一覧（AC-9）

    // ADR-0126 決定 1: 群に入るのは有効で資格情報を宣言する種別だけ（ファイルサーバー・無効化は入らない）。
    [Fact]
    public async Task ListCredentials_returns_only_active_sources_whose_connector_declares_keys()
    {
        var wiki = await CreateAsync("wiki");
        var db = await CreateAsync("db", new() { ["password"] = PlaintextMarker });
        var fs = await CreateAsync("filesystem");
        var disabled = await CreateAsync("saas");
        (await factory.CreateClient().DeleteAsync($"/datasources/{disabled}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var items = await ListAsync();

        var ids = items.Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(wiki).And.Contain(db).And.NotContain(fs).And.NotContain(disabled);
        SupplyOf(items, wiki, "apiToken").Should().Be(DataSourceCredentialSupply.Absent);
        SupplyOf(items, db, "password").Should().Be(DataSourceCredentialSupply.Other);
        items.Single(i => i.GetProperty("id").GetGuid() == db).GetProperty("sourceType").GetString().Should().Be("db");
    }

    // 🔴 一覧は値も参照の文字列も返さない（陽性対照: 参照と平文の両方を保存した状態で測る）。
    [Fact]
    public async Task ListCredentials_never_carries_values_or_reference_strings()
    {
        var plain = await CreateAsync("saas", new() { ["apiToken"] = PlaintextMarker });
        var referenced = await CreateAsync("wiki");
        (await PlaceAsync(referenced, "apiToken")).StatusCode.Should().Be(HttpStatusCode.OK);
        StoredConfig(referenced, "apiToken").Should().StartWith("vault:", "陽性対照: 参照は保存されている");

        var resp = await factory.CreateClient().GetAsync("/datasources/credentials", TestContext.Current.CancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        raw.Should().Contain(plain.ToString("D")).And.Contain(referenced.ToString("D"));
        raw.Should().NotContain(PlaintextMarker).And.NotContain("vault:").And.NotContain("datasource/");
    }

    // ADR-0126 決定 4: 正規の参照（自分の ID・宣言したキー）だけが `reference`。別の場所を指す参照は `other`。
    [Fact]
    public async Task ListCredentials_reports_reference_only_for_the_canonical_reference()
    {
        var elsewhere = await CreateAsync("wiki", new() { ["apiToken"] = "vault:datasource/some-other-source#apiToken" });
        var canonical = await CreateAsync("wiki");
        (await PlaceAsync(canonical, "apiToken")).StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ListAsync();

        SupplyOf(items, canonical, "apiToken").Should().Be(DataSourceCredentialSupply.Reference);
        SupplyOf(items, elsewhere, "apiToken").Should().Be(DataSourceCredentialSupply.Other);
    }

    // ADR-0126 決定 3: 運用者は群を閲覧できる。権限外は 403。
    [Fact]
    public async Task ListCredentials_is_open_to_operators_but_not_to_other_roles()
    {
        (await ClientAs("platform-operator").GetAsync("/datasources/credentials", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientAs("viewer").GetAsync("/datasources/credentials", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 参照の配置（AC-8・AC-9。窓の両端）

    // P+: 値なしのキーに正規の参照（`vault:datasource/<ID>#<キー>`）を置く。2 度目は何も変えない（冪等）。
    [Fact]
    public async Task PlaceReference_sets_the_canonical_reference_only_when_absent()
    {
        var wiki = await CreateAsync("wiki", new() { ["listPath"] = "/pages" });

        var first = await PlaceAsync(wiki, "apiToken");
        var second = await PlaceAsync(wiki, "apiToken");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("supply").GetString().Should().Be(DataSourceCredentialSupply.Reference);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        StoredConfig(wiki, "apiToken").Should().Be($"vault:datasource/{wiki:D}#apiToken");
        StoredConfig(wiki, "listPath").Should().Be("/pages", "ほかの設定は消さない");
    }

    // 🔴 P−: 平文は置き換えない（移送は段 S4。ADR-0126 決定 4「画面以外」）。応答は `other`。
    [Fact]
    public async Task PlaceReference_never_replaces_plaintext()
    {
        var db = await CreateAsync("db", new() { ["password"] = PlaintextMarker });

        var resp = await PlaceAsync(db, "password");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("supply").GetString().Should().Be(DataSourceCredentialSupply.Other);
        StoredConfig(db, "password").Should().Be(PlaintextMarker);
    }

    // ADR-0126 決定 3: 参照の配置は管理者だけ（BFF の群の書き込みと同じ。多層防御）。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("viewer")]
    public async Task PlaceReference_is_admin_only(string role)
    {
        var wiki = await CreateAsync("wiki");

        var resp = await PlaceAsync(wiki, "apiToken", ClientAs(role));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        StoredConfig(wiki, "apiToken").Should().BeNull();
    }

    // 未登録・無効・資格情報を使わない種別は 404、コネクタが宣言しないキーは 400。いずれも設定を変えない。
    [Fact]
    public async Task PlaceReference_rejects_unknown_disabled_credentialless_sources_and_undeclared_keys()
    {
        var disabled = await CreateAsync("wiki");
        (await factory.CreateClient().DeleteAsync($"/datasources/{disabled}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var fs = await CreateAsync("filesystem");
        var wiki = await CreateAsync("wiki");

        (await PlaceAsync(Guid.NewGuid(), "apiToken")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PlaceAsync(disabled, "apiToken")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PlaceAsync(fs, "apiToken")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PlaceAsync(wiki, "password")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PlaceAsync(wiki, "ApiToken")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        StoredConfig(disabled, "apiToken").Should().BeNull();
        StoredConfig(wiki, "password").Should().BeNull();
        StoredConfig(wiki, "ApiToken").Should().BeNull();
    }

    // 置いた参照は通常の応答でもマスクされる（Vault のパス構造を漏らさない。作業仕様書 §未決事項 3）。
    [Fact]
    public async Task Placed_reference_is_masked_in_the_ordinary_response()
    {
        var wiki = await CreateAsync("wiki");
        (await PlaceAsync(wiki, "apiToken")).StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await factory.CreateClient().GetStringAsync($"/datasources/{wiki}", TestContext.Current.CancellationToken);

        raw.Should().NotContain("vault:").And.NotContain("datasource/");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("config").GetProperty("apiToken").GetString().Should().Be(SecretMask.Placeholder);
    }
}
