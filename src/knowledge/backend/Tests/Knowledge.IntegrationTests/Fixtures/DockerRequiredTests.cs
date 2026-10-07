using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Fixtures;

// NFR / #1796 / ADR-0090 決定 1: `DockerRequired.IsAvailable()` が `CI=true` を理由に「ある」と答えないことを固定する。
//
// 🔴 近道があると、CI で依存が欠けた試験は skip ではなく失敗になり、
//   回収実行の「依存を得られない skip」の門（IADR-0507）が CI で発火できない。
// 🔴 Docker を要さない純粋な判定なので `DockerRequired` のガードを置かない（置くと測る機械が消える）。
[Trait("TestKind", "Unit")]
public sealed class DockerRequiredTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    // 🔴 陰性: CI=true でも、Docker API の入口が無ければ「無い」と答える（近道の再混入を止める）。
    [Fact]
    public void CiTrue_WithoutAnyDockerEndpoint_IsNotAvailable()
    {
        DockerRequired.IsAvailable(Env(("CI", "true")), probeDefaultEndpoint: () => false)
            .Should().BeFalse("CI であることは Docker がある理由にならない（ADR-0090 が退けた形）");
    }

    // 陽性: 既定の端点（ソケット／パイプ）が在れば、CI でもローカルでも「ある」。
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void DefaultEndpointPresent_IsAvailable(string? ci)
    {
        var env = ci is null ? Env() : Env(("CI", ci));
        DockerRequired.IsAvailable(env, probeDefaultEndpoint: () => true).Should().BeTrue();
    }

    // 陽性: DOCKER_HOST が与えられていれば既定の端点を見ない（#1336 の挙動を保つ）。
    [Fact]
    public void DockerHostGiven_IsAvailable_WithoutProbing()
    {
        DockerRequired.IsAvailable(Env(("DOCKER_HOST", "tcp://remote:2375")), probeDefaultEndpoint: () => false)
            .Should().BeTrue();
    }

    // 陰性: 空の DOCKER_HOST は未設定と同じ。
    [Fact]
    public void EmptyDockerHost_FallsBackToTheDefaultEndpoint()
    {
        DockerRequired.IsAvailable(Env(("DOCKER_HOST", "")), probeDefaultEndpoint: () => false)
            .Should().BeFalse();
    }
}
