using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, FR-09, NFR-09, AST/FR-08, AST/FR-04, 計画 ADR-0085 決定 2（例外。ADR-0125 が部分改定）・決定 3, ADR-0080 決定 2,
// ADR-0088 決定 1, ADR-0125 決定 2, [[IADR-0420]], [[IADR-0492]], [[IADR-0500]] (#1696 / #1755 / AST#1078):
// **AST の KB の読み手が `/authz/scope` で何を得るか**を、実物の realm の宣言・実物の seed・実物の端点で固定する。
//
// 裁定（#1696・案 B）の受け入れ基準 1 の写像:
//   「AST の読み手のトークンで `/authz/scope` を引くと `Granted=true` になり、AST の文書だけを読める範囲が返る」
//
// 🔴 **主体の属性は realm の宣言から読む**（書き写さない）。`ScopeUserAttributeSource` は IdP から属性を引き直すので
//   （`ADR-0088` 決定 1）、本番の主体の属性は realm の宣言（とそれを当てる reconcile）が決める。
//   ここで手書きの属性を与えると、realm から属性が消えても試験だけが緑で残る。
// 🔴 **AST の文書だけ**とは「束縛されない分岐（＝誰の文書でも一致し得る分岐）が `project ∈ {ai-stock-trading}` の
//   1 本だけで、他は自分の名前に束縛された所有者・共有先の分岐」という意味である。
//   読み手は文書を作らない（ロールを持たない）ので、束縛された分岐は実際には何にも一致しない。
[Trait("TestKind", "Integration")]
public class AstKbReaderPolicySeedTests : IClassFixture<TestWebApplicationFactory>
{
    private const string ReaderClientId = "ai-stock-trading-kb-reader";
    private const string Reader = "service-account-" + ReaderClientId;
    private const string Writer = "service-account-ai-stock-trading-kb-writer";
    private const string AstPolicyName = "dev: AST の KB の読み手は AST の文書を読める";
    private const string AstProject = "ai-stock-trading";

    // FR-05, 計画 ADR-0125 決定 2, [[IADR-0500]] (#1755): 読み手の枝の機密区分の上限。
    private static readonly string[] ReaderCeiling = ["public", "internal"];

    private readonly TestWebApplicationFactory _factory;

    public AstKbReaderPolicySeedTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        LoadSeedIntoDatabase();
        foreach (var subject in SeedScopeFixture.Subjects())
        {
            _factory.Identity.Attributes[subject.UserId] = subject.Attributes;
            _factory.Identity.Groups[subject.UserId] = [];
        }
        // 読み手だけは realm の宣言から入れ直す（上の fixture の値と同じであることは T-1 が見る）。
        _factory.Identity.Attributes[Reader] = RealmReaderAttributes();
    }

    private void LoadSeedIntoDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        if (db.Policies.Any()) return;
        foreach (var p in SeedScopeFixture.SeedPolicies())
            db.Policies.Add(AbacPolicy.Create(p.Name, p.Action, p.UserConditions, p.DocumentConditions));
        db.SaveChanges();
    }

    private async Task<AccessScopeResponse> ResolveAsync(string userId, string action = PolicyAction.Read)
    {
        var resp = await _factory.CreateServiceCallerClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(userId, [], action), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(TestContext.Current.CancellationToken))!;
    }

    // 束縛されない分岐（自分の名前を値に持たない分岐）。読み手にとっては「誰の文書でも一致し得る」分岐である。
    private static List<AccessScopeBranch> UnboundBranches(AccessScopeResponse scope, string self)
        => [.. (scope.Branches ?? []).Where(b => !b.Filters.All(
            f => f.Key is "owner" or "shared_with" && f.AllowedValues.All(v => v == self)))];

    // T-1（主体の宣言）: realm が宣言する読み手の属性は `projects = ai-stock-trading` だけで、`clearance` を持たない。
    // fixture（消費側の試験の入力）の属性と一致する。
    [Fact]
    public void Realmの読み手の属性はprojectsだけでfixtureと一致する()
    {
        var declared = RealmReaderAttributes();

        declared.Should().Equal(new Dictionary<string, string> { ["projects"] = AstProject },
            "裁定（案 B）: clearance=internal は与えない（与えると基盤全体の internal が読める）");
        SeedScopeFixture.Subjects().Single(s => s.UserId == Reader).Attributes.Should().Equal(declared,
            "Fixtures/owner-read-seed-scopes.json の読み手の属性は realm の宣言と同じでなければならない");
    }

    // T-2（受け入れ基準 1）: Granted=true で、束縛されない分岐は AST の文書の 1 本だけ。階段（clearance の段）には 1 つもマッチしない。
    // ［2026-10-06 / #1755・IADR-0500］その 1 本は `project ∈ {ai-stock-trading}` ∧ `confidentiality ∈ {public, internal}` の連言である
    // （計画 ADR-0125 決定 2）。confidential・restricted は、ラベルが付いていてもこの枝では届かない。
    [Fact]
    public async Task 読み手はGrantedでASTの文書だけの範囲を得る()
    {
        var scope = await ResolveAsync(Reader);

        scope.Granted.Should().BeTrue("読み手は AST の文書を読める（受け入れ基準 1）");
        var unbound = UnboundBranches(scope, Reader);
        unbound.Should().ContainSingle("束縛されない分岐は AST の文書の 1 本だけ（他の文書へ届かない）");
        unbound[0].Name.Should().Be(AstPolicyName);
        unbound[0].Filters.Should().BeEquivalentTo(
            [new AttributeFilter("project", [AstProject]), new AttributeFilter("confidentiality", [.. ReaderCeiling])],
            "文書の条件は project と機密区分の上限の 2 つだけ（ADR-0125 決定 2）");
        (scope.Branches ?? []).Should().NotContain(b => b.Filters.All(f => f.Key == "confidentiality"),
            "clearance の階段（機密区分だけで絞る段）にマッチしてはならない（裁定が退けた案 A）");
    }

    // T-2b（#1755・ADR-0125 決定 2）: 読み手の束縛されない分岐の機密区分は public・internal だけで、confidential・restricted を含まない。
    // 束縛されない分岐のどれも confidentiality を欠かない（欠けると区分を問わず届く）。
    [Fact]
    public async Task 読み手の束縛されない分岐はconfidentialとrestrictedを許さない()
    {
        var scope = await ResolveAsync(Reader);

        var unbound = UnboundBranches(scope, Reader);
        unbound.Should().NotBeEmpty();
        foreach (var branch in unbound)
        {
            var ceiling = branch.Filters.Where(f => f.Key == "confidentiality").ToList();
            ceiling.Should().ContainSingle($"分岐 {branch.Name} は機密区分の上限を持たなければならない");
            ceiling[0].AllowedValues.Should().BeEquivalentTo(ReaderCeiling);
            ceiling[0].AllowedValues.Should().NotContain(["confidential", "restricted"]);
        }
    }

    // T-3（陰性対照）: realm の属性が無ければ AST の文書の分岐は立たない —— 分岐を立てているのは宣言の属性である。
    [Fact]
    public async Task 属性が無ければASTの文書の分岐は立たない()
    {
        _factory.Identity.Attributes[Reader] = [];
        try
        {
            var scope = await ResolveAsync(Reader);

            (scope.Branches ?? []).Should().NotContain(b => b.Name == AstPolicyName);
            UnboundBranches(scope, Reader).Should().BeEmpty();
        }
        finally
        {
            _factory.Identity.Attributes[Reader] = RealmReaderAttributes();
        }
    }

    // T-4（読み手は書けない・分析も依頼できない）: write の範囲は自分の名前に束縛された所有者の分岐だけ
    // （読み手は文書を作れないので何にも一致しない）。analyze は Granted=false。
    [Fact]
    public async Task 読み手のwriteは自分の所有だけでanalyzeは拒否される()
    {
        var write = await ResolveAsync(Reader, PolicyAction.Write);
        UnboundBranches(write, Reader).Should().BeEmpty("読み手に AST の文書を書かせる分岐は無い");
        write.AllowedFilters.Should().NotContain(f => f.Key == "project");

        var analyze = await ResolveAsync(Reader, PolicyAction.Analyze);
        analyze.Granted.Should().BeFalse("読み手に分析の依頼を許すポリシーは無い");
    }

    // T-5（他の主体の範囲は変わらない）: AST の文書の分岐は読み手だけに立つ。書き手・人の利用者には立たない
    // （書き手は自分の写しを所有者の分岐で読む。#1615）。
    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    [InlineData(Writer)]
    public async Task ASTの文書の分岐は読み手以外に立たない(string userId)
    {
        var scope = await ResolveAsync(userId);

        (scope.Branches ?? []).Should().NotContain(b => b.Name == AstPolicyName);
        scope.AllowedFilters.Should().NotContain(f => f.Key == "project");
    }

    // T-6（seed の形）: projects / project を条件に持つポリシーは read の 1 本だけ。利用者の条件は 1 キー 1 値。
    // ［2026-10-06 / #1755・IADR-0500］文書の条件は project（1 値）と confidentiality（public・internal）の 2 キーである。
    [Fact]
    public void Seedのprojectを持つポリシーはreadの1本だけ()
    {
        var withProject = SeedScopeFixture.SeedPolicies()
            .Where(p => p.UserConditions.ContainsKey("projects") || p.DocumentConditions.ContainsKey("project"))
            .ToList();

        var only = withProject.Should().ContainSingle("ADR-0085 決定 2 の例外は読み手の 1 本に限る").Which;
        only.Name.Should().Be(AstPolicyName);
        only.Action.Should().Be(PolicyAction.Read, "読み手は書けない");
        only.UserConditions.Should().ContainSingle().Which.Value.Should().Equal([AstProject]);
        only.UserConditions.Should().ContainKey("projects");
        only.DocumentConditions.Keys.Should().BeEquivalentTo(["project", "confidentiality"],
            "文書の条件は project と機密区分の上限の 2 つだけ（ADR-0125 決定 2）");
        only.DocumentConditions["project"].Should().Equal([AstProject]);
        only.DocumentConditions["confidentiality"].Should().BeEquivalentTo(ReaderCeiling);
    }

    // realm（deploy/keycloak/microservices-platform-realm.json）の読み手のサービスアカウントの属性を、
    // `KeycloakIdentityAdminClient` と同じ規則（集合値キーは連結・単一値キーは先頭）で 1 キー 1 値へ畳む。
    private static Dictionary<string, string> RealmReaderAttributes()
    {
        var realm = JsonNode.Parse(File.ReadAllText(
            SeedScopeFixture.RepoFile("deploy/keycloak/microservices-platform-realm.json")))!;
        var user = realm["users"]!.AsArray()
            .Single(u => (string?)u!["serviceAccountClientId"] == ReaderClientId)!;
        ((string)user["username"]!).Should().Be(Reader);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in user["attributes"]?.AsObject() ?? [])
        {
            var values = node!.AsArray().Select(v => (string?)v).ToList();
            var value = UserAttributeEncoding.IsSetValued(key)
                ? UserAttributeEncoding.Join(values)
                : values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
            if (value.Length > 0) result[key] = value;
        }
        return result;
    }
}
