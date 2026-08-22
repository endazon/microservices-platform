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
    /// <summary>Docker が使えないならテストを**真の Skipped**にする。</summary>
    /// <remarks>
    /// 🔴 `if (!IsAvailable()) return;` のソフトスキップにしないこと。
    /// `IADR-0231` 決定 3 が撲滅した「走っていないのに Passed」へ退化する。
    /// </remarks>
    public static void SkipUnlessAvailable() =>
        Assert.SkipUnless(
            IsAvailable(),
            "Docker is not available – start Docker Desktop to run integration tests");

    internal static bool IsAvailable()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
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
