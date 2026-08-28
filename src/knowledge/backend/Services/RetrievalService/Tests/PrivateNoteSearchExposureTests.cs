using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search;

namespace RetrievalService.Tests;

// FR-21 受け入れ基準 ⑨, FR-19, FR-03, [[IADR-0283]] 決定 3 (#447):
//
// > 「横断検索に含める」が ON、「AI の入力に含める」が OFF の個人資料は、
// > **検索結果に現れるが RAG 回答のコンテキストには含まれない**
//
// 本テストが測るのは**前半**（検索結果に現れること）である。後半（RAG の文脈に入らないこと）は
// `AiAnalysisService.Tests` の `RagContextAiInputExclusionTests` が測る。
//
// 🔴 **本テストの役割は「検索側を絞らせないこと」である。** ⑨ は 2 つの経路にまたがる基準であり、
// 分離を検索側で実装すると**前半が静かに壊れる**（「検索にも出ない」になり、基準が半分だけ満たされる）。
// [[IADR-0283]] が B-2（Retrieval への要求属性）を採らなかった理由がこれであり、
// **その決定が守られていることを機械で見張る**のが本テストである。
public class PrivateNoteSearchExposureTests
{
    // 「横断検索に含める」ON・「AI の入力に含める」OFF の個人資料（⑨ の主語そのもの）。
    private static ChunkPayload AiOffPrivateNote() => Chunk("AI 入力 OFF の個人資料",
        (DocumentScopes.Key, DocumentScopes.PrivateNote),
        ("owner", "alice"),
        (ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted),
        (AiInputExposure.AttributeKey, AiInputExposure.Excluded));

    private static ChunkPayload AiOnPrivateNote() => Chunk("AI 入力 ON の個人資料",
        (DocumentScopes.Key, DocumentScopes.PrivateNote),
        ("owner", "alice"),
        (ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted),
        (AiInputExposure.AttributeKey, AiInputExposure.Included));

    private static ChunkPayload Chunk(string title, params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文", [0.1f, 0.2f], null,
            attrs.ToDictionary(a => a.Key, a => a.Value), []);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    // 所有者ベースの分岐（ADR-0036 read 規則の「所有者ベース」。IADR-0253 決定 1）。
    private static ScopeFilter OwnerBranch(string owner) =>
        new([], [new List<AttributeFilter> { new("owner", [owner]) }]);

    // FR-21 ⑨（前半）: **AI 入力 OFF の個人資料もベクトル検索に現れる。**
    [Fact]
    public async Task AI入力OFFの個人資料もベクトル検索の結果に現れる()
    {
        var store = StoreWith(AiOffPrivateNote(), AiOnPrivateNote());

        var results = await store.SearchAsync([0.1f, 0.2f], 10, OwnerBranch("alice"),
            TestContext.Current.CancellationToken);

        results.Select(r => r.DocumentTitle).Should()
            .BeEquivalentTo(["AI 入力 OFF の個人資料", "AI 入力 ON の個人資料"],
                "⑨ は「検索結果に現れる」ことを要求している——検索側で AI 入力を絞らない");
    }

    // 全文検索側でも同じ（片方の系統だけに絞りが入る事故を防ぐ）。
    [Fact]
    public async Task AI入力OFFの個人資料も全文検索の結果に現れる()
    {
        var store = StoreWith(AiOffPrivateNote());

        var results = await store.KeywordSearchAsync("個人資料", 10, OwnerBranch("alice"),
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle()
            .Which.Attributes.Should()
            .Contain(AiInputExposure.AttributeKey, AiInputExposure.Excluded);
    }

    // FR-03, FR-21 ⑨: ハイブリッド検索（本番経路の形）でも現れ、
    // **属性がそのまま応答へ載る**（RAG 側が判定に使える）。
    [Fact]
    public async Task ハイブリッド検索の応答にAI入力属性がそのまま載る()
    {
        var store = StoreWith(AiOffPrivateNote());
        var search = new HybridSearchService(store, new CountingEmbeddingService(),
            NullLogger<HybridSearchService>.Instance);

        var scope = new AccessScope([], GrantsAccess: true,
            [new AccessScopeBranch("所有者ベース", [new AttributeFilter("owner", ["alice"])])]);
        var results = await search.SearchAsync(
            new SearchRequest("個人資料", 10, null, scope), TestContext.Current.CancellationToken);

        results.Should().ContainSingle("検索側は AI 入力トグルで絞らない");
        results[0].Attributes.Should()
            .Contain(AiInputExposure.AttributeKey, AiInputExposure.Excluded,
                "判定に要る属性が応答へ載らないと、RAG 側は fail-closed で全件落とすしかなくなる");

        // 🔴 同じ属性から導かれる判定は「AI 入力に含めない」である ——
        // **検索は返し、RAG は落とす**という ⑨ の 2 つの向きが同じ 1 件で成り立っている。
        AiInputExposure.IsAllowed(results[0].Attributes).Should().BeFalse();
    }

    // 陽性対照（fail-closed の否定）: ABAC スコープの側では従来どおり絞られる。
    // 「検索側は何も絞らない」実装になっていないことを対で固定する。
    [Fact]
    public async Task 他者の個人資料は所有者ベースの分岐で従来どおり除かれる()
    {
        var store = StoreWith(AiOffPrivateNote(), Chunk("他者の個人資料",
            (DocumentScopes.Key, DocumentScopes.PrivateNote),
            ("owner", "bob"),
            (AiInputExposure.AttributeKey, AiInputExposure.Included)));

        var results = await store.SearchAsync([0.1f, 0.2f], 10, OwnerBranch("alice"),
            TestContext.Current.CancellationToken);

        results.Select(r => r.DocumentTitle).Should().Equal("AI 入力 OFF の個人資料");
    }
}
