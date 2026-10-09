using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-19, FR-03, FR-04, FR-21 ⑨, ADR-0061 決定 3, [[IADR-0512]], [[IADR-0529]] (#1879):
// **露出の 3 属性を 3 つとも `excluded` にした組織文書は、横断検索にも RAG の候補にも返らない。**
//
// planning#784 の裁定: AST の承認待ちの報告書（ドラフト）は 3 つとも `excluded` の組織文書として保存する。
// 取り込み側はチャンクを作らない（`PrivateNoteIndexProductionTests`）。本ファイルは**出口の二層目**を測る ——
// 索引に古いチャンクが残っていても（切り替えの撤収の前に検索が走った等）、検索の出口が明示値で落とす。
//
// 🔴 陰性の主張には陽性対照を対で置く（同じ索引・同じスコープで露出キーだけを変える）。
[Trait("TestKind", "Unit")]
public class OrganizationDocumentExposureTests
{
    private const string Query = "報告書";
    private const string Draft = "全て除外の組織文書";
    private const string Included = "全て含めるの組織文書";
    private const string Plain = "露出キーの無い組織文書";

    private static ChunkPayload Organization(string title, bool? exposure)
    {
        var attrs = new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.Organization,
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Internal,
            ["owner"] = "alice",
        };
        if (exposure is { } on)
            foreach (var (k, v) in DocumentExposure.Project(on, on, on)) attrs[k] = v;
        return new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} は {Query} を含む本文", [0.1f, 0.2f],
            null, attrs, []);
    }

    private static InMemoryVectorStore Store()
    {
        var store = new InMemoryVectorStore();
        foreach (var c in new[] { Organization(Draft, false), Organization(Included, true), Organization(Plain, null) })
            store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    private static SearchRequest Request() =>
        new(Query, 10, null, new AccessScope([], GrantsAccess: true,
            [new AccessScopeBranch("所有者ベース", [new AttributeFilter("owner", ["alice"])])]));

    private static async Task<List<string>> TitlesAsync(SearchUserContext user) =>
        (await new HybridSearchService(Store(), new CountingEmbeddingService(), NullLogger<HybridSearchService>.Instance)
            .SearchAsync(Request(), user, TestContext.Current.CancellationToken))
        .Select(r => r.DocumentTitle).ToList();

    // 横断検索（REST の一覧・MCP の `retrieval.search_documents`）の用途。
    [Fact]
    public async Task 横断検索の用途は全て除外の組織文書を返さない()
    {
        var titles = await TitlesAsync(TestSearchUser.FromBody("alice"));

        titles.Should().NotContain(Draft, "3 つとも除外の組織文書は横断検索に出さない（planning#784）");
        titles.Should().Contain([Included, Plain], "陽性対照: 含めるの組織文書と露出キーの無い組織文書は出る");
    }

    // RAG の候補（AI の入力）の用途。AST の取引判断の知識検索もこの用途で引く。
    [Fact]
    public async Task AI入力の用途は全て除外の組織文書を返さない()
    {
        var titles = await TitlesAsync(TestSearchUser.FromBody("alice").ForAiInput());

        titles.Should().NotContain(Draft, "3 つとも除外の組織文書は AI の入力に含めない（planning#784）");
        titles.Should().Contain([Included, Plain], "陽性対照");
    }
}
