using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-05, NFR-09, AST/FR-08, AST/FR-04, 計画 ADR-0085 決定 2（例外）, ADR-0034 決定 1,
// [[IADR-0416]], [[IADR-0492]] (#1696 / AST#1078):
// **AST の KB の読み手の許可スコープで検索すると、何が出るか**を固定する（裁定 案 B の受け入れ基準 1 の検索側）。
//
// 入力は、seed を入れた認可サービスの `/authz/scope` の応答の期待値
// （`AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`。認可サービスの試験が実物と突き合わせる）。
// 絞り込み（`ScopeNarrowing`）→ 分岐のフィルタ → 索引（`InMemoryVectorStore`）を、検索サービスと同じ順で通す。
//
// 🔴 **出るのは AST の文書（`project=ai-stock-trading`）だけである。** 基盤の internal 文書・他のプロジェクトの文書・
//   `project` を持たない文書は出ない。`project=ai-stock-trading` を名乗る他人の個人資料も出ない
//   （属性の分岐は個人資料を除く。`PrivateNoteVisibility`）。陽性対照（階段の利用者には基盤の internal 文書が出る）で
//   索引が壊れて空を返しているのではないことを示す。
[Trait("TestKind", "Unit")]
public class AstKbReaderSearchScopeTests
{
    private const string Reader = "service-account-ai-stock-trading-kb-reader";
    private const string AstProject = "ai-stock-trading";

    private static ChunkPayload Chunk(string title, params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文", [0.1f], null,
            attrs.ToDictionary(a => a.Key, a => a.Value), []);

    private static InMemoryVectorStore Store()
    {
        var store = new InMemoryVectorStore();
        foreach (var c in new[]
        {
            Chunk("AST の収集記事", ("project", AstProject), ("owner", "service-account-ai-stock-trading-kb-writer"), ("confidentiality", "internal")),
            Chunk("AST の古い写し", ("project", AstProject), ("owner", "system"), ("confidentiality", "internal")),
            Chunk("基盤の internal 文書", ("owner", "bob"), ("confidentiality", "internal")),
            Chunk("他のプロジェクトの文書", ("project", "apollo"), ("owner", "bob"), ("confidentiality", "internal")),
            Chunk("AST を名乗る個人資料", ("doc_scope", "private-note"), ("project", AstProject), ("owner", "alice"), ("confidentiality", "internal")),
        })
            store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    // AST の `HttpKnowledgeBaseSearch` が本文の Scope で送る主張（`project ∈ {ai-stock-trading}`・GrantsAccess=true）。
    private static AccessScope AstClaim() => new([new AttributeFilter("project", [AstProject])], true);

    // `HybridSearchService.BuildFilters` と同じ順（絞り込み → 全体 deny か → 分岐のフィルタ）。
    private static async Task<IReadOnlyList<string>> SearchAsAsync(string userId, AccessScope? requested)
    {
        var seed = SeedScope(userId);
        var effective = ScopeNarrowing.Apply(seed, requested);
        if (!effective.GrantsAccess) return [];
        effective.Branches.Should().NotBeNullOrEmpty("seed の応答は分岐を運ぶ");

        var results = await Store().SearchAsync([0.1f], 10,
            new ScopeFilter([], [.. effective.Branches!.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)]),
            TestContext.Current.CancellationToken);
        return [.. results.Select(r => r.DocumentTitle)];
    }

    // 受け入れ基準 1（検索側）: AST の主張つきの要求で、AST の文書だけが出る。
    [Fact]
    public async Task 読み手がASTの主張で検索するとASTの文書だけが出る()
        => (await SearchAsAsync(Reader, AstClaim())).Should().BeEquivalentTo(["AST の収集記事", "AST の古い写し"]);

    // 🔴 主張を送らなくても広がらない —— 狭めているのは主張ではなく、認可サービスが返した許可である。
    [Fact]
    public async Task 読み手は主張が無くてもASTの文書だけが出る()
        => (await SearchAsAsync(Reader, null)).Should().BeEquivalentTo(["AST の収集記事", "AST の古い写し"]);

    // 陽性対照: 同じ索引で、階段の利用者（clearance=internal）には基盤の internal 文書が出る（索引は空ではない）。
    [Fact]
    public async Task 陽性対照_階段の利用者には基盤のinternal文書が出る()
        => (await SearchAsAsync("bob", null)).Should().Contain("基盤の internal 文書");

    // 陰性対照: 書き手には AST の文書の分岐が無い（書き手は自分が所有する写しだけを所有者の分岐で読む。#1615）。
    [Fact]
    public async Task 書き手は自分が所有するASTの写しだけを読む()
        => (await SearchAsAsync("service-account-ai-stock-trading-kb-writer", AstClaim()))
            .Should().BeEquivalentTo(["AST の収集記事"]);

    private static AccessScopeResponse SeedScope(string userId)
    {
        const string relative = "src/platform/backend/Services/AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            return JsonNode.Parse(File.ReadAllText(path))!["subjects"]!.AsArray()
                .Single(s => (string)s!["userId"]! == userId)!["scope"]!
                .Deserialize<AccessScopeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        throw new FileNotFoundException($"リポジトリの {relative} が見つからない（走査の起点: {AppContext.BaseDirectory}）");
    }
}
