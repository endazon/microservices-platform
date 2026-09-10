using AwesomeAssertions;
using GraphService.Domain.Clustering;

namespace GraphService.Tests.Domain.Clustering;

// FR-10, FR-17, FR-18, SC-10, ADR-0035 決定 6, ADR-0083 決定 2・3, [[IADR-0425]] (#1363):
// 「未要約」の判定。計画 ADR-0083 決定 3 の 3 条件を 1 条件 1 テストで固定する。
//
// 🔴 **本ファイルの中心は T-15（陰性対照）である。** これが無いと、
// 「常に未要約を返す実装」でも条件 1〜3 のテストが全部緑になる。
public sealed class UnsummarizedClusterRuleTests
{
    private static readonly DateTimeOffset Generated = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    // FR-18, ADR-0083 決定 3 条件 1 (T-11): 要約が 1 つも無ければ未要約である。
    [Fact]
    public void 要約が一つも無いクラスタは未要約である()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), Generated.AddDays(-10), Summaries());

        reason.Should().Be(UnsummarizedClusterRule.NoSummary);
    }

    // 🔴 FR-18, ADR-0083 決定 2 (T-12): **機密区分 4 通りのうち 1 つでも欠ければ未要約**である。
    // 「あるクラスタを作り直すときは 4 通りすべてを作り直す」（ADR-0035 決定 6）と揃える。
    [Theory]
    [InlineData(ClusterConfidentiality.Public)]
    [InlineData(ClusterConfidentiality.Internal)]
    [InlineData(ClusterConfidentiality.Confidential)]
    [InlineData(ClusterConfidentiality.Restricted)]
    public void 機密区分が一つでも欠ければ未要約である(string missing)
    {
        var present = ClusterConfidentiality.All.Where(c => c != missing).ToArray();

        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), Generated.AddDays(-10), Summaries(present));

        reason.Should().Be(UnsummarizedClusterRule.NoSummary,
            "4 通りのうち 1 つでも欠ければそのクラスタは未要約である（ADR-0083 決定 2）");
    }

    // FR-18, ADR-0083 決定 3 条件 2 (T-13): 構成変更が最終生成より後なら未要約である。
    [Fact]
    public void 構成変更が最終生成より後なら未要約である()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(1), Generated.AddDays(-10), AllSummaries());

        reason.Should().Be(UnsummarizedClusterRule.CompositionChanged);
    }

    // FR-18, ADR-0083 決定 3 条件 3 (T-14): 所属文書の更新が最終生成より後なら未要約である。
    [Fact]
    public void 所属文書の更新が最終生成より後なら未要約である()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), Generated.AddDays(1), AllSummaries());

        reason.Should().Be(UnsummarizedClusterRule.DocumentsUpdated);
    }

    // 🔴 FR-18 (T-15): **陰性対照。** 4 通り揃い・構成変更も文書更新も生成より前なら、
    // そのクラスタは**未要約に数えない**。
    [Fact]
    public void 四通り揃い構成も文書も生成より前なら未要約ではない()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), Generated.AddDays(-10), AllSummaries());

        reason.Should().BeNull(
            "🔴 これが無いと「常に未要約」の実装でも条件 1〜3 のテストが全部緑になる");
    }

    // FR-18 (T-15b): **境界。** 生成時刻ちょうどは「後」ではない（`>` であって `>=` ではない）。
    [Fact]
    public void 生成時刻ちょうどの更新は未要約に数えない()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated, Generated, AllSummaries());

        reason.Should().BeNull("同時刻を未要約に数えると、生成直後のクラスタが即座に未要約になる");
    }

    // FR-18 (T-15c): 4 通りのうち**最も古い生成時刻**で判定する
    //（「1 つでも古ければ未要約」と同値。ADR-0083 決定 2）。
    [Fact]
    public void 四通りのうち最も古い生成時刻で判定する()
    {
        var summaries = new Dictionary<string, DateTimeOffset>
        {
            [ClusterConfidentiality.Public] = Generated.AddDays(-30),
            [ClusterConfidentiality.Internal] = Generated,
            [ClusterConfidentiality.Confidential] = Generated,
            [ClusterConfidentiality.Restricted] = Generated,
        };

        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), Generated.AddDays(-40), summaries);

        reason.Should().Be(UnsummarizedClusterRule.CompositionChanged,
            "public だけ 30 日古いので、その 1 通に対しては構成変更の方が新しい");
    }

    // FR-18 (T-15d): 複数条件に当たるときは 1 → 2 → 3 の順で先勝ちである（内訳は 1 クラスタ 1 軸）。
    [Fact]
    public void 複数条件に当たるときは要約無しが優先される()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(1), Generated.AddDays(1), Summaries(ClusterConfidentiality.Public));

        reason.Should().Be(UnsummarizedClusterRule.NoSummary);
    }

    // FR-18 (T-15e): 所属文書が 1 件も無ければ条件 3 は成立しない（null は「更新が無い」）。
    [Fact]
    public void 所属文書の更新時刻が無ければ条件三は成立しない()
    {
        var reason = UnsummarizedClusterRule.Evaluate(
            Generated.AddDays(-10), null, AllSummaries());

        reason.Should().BeNull();
    }

    private static Dictionary<string, DateTimeOffset> Summaries(params string[] confidentialities)
        => confidentialities.ToDictionary(c => c, _ => Generated);

    private static Dictionary<string, DateTimeOffset> AllSummaries()
        => ClusterConfidentiality.All.ToDictionary(c => c, _ => Generated);
}
