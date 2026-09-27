using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 1・フォローアップ 6, ADR-0036 D-01・D-08, [[IADR-0253]] (#1664):
// **dev seed を入れた構成で、所有者の分岐が検索のスコープへ何を足すか**を固定する。
//
// 入力は、seed を入れた認可サービスの `/authz/scope` の応答の期待値
// （`AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`。認可サービスの試験が実物と突き合わせる）。
// 絞り込み（`ScopeNarrowing`）→ 分岐のフィルタ → 索引（`InMemoryVectorStore`）を、検索サービスと同じ順で通す。
//
// 🔴 **広がるのは「自分の文書」だけであること**を陰性対照で見る（他人・予約値 `system` の文書は出ない）。
[Trait("TestKind", "Unit")]
public class OwnerReadSeedSearchScopeTests
{
    private static ChunkPayload Chunk(string title, params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文", [0.1f], null,
            attrs.ToDictionary(a => a.Key, a => a.Value), []);

    private static InMemoryVectorStore Store()
    {
        var store = new InMemoryVectorStore();
        foreach (var c in new[]
        {
            Chunk("alice の組織文書", ("owner", "alice"), ("confidentiality", "restricted")),
            Chunk("bob の組織文書", ("owner", "bob"), ("confidentiality", "restricted")),
            Chunk("system の写し", ("owner", "system"), ("confidentiality", "internal")),
            Chunk("alice の個人資料", ("doc_scope", "private-note"), ("owner", "alice"), ("confidentiality", "restricted")),
            Chunk("bob の個人資料", ("doc_scope", "private-note"), ("owner", "bob"), ("confidentiality", "restricted")),
        })
            store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    // `HybridSearchService.BuildFilters` と同じ順（絞り込み → 全体 deny か → 分岐のフィルタ）。
    private static async Task<IReadOnlyList<string>> SearchAsAsync(
        string userId, Dictionary<string, string>? requested = null)
    {
        var seed = SeedScope(userId);
        var effective = ScopeNarrowing.Apply(
            new AccessScope(seed.AllowedFilters, seed.Granted, seed.Branches), requested);
        if (!effective.GrantsAccess) return [];
        effective.Branches.Should().NotBeNullOrEmpty("seed の応答は分岐を運ぶ");

        var results = await Store().SearchAsync([0.1f], 10,
            new ScopeFilter([], [.. effective.Branches!.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)]),
            TestContext.Current.CancellationToken);
        return [.. results.Select(r => r.DocumentTitle)];
    }

    // T-36（陽性）: 属性を持たない利用者には、自分の文書（組織文書・個人資料）だけが出る。
    // 陰性対照: 他人の文書・予約値 `system` の写しは出ない。
    [Fact]
    public async Task 属性を持たない利用者には自分の文書だけが出る()
        => (await SearchAsAsync("alice")).Should().BeEquivalentTo(["alice の組織文書", "alice の個人資料"]);

    // T-36（陽性対照・階段の利用者）: 階段で読める区分の文書に加えて、自分の文書が区分に関わらず出る。
    // 他人の個人資料・他人の restricted の文書は出ない。
    [Fact]
    public async Task 階段の利用者には区分で読める文書と自分の文書が出る()
        => (await SearchAsAsync("bob")).Should().BeEquivalentTo(["bob の組織文書", "system の写し", "bob の個人資料"]);

    // T-37（利用者の絞り込みで広がらない）: `owner` を他人に絞った要求でも、他人の文書は出ない
    // （所有者の分岐は交差が空で落ちる。静的の分岐を持たない利用者は何も得ない）。
    [Theory]
    [InlineData("bob")]
    [InlineData("system")]
    public async Task ownerを他人に絞っても他人の文書は出ない(string other)
        => (await SearchAsAsync("alice", new() { ["owner"] = other })).Should().BeEmpty();

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
