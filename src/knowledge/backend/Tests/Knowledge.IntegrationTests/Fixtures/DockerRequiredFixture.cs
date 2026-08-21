using System.IO.Pipes;
using System.Runtime.CompilerServices;

namespace Knowledge.IntegrationTests.Fixtures;

// Docker が利用不可の場合にテストをスキップする FactAttribute
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    // #455 A-2: xUnit v3 の FactAttribute は呼び出し元のソース位置を受け取る（xUnit3003）。
    // 引数なしの派生コンストラクタのままだと、スキップ／失敗の報告に本ファイルの位置が出てしまい、
    // どのテストの話かが読めなくなる。Caller 情報をそのまま基底へ渡す。
    public DockerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsDockerAvailable())
            Skip = "Docker is not available – start Docker Desktop to run integration tests";
    }

    internal static bool IsDockerAvailable()
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
