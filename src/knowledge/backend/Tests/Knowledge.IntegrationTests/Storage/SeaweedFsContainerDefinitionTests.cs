using AwesomeAssertions;
using DotNet.Testcontainers.Images;
using Knowledge.IntegrationTests.Fixtures;
using Xunit;

namespace Knowledge.IntegrationTests.Storage;

// FR-06, ADR-0106 決定 4・5, ADR-0107 決定 3・4, [[IADR-0464]] (#1499):
// 受け入れ試験（ObjectStorageRoundTripTests）が**配備と同じ SeaweedFS** を試していることの確認。
//
// 🔴 **Trait を付けない**（コンテナを起こさないので PR の ci.yml で走る）。
// 受け入れ試験そのものは Integration（develop への push・日次）でしか走らない。PR の段で
// 「試験が別のイメージ・別の起動形を試している」「テレメトリの無効化が抜けた」を止めるのが本クラスの役目である。
public sealed class SeaweedFsContainerDefinitionTests
{
    private const string Digest = "sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882";

    // ADR-0107 決定 3: タグだけの参照は、同じタグの中身が差し替わっても検知できない。
    [Fact]
    public void Test_image_is_pinned_by_digest()
    {
        var image = new DockerImage(SeaweedFsContainer.Image);

        image.Repository.Should().Be("chrislusf/seaweedfs");
        image.Tag.Should().Be("4.47");
        image.Digest.Should().Be(Digest);
    }

    // ADR-0106 決定 5 / 08_data-egress-policy: SeaweedFS のテレメトリは既定で有効である。
    [Fact]
    public void Test_container_disables_telemetry()
    {
        SeaweedFsContainer.ServerArguments.Should().Contain("-master.telemetry=false");
    }

    // 試験が確かめた形と配備の形がずれると、受け入れ試験は配備の保証にならない。
    [Fact]
    public void Compose_uses_the_same_image_and_disables_telemetry()
    {
        var compose = File.ReadAllText(RepoFile("deploy/docker-compose.yml"));

        compose.Should().Contain($"image: {SeaweedFsContainer.Image}");
        compose.Should().Contain("-master.telemetry=false");
    }

    [Fact]
    public void Helm_uses_the_same_image_and_disables_telemetry()
    {
        var values = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/values.yaml"));
        var template = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/templates/seaweedfs.yaml"));

        values.Should().Contain("image: chrislusf/seaweedfs").And.Contain("tag: \"4.47\"")
            .And.Contain($"digest: \"{Digest}\"");
        template.Should().Contain("-master.telemetry=false");
    }

    // リポジトリの実ファイルをテストから引く（出力ディレクトリから上へ辿る）。
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"リポジトリのファイルが見つからない: {relative}");
    }
}
