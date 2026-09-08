using System.IO.Pipes;

namespace Knowledge.IntegrationTests.Fixtures;

// Docker が利用不可の場合に統合テストをスキップする判定。
//
// 🔴 以前は `DockerFactAttribute : FactAttribute` として**属性**で skip していたが、
// **xUnit1051 は FactAttribute 派生のカスタム属性を認識せず、そのメソッド本体をまるごと
// 検査しない**（#946 形 5。`[DockerFact]` → `[Fact]` へ変えるだけで診断が一斉に現れることを実測した）。
// `IADR-0231` 決定 3 が既に「動的スキップは `Assert.Skip*` に統一する」と定めているので、
// **本ファイルはその決定の適用**であって新しいパターンの導入ではない。
//
// 属性をやめたことで `[CallerFilePath]` / `[CallerLineNumber]` の配管も不要になった ——
// あれは「派生属性だと skip / 失敗の報告が本ファイルの位置を指す」（xUnit3003）ためだけに
// 在ったので、派生をやめると問題ごと消える。
public static class DockerRequired
{
    // 🔴 **本クラスが答えるのは「Docker Engine API へ届くか」だけである**（[[IADR-0414]] / #1336）。
    // 「この試験を走らせてよいか」を訊くのは `RequiredServices` のほうである ——
    // **依存は外から与えることもできる**ので、Docker の有無は答えの半分でしかない。
    // ここを門として直接使ってよいのは、`ContainerStartupFailure`（Docker があるのに
    // 起動できなかったのかを見分ける）だけである。

    internal static bool IsAvailable()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
            return true;

        // 🔴 **`DOCKER_HOST` を尊重する**（#1336）。Testcontainers はこの変数を見るのに、
        // 従前の判定は**既定のパイプ／ソケットしか見ていなかった** ——
        // 別の場所へ Docker API を公開している環境（リモートの daemon・
        // 互換ソケットを別パスへ出すランタイム）で、**使えるのに「無い」と答えていた。**
        // 値の妥当性までは確かめない（確かめるのは Testcontainers の仕事であり、
        // 起動に失敗したら `ContainerStartupFailure` が原因を添えて落とす）。
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            return true;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut);
                pipe.Connect(500);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return File.Exists("/var/run/docker.sock");
    }
}
