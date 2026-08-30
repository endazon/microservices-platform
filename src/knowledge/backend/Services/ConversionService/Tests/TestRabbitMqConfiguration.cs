using System.Runtime.CompilerServices;

namespace ConversionService.Tests;

// NFR, #1022: 本番の appsettings.json から RabbitMq:ConnectionString（既定資格情報）を撤去し、
// Program.cs は未設定なら起動時に落ちるようにした。テストも**実配備と同じ経路（環境変数）**で注入する。
//
// 🔴 `WebApplicationFactory.ConfigureAppConfiguration` では間に合わない —— トップレベル文の
// `builder.Configuration["RabbitMq:ConnectionString"]` は `builder.Build()` より前に評価されるため、
// ホスト構築時に足すコールバックは既に読まれた後に適用される（#1012 の DB と同型。IADR-0286 決定 4）。
// **従前ここに在った ConfigureAppConfiguration の上書きは、実は一度も効いていなかった** ——
// 効いていたのは appsettings.json の `amqp://guest:guest@rabbitmq:5672` の側である（#1022 で実測）。
//
// 実ブローカへは繋がない（テスト器が `DisableAllExternalWolverineTransports()` 等で外部トランスポートを
// 切る）ので、**資格情報を持たない到達不能な値**で足りる。
internal static class TestRabbitMqConfiguration
{
    [ModuleInitializer]
    internal static void SetConnectionString() =>
        Environment.SetEnvironmentVariable(
            "RabbitMq__ConnectionString", "amqp://localhost:5672");
}
