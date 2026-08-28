using System.Runtime.CompilerServices;

namespace WikiService.Tests;

// NFR, #1012: 本番の appsettings.json から接続文字列（既定資格情報）を撤去し、Program.cs は
// 未設定なら起動時に落ちるようにした。テストも**実配備と同じ経路（環境変数）**で注入する。
//
// 🔴 `WebApplicationFactory.ConfigureAppConfiguration` では間に合わない —— トップレベル文の
// `builder.Configuration.GetConnectionString(...)` は `builder.Build()` より前に評価されるため、
// ホスト構築時に足すコールバックは既に読まれた後に適用される（実測: 注入しても起動が落ちた）。
//
// DbContext はテスト器が InMemory へ差し替えるので、**資格情報を持たない到達不能な値**で足りる。
internal static class TestDatabaseConfiguration
{
    [ModuleInitializer]
    internal static void SetConnectionString() =>
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", "Host=localhost;Database=wiki_test");
}
