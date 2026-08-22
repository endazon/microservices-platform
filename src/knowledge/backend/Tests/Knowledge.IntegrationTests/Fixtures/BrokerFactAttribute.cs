using System.Runtime.CompilerServices;

namespace Knowledge.IntegrationTests.Fixtures;

// #455 W3: 実ブローカ結合テスト（ADR-0027 手順 8）専用の skip 判定。
//
// 既存の DockerFactAttribute を緩めない理由: あちらは Postgres / MinIO も要求するテスト群が
// 使っており、「RabbitMQ だけ外から与えられている」状態で走らせると、それらは skip されずに
// 落ちる。判定を分けるほうが、既存テストの意味を変えずに済む。
//
// 条件は「Docker API が使える **または** 外部ブローカが設定されている」。
[AttributeUsage(AttributeTargets.Method)]
public sealed class BrokerFactAttribute : FactAttribute
{
    // xUnit v3 の FactAttribute は呼び出し元のソース位置を受け取る（xUnit3003）。
    // 渡さないと skip / 失敗の報告が本ファイルの位置を指し、どのテストの話か読めなくなる。
    public BrokerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsBrokerObtainable())
        {
            Skip = "No broker available – start Docker, or set "
                + $"{RabbitMqFixture.ExternalEndpointVariable} to an AMQP endpoint "
                + "(e.g. amqp://guest:guest@localhost:5672)";
        }
    }

    internal static bool IsBrokerObtainable() =>
        RabbitMqFixture.ExternalEndpoint is not null || DockerFactAttribute.IsDockerAvailable();
}
