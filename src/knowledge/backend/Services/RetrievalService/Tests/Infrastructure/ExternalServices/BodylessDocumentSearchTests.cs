using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-02, FR-03, FR-05, SC-02, ADR-0070 決定 4, #1193, [[IADR-0354]]:
// **本文を持たない文書（メタデータだけで索引した点）が検索に現れ、抜粋は空で返る**ことを固定する。
//
// ADR-0070 決定 4: 「タイトル・パス・データソース・更新日時などのメタデータで FR-03 の検索に載せる」
// 「SC-02 の検索結果では本文抜粋が出せないため『本文なし（原本を参照）』である旨を示す。**結果から除外しない**」
public class BodylessDocumentSearchTests
{
    // 本文なしの点: `Text` は**索引テキスト**（題名・タグ由来）であり、本文ではない。
    private static ChunkPayload MetadataOnly(string title, Dictionary<string, string> attributes) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, title, [0.1f], "storage://b/k",
            attributes, [], null, HasBody: false);

    private static ChunkPayload WithBody(string title, Dictionary<string, string> attributes) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文である", [0.1f], "storage://b/k",
            attributes, [], null);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    private static Dictionary<string, string> Dept(string value) => new() { ["department"] = value };

    // 受け入れ基準: 本文なしの文書が**その題名で全文検索に現れる**（除外されない）。
    [Fact]
    public async Task 本文なしの文書は題名で検索に現れる()
    {
        var store = StoreWith(MetadataOnly("スキャン版 就業規則", Dept("hr")));

        var results = await store.KeywordSearchAsync("就業規則", 10, null,
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("スキャン版 就業規則");
    }

    // 🔴 **索引テキストは本文抜粋として返さない。** `HasBody = false` で `Text` は空になる
    // （変異試験の対象は `DocumentBodyPresence.Excerpt`。恒等関数へ変異させるとここが落ちる）。
    [Fact]
    public async Task 本文なしの結果は抜粋が空で本文なしの印を持つ()
    {
        var store = StoreWith(MetadataOnly("スキャン版 就業規則", Dept("hr")));

        var result = (await store.KeywordSearchAsync("就業規則", 10, null,
            TestContext.Current.CancellationToken)).Single();

        result.HasBody.Should().BeFalse();
        result.Text.Should().BeEmpty("索引に載せたメタデータを本文の抜粋として外へ出さない");
    }

    // **陽性対照**: 本文ありは従来どおり抜粋が返り、`HasBody` は真である
    // （「全件を本文なしにする」実装では上のテストだけでは緑になる）。
    [Fact]
    public async Task 本文ありは従来どおり抜粋を返す()
    {
        var store = StoreWith(WithBody("経費精算規程", Dept("finance")));

        var result = (await store.KeywordSearchAsync("経費精算規程", 10, null,
            TestContext.Current.CancellationToken)).Single();

        result.HasBody.Should().BeTrue();
        result.Text.Should().Be("経費精算規程 の本文である");
    }

    // FR-05: **ABAC は本文の有無に関わらず効く。** 本文が無いことを理由に権限判定を緩めない。
    [Fact]
    public async Task 本文なしの文書にも同じABACフィルタが効く()
    {
        var store = StoreWith(
            MetadataOnly("人事のスキャン文書", Dept("hr")),
            MetadataOnly("営業のスキャン文書", Dept("sales")));

        var results = await store.SearchAsync([0.1f], 10,
            new ScopeFilter([new AttributeFilter("department", ["sales"])]),
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("営業のスキャン文書");
    }

    // 意味検索（ベクトル側）でも同じ射影を通る（系統ごとに扱いが割れない）。
    [Fact]
    public async Task 意味検索でも抜粋は空になる()
    {
        var store = StoreWith(MetadataOnly("スキャン版 就業規則", Dept("hr")));

        var result = (await store.SearchAsync([0.1f], 10, null,
            TestContext.Current.CancellationToken)).Single();

        result.HasBody.Should().BeFalse();
        result.Text.Should().BeEmpty();
    }
}
