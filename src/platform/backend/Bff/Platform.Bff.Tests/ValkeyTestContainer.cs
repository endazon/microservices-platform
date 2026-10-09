using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Platform.Bff.Tests;

// NFR-18, ADR-0131 決定 1・4, [[IADR-0522]] (#1839):
// キャッシュ・セッションストア（Valkey）の**実イメージ**を Testcontainers で起こす定義。
//
// 🔴 **image と起動スクリプトは配備（compose・経路 B の deploy/local/infra/valkey.yaml）と同じにする。**
// 受入条件 1（BFF の 3 用途が実イメージで通る）が確かめたいのは「配備する形の Valkey が BFF の使う機能を満たすか」であり、
// 試験だけ別の版・別の起動形（認証なし等）で通しても配備の保証にならない。一致は `ValkeyContainerDefinitionTests`
// （Docker 不要・PR で走る）が突き合わせる。
public static class ValkeyTestContainer
{
    /// <summary>
    /// 🔴 **digest で固定する**（ADR-0107 決定 3）。digest は multi-arch の image index のもの。
    /// **compose・経路 B と同じ参照**である（Testcontainers の参照として `check-image-digests.js` も読む）。
    /// </summary>
    public const string Image =
        "valkey/valkey:9.1-alpine@sha256:48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11";

    /// <summary>Valkey の待受ポート。</summary>
    public const int Port = 6379;

    /// <summary>
    /// コンテナの起動スクリプト（イメージの entrypoint へ <c>sh -c</c> として渡す）。
    /// </summary>
    /// <remarks>
    /// 🔴 ADR-0131 決定 4 の 2（基準 D）: **パスワードが空なら valkey-server を呼ばずに終わる**（fail-closed）。
    /// パスワードは here-document で標準入力の設定（<c>valkey-server -</c>）へ渡し、プロセスの引数へ載せない（#1793）。
    /// <c>docker-entrypoint.sh</c> を経由するのは、root で起きたときに <c>valkey</c> 利用者へ落とすためである。
    /// compose は同じ文字列の <c>$</c> を <c>$$</c> と書く（compose の変数展開を避ける）。
    /// </remarks>
    public const string StartupScript =
        "[ -n \"$SESSION_STORE_PASSWORD\" ] || { echo 'valkey: SESSION_STORE_PASSWORD が空。認証なしでは起動しない' >&2; exit 1; }\n"
        + "exec docker-entrypoint.sh valkey-server - <<EOF\n"
        + "requirepass \"$SESSION_STORE_PASSWORD\"\n"
        + "EOF\n";

    /// <summary>コンテナを組む。パスワードは配備と同じ環境変数で渡す。</summary>
    public static IContainer Build(string password) =>
        new ContainerBuilder(Image)
            .WithCommand("sh", "-c", StartupScript)
            .WithEnvironment("SESSION_STORE_PASSWORD", password)
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();

    /// <summary>ホスト側から見た接続先（<c>host:port</c>。パスワードを含まない）。</summary>
    public static string EndpointOf(IContainer container) =>
        $"{container.Hostname}:{container.GetMappedPublicPort(Port)}";
}
