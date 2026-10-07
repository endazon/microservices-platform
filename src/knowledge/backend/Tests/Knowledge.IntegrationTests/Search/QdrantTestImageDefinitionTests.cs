using System.Text.RegularExpressions;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Xunit;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-03, ADR-0009, [[IADR-0315]] (#1790):
// 実 Qdrant を起こす統合試験（IngestToSearch / LexicalIndex / KeywordIndex）が**配備と同じ版**を試していることの確認。
//
// 🔴 **Trait を付けない**（コンテナを起こさないので PR の ci.yml で走る。`SeaweedFsContainerDefinitionTests` と同型）。
// 統合試験そのものは integration.yml でしか走らないので、版のずれは PR の段でここが止める。
// 配備の版を上げて試験の定数を据え置く（またはその逆）と赤くなる。
public sealed class QdrantTestImageDefinitionTests
{
    // `image:` 行の qdrant の参照を全部拾う（1 件も無ければ検査が空振りするので件数も確かめる）。
    private static readonly Regex QdrantImageLine =
        new(@"^\s*image:\s*(?<ref>\S*qdrant/qdrant\S*)\s*$", RegexOptions.Multiline);

    [Theory]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("deploy/local/infra/qdrant.yaml")]
    public void Test_image_matches_deployment(string relative)
    {
        var refs = QdrantImageLine.Matches(RepoFile.Read(relative))
            .Select(m => m.Groups["ref"].Value)
            .ToArray();

        refs.Should().NotBeEmpty($"{relative} に qdrant の image 行が無いと、版の突合が何も検査しない");
        refs.Should().AllBe(QdrantTestImage.Reference,
            $"統合試験の Qdrant は配備（{relative}）と同じ版で測る（IADR-0315: サーバ版はクライアント版へ揃える）");
    }
}
