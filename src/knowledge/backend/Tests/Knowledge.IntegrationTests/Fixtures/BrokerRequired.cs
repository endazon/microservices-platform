namespace Knowledge.IntegrationTests.Fixtures;

// #455 W3: 実ブローカ結合テスト（ADR-0027 手順 8）専用の skip 判定。
//
// 既存の DockerRequired を緩めない理由: あちらは Postgres / MinIO も要求するテスト群が
// 使っており、「RabbitMQ だけ外から与えられている」状態で走らせると、それらは skip されずに
// 落ちる。判定を分けるほうが、既存テストの意味を変えずに済む。
//
// 条件は「Docker API が使える **または** 外部ブローカが設定されている」。
//
// 🔴 以前は `BrokerFactAttribute : FactAttribute` として**属性**で skip していたが、
// **xUnit1051 は FactAttribute 派生のカスタム属性を認識しない**（#946 形 5）。
// `DockerRequired` と同じ理由で `Assert.SkipUnless` へ移した（`IADR-0231` 決定 3 の適用）。
public static class BrokerRequired
{
    /// <summary>ブローカが得られないならテストを**真の Skipped**にする。</summary>
    public static void SkipUnlessObtainable() =>
        Assert.SkipUnless(
            IsObtainable(),
            "No broker available – start Docker, or set "
            + $"{RabbitMqFixture.ExternalEndpointVariable} to an AMQP endpoint "
            + "(e.g. amqp://guest:guest@localhost:5672)");

    internal static bool IsObtainable() =>
        RabbitMqFixture.ExternalEndpoint is not null || DockerRequired.IsAvailable();
}
