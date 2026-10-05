using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Bff.Foundation.Endpoints;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Secrets;

namespace Platform.Bff.Tests;

// SC-22, SC-06, NFR-18, 計画 ADR-0126 決定 1〜4, IADR-0501 (#458 段 S2):
// SC-22 の群「データソースの資格情報」（`/bff/secrets/groups/datasource-credentials`）。
// 認可（書き込みは管理者だけ）・登録済みの成員だけ・専用接頭辞の 1 セグメント・値を出さない・供給元・参照の配置の順序を固定する。
//
// 🔴 成員の供給は**本物の** `DataSourceCredentialGroupSource`（knowledge の BFF モジュール）が走り、後段（DataSourceService）だけを
// スタブにしている。否定形（Vault へ届かない・値が出ない）は陽性対照と対で置く。
public class BffSecretItemGroupEndpointTests : IClassFixture<BffTestFactory>
{
    // 🔴 テスト用の明白なダミー値。本物の秘密は 1 つも書かない。
    private const string PlaceholderValue = "placeholder-value-for-sc22-group-tests-0001";
    private const string Group = "datasource-credentials";

    private static readonly Guid WikiId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly Guid DbId = Guid.Parse("6fa459ea-ee8a-3ca4-894e-db77e160355e");
    private static readonly Guid SaasId = Guid.Parse("1b4e28ba-2fa1-11d2-883f-0016d3cca427");
    private static readonly Guid UnregisteredId = Guid.Parse("9c5b94b1-35ad-49bb-b118-8e8fc24abf80");

    private readonly BffTestFactory _factory;

    public BffSecretItemGroupEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.Vault.Reset();
        _factory.KubernetesApi.Reset();
        _factory.SecretWriteRecords.Records.Clear();
        _factory.RecordedAuditEntries.Clear();
        _factory.ResetDataSourceCredentials();
        _factory.StubDataSourceCredentials =
        [
            new(WikiId, "社内 Wiki", "wiki", [new("apiToken", DataSourceCredentialSupplies.Absent)]),
            new(DbId, "業務 DB", "db", [new("password", DataSourceCredentialSupplies.Reference)]),
            new(SaasId, "SaaS", "saas", [new("apiToken", DataSourceCredentialSupplies.Other)]),
        ];
    }

    private static HttpRequestMessage Get(string group = Group, string? roles = null, bool anonymous = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/bff/secrets/groups/{group}");
        Decorate(request, roles, anonymous);
        return request;
    }

    private static HttpRequestMessage Put(string member, object body, string group = Group, string? roles = null, bool anonymous = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/secrets/groups/{group}/{member}") { Content = JsonContent.Create(body) };
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

    private static object Body(string property = "apiToken", string value = PlaceholderValue, string? reason = null) =>
        new { property, value, reason };

    private IEnumerable<FakeVault.RecordedRequest> VaultWrites() =>
        _factory.Vault.Requests.Where(r => r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal) && r.Method != "GET");

    // ── 認可（AC-1・AC-2）

    // ADR-0126 決定 3: 一覧は運用者・管理者が引ける。`writable` は管理者だけ true（運用者は閲覧だけ）。
    [Theory]
    [InlineData(PlatformAuthPolicies.OperatorRole, false)]
    [InlineData(PlatformAuthPolicies.AdminRole, true)]
    public async Task Group_list_is_open_to_operators_and_admins_and_reports_writable_by_role(string role, bool writable)
    {
        using var response = await SendAsync(Get(roles: role));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var group = await response.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken);
        group!.Group.Should().Be(Group);
        group.Writable.Should().Be(writable);
        group.Members.Select(m => m.MemberId).Should().Equal(WikiId.ToString("D"), DbId.ToString("D"), SaasId.ToString("D"));
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.ListAction && e.Outcome == "granted");
    }

    // 他のロールは一覧も書き込みも 403。拒否は監査に残り、Vault にも後段にも触れない。
    [Fact]
    public async Task Other_roles_get_403_for_the_group_list_and_write()
    {
        using var list = await SendAsync(Get(roles: "platform-user"));
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body(), roles: "platform-user"));

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.ListAction && e.Outcome == "denied" && e.Detail!.Contains("reason=forbidden"));
        _factory.Vault.Requests.Should().BeEmpty();
        _factory.DataSourceReferenceRequests.Should().BeEmpty();
    }

    // 🔴 ADR-0126 決定 3: **運用者は群へ書けない**（静的な項目へは書ける —— 陽性対照）。拒否は本文を読む前で、Vault へ届かない。
    [Fact]
    public async Task Group_write_is_admin_only_and_operator_denial_is_audited_without_touching_vault()
    {
        using var group = await SendAsync(Put(WikiId.ToString("D"), Body(), roles: PlatformAuthPolicies.OperatorRole));

        group.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.RecordedAuditEntries.Should().ContainSingle(e =>
            e.Action == SecretItemGroupBffEndpoints.UpdateAction && e.Outcome == "denied"
            && e.Detail!.Contains($"item={Group}") && e.Detail.Contains("reason=forbidden"));
        _factory.Vault.Requests.Should().BeEmpty();
        _factory.DataSourceReferenceRequests.Should().BeEmpty();

        // 陽性対照: 同じ運用者が静的な項目へは書ける（SC-22 のほかの項目のロールは変えない。決定 3）。
        var staticItem = new HttpRequestMessage(HttpMethod.Put, "/bff/secrets/wikijs-sync")
        {
            Content = JsonContent.Create(new { property = "apiKey", value = PlaceholderValue }),
        };
        staticItem.Headers.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.OperatorRole);
        using var allowed = await SendAsync(staticItem);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 未認証は 401（存在は秘匿しない）。
    [Fact]
    public async Task Anonymous_gets_401_for_the_group()
    {
        using var list = await SendAsync(Get(anonymous: true));
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body(), anonymous: true));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        update.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // ── 登録済みの成員だけ・専用接頭辞だけ（AC-3・AC-4）

    // 🔴 ADR-0126 決定 2: 未登録（無効化・資格情報を持たない種別も一覧に無い）の ID へは 404 で、Vault へ届かない。
    [Fact]
    public async Task Unregistered_member_gets_404_and_vault_is_never_touched()
    {
        using var response = await SendAsync(Put(UnregisteredId.ToString("D"), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.UpdateAction && e.Outcome == "denied" && e.Detail!.Contains("reason=not-registered"));
        _factory.Vault.Requests.Should().BeEmpty();
        _factory.DataSourceReferenceRequests.Should().BeEmpty();

        // 陽性対照: 登録済みの ID は通る。
        using var registered = await SendAsync(Put(WikiId.ToString("D"), Body()));
        registered.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 成員 ID は Vault のパスの 1 セグメントになる。形が崩れた ID（大文字・`..`・符号化した `/`・長すぎ）は Vault へ届かない。
    [Theory]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("a%2Fb")]
    [InlineData("a.b")]
    [InlineData("a_b")]
    public async Task Malformed_member_ids_never_reach_vault(string member)
    {
        using var response = await SendAsync(Put(member, Body()));

        // `..` と `%2e%2e` は URI の正規化で 1 段上（`PUT /bff/secrets/groups` ＝ 静的な項目 `groups`）へ畳まれ、
        // allowlist 外の 400 になる。どちらでも Vault へは届かない。
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // 成員 ID の形そのもの（後段の一覧に頼らない 1 段目の守り）。小文字英数とハイフンの 1 セグメント・64 文字まで。
    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
    [InlineData("a", true)]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("-a", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // #458 段 S2 の独立監査: `$` は末尾の改行の前でも一致する。終端は `\z`。
    [InlineData("a\n", false)]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301\n", false)]
    public void Member_id_pattern_admits_only_one_lowercase_segment(string? member, bool expected)
    {
        SecretItemGroupBffEndpoints.IsMemberId(member).Should().Be(expected);
        SecretItemGroupBffEndpoints.IsMemberId(new string('a', 64)).Should().BeTrue();
        SecretItemGroupBffEndpoints.IsMemberId(new string('a', 65)).Should().BeFalse();
    }

    // 長すぎる ID（65 文字）も同じ。
    [Fact]
    public async Task Over_long_member_id_never_reaches_vault()
    {
        using var response = await SendAsync(Put(new string('a', 65), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // 🔴 ADR-0126 決定 1・2: 書き込みは `secret/data/datasource/<ID>` の 1 本だけ（KV v2 の部分更新）。ほかのパスへ書かない。
    [Fact]
    public async Task Write_goes_to_the_dedicated_prefix_path_only()
    {
        using var response = await SendAsync(Put(DbId.ToString("D"), Body("password")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        VaultWrites().Select(r => r.Path).Distinct().Should().Equal($"/v1/secret/data/datasource/{DbId:D}");
        _factory.Vault.Store[$"datasource/{DbId:D}"].Data["password"].Should().Be(PlaceholderValue);
    }

    // ADR-0126 決定 1: 成員が宣言しないプロパティは 400（Vault へ届かない）。
    [Theory]
    [InlineData("password")]
    [InlineData("ApiToken")]
    [InlineData("connectionUri")]
    public async Task Property_not_declared_by_the_member_gets_400(string property)
    {
        using var response = await SendAsync(Put(WikiId.ToString("D"), Body(property)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // 宣言に無い群は 400（allowlist 外）。静的な項目の名前を群として使っても書けない。
    [Theory]
    [InlineData("msp")]
    [InlineData("llm-provider-credentials")]
    [InlineData("unknown-group")]
    public async Task Group_not_in_the_allowlist_gets_400(string group)
    {
        using var list = await SendAsync(Get(group));
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body(), group));

        list.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // 値・理由の規則（静的な項目と同じ上限）。
    [Fact]
    public async Task Empty_or_oversized_values_and_reasons_get_400()
    {
        using var empty = await SendAsync(Put(WikiId.ToString("D"), Body(value: "")));
        using var tooLong = await SendAsync(Put(WikiId.ToString("D"), Body(value: new string('x', SecretItemBffEndpoints.MaxValueLength + 1))));
        using var reason = await SendAsync(Put(WikiId.ToString("D"), Body(reason: new string('r', SecretItemBffEndpoints.MaxReasonLength + 1))));

        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        reason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // 本文は JSON だけ（415 も監査に残る）。
    [Fact]
    public async Task Non_json_body_gets_415_and_is_audited()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/bff/secrets/groups/{Group}/{WikiId:D}")
        {
            Content = new StringContent("property=apiToken", Encoding.UTF8, "text/plain"),
        };
        using var response = await SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.UpdateAction && e.Detail!.Contains("reason=unsupported-media-type"));
        _factory.Vault.Requests.Should().BeEmpty();
    }

    // ── 成員が取れない（AC-7）

    // 🔴 成員が取れないときは空の群へ縮退させない（一覧も書き込みも 502。書き込みは Vault へ届かない）。
    [Fact]
    public async Task Registry_unavailable_returns_502_not_an_empty_group()
    {
        _factory.DataSourceCredentialsStatusCode = HttpStatusCode.ServiceUnavailable;

        using var list = await SendAsync(Get());
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body()));

        list.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        update.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        VaultWrites().Should().BeEmpty();
        _factory.RecordedAuditEntries.Should().Contain(e => e.Detail!.Contains("reason=members-unavailable"));
    }

    // ── 供給元（AC-7。ADR-0126 決定 4）

    // 参照あり →「画面」、値なし →「画面」（書けば参照が置かれる）、平文 →「画面以外」（`git`）。ExternalSecret は引かない。
    [Fact]
    public async Task Supply_source_follows_the_members_credential_state()
    {
        _factory.Vault.Put($"datasource/{DbId:D}", ("password", PlaceholderValue));

        using var response = await SendAsync(Get());

        var members = (await response.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken))!
            .Members.ToDictionary(m => m.MemberId);
        members[WikiId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Screen);
        members[WikiId.ToString("D")].Status.Should().Be("notSet");
        members[DbId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Screen);
        members[DbId.ToString("D")].Status.Should().Be("set");
        members[DbId.ToString("D")].VaultPath.Should().Be($"datasource/{DbId:D}");
        members[DbId.ToString("D")].PropertyDetails.Should().Equal(new SecretItemPropertyDto("password", "value", true));
        members[SaasId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Git);
        _factory.KubernetesApi.Requests.Should().BeEmpty("群の供給元は ExternalSecret の有無で判定しない");
    }

    // 🔴 計画 ADR-0126 決定 4「不明を 2 値へ寄せない」: 後段が未知の符号を返したら「確認できない」（`unknown`）。
    // 「画面」にも「画面以外」にも倒さない（#458 段 S2 の独立監査。従来は「画面以外」へ倒していた）。
    // ただし別のプロパティが平文（`other`）を持てば「画面以外」が勝つ（書いた値が使われないことは確か）。
    [Fact]
    public async Task Unknown_supply_codes_are_reported_as_unknown_not_folded_into_two_values()
    {
        _factory.StubDataSourceCredentials =
        [
            new(WikiId, "社内 Wiki", "wiki", [new("apiToken", "migrating")]),
            new(DbId, "業務 DB", "db", [new("password", "migrating"), new("user", DataSourceCredentialSupplies.Other)]),
        ];

        using var response = await SendAsync(Get());

        var members = (await response.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken))!
            .Members.ToDictionary(m => m.MemberId);
        members[WikiId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Unknown);
        members[DbId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Git);
    }

    // 🔴 保管先（Vault）には値があるのに、成員の設定が値を持たない（`absent`）—— 書いた後の参照の配置が失敗した・
    // 並行する更新に負けた状態。コネクタはその値を読まないので「画面」と出さず「確認できない」（#458 段 S2 の独立監査）。
    // 陽性対照: 同じ「値なし」でも保管先が空なら「画面」（書けば参照が置かれる）。
    [Fact]
    public async Task Vault_set_but_member_without_a_value_is_reported_as_unknown()
    {
        _factory.Vault.Put($"datasource/{WikiId:D}", ("apiToken", PlaceholderValue));

        using var response = await SendAsync(Get());

        var members = (await response.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken))!
            .Members.ToDictionary(m => m.MemberId);
        members[WikiId.ToString("D")].Status.Should().Be("set");
        members[WikiId.ToString("D")].SupplySource.Should().Be(SecretItemSupplySources.Unknown);

        _factory.Vault.Reset();
        using var empty = await SendAsync(Get());
        var wiki = (await empty.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken))!
            .Members.Single(m => m.MemberId == WikiId.ToString("D"));
        wiki.Status.Should().Be("notSet");
        wiki.SupplySource.Should().Be(SecretItemSupplySources.Screen);
    }

    // 供給元の判定そのもの（`SupplySourceOf`）。`git` が最優先、次に `unknown`、残りが `screen`。
    [Theory]
    [InlineData(new[] { SecretItemGroupSupply.Referenced }, false, SecretItemSupplySources.Screen)]
    [InlineData(new[] { SecretItemGroupSupply.Referenced }, true, SecretItemSupplySources.Screen)]
    [InlineData(new[] { SecretItemGroupSupply.Unset }, false, SecretItemSupplySources.Screen)]
    [InlineData(new[] { SecretItemGroupSupply.Unset }, true, SecretItemSupplySources.Unknown)]
    [InlineData(new[] { SecretItemGroupSupply.Unknown }, false, SecretItemSupplySources.Unknown)]
    [InlineData(new[] { SecretItemGroupSupply.Unknown, SecretItemGroupSupply.OtherSource }, true, SecretItemSupplySources.Git)]
    [InlineData(new[] { SecretItemGroupSupply.Unset, SecretItemGroupSupply.OtherSource }, true, SecretItemSupplySources.Git)]
    public void Supply_source_never_folds_unknown_into_screen_or_not_screen(
        SecretItemGroupSupply[] supplies, bool vaultHasValue, string expected)
    {
        SecretItemGroupBffEndpoints.SupplySourceOf(supplies, vaultHasValue).Should().Be(expected);
    }

    // ── 書き込みと参照の配置（AC-5・AC-8。窓の両端）

    // P+: 値なしの成員へ書く → **Vault が先、参照が後**。参照は `PUT /datasources/<ID>/credentials/<キー>/reference`。
    // 応答は版と「画面」だけで、同期依頼（ExternalSecret）は無い。最終更新者が一覧に出る。
    [Fact]
    public async Task Write_to_a_member_without_a_value_writes_vault_then_places_the_reference()
    {
        using var response = await SendAsync(Put(WikiId.ToString("D"), Body(reason: "初回の投入")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemGroupWriteResultDto>(TestContext.Current.CancellationToken);
        result!.MemberId.Should().Be(WikiId.ToString("D"));
        result.Property.Should().Be("apiToken");
        result.Version.Should().Be(1);
        result.SupplySource.Should().Be(SecretItemSupplySources.Screen);

        var reference = _factory.DataSourceReferenceRequests.Should().ContainSingle().Subject;
        reference.Path.Should().Be($"/datasources/{WikiId:D}/credentials/apiToken/reference");
        reference.VaultDataWrites.Should().BeGreaterThan(0, "参照は Vault へ書いた後に置く");
        _factory.KubernetesApi.Requests.Should().BeEmpty("群は同期依頼をしない");
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.ReferenceAction && e.Outcome == "granted");

        using var list = await SendAsync(Get());
        var member = (await list.Content.ReadFromJsonAsync<SecretItemGroupDto>(TestContext.Current.CancellationToken))!
            .Members.Single(m => m.MemberId == WikiId.ToString("D"));
        member.Status.Should().Be("set");
        member.LastUpdatedBy.Should().Be("test-user");
    }

    // P−: 平文を持つ成員へ書く → 書き込みは成立するが、後段は平文を置き換えない。応答は「画面以外」。
    [Fact]
    public async Task Write_to_a_member_with_plaintext_keeps_it_and_reports_not_screen()
    {
        using var response = await SendAsync(Put(SaasId.ToString("D"), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemGroupWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SupplySource.Should().Be(SecretItemSupplySources.Git);
        VaultWrites().Should().NotBeEmpty();
    }

    // 🔴 Vault が書き込みを拒んだら参照を置かない（値の無い Vault を指す参照を残さない）。
    [Fact]
    public async Task Failed_vault_write_does_not_place_a_reference()
    {
        _factory.Vault.WriteStatus = HttpStatusCode.Forbidden;

        using var response = await SendAsync(Put(WikiId.ToString("D"), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _factory.DataSourceReferenceRequests.Should().BeEmpty();
    }

    // 参照の配置に失敗しても書き込みは成立している（200・供給元は「確認できない」・別の監査行に failed）。
    [Fact]
    public async Task Reference_failure_keeps_the_write_and_reports_unknown()
    {
        _factory.DataSourceReferenceStatusCode = HttpStatusCode.InternalServerError;

        using var response = await SendAsync(Put(WikiId.ToString("D"), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemGroupWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SupplySource.Should().Be(SecretItemSupplySources.Unknown);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.UpdateAction && e.Outcome == "granted");
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.ReferenceAction && e.Outcome == "failed");
    }

    // 後段が配置の結果に未知の符号を返したら、書き込みの結果も「確認できない」（「画面以外」へ倒さない）。
    [Fact]
    public async Task Unknown_reference_result_code_is_reported_as_unknown()
    {
        _factory.StubDataSourceCredentials = [new(WikiId, "社内 Wiki", "wiki", [new("apiToken", "migrating")])];

        using var response = await SendAsync(Put(WikiId.ToString("D"), Body()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemGroupWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SupplySource.Should().Be(SecretItemSupplySources.Unknown);
    }

    // 🔴 Vault へ書けた後の参照の配置が**予期しない例外**（HTTP の失敗ではない）で落ちても 500 にしない（#458 段 S2 の独立監査）。
    // 500 だと画面は「値は保存されていません」と出すが、値は保存されている（偽）。200・「確認できない」・配置は failed の監査行。
    // ログには例外の型名だけを出す（値もメッセージも出さない）。
    [Fact]
    public async Task Unexpected_exception_after_the_vault_write_keeps_the_write_and_reports_unknown()
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));
        _factory.DataSourceReferenceException = () => new InvalidOperationException("fault-message-must-not-be-logged");

        using var response = await SendAsync(Put(WikiId.ToString("D"), Body()), logged.CreateClient());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SecretItemGroupWriteResultDto>(TestContext.Current.CancellationToken);
        result!.SupplySource.Should().Be(SecretItemSupplySources.Unknown);
        _factory.Vault.Store[$"datasource/{WikiId:D}"].Data["apiToken"].Should().Be(PlaceholderValue);
        _factory.RecordedAuditEntries.Should().Contain(e =>
            e.Action == SecretItemGroupBffEndpoints.ReferenceAction && e.Outcome == "failed");
        sink.Should().Contain(line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        sink.Should().NotContain(line => line.Contains("fault-message-must-not-be-logged", StringComparison.Ordinal));
        sink.Should().NotContain(line => line.Contains(PlaceholderValue, StringComparison.Ordinal));
    }

    // 成員の一覧が予期しない例外で落ちても 500 にしない（「取れない」= 502。書き込みは Vault へ届かない）。
    [Fact]
    public async Task Unexpected_exception_while_listing_members_returns_502()
    {
        _factory.DataSourceCredentialsException = () => new InvalidOperationException("fault");

        using var list = await SendAsync(Get());
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body()));

        list.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        update.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        VaultWrites().Should().BeEmpty();
    }

    // 🔴 値は応答・監査・ログのどこにも出ない（成功・拒否・成員の取得失敗のいずれの経路でも）。理由は引用符で囲む。
    [Fact]
    public async Task Group_write_never_echoes_the_value_in_response_audit_or_logs()
    {
        var sink = new ConcurrentQueue<string>();
        using var logged = _factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)).SetMinimumLevel(LogLevel.Trace)));
        var client = logged.CreateClient();

        using var ok = await SendAsync(Put(WikiId.ToString("D"), Body(reason: "x version=99")), client);
        var okBody = await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var denied = await SendAsync(Put(WikiId.ToString("D"), Body("password")), client);
        _factory.DataSourceCredentialsStatusCode = HttpStatusCode.ServiceUnavailable;
        using var down = await SendAsync(Put(WikiId.ToString("D"), Body()), client);

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        down.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        okBody.Should().NotContain(PlaceholderValue);
        _factory.RecordedAuditEntries.Should().NotBeEmpty();
        _factory.RecordedAuditEntries.Should().NotContain(e => (e.Detail ?? "").Contains(PlaceholderValue));
        _factory.RecordedAuditEntries.Should().Contain(e => e.Detail!.Contains("reason=\"x version=99\""));
        sink.Should().NotBeEmpty("ログを捕捉できていること（陽性対照）");
        sink.Should().NotContain(line => line.Contains(PlaceholderValue, StringComparison.Ordinal));
        // 陽性対照: 値は Vault へは届いている。
        _factory.Vault.Store[$"datasource/{WikiId:D}"].Data["apiToken"].Should().Be(PlaceholderValue);
    }

    // Vault を配備していない構成は 503（静的な項目と同じ）。後段にも参照の配置にも進まない。
    [Fact]
    public async Task Vault_not_configured_returns_503_for_both_operations()
    {
        using var unconfigured = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Vault:Address"] = "" })));
        var client = unconfigured.CreateClient();

        using var list = await SendAsync(Get(), client);
        using var update = await SendAsync(Put(WikiId.ToString("D"), Body()), client);

        list.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        update.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.DataSourceReferenceRequests.Should().BeEmpty();
        _factory.Vault.Requests.Should().BeEmpty();
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
                sink.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
