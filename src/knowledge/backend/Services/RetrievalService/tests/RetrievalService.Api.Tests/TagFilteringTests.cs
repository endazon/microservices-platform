using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Composable.Adapters;
using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Tests;

// FR-04, FR-05, SC-01, SC-08, #539: **タグで絞れること**を固定する。
//
// **これが直るまで、候補と絞り込みが食い違っていた。**
// #540 が入れた値集合の照会は `AttributeValueKeys.ToPayloadKey` を通じて `tags` を知っていたのに、
// **絞り込み側は `attributes.{key}` をハードコードしており `tags` を絞れなかった**（着手時の実測）。
// 画面は候補として出したタグで絞れると期待するので、**食い違うと利用者から見て壊れている**。
//
// **写像を 1 つの関数（`ToPayloadKey`）へ寄せたことを、ここで両側から確かめる。**
public class TagFilteringTests
{
    private static ChunkPayload Chunk(string title, Dictionary<string, string> attributes, List<string> tags) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文", [0.1f], null, attributes, tags);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    private static Dictionary<string, string> Dept(string value) => new() { ["department"] = value };

    // 受け入れ基準 4: **`tags` で絞れる。**
    [Fact]
    public async Task Filter_ByTag_KeepsOnlyChunksCarryingIt()
    {
        var store = StoreWith(
            Chunk("経理の文書", Dept("finance"), ["経理", "規程"]),
            Chunk("営業の文書", Dept("sales"), ["営業"]));

        var results = await store.SearchAsync([0.1f], 10,
            [new AttributeFilter(AttributeValueKeys.Tags, ["経理"])], TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("経理の文書");
    }

    // **タグはリストなので「いずれかが一致」で真になる**（属性の単一値の完全一致とは違う）。
    [Fact]
    public async Task Filter_ByTag_MatchesAnyElementOfTheList()
    {
        var store = StoreWith(Chunk("複数タグ", Dept("finance"), ["経理", "規程", "年次"]));

        var results = await store.SearchAsync([0.1f], 10,
            [new AttributeFilter(AttributeValueKeys.Tags, ["規程"])], TestContext.Current.CancellationToken);

        results.Should().ContainSingle("配列の 2 番目の要素でも一致する");
    }

    // 値集合内は OR（複数のチップを選んだら、どれかを持つ文書が残る）。
    [Fact]
    public async Task Filter_ByTag_MultipleValuesAreOr()
    {
        var store = StoreWith(
            Chunk("経理", Dept("finance"), ["経理"]),
            Chunk("営業", Dept("sales"), ["営業"]),
            Chunk("無関係", Dept("hr"), ["採用"]));

        var results = await store.SearchAsync([0.1f], 10,
            [new AttributeFilter(AttributeValueKeys.Tags, ["経理", "営業"])], TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
    }

    // 受け入れ基準 5（回帰）: 属性経路は従来どおり効く。**タグ対応で壊していない。**
    [Fact]
    public async Task Filter_ByAttribute_StillWorks()
    {
        var store = StoreWith(
            Chunk("経理", Dept("finance"), ["経理"]),
            Chunk("営業", Dept("sales"), ["営業"]));

        var results = await store.SearchAsync([0.1f], 10,
            [new AttributeFilter("department", ["finance"])], TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("経理");
    }

    // フィルタ間は AND（タグと属性を両方指定したら、両方を満たす文書だけが残る）。
    [Fact]
    public async Task Filter_TagAndAttribute_AreCombinedWithAnd()
    {
        var store = StoreWith(
            Chunk("両方満たす", Dept("finance"), ["経理"]),
            Chunk("タグだけ", Dept("sales"), ["経理"]),
            Chunk("属性だけ", Dept("finance"), ["営業"]));

        var results = await store.SearchAsync([0.1f], 10,
        [
            new AttributeFilter(AttributeValueKeys.Tags, ["経理"]),
            new AttributeFilter("department", ["finance"]),
        ], TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("両方満たす");
    }

    // **タグを持たない文書は、タグ指定で残らない**（属性辞書に `tags` キーが無いことを
    // 「一致」と取り違えない）。
    [Fact]
    public async Task Filter_ByTag_ExcludesChunksWithoutTags()
    {
        var store = StoreWith(Chunk("タグ無し", Dept("finance"), []));

        var results = await store.SearchAsync([0.1f], 10,
            [new AttributeFilter(AttributeValueKeys.Tags, ["経理"])], TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    // 全文検索の経路にも同じフィルタが効く（ABAC は両系統に掛かる）。
    [Fact]
    public async Task KeywordSearch_AppliesTagFilterToo()
    {
        var store = StoreWith(
            Chunk("経理の文書", Dept("finance"), ["経理"]),
            Chunk("営業の文書", Dept("sales"), ["営業"]));

        var results = await store.KeywordSearchAsync("文書", 10,
            [new AttributeFilter(AttributeValueKeys.Tags, ["経理"])], TestContext.Current.CancellationToken);

        results.Should().OnlyContain(r => r.DocumentTitle == "経理の文書");
    }

    // ── Qdrant 側: 実機なしで固定できるのは**キーの写像**である ────────────────
    //
    // **受け入れ基準 8**: 候補に出る値（`ListAttributeValuesAsync` が `ToPayloadKey` で引く）と、
    // 絞れる値（ここ）が**同じ関数**を通ること。

    [Fact]
    public void QdrantConditions_MapTagsToListField_AndAttributesToNestedPath()
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions(
        [
            new AttributeFilter(AttributeValueKeys.Tags, ["経理"]),
            new AttributeFilter("department", ["finance"]),
        ]);

        conditions.Select(c => c.Field.Key).Should().Equal("tags", "attributes.department");
    }

    // `Tags` / `TAGS` と書いても同じ口へ寄る（`tags` は**本コードが所有するリテラル**なので畳んでよい）。
    // **属性キーは畳まない**——投入時に書いたキーがそのままペイロードのキーになるため、
    // 畳むと書き込み時の `department` と一致せず**黙って空集合が返る**（`ToPayloadKey` の設計）。
    [Theory]
    [InlineData("tags")]
    [InlineData("Tags")]
    [InlineData("TAGS")]
    public void QdrantConditions_TagKeyIsCaseInsensitive(string key)
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions([new AttributeFilter(key, ["経理"])]);

        conditions.Should().ContainSingle().Which.Field.Key.Should().Be("tags");
    }

    [Fact]
    public void QdrantConditions_AttributeKeyCaseIsPreserved()
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions(
            [new AttributeFilter("Department", ["finance"])]);

        conditions.Should().ContainSingle().Which.Field.Key.Should().Be("attributes.Department",
            "属性キーは投入時の綴りのままペイロードのキーになる（畳むと黙って空集合になる）");
    }

    // 値集合が空のフィルタは条件を作らない（「絞らない」と「何にも一致しない」を取り違えない）。
    [Fact]
    public void QdrantConditions_EmptyAllowedValuesProduceNoCondition()
    {
        QdrantVectorStore.BuildAttributeConditions([new AttributeFilter(AttributeValueKeys.Tags, [])])
            .Should().BeEmpty();
    }
}
