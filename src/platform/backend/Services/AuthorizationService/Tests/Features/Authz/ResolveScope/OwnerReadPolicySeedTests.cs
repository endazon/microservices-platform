using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, FR-19, NFR-09, UC-05, 計画 ADR-0121 決定 1・フォローアップ 1・6, ADR-0036 D-01・D-02,
// ADR-0062 実測 9, [[IADR-0253]], [[IADR-0384]] (#1664):
// **dev seed を入れた認可サービスが、所有者の読み取りをどう解決するか**を実物の seed と実物の端点で固定する。
//
// 🔴 **seed のファイルを試験へ書き写さない。** `deploy/local/abac-seed/policies.json` をそのまま読み込み、
// `POST /authz/scope` の応答を期待値のファイル（`Fixtures/owner-read-seed-scopes.json`）と突き合わせる。
// 消費側（BFF・MCP の登録者・検索・DocumentService）の試験は**同じ期待値のファイル**を入力にする ——
// seed が変われば、先にここが赤になる（期待値を各所へ書き写すと、消費側だけが作り物のまま緑で残る）。
[Trait("TestKind", "Integration")]
public class OwnerReadPolicySeedTests : IClassFixture<TestWebApplicationFactory>
{
    private const string OwnerPolicyName = "dev: 所有者は自分の文書を読める";
    private readonly TestWebApplicationFactory _factory;

    public OwnerReadPolicySeedTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        LoadSeedIntoDatabase();
        foreach (var subject in SeedScopeFixture.Subjects())
        {
            _factory.Identity.Attributes[subject.UserId] = subject.Attributes;
            _factory.Identity.Groups[subject.UserId] = [];
        }
    }

    // 投入スクリプト（`scripts/seed-abac-policies.js`）と同じく **seed の全ポリシー**を入れる
    // （read だけに絞ると、write / analyze が read へ混ざらないことを測れない）。
    private void LoadSeedIntoDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        if (db.Policies.Any()) return;
        foreach (var p in SeedScopeFixture.SeedPolicies())
            db.Policies.Add(AbacPolicy.Create(p.Name, p.Action, p.UserConditions, p.DocumentConditions));
        db.SaveChanges();
    }

    private async Task<(HttpStatusCode Status, AccessScopeResponse? Scope)> ResolveAsync(string userId)
    {
        var resp = await _factory.CreateServiceCallerClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(userId, []), TestContext.Current.CancellationToken);
        var scope = resp.StatusCode == HttpStatusCode.OK
            ? await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(TestContext.Current.CancellationToken)
            : null;
        return (resp.StatusCode, scope);
    }

    // T-24（ADR-0121 決定 1）: **所有者の read はポリシー 1 本で、形が決まっている。**
    // 利用者の条件なし・文書の条件は `owner ∈ {${current_user}}` だけ。
    [Fact]
    public void Seedは所有者の読み取りのポリシーをちょうど1本持つ()
    {
        var owners = SeedScopeFixture.SeedPolicies()
            .Where(p => p.Action == PolicyAction.Read && p.DocumentConditions.ContainsKey("owner"))
            .ToList();

        var owner = owners.Should().ContainSingle("所有者の分岐はポリシー 1 本で表す（1 ポリシー = 1 分岐）").Which;
        owner.Name.Should().Be(OwnerPolicyName);
        owner.UserConditions.Should().BeEmpty("利用者の条件は置かない（ADR-0121 決定 1）");
        owner.DocumentConditions.Should().ContainSingle()
            .Which.Value.Should().Equal(["${current_user}"], "文書の条件は owner ∈ {${current_user}} だけ");
    }

    // T-25（ADR-0121 フォローアップ 6 の入力）: seed を入れた端点の応答が**期待値のファイルと一致する**。
    // 消費側の試験はこのファイルを入力にするので、ここが消費側の入力の正しさの担保である。
    // ［2026-09-28 / #1615］AST の KB の書き手のサービスアカウントも入れる（内容の ABAC の下で自分の写しを見つけられることの入力）。
    // ［2026-10-02 / #1696・IADR-0492］AST の KB の読み手のサービスアカウントも入れる（AST の文書の分岐の形は AstKbReaderPolicySeedTests）。
    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    [InlineData("service-account-ai-stock-trading-kb-writer")]
    [InlineData("service-account-ai-stock-trading-kb-reader")]
    public async Task Seedを入れた端点の応答は期待値のファイルと一致する(string userId)
    {
        var expected = SeedScopeFixture.ScopeOf(userId);

        var (status, actual) = await ResolveAsync(userId);

        status.Should().Be(HttpStatusCode.OK);
        SeedScopeFixture.Normalize(actual!).Should().Be(SeedScopeFixture.Normalize(expected),
            "seed を変えたなら Fixtures/owner-read-seed-scopes.json を実物の応答に合わせて直す（消費側の試験の入力である）");
    }

    // T-26（許可が広がらないこと）: 所有者の分岐は**本人の名前 1 つだけ**に束縛され、プレースホルダを残さない。
    // 条件の空な分岐（＝そのポリシーの範囲で全件許可）は 1 本も無い。
    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    [InlineData("service-account-ai-stock-trading-kb-writer")]
    [InlineData("service-account-ai-stock-trading-kb-reader")]
    public async Task 所有者の分岐は本人だけに束縛され全件許可の分岐は無い(string userId)
    {
        var (_, scope) = await ResolveAsync(userId);

        scope!.Branches.Should().NotBeNull();
        scope.Branches!.Single(b => b.Name == OwnerPolicyName).Filters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AttributeFilter("owner", [userId]));
        scope.Branches.Should().OnlyContain(b => b.Filters.Count > 0,
            "条件の空な分岐は全件許可として読まれる（BffScopeResolver・AbacPageFilter・検索）");
        scope.Branches.SelectMany(b => b.Filters).SelectMany(f => f.AllowedValues)
            .Should().NotContain(v => v.Contains("${", StringComparison.Ordinal), "束縛は評価器の中で済ませる");
    }

    // T-27（束縛の失敗）: **名簿に居ない名前は所有者の分岐を得ない。** 利用者の条件が空なので、
    // 評価へ進めば誰にでもマッチする —— 手前の名簿の引き当てが `granted=false` へ倒すことを固定する。
    //   - `anonymous`: 未認証の要求で BFF が送る身元（`BffScopeResolver`）
    //   - `system`: 取り込みで所有者を解決できなかった印（`DataSource.UnresolvedOwner`）・AST の古い写しの owner
    //   - 空文字: 名前の無い主体
    // 🔴 **器の `Unknown` に入れていない** —— 入れると「居ないことにした」試験になる。
    // 名簿（偽の IdP）が本当にその名前を持たないことで deny になるのを見る（本番は IdP の運用で同じ状態を保つ）。
    [Theory]
    [InlineData("anonymous")]
    [InlineData("system")]
    [InlineData("")]
    public async Task 名簿に居ない名前は所有者の分岐を得ない(string userId)
    {
        var (status, scope) = await ResolveAsync(userId);

        status.Should().Be(HttpStatusCode.OK, "「居ない」は応答である");
        scope!.Granted.Should().BeFalse();
        (scope.Branches ?? []).Should().BeEmpty();
        scope.AllowedFilters.Should().BeEmpty();
    }
}
