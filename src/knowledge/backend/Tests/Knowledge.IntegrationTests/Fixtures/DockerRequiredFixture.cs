using System.IO.Pipes;

namespace Knowledge.IntegrationTests.Fixtures;

// Docker が利用不可の場合にテストをスキップする FactAttribute
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
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
