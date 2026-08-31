using System.Runtime.CompilerServices;

namespace AuthorizationService.Tests;

// NFR, #1012: 本番の appsettings.json から接続文字列（既定資格情報）を撤去し、Program.cs は
// 未設定なら起動時に落ちるようにした。テストも**実配備と同じ経路（環境変数）**で注入する。
//
// 🔴 `WebApplicationFactory.ConfigureAppConfiguration` では間に合わない —— トップレベル文の
// `builder.Configuration.GetConnectionString(...)` は `builder.Build()` より前に評価されるため、
// ホスト構築時に足すコールバックは既に読まれた後に適用される（実測: 注入しても起動が落ちた）。
//
// DbContext はテスト器が InMemory へ差し替えるので、**資格情報を持たない到達不能な値**で足りる。
//
// SC-17, IADR-0301 決定 3 (#452): 身元プロバイダの宣言（`IdentityAdmin:Provider`）も**同じ理由で
// ここに置く**。既定を持たない設計なので、宣言が無ければトップレベル文で落ちる。
// **`ConfigureAppConfiguration` では間に合わない**（上の注記と同型。実測で 41 件が赤くなった）。
internal static class TestDatabaseConfiguration
{
    [ModuleInitializer]
    internal static void SetConnectionString()
    {
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", "Host=localhost;Database=authz_test");
        // テストは実 IdP を持たない。**偽物であることを明示的に宣言する**（既定では選ばれない）。
        Environment.SetEnvironmentVariable("IdentityAdmin__Provider", "in-memory");
        // IADR-0321 (#1101): 偽物を選べるのは**非配備ホスト**（Development / Testing / Integration）
        // だけになった。単体テストのホストは `TestWebApplicationFactory` が `Testing` を宣言するので、
        // ここへ環境の宣言は要らない（宣言すると器の宣言と二重になり、どちらが効くか読めなくなる）。
    }
}
