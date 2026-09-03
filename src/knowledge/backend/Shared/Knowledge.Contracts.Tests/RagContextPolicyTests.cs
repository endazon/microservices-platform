using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-21 受け入れ基準 ⑨, FR-19: 検索結果の集合と RAG 回答のコンテキストの集合が**別物である**
// ことを固定する。計画は「⑨＝検索結果をそのまま LLM へ渡す構造では分離できない」と名指ししており、
// ここで固定するのはその**構造**である（どのチャンクを外すかの判定条件は FR-19 が定める）。
public class RagContextPolicyTests
{
    private static SearchResultDto Result(Guid chunkId, string owner, bool aiAllowed) => new(
        chunkId, Guid.NewGuid(), "文書", "本文", 1.0f, "storage://b/k",
        new Dictionary<string, string> { ["owner"] = owner, ["ai"] = aiAllowed ? "on" : "off" },
        []);

    // 本テストの述語は**仮のもの**である。実際のトグルの属性キーは FR-19 の実装が定める。
    private static bool AiAllowed(IReadOnlyDictionary<string, string> attributes) =>
        attributes.TryGetValue("ai", out var v) && v == "on";

    // FR-21 ⑨: 「横断検索に含める」ON・「AI の入力に含める」OFF のチャンクは、
    // **検索結果に現れるが RAG のコンテキストには含まれない。**
    [Fact]
    public void AI入力から外したチャンクは検索結果に残りコンテキストから落ちる()
    {
        var excludedId = Guid.NewGuid();
        var keptId = Guid.NewGuid();
        var results = new List<SearchResultDto>
        {
            Result(keptId, "alice", aiAllowed: true),
            Result(excludedId, "alice", aiAllowed: false),
        };

        var selection = RagContextPolicy.Select(results, AiAllowed);

        selection.SearchResults.Should().HaveCount(2);
        selection.SearchResults.Select(r => r.ChunkId).Should().Contain(excludedId);
        selection.ContextChunks.Should().ContainSingle().Which.ChunkId.Should().Be(keptId);
        selection.ExcludedFromContextChunkIds.Should().ContainSingle().Which.Should().Be(excludedId);
    }

    // FR-21 ⑨（陽性対照）: 「全部落ちる」実装でないこと —— すべて許可なら 2 つの集合は一致する。
    [Fact]
    public void すべて許可ならコンテキストは検索結果と一致する()
    {
        var results = new List<SearchResultDto>
        {
            Result(Guid.NewGuid(), "alice", aiAllowed: true),
            Result(Guid.NewGuid(), "bob", aiAllowed: true),
        };

        var selection = RagContextPolicy.Select(results, AiAllowed);

        selection.ContextChunks.Should().HaveCount(2);
        selection.ExcludedFromContextChunkIds.Should().BeEmpty();
    }

    // FR-21 ⑨: コンテキストは検索結果の**部分集合**である（別の集合を混ぜて返さない）。
    [Fact]
    public void コンテキストは検索結果の部分集合である()
    {
        var results = new List<SearchResultDto>
        {
            Result(Guid.NewGuid(), "alice", aiAllowed: true),
            Result(Guid.NewGuid(), "alice", aiAllowed: false),
        };

        var selection = RagContextPolicy.Select(results, AiAllowed);

        selection.ContextChunks.Should().BeSubsetOf(selection.SearchResults);
    }

    // FR-02, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 5:
    // **本文を持たない文書は検索結果に残り、コンテキストからは落ちる。**
    // 根拠に使える本文が無い以上、出典にも文脈にもならない —— 入れると `[n] タイトル` だけの
    // 空の根拠が LLM へ渡り、中身を知らない文書について答えることになる。
    [Fact]
    public void 本文なしの文書は検索結果に残りコンテキストから落ちる()
    {
        var bodylessId = Guid.NewGuid();
        var withBodyId = Guid.NewGuid();
        var results = new List<SearchResultDto>
        {
            Result(withBodyId, "alice", aiAllowed: true),
            // 本文なし: 索引はメタデータだけで、抜粋（Text）は空になっている。
            Result(bodylessId, "alice", aiAllowed: true) with { Text = "", HasBody = false },
        };

        var selection = RagContextPolicy.Select(results, AiAllowed);

        selection.SearchResults.Select(r => r.ChunkId).Should().Contain(bodylessId,
            "ADR-0070 決定 4 は「結果から除外しない」と定める");
        selection.ContextChunks.Should().ContainSingle().Which.ChunkId.Should().Be(withBodyId);
        selection.ExcludedFromContextChunkIds.Should().ContainSingle().Which.Should().Be(bodylessId);
    }

    // FR-21 ⑨: **既定の述語を持たない。** 判定を渡し忘れた呼び出しが黙って「全件許可」へ倒れると
    // ⑨ が静かに破れるため、null は例外にする。
    [Fact]
    public void 判定を渡さない呼び出しは例外になる()
    {
        var results = new List<SearchResultDto>();
        var act = () => RagContextPolicy.Select(results, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
