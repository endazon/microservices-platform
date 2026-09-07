using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Fixtures;

// NFR / #1292: コンテナ起動が失敗したときの倒し方を固定する。
//
// 🔴 **対で置く。** 片方だけだと縮退実装が通る ——
//   「常に null」なら従前の握り潰しへ戻り（**#1292 の欠陥そのもの**）、
//   「常に投げる」なら Docker の無い開発機で統合テストが**skip ではなく失敗**になる。
//
// 🔴 Docker を要さない純粋な判定なので `DockerRequired` のガードを置かない
//   （置くと Docker の無い環境でこの試験自体が skip され、**倒し方を測る機械が消える**）。
[Trait("TestKind", "Unit")]
public sealed class ContainerStartupFailureTests
{
    private static readonly Exception Cause = new TimeoutException("docker daemon did not respond");

    // 陽性: Docker があるのに起動できない ＝ 報告すべき失敗である。
    [Fact]
    public void DockerIsAvailable_ButStartupFailed_ProducesAnExceptionThatKeepsTheCause()
    {
        var ex = ContainerStartupFailure.ToThrow(
            "PostgresFixture", "PostgreSQL", "PLATFORM_TEST_POSTGRES", Cause, dockerAvailable: true);

        ex.Should().NotBeNull("Docker があるのに起きないのは skip すべき事情ではない（#1292）");
        ex!.InnerException.Should().BeSameAs(Cause, "🔴 原因を握り潰さない —— これが #1292 の核心である");
        ex.Message.Should().Contain("PostgresFixture", "どの fixture かが分からないと追えない");
        ex.Message.Should().Contain("PostgreSQL");
        ex.Message.Should().Contain("PLATFORM_TEST_POSTGRES", "外部供給への逃げ道を示す");
    }

    // 陰性対照: Docker が無ければ従来どおり skip へ倒す（**挙動を変えない**）。
    [Fact]
    public void DockerIsNotAvailable_FallsBackToSkip()
    {
        var ex = ContainerStartupFailure.ToThrow(
            "PostgresFixture", "PostgreSQL", "PLATFORM_TEST_POSTGRES", Cause, dockerAvailable: false);

        ex.Should().BeNull("Docker の無い開発機では従来どおり skip へ倒す（挙動を変えない）");
    }

    // 呼び出し元 2 つが同じ規則を使っていること（片方だけ直せる形が無いことの担保）。
    [Theory]
    [InlineData("PostgresFixture", "PostgreSQL", "PLATFORM_TEST_POSTGRES")]
    [InlineData("RabbitMqFixture", "RabbitMQ", "PLATFORM_TEST_RABBITMQ")]
    public void BothFixtures_ShareTheSameDecision(string fixtureName, string what, string envVar)
    {
        ContainerStartupFailure.ToThrow(fixtureName, what, envVar, Cause, dockerAvailable: true)
            .Should().NotBeNull();
        ContainerStartupFailure.ToThrow(fixtureName, what, envVar, Cause, dockerAvailable: false)
            .Should().BeNull();
    }

    // 🔴 環境変数の名前は fixture の公開定数から取る（文字列を試験へ書き写さない）。
    // 写すと、変数名を変えたときに**試験だけが古い名前で緑のまま**になる。
    [Fact]
    public void TheEnvironmentVariableNamesComeFromTheFixturesThemselves()
    {
        PostgresFixture.ExternalEndpointVariable.Should().Be("PLATFORM_TEST_POSTGRES");
        RabbitMqFixture.ExternalEndpointVariable.Should().Be("PLATFORM_TEST_RABBITMQ");
    }
}
