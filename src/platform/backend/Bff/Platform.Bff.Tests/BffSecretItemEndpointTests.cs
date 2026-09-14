using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Bff.Foundation.Endpoints;
using Platform.Bff.Foundation.Secrets;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Bff.Tests;

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3・4, ADR-0042 決定 2, IADR-0433, IADR-0453 (#1411):
// 秘密情報の投入（/bff/secrets）。認可・allowlist・Vault への書き込み方式・監査・503 を固定する。
//
// 🔴 **否定形（値が出ない・400 で 404 でない・PUT を使わない）は陽性対照と対で置く。**
public class BffSecretItemEndpointTests : IClassFixture<BffTestFactory>
{
    // 🔴 テスト用の明白なダミー値。本物の秘密は 1 つも書かない。
    private const string PlaceholderValue = "placeholder-value-for-sc22-tests-0001";
    private const string ExistingOtherValue = "placeholder-existing-other-property";

    private readonly BffTestFactory _factory;

    public BffSecretItemEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.Vault.Reset();
        _factory.SecretWriteRecords.Records.Clear();
        _factory.RecordedAuditEntries.Clear();
    }

    private static HttpRequestMessage Get(string? roles = null, bool anonymous = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/bff/secrets");
        Decorate(request, roles, anonymous);
        return request;
    }

    private static HttpRequestMessage Put(string item, object body, string? roles = null, bool anonymous = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/secrets/{item}") { Content = JsonContent.Create(body) };
        Decorate(request, roles, anonymous);
        return request;
    }

    private static void Decorate(HttpRequestMessage request, string? roles, bool anonymous)
    {
        if (anonymous) request.Headers.Add(TestAuthHandler.AnonymousHeader, "1");
        else if (roles is not null) request.Headers.Add(TestAuthHandler.RolesHeader, roles);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpClient? client = null) =>
        await (client ?? _factory.CreateClient()).SendAsync(request, TestContext.Current.CancellationToken);

    private IEnumerable<string> AllAuditDetails() =>
        _factory.RecordedAuditEntries.Select(e => $"{e.Action} {e.Subject} {e.Outcome} {e.Detail}");

    // ── 認可（AC-06）

    // SC-22, ADR-0042 決定 2, IADR-0453 決定 1: システム管理者は一覧を引ける。
    [Fact]
    public async Task List_as_admin_returns_every_allowlisted_item()
    {
        _factory.Vault.Put("msp/llm-provider-credentials", ("anthropic-api-key", PlaceholderValue));

        using var response = await SendAsync(Get());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<SecretItemStatusDto>>(TestContext.Current.CancellationToken);
        rows!.Select(r => r.Item).Should().Equal("llm-provider-credentials", "keycloak-smtp", "wikijs-sync", "ast-app-secrets");
        _factory.RecordedAuditEntries.Should().Contain(e => e.Action == SecretItemBffEndpoints.ListAction && e.Outcome == "granted");
    }

    // SC-22「運用者・システム管理者ロール限定」, IADR-0453 決定 1: 運用者も通る。
    [Theory]
    [InlineData(PlatformAuthPolicies.OperatorRole)]
    [InlineData(PlatformAuthPolicies.AdminRole)]
    public async Task Operators_and_admins_are_allowed(string role)
    {
        using var list = await SendAsync(Get(role));
        using var update = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }, role));

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-22, IADR-0453 決定 1: 他のロールは 403。拒否は監査に残り、Vault へは 1 度も触れない。
    [Fact]
    public async Task Other_roles_get_403_and_the_denial_is_audited()
    {
        using var list = await SendAsync(Get("platform-user"));
        using var update = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }, "platform-user"));

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemBffEndpoints.UpdateAction && e.Outcome == "denied" && e.Detail!.Contains("reason=forbidden"));
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // SC-22, IADR-0453 決定 1: 未認証は 401（存在は秘匿しない）。
    [Fact]
    public async Task Anonymous_gets_401()
    {
        using var list = await SendAsync(Get(anonymous: true));
        using var update = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }, anonymous: true));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        update.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // ── 一覧の形（AC-01・AC-03）

    // SC-22 主要素 1・3, IADR-0453 決定 4: 状態は 3 値で出し分け、値は応答のどこにも無い。
    [Fact]
    public async Task List_distinguishes_not_set_from_unavailable_and_never_carries_values()
    {
        _factory.Vault.Put("msp/llm-provider-credentials", ("anthropic-api-key", PlaceholderValue));
        _factory.Vault.MetadataStatus["msp/wikijs-sync"] = HttpStatusCode.InternalServerError;
        // keycloak-smtp と ast-app-secrets は KV が無い。

        using var response = await SendAsync(Get());
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(raw);
        var rows = document.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("item").GetString()!);
        rows["llm-provider-credentials"].GetProperty("status").GetString().Should().Be("set");
        rows["llm-provider-credentials"].GetProperty("currentVersion").GetInt32().Should().Be(1);
        rows["keycloak-smtp"].GetProperty("status").GetString().Should().Be("notSet");
        rows["wikijs-sync"].GetProperty("status").GetString().Should().Be("unavailable");
        rows["keycloak-smtp"].GetProperty("properties").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("from", "user", "password");

        // 🔴 値の列が無い。値そのものも出ない（陽性対照: Vault 側には値が入っている）。
        _factory.Vault.Store["msp/llm-provider-credentials"].Data.Should().ContainValue(PlaceholderValue);
        raw.Should().NotContain(PlaceholderValue);
        foreach (var row in rows.Values)
            row.EnumerateObject().Select(p => p.Name).Should().NotContain(n => n.Contains("value", StringComparison.OrdinalIgnoreCase));
        // 🔴 BFF は data を読みに行かない（metadata だけ）。
        _factory.Vault.Requests.Should().NotContain(r => r.Method == "GET" && r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
    }

    // SC-22 主要素 1「最終更新者」, IADR-0453 決定 3: 画面から書いた版にだけ名前が付き、別経路で版が進むと「記録なし」。
    [Fact]
    public async Task Last_updater_is_shown_only_while_the_bff_written_version_is_current()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        using (var update = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue })))
            update.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterScreen = await ListAsync();
        afterScreen["wikijs-sync"].LastUpdatedBy.Should().Be("test-user");
        afterScreen["wikijs-sync"].CurrentVersion.Should().Be(2);
        afterScreen["wikijs-sync"].LastUpdatedAt.Should().NotBeNull();

        // コンソールから版が進んだ（画面を経由しない書き込み）。
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));

        var afterConsole = await ListAsync();
        afterConsole["wikijs-sync"].CurrentVersion.Should().Be(3);
        afterConsole["wikijs-sync"].LastUpdatedBy.Should().BeNull();
    }

    private async Task<Dictionary<string, SecretItemStatusDto>> ListAsync()
    {
        using var response = await SendAsync(Get());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<SecretItemStatusDto>>(TestContext.Current.CancellationToken);
        return rows!.ToDictionary(r => r.Item);
    }

    // ── 書き込みの方式（AC-10）

    // SC-22, IADR-0433 決定 2: KV v2 の PATCH（merge-patch）で 1 プロパティだけを書き、同居するプロパティを消さない。
    [Fact]
    public async Task Update_patches_a_single_property_with_merge_patch()
    {
        _factory.Vault.Put("msp/llm-provider-credentials", ("openai-api-key", ExistingOtherValue), ("anthropic-api-key", "old"));

        using var response = await SendAsync(Put("llm-provider-credentials",
            new { property = "anthropic-api-key", value = PlaceholderValue, reason = "定期ローテーション" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemWriteResultDto>(TestContext.Current.CancellationToken);
        result!.Item.Should().Be("llm-provider-credentials");
        result.Property.Should().Be("anthropic-api-key");
        result.Version.Should().Be(2);

        var write = _factory.Vault.Requests.Single(r => r.Path == "/v1/secret/data/msp/llm-provider-credentials");
        write.Method.Should().Be("PATCH");
        write.ContentType.Should().Be("application/merge-patch+json");
        write.Token.Should().Be(_factory.Vault.CurrentToken);
        using (var body = JsonDocument.Parse(write.Body!))
        {
            body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("data");
            body.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).Should().Equal("anthropic-api-key");
        }

        // 🔴 同居するプロパティは残る（put の全置換をしていない）。
        _factory.Vault.Store["msp/llm-provider-credentials"].Data["openai-api-key"].Should().Be(ExistingOtherValue);
        _factory.Vault.Requests.Should().NotContain(r => r.Method == "PUT");
    }

    // SC-22, IADR-0453 決定 7: KV が無いときだけ `cas=0` で作る（既存を全置換しない作り方）。
    [Fact]
    public async Task Update_creates_the_kv_with_cas_zero_only_when_it_is_absent()
    {
        using var response = await SendAsync(Put("keycloak-smtp", new { property = "password", value = PlaceholderValue }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var writes = _factory.Vault.Requests.Where(r => r.Path == "/v1/secret/data/msp/keycloak-smtp").ToList();
        writes.Select(w => w.Method).Should().Equal("PATCH", "POST");
        using var body = JsonDocument.Parse(writes[1].Body!);
        body.RootElement.GetProperty("options").GetProperty("cas").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).Should().Equal("password");
    }

    // SC-22, IADR-0453 フォローアップ 5, IADR-0454 決定 1 (#1467): 現在版がソフト削除・破棄された KV へは書かない。
    // 🔴 409 と区別した problem type・監査理由で返し、`POST`（metadata が在る path では `update` を要し 403 になる）を送らない。
    // 陽性対照: KV が無いときは `cas=0` で作る（`Update_creates_the_kv_with_cas_zero_only_when_it_is_absent`）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_on_a_kv_whose_current_version_is_deleted_returns_409_without_posting(bool destroyed)
    {
        var kv = _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        if (destroyed) kv.Destroyed = true;
        else kv.Deleted = true;

        using var response = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }));
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var problem = JsonDocument.Parse(raw))
            problem.RootElement.GetProperty("type").GetString()
                .Should().Be("urn:microservices-platform:secret-items:current-version-deleted");
        raw.Should().NotContain(PlaceholderValue);

        _factory.RecordedAuditEntries.Where(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Should().ContainSingle()
            .Which.Should().Be((SecretItemBffEndpoints.UpdateAction, "test-user", "failed",
                (string?)"item=wikijs-sync property=apiKey reason=current-version-deleted"));

        // 🔴 作成（POST）を送らず、Vault の中身も削除状態も変えず、最終更新者の記録も作らない。
        _factory.Vault.Requests.Should().NotContain(r => r.Method == "POST");
        kv.Version.Should().Be(1);
        kv.Data["apiKey"].Should().Be(ExistingOtherValue);
        (destroyed ? kv.Destroyed : kv.Deleted).Should().BeTrue();
        _factory.SecretWriteRecords.Records.Should().BeEmpty();

        // 一覧は従来どおり「未設定」と出す（状態の値域は変えない）。
        (await ListAsync())["wikijs-sync"].Status.Should().Be("notSet");
    }

    // SC-22, IADR-0453 決定 7: ログインは使い回し、トークンが無効になったら 1 度だけ取り直す。
    [Fact]
    public async Task Vault_token_is_reused_and_renewed_once_on_403()
    {
        var client = _factory.CreateClient();
        using (var first = await SendAsync(Get(), client)) first.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginsAfterFirst = _factory.Vault.LoginCount;
        using (var second = await SendAsync(Get(), client)) second.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Vault.LoginCount.Should().Be(loginsAfterFirst, "リース中のトークンを使い回すこと");

        _factory.Vault.CurrentToken = "fake-vault-token-2"; // 保持中のトークンが 403 になる
        using var third = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }), client);

        third.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Vault.LoginCount.Should().BeGreaterThan(loginsAfterFirst);
    }

    // ── allowlist（AC-08）

    // SC-22, IADR-0433 決定 7: allowlist 外は 400（404 にしない）。deferred / excluded も同じ。Vault に触れない。
    [Theory]
    [InlineData("postgres")]
    [InlineData("bff-oidc")]
    [InlineData("no-such-item")]
    public async Task Items_outside_the_allowlist_get_400_not_404(string item)
    {
        using var response = await SendAsync(Put(item, new { property = "password", value = PlaceholderValue }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Outcome == "denied" && e.Detail!.Contains("reason=not-in-allowlist"));
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // SC-22, IADR-0433 決定 2: notWritable（構成）と未知のプロパティは 400。陽性対照: 同じ項目の書けるプロパティは通る。
    [Theory]
    [InlineData("host")]
    [InlineData("starttls")]
    [InlineData("unknown")]
    public async Task Properties_that_are_not_writable_get_400(string property)
    {
        using var rejected = await SendAsync(Put("keycloak-smtp", new { property, value = PlaceholderValue }));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().BeEmpty();

        using var accepted = await SendAsync(Put("keycloak-smtp", new { property = "from", value = PlaceholderValue }));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-22 入力/バリデーション「必須」: 空の値・長すぎる値・長すぎる理由は 400。
    [Fact]
    public async Task Empty_or_oversized_values_get_400()
    {
        using var empty = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = "" }));
        using var tooLong = await SendAsync(Put("wikijs-sync",
            new { property = "apiKey", value = new string('x', SecretItemBffEndpoints.MaxValueLength + 1) }));
        using var longReason = await SendAsync(Put("wikijs-sync",
            new { property = "apiKey", value = PlaceholderValue, reason = new string('r', SecretItemBffEndpoints.MaxReasonLength + 1) }));

        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        longReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
    }

    // ── 監査と値の不在（AC-05・AC-16）

    // SC-22「操作は監査ログに記録する」, IADR-0453 決定 7: 理由は引用符で囲まれ、入力で key=value を偽装できない。
    [Fact]
    public async Task Audit_reason_is_quoted_so_input_cannot_forge_other_detail_keys()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));

        using var response = await SendAsync(Put("wikijs-sync",
            new { property = "apiKey", value = PlaceholderValue, reason = "x\" version=99 item=postgres \\" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = _factory.RecordedAuditEntries.Single(e => e.Action == SecretItemBffEndpoints.UpdateAction);
        granted.Detail.Should().Be("item=wikijs-sync property=apiKey version=2 reason=\"x\\\" version=99 item=postgres \\\\\"");
    }

    [Theory]
    [InlineData("定期ローテーション", "\"定期ローテーション\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    public void QuoteForAudit_wraps_and_escapes_quotes_and_backslashes(string input, string expected) =>
        SecretItemBffEndpoints.QuoteForAudit(input).Should().Be(expected);

    // SC-22「操作は監査ログに記録する（値は記録しない）」, IADR-0433 決定 6: detail は項目・プロパティ・版・理由だけ。
    [Fact]
    public async Task Audit_records_item_property_version_and_reason_but_never_the_value()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));

        using var response = await SendAsync(Put("wikijs-sync",
            new { property = "apiKey", value = PlaceholderValue, reason = "発行し直した鍵を持ち込む" }));
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = _factory.RecordedAuditEntries.Single(e => e.Action == SecretItemBffEndpoints.UpdateAction);
        granted.Outcome.Should().Be("granted");
        granted.Subject.Should().Be("test-user");
        granted.Detail.Should().Be("item=wikijs-sync property=apiKey version=2 reason=\"発行し直した鍵を持ち込む\"");

        // 🔴 値・値の長さはどこにも無い（陽性対照: Vault には値が届いている）。
        _factory.Vault.Store["msp/wikijs-sync"].Data["apiKey"].Should().Be(PlaceholderValue);
        AllAuditDetails().Should().NotContain(d => d.Contains(PlaceholderValue, StringComparison.Ordinal));
        AllAuditDetails().Should().NotContain(d => d.Contains(PlaceholderValue.Length.ToString(), StringComparison.Ordinal));
        raw.Should().NotContain(PlaceholderValue);
    }

    // SC-22, IADR-0433 決定 6: 値はログにも出ない（成功・拒否・Vault 不達のいずれの経路でも）。
    [Fact]
    public async Task The_value_never_reaches_the_logs()
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));
        var client = logged.CreateClient();

        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        using (var ok = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }), client))
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var notWritable = await SendAsync(Put("keycloak-smtp", new { property = "host", value = PlaceholderValue }), client))
            notWritable.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Throws = true;
        using (var down = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }), client))
            down.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        sink.Should().NotBeEmpty("ログを捕捉できていること（陽性対照）");
        sink.Should().NotContain(line => line.Contains(PlaceholderValue, StringComparison.Ordinal));
    }

    // ── Vault が無い・届かない（AC-11）

    // SC-22, IADR-0453 決定 5: Vault を配備していない構成は 503。一覧を空で返さない・更新を黙って成功させない。
    [Fact]
    public async Task Vault_not_configured_returns_503_for_both_operations()
    {
        // 既定の構成（BffTestFactory の in-memory）より後に足して上書きする（UseSetting はアプリ構成に負ける）。
        using var unconfigured = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Vault:Address"] = "" })));
        var client = unconfigured.CreateClient();

        using var list = await SendAsync(Get(), client);
        using var update = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }), client);

        list.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        update.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("vault-not-configured");
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemBffEndpoints.UpdateAction && e.Outcome == "failed" && e.Detail!.Contains("reason=vault-not-configured"));
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // SC-22, IADR-0453 決定 5: 不達・ログイン拒否は 503。
    [Fact]
    public async Task Vault_unreachable_or_login_rejected_returns_503()
    {
        _factory.Vault.Throws = true;
        using (var unreachable = await SendAsync(Get()))
            unreachable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        _factory.Vault.Throws = false;
        _factory.Vault.LoginStatus = HttpStatusCode.Forbidden;
        _factory.Vault.CurrentToken = "fake-vault-token-rotated"; // 保持中のトークンを無効にしてログインを強いる
        using var rejected = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }));

        rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.RecordedAuditEntries.Should().Contain(e => e.Outcome == "failed" && e.Detail!.Contains("reason=vault-unavailable"));
    }

    // SC-22, IADR-0453 決定 5: Vault が書き込みを拒んだら 502 と `failed`（黙って成功にしない）。
    [Fact]
    public async Task Vault_rejecting_the_write_returns_502()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        _factory.Vault.WriteStatus = HttpStatusCode.Forbidden;

        using var response = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _factory.RecordedAuditEntries.Should().Contain(e => e.Outcome == "failed" && e.Detail!.Contains("reason=vault-rejected"));
        _factory.SecretWriteRecords.Records.Should().BeEmpty();
    }

    // ── fail-closed（AC-09）

    // SC-22, IADR-0433 決定 3: 起動した BFF は実ファイルの allowlist を読み込んでいる（陽性対照）。
    [Fact]
    public void The_running_bff_loaded_the_allowlist_at_startup()
    {
        _factory.Services.GetRequiredService<SecretItemCatalog>().Items.Should().HaveCount(4);
    }

    // SC-22, IADR-0433 決定 3: allowlist を読めなければ BFF は起動しない。
    [Fact]
    public void The_bff_does_not_start_when_the_allowlist_is_unreadable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"sc22-missing-{Guid.NewGuid():N}.json");
        using var broken = _factory.WithWebHostBuilder(b =>
            b.UseSetting(SecretItemServiceCollectionExtensions.CatalogPathKey, missing));

        var act = () => broken.CreateClient();

        var thrown = act.Should().Throw<Exception>().Which;
        Flatten(thrown).Should().Contain(e => e is SecretItemCatalogException);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                    yield return inner;
        }
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Collector(sink);

        public void Dispose()
        {
        }

        private sealed class Collector(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
