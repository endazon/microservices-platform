using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        _factory.KubernetesApi.Reset();
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
        rows!.Select(r => r.Item).Should().Equal(
            "llm-provider-credentials", "keycloak-smtp", "wikijs-sync", "ast-app-secrets", "ast-moomoo", "ast-moomoo-rsa");
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

    // SC-22 主要素 1「最終更新者」, IADR-0453 フォローアップ 7, IADR-0454 決定 3 (#1467):
    // metadata を消して作り直すと版は 1 から振り直される。🔴 古い記録（版 1・画面の利用者）は**版だけなら一致してしまう**。
    // 作成時刻も突き合わせ、一致しなければ「記録なし」。陽性対照: 作り直す前（画面で書いた版が現在版）は名前が出る。
    [Fact]
    public async Task Last_updater_is_not_attached_to_a_recreated_kv_that_restarted_at_version_1()
    {
        using (var update = await SendAsync(Put("keycloak-smtp", new { property = "password", value = PlaceholderValue })))
            update.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeRecreate = await ListAsync();
        beforeRecreate["keycloak-smtp"].CurrentVersion.Should().Be(1);
        beforeRecreate["keycloak-smtp"].LastUpdatedBy.Should().Be("test-user");

        // コンソールで metadata ごと消して作り直した（`vault kv metadata delete` → `vault kv put`）。版は 1 に戻る。
        _factory.Vault.Store.TryRemove("msp/keycloak-smtp", out _).Should().BeTrue();
        _factory.Vault.Put("msp/keycloak-smtp", ("password", ExistingOtherValue));

        var afterRecreate = await ListAsync();
        afterRecreate["keycloak-smtp"].CurrentVersion.Should().Be(1);
        _factory.SecretWriteRecords.Records["keycloak-smtp"].Version.Should().Be(1, "古い記録は版だけなら一致する（変異の検出点）");
        afterRecreate["keycloak-smtp"].LastUpdatedBy.Should().BeNull();
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

    // SC-22, IADR-0456 決定 6 (#1477): ast-app-secrets への画面の書き込みは、同じ KV の realm と対の *-auth-client-*
    // （許可リストの外）を消さない。消すと ESO が ast-secrets から auth キーを落とし、AST のサービス間トークン取得が止まる。
    [Fact]
    public async Task Update_to_ast_app_secrets_keeps_the_auth_client_keys()
    {
        _factory.Vault.Put("ai-stock-trading/app-secrets",
            ("service-auth-client-id", "ai-stock-trading-svc"), ("service-auth-client-secret", ExistingOtherValue), ("finnhub-api-key", "old"));

        using var response = await SendAsync(Put("ast-app-secrets",
            new { property = "finnhub-api-key", value = PlaceholderValue, reason = "PoC の立ち上げ" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = _factory.Vault.Store["ai-stock-trading/app-secrets"].Data;
        data["finnhub-api-key"].Should().Be(PlaceholderValue);
        data["service-auth-client-id"].Should().Be("ai-stock-trading-svc");
        data["service-auth-client-secret"].Should().Be(ExistingOtherValue);
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
        // 対象は KV の path への POST だけに絞る —— k8s auth のログイン（POST /v1/auth/kubernetes/login）も POST であり、
        // 本試験が最初にログインする実行順（CI の Linux で実測）ではそれを数えて落ちていた（試験側の順序依存）。
        _factory.Vault.Requests.Should().NotContain(r => r.Method == "POST" && r.Path.StartsWith("/v1/secret/", StringComparison.Ordinal));
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

    // ── 本文の上限と解釈（IADR-0453 フォローアップ 6, IADR-0454 決定 2, #1467）
    //
    // 🔴 TestServer は Kestrel の本文上限を強制しない。**端点が自分で上限付きに読む**ことを、`Content-Length` がある送り方と
    // 無い送り方の両方で固定する（片方だけだと、`Content-Length` だけを見る実装が緑になる）。

    private const int DocumentedMaxRequestBodyBytes = 64 * 1024;

    private static HttpRequestMessage PutRaw(string item, HttpContent content, string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/secrets/{item}") { Content = content };
        Decorate(request, roles, anonymous: false);
        return request;
    }

    private static ByteArrayContent JsonBytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    // SC-22, IADR-0454 決定 2: 上限を超える本文は 413。Vault に触れず、拒否が監査に残る。
    // 本文は**入力規則を満たす**（値は短い）ので、上限が無ければ書き込みまで進む —— 上限だけが止めていることを示す。
    [Fact]
    public async Task Oversized_body_gets_413_before_anything_reaches_vault()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        var bytes = OversizedBody();

        using var response = await SendAsync(PutRaw("wikijs-sync", JsonBytes(bytes)));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        AssertOversizedWasRejectedWithoutTouchingVault();
    }

    // SC-22, IADR-0454 決定 2: Content-Length の無い（chunked の）本文でも 413。
    // 🔴 HttpClient 経由では TestServer が長さを計算して付けてしまい、読み取りループの上限を一度も通らない
    //    （PR #1469 の監査が実測）。サーバ側の HttpContext を直接組み、ContentLength = null・シーク不能な本文で送る。
    [Fact]
    public async Task Oversized_body_without_content_length_gets_413_from_the_read_loop()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        var bytes = OversizedBody();

        var context = await _factory.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Put;
            ctx.Request.Path = "/bff/secrets/wikijs-sync";
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = null;
            ctx.Request.Body = new NonSeekableReadStream(bytes);
        }, TestContext.Current.CancellationToken);

        context.Request.ContentLength.Should().BeNull("読み取りループの上限だけが止めていることを示すため");
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        AssertOversizedWasRejectedWithoutTouchingVault();
    }

    private static byte[] OversizedBody() =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            property = "apiKey",
            value = PlaceholderValue,
            padding = new string('p', DocumentedMaxRequestBodyBytes),
        });

    private void AssertOversizedWasRejectedWithoutTouchingVault()
    {
        _factory.RecordedAuditEntries.Where(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Should().ContainSingle()
            .Which.Should().Be((SecretItemBffEndpoints.UpdateAction, "test-user", "denied",
                (string?)"item=wikijs-sync reason=body-too-large"));
        _factory.Vault.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
        _factory.Vault.Store["msp/wikijs-sync"].Data["apiKey"].Should().Be(ExistingOtherValue);
    }

    // SC-22, IADR-0454 決定 2（陽性対照）: 値 8192 文字・理由 500 文字を**すべて `\uXXXX`（1 文字 6 バイト）で送る**最悪の本文は通る。
    [Fact]
    public async Task Worst_case_body_within_the_maxima_is_accepted()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            property = "apiKey",
            value = new string('あ', SecretItemBffEndpoints.MaxValueLength),
            reason = new string('い', SecretItemBffEndpoints.MaxReasonLength),
        });
        bytes.Length.Should().BeGreaterThan(52_000, "既定の JSON は非 ASCII を \\uXXXX で送る（最悪の大きさになっていること）");
        bytes.Length.Should().BeLessThan(DocumentedMaxRequestBodyBytes);

        using var response = await SendAsync(PutRaw("wikijs-sync", JsonBytes(bytes)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-22, IADR-0454 決定 2: 解釈できない本文は 400 で、拒否が監査に残る。🔴 本文の断片は監査にもログにも出ない。
    [Theory]
    [InlineData("{\"property\":\"apiKey\",\"value\":\"placeholder-value-for-sc22-tests-0001\"")]
    [InlineData("null")]
    [InlineData("not json placeholder-value-for-sc22-tests-0001")]
    public async Task Unparseable_body_gets_400_and_is_audited_without_its_content(string raw)
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));

        using var response = await SendAsync(
            PutRaw("wikijs-sync", new StringContent(raw, Encoding.UTF8, "application/json")), logged.CreateClient());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.RecordedAuditEntries.Where(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Should().ContainSingle()
            .Which.Should().Be((SecretItemBffEndpoints.UpdateAction, "test-user", "denied",
                (string?)"item=wikijs-sync reason=invalid-body"));
        _factory.Vault.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
        sink.Should().NotBeEmpty("ログを捕捉できていること（陽性対照）");
        sink.Should().NotContain(line => line.Contains(PlaceholderValue, StringComparison.Ordinal));
        AllAuditDetails().Should().NotContain(d => d.Contains(PlaceholderValue, StringComparison.Ordinal));
    }

    // SC-22, IADR-0454 決定 2: JSON でない本文は 415 で、拒否が監査に残る（暗黙バインドが持っていた要求を保つ）。
    [Fact]
    public async Task Non_json_content_type_gets_415_and_is_audited()
    {
        var json = JsonSerializer.Serialize(new { property = "apiKey", value = PlaceholderValue });

        using var response = await SendAsync(PutRaw("wikijs-sync", new StringContent(json, Encoding.UTF8, "text/plain")));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemBffEndpoints.UpdateAction && e.Outcome == "denied"
            && e.Detail == "item=wikijs-sync reason=unsupported-media-type");
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // SC-22, IADR-0454 決定 2: 🔴 **本文より先にロールを見る。** 非権限者は本文が壊れていても 403 ＋ 監査 forbidden。
    [Fact]
    public async Task Non_writers_get_403_before_the_body_is_read()
    {
        using var response = await SendAsync(
            PutRaw("wikijs-sync", new StringContent("{", Encoding.UTF8, "application/json"), roles: "platform-user"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.RecordedAuditEntries.Should().ContainSingle(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Which.Detail.Should().Be("item=wikijs-sync reason=forbidden");
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // `Content-Length` を持たない本文（chunked 相当）。上限の判定が長さの宣言だけに頼っていないことを確かめる。
    // 長さを持たない（シーク不能な）本文。Kestrel の chunked 受信と同じく、読み取り側は終わりまで読まないと大きさが分からない。
    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
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

    // ── プロパティの種別（IADR-0456 決定 1〜3, #1477）

    // 🔴 テスト用の明白なダミーのパスワード。本物の資格情報は書かない。
    private const string PlaceholderPassword = "placeholder-password-for-sc22-md5-test";

    // 🔴 PEM の見出しを字面で書かない（`.claude/hooks/guard-secrets.js` が秘密鍵の混入として止める）。
    private const string PrivateKeyMarker = "PRIVATE " + "KEY";

    private static string Md5Hex(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));

    // SC-22, IADR-0456 決定 1: 一覧は書けるプロパティごとの種別と秘密かどうかを返す（`properties` と同じ並び）。
    [Fact]
    public async Task List_returns_property_details_for_each_writable_property()
    {
        using var response = await SendAsync(Get());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var rows = document.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("item").GetString()!);

        static IEnumerable<string> Details(JsonElement row) => row.GetProperty("propertyDetails").EnumerateArray()
            .Select(p => $"{p.GetProperty("name").GetString()}|{p.GetProperty("kind").GetString()}|{p.GetProperty("sensitive").GetBoolean()}");

        Details(rows["ast-moomoo"]).Should().Equal("login-account|value|True", "login-pwd-md5|md5-from-password|True");
        Details(rows["ast-moomoo-rsa"]).Should().Equal("opend_rsa.pem|generate-rsa-pkcs1|True");
        Details(rows["ast-app-secrets"]).Should().Contain("finnhub-api-key|value|True")
            .And.Contain("discord-bot-guild-id|value|False")
            .And.Contain("discord-bot-user-mapping|value|False")
            .And.Contain("sec-edgar-user-agent|value|False");
        foreach (var row in rows.Values)
            row.GetProperty("propertyDetails").EnumerateArray().Select(p => p.GetProperty("name").GetString())
                .Should().Equal(row.GetProperty("properties").EnumerateArray().Select(p => p.GetString()));
    }

    // SC-22, IADR-0456 決定 2: パスワードは平文ではなく小文字 hex の MD5 で保管する。
    // 🔴 平文もハッシュも応答・監査・ログに出ない。Vault への要求本文にも平文が載らない。
    // 陽性対照: 同じ項目の `login-account`（種別 value）はそのまま保管される。
    [Fact]
    public async Task Md5_property_stores_the_lowercase_hex_md5_and_never_the_plaintext()
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));
        var client = logged.CreateClient();

        using (var account = await SendAsync(Put("ast-moomoo", new { property = "login-account", value = PlaceholderValue }), client))
            account.StatusCode.Should().Be(HttpStatusCode.OK);
        using var response = await SendAsync(Put("ast-moomoo", new { property = "login-pwd-md5", value = PlaceholderPassword }), client);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = Md5Hex(PlaceholderPassword);
        expected.Should().MatchRegex("^[0-9a-f]{32}$");
        var stored = _factory.Vault.Store["ai-stock-trading/moomoo"].Data;
        stored["login-account"].Should().Be(PlaceholderValue, "種別 value はそのまま書く（陽性対照）");
        stored["login-pwd-md5"].Should().Be(expected).And.NotBe(PlaceholderPassword);

        _factory.Vault.Requests.Should().NotContain(r => r.Body != null && r.Body.Contains(PlaceholderPassword, StringComparison.Ordinal));
        raw.Should().NotContain(PlaceholderPassword).And.NotContain(expected);
        AllAuditDetails().Should().NotContain(d => d.Contains(PlaceholderPassword, StringComparison.Ordinal) || d.Contains(expected, StringComparison.Ordinal));
        sink.Should().NotBeEmpty("ログを捕捉できていること（陽性対照）");
        sink.Should().NotContain(line => line.Contains(PlaceholderPassword, StringComparison.Ordinal) || line.Contains(expected, StringComparison.Ordinal));
        _factory.RecordedAuditEntries.Should().Contain(e => e.Action == SecretItemBffEndpoints.UpdateAction && e.Outcome == "granted"
            && e.Detail == "item=ast-moomoo property=login-pwd-md5 version=2");
    }

    // SC-22, IADR-0456 決定 3: 生成は値を受けず、RSA 1024 bit の PKCS#1 PEM を作って保管する。
    // 🔴 鍵は応答・監査・ログに出ない。生成し直すと別の鍵になる。
    [Fact]
    public async Task Generate_property_stores_a_fresh_rsa_1024_pkcs1_key_and_never_returns_it()
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));
        var client = logged.CreateClient();

        using var first = await SendAsync(Put("ast-moomoo-rsa", new { property = "opend_rsa.pem", value = "", reason = "初回の鍵" }), client);
        var raw = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var pem = _factory.Vault.Store["ai-stock-trading/moomoo-rsa"].Data["opend_rsa.pem"];

        pem.Should().StartWith("-----BEGIN RSA " + PrivateKeyMarker + "-----", "PKCS#1 の見出しであること（PKCS#8 の見出しではない）");
        using (var rsa = System.Security.Cryptography.RSA.Create())
        {
            rsa.ImportFromPem(pem);
            rsa.KeySize.Should().Be(1024);
        }

        var body = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[1];
        raw.Should().NotContain(PrivateKeyMarker).And.NotContain(body);
        AllAuditDetails().Should().NotContain(d => d.Contains(PrivateKeyMarker, StringComparison.Ordinal) || d.Contains(body, StringComparison.Ordinal));
        sink.Should().NotBeEmpty("ログを捕捉できていること（陽性対照）");
        sink.Should().NotContain(line => line.Contains(PrivateKeyMarker, StringComparison.Ordinal) || line.Contains(body, StringComparison.Ordinal));

        using (var second = await SendAsync(Put("ast-moomoo-rsa", new { property = "opend_rsa.pem", value = "" }), client))
            second.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Vault.Store["ai-stock-trading/moomoo-rsa"].Data["opend_rsa.pem"].Should().NotBe(pem, "生成し直すと別の鍵になる");
    }

    // SC-22, IADR-0456 決定 3: 生成の種別へ値を送ると 400（利用者の持ち込んだ鍵を書かない）。Vault に触れない。
    // IADR-0456 決定 2: MD5 の種別もパスワードが空なら 400（従来の入力規則のまま）。
    [Theory]
    [InlineData("ast-moomoo-rsa", "opend_rsa.pem", PlaceholderValue)]
    [InlineData("ast-moomoo", "login-pwd-md5", "")]
    public async Task Kind_specific_value_rules_reject_with_400(string item, string property, string value)
    {
        using var response = await SendAsync(Put(item, new { property, value }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.RecordedAuditEntries.Should().ContainSingle(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Which.Detail.Should().Be($"item={item} property={property} reason=invalid-value");
        _factory.Vault.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
        _factory.KubernetesApi.Requests.Should().BeEmpty();
    }

    // SC-22, IADR-0456 決定 1: 秘密でない（sensitive: false）プロパティも書き込み専用のまま、値はそのまま保管し応答に出さない。
    [Fact]
    public async Task Non_sensitive_property_is_written_as_is_and_still_not_returned()
    {
        using var response = await SendAsync(Put("ast-app-secrets", new { property = "discord-bot-guild-id", value = PlaceholderValue }));
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Vault.Store["ai-stock-trading/app-secrets"].Data["discord-bot-guild-id"].Should().Be(PlaceholderValue);
        raw.Should().NotContain(PlaceholderValue);
    }

    // ── 即時同期（IADR-0456 決定 4, #1477）

    private const string SyncAction = "secret.item.sync";

    // SC-22, IADR-0456 決定 4: 書き込みが成功したら、項目の ExternalSecret へ force-sync の注釈を merge-patch する。
    [Theory]
    [InlineData("llm-provider-credentials", "anthropic-api-key", "microservices-platform", "llm-provider-credentials")]
    [InlineData("keycloak-smtp", "password", "platform-infra", "keycloak-smtp")]
    [InlineData("ast-app-secrets", "finnhub-api-key", "ai-stock-trading", "ast-secrets")]
    [InlineData("ast-moomoo", "login-account", "ai-stock-trading", "moomoo-credentials")]
    public async Task Successful_write_requests_force_sync_on_the_items_external_secret(
        string item, string property, string ns, string externalSecret)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var response = await SendAsync(Put(item, new { property, value = PlaceholderValue }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SyncRequested.Should().BeTrue();

        var patch = _factory.KubernetesApi.Requests.Should().ContainSingle().Which;
        patch.Method.Should().Be("PATCH");
        patch.Path.Should().Be($"/apis/external-secrets.io/v1/namespaces/{ns}/externalsecrets/{externalSecret}");
        patch.ContentType.Should().Be("application/merge-patch+json");
        patch.Authorization.Should().Be("Bearer " + FakeVault.ServiceAccountJwt);
        using (var body = JsonDocument.Parse(patch.Body!))
        {
            body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("metadata");
            var annotations = body.RootElement.GetProperty("metadata").GetProperty("annotations");
            annotations.EnumerateObject().Select(p => p.Name).Should().Equal("force-sync");
            long.Parse(annotations.GetProperty("force-sync").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeGreaterThanOrEqualTo(before);
        }

        patch.Body.Should().NotContain(PlaceholderValue);
        _factory.RecordedAuditEntries.Should().ContainSingle(e => e.Action == SyncAction)
            .Which.Should().Be((SyncAction, "test-user", "granted", (string?)$"item={item} externalSecret={ns}/{externalSecret}"));
    }

    // SC-22, IADR-0456 決定 4: 🔴 **同期の依頼が失敗しても書き込みを失敗にしない。** 200 ＋ syncRequested=false、監査は別の行で failed。
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(null, true)]
    public async Task Failed_sync_request_does_not_fail_the_write(HttpStatusCode? status, bool throws)
    {
        _factory.KubernetesApi.ForcedStatus = status;
        _factory.KubernetesApi.Throws = throws;

        using var response = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SyncRequested.Should().BeFalse();
        result.Version.Should().Be(1);
        _factory.Vault.Store["msp/wikijs-sync"].Data["apiKey"].Should().Be(PlaceholderValue, "書き込みは成立している");
        _factory.SecretWriteRecords.Records.Should().ContainKey("wikijs-sync");
        _factory.RecordedAuditEntries.Where(e => e.Action == SecretItemBffEndpoints.UpdateAction)
            .Should().ContainSingle().Which.Outcome.Should().Be("granted");
        _factory.RecordedAuditEntries.Should().ContainSingle(e => e.Action == SyncAction)
            .Which.Should().Be((SyncAction, "test-user", "failed",
                (string?)"item=wikijs-sync externalSecret=microservices-platform/wikijs-sync reason=sync-request-failed"));
    }

    // SC-22, IADR-0456 決定 4: 同期が構成されていない（クラスタ外・無効化）なら依頼を送らず、false と監査 sync-not-configured。
    [Fact]
    public async Task Sync_not_configured_reports_false_without_calling_the_api()
    {
        using var disabled = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["ExternalSecretSync:Enabled"] = "false" })));

        using var response = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue }), disabled.CreateClient());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SecretItemWriteResultDto>(TestContext.Current.CancellationToken))!
            .SyncRequested.Should().BeFalse();
        _factory.KubernetesApi.Requests.Should().BeEmpty();
        _factory.RecordedAuditEntries.Should().ContainSingle(e => e.Action == SyncAction)
            .Which.Detail.Should().Be("item=wikijs-sync externalSecret=microservices-platform/wikijs-sync reason=sync-not-configured");
    }

    // SC-22, IADR-0456 決定 4: 書き込みが成立しなかったら同期を依頼しない（502・409）。
    [Fact]
    public async Task Failed_write_does_not_request_sync()
    {
        _factory.Vault.Put("msp/wikijs-sync", ("apiKey", ExistingOtherValue));
        _factory.Vault.WriteStatus = HttpStatusCode.Forbidden;
        using (var rejected = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue })))
            rejected.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        _factory.Vault.WriteStatus = null;
        _factory.Vault.Store["msp/wikijs-sync"].Deleted = true;
        using (var deleted = await SendAsync(Put("wikijs-sync", new { property = "apiKey", value = PlaceholderValue })))
            deleted.StatusCode.Should().Be(HttpStatusCode.Conflict);

        _factory.KubernetesApi.Requests.Should().BeEmpty();
        _factory.RecordedAuditEntries.Should().NotContain(e => e.Action == SyncAction);
    }

    // ── 供給元（ADR-0104 決定 2・IADR-0460 決定 1。#1502）

    private static readonly string[] ItemsInOrder =
        ["llm-provider-credentials", "keycloak-smtp", "wikijs-sync", "ast-app-secrets", "ast-moomoo", "ast-moomoo-rsa"];

    private async Task<Dictionary<string, string>> SupplySourcesAsync(HttpClient? client = null)
    {
        using var response = await SendAsync(Get(), client);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<SecretItemStatusDto>>(TestContext.Current.CancellationToken);
        return rows!.ToDictionary(r => r.Item, r => r.SupplySource);
    }

    // T-68: 同期先の ExternalSecret が在れば `screen`、無ければ `git`。判定は `get` だけで、項目ごとに 1 回・SA トークンで名乗る。
    // 🔴 陽性（在る＝screen）と陰性（無い＝git）を同じ一覧の中で対にする（AST_ESO=0 の配備で `ast-secrets` だけ無い形）。
    [Fact]
    public async Task Supply_source_is_screen_when_the_external_secret_exists_and_git_when_it_is_absent()
    {
        _factory.KubernetesApi.MarkMissing("ai-stock-trading", "ast-secrets");

        var sources = await SupplySourcesAsync();

        sources.Keys.Should().Equal(ItemsInOrder);
        sources["ast-app-secrets"].Should().Be(SecretItemSupplySources.Git, "同期先が無い＝画面で書いた値は届かない");
        foreach (var item in ItemsInOrder.Where(i => i != "ast-app-secrets"))
            sources[item].Should().Be(SecretItemSupplySources.Screen, $"{item} の同期先は在る");

        var requests = _factory.KubernetesApi.Requests.ToList();
        requests.Should().OnlyContain(r => r.Method == "GET", "一覧は注釈を付けない（同期を依頼しない）");
        requests.Should().OnlyContain(r => r.Authorization == "Bearer " + FakeVault.ServiceAccountJwt);
        requests.Select(r => r.Path).Should().BeEquivalentTo(
            "/apis/external-secrets.io/v1/namespaces/microservices-platform/externalsecrets/llm-provider-credentials",
            "/apis/external-secrets.io/v1/namespaces/platform-infra/externalsecrets/keycloak-smtp",
            "/apis/external-secrets.io/v1/namespaces/microservices-platform/externalsecrets/wikijs-sync",
            "/apis/external-secrets.io/v1/namespaces/ai-stock-trading/externalsecrets/ast-secrets",
            "/apis/external-secrets.io/v1/namespaces/ai-stock-trading/externalsecrets/moomoo-credentials",
            "/apis/external-secrets.io/v1/namespaces/ai-stock-trading/externalsecrets/moomoo-rsa");
        _factory.RecordedAuditEntries.Should().NotContain(e => e.Action == SyncAction);
    }

    // T-69: 🔴 **判定できないときは `unknown`**（RBAC の拒否・障害・不達）。在る／無いのどちらにも倒さない。
    // 陽性対照: 同じ偽物が 404 を返すと `git` になる（404 だけが「無い」の根拠である）。
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false, SecretItemSupplySources.Unknown)]
    [InlineData(HttpStatusCode.Unauthorized, false, SecretItemSupplySources.Unknown)]
    [InlineData(HttpStatusCode.InternalServerError, false, SecretItemSupplySources.Unknown)]
    [InlineData(null, true, SecretItemSupplySources.Unknown)]
    [InlineData(HttpStatusCode.NotFound, false, SecretItemSupplySources.Git)]
    public async Task Supply_source_is_unknown_unless_the_api_answers_present_or_absent(
        HttpStatusCode? status, bool throws, string expected)
    {
        _factory.KubernetesApi.ForcedStatus = status;
        _factory.KubernetesApi.Throws = throws;

        var sources = await SupplySourcesAsync();

        sources.Values.Should().OnlyContain(s => s == expected);
        _factory.KubernetesApi.Requests.Should().HaveCount(ItemsInOrder.Length, "項目ごとに 1 回問い合わせている");
    }

    // T-69: 同期（＝Kubernetes API への接続）が構成されていなければ問い合わせず、全項目 `unknown`。
    [Fact]
    public async Task Supply_source_is_unknown_without_calling_the_api_when_sync_is_not_configured()
    {
        using var disabled = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["ExternalSecretSync:Enabled"] = "false" })));

        var sources = await SupplySourcesAsync(disabled.CreateClient());

        sources.Values.Should().OnlyContain(s => s == SecretItemSupplySources.Unknown);
        _factory.KubernetesApi.Requests.Should().BeEmpty();
    }

    // T-70: 権限外・保管先不達の一覧は Kubernetes API へ 1 度も触れない（判定は一覧を返すと決まった後）。
    // 🔴 保管先不達は**トークンを保持した状態**で起こす —— ログイン確認を飛ばして metadata が 1 件も取れない経路（IADR-0453 決定 5）で、
    // 判定を前に置いた初版はここで漏れた（#1502。クラス全体の実行順でだけ再現した）。
    [Fact]
    public async Task List_does_not_touch_the_kubernetes_api_when_denied_or_when_vault_is_unreachable()
    {
        using (var denied = await SendAsync(Get("platform-user")))
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.KubernetesApi.Requests.Should().BeEmpty("権限外は判定まで進まない");

        // 陽性対照: 通る一覧は問い合わせる（この一覧で BFF は Vault のトークンを保持する）。
        (await SupplySourcesAsync()).Should().HaveCount(ItemsInOrder.Length);
        _factory.KubernetesApi.Requests.Should().NotBeEmpty();
        _factory.KubernetesApi.Requests.Clear();

        _factory.Vault.Throws = true;
        using (var unreachable = await SendAsync(Get()))
            unreachable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.KubernetesApi.Requests.Should().BeEmpty("保管先に届かない一覧は 503 で返し、供給元を判定しない");
    }

    // T-71: 本番の合成（Program.cs → AddSecretItemInjection）が判定器を登録している。偽物は通信路（HttpMessageHandler）だけで、
    // 判定器・一覧の端点は本物が走る（上の T-68〜T-70 は本物の Program を起動した BffTestFactory 経由である）。
    [Fact]
    public void The_running_bff_resolves_the_real_presence_reader()
    {
        _factory.Services.GetRequiredService<IExternalSecretPresenceReader>()
            .Should().BeOfType<ExternalSecretPresenceReader>();
    }

    // ── fail-closed（AC-09）

    // SC-22, IADR-0433 決定 3: 起動した BFF は実ファイルの allowlist を読み込んでいる（陽性対照）。
    [Fact]
    public void The_running_bff_loaded_the_allowlist_at_startup()
    {
        _factory.Services.GetRequiredService<SecretItemCatalog>().Items.Should().HaveCount(6);
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
