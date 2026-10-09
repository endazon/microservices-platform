using AwesomeAssertions;
using DotNet.Testcontainers.Images;
using System.Diagnostics;

namespace Platform.Bff.Tests;

// NFR-18, ADR-0131 決定 4, ADR-0107 決定 3, [[IADR-0522]] (#1839):
// 受入条件 1 の試験（BffSessionStoreValkeyTests）が**配備と同じ Valkey を、配備と同じ起動形で**試していることの確認。
//
// 🔴 **Trait を付けない**（コンテナを起こさないので PR の ci.yml で走る）。受入条件 1 の試験そのものは
// Integration（develop への push・日次）でしか走らない。PR の段で「試験が別のイメージ・別の起動形を試している」
// 「認証なしで起動する形へ戻った」を止めるのが本クラスの役目である（IADR-0461 の SeaweedFsContainerDefinitionTests と同じ形）。
public sealed class ValkeyContainerDefinitionTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string RouteB = "deploy/local/infra/valkey.yaml";

    // ADR-0107 決定 3: タグだけの参照は、同じタグの中身が差し替わっても検知できない。
    [Fact]
    public void Test_image_is_pinned_by_digest()
    {
        var image = new DockerImage(ValkeyTestContainer.Image);

        image.Repository.Should().Be("valkey/valkey");
        image.Tag.Should().Be("9.1-alpine");
        image.Digest.Should().Be("sha256:48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11");
    }

    // 試験の image は配備の 2 経路と同じ参照である（片方だけ版を上げると、試験は配備しない版を試す）。
    [Theory]
    [InlineData(Compose)]
    [InlineData(RouteB)]
    public void Deployments_use_the_same_image_as_the_test(string relative)
    {
        Read(relative).Should().Contain($"image: {ValkeyTestContainer.Image}\n");
    }

    // 起動スクリプトは配備の 2 経路と同じ文字列である（YAML のブロックの字下げを足した形で行ごとに一致する）。
    // compose は `$` を `$$` と書く（compose の変数展開を避ける）ので、戻してから比べる。
    [Theory]
    [InlineData(Compose, 8)]
    [InlineData(RouteB, 14)]
    public void Deployments_use_the_same_startup_script_as_the_test(string relative, int indent)
    {
        var text = Read(relative);
        if (relative == Compose) text = text.Replace("$$", "$", StringComparison.Ordinal);
        var pad = new string(' ', indent);
        var block = "- |\n" + string.Concat(
            ValkeyTestContainer.StartupScript.TrimEnd('\n').Split('\n').Select(line => pad + line + "\n"));

        text.Should().Contain(block);
    }

    // 🔴 ADR-0131 決定 4 の 2: compose はホストへ 6379 を公開しない（`ports` を持たない）。
    [Fact]
    public void Compose_does_not_publish_the_store_to_the_host()
    {
        var service = ServiceBlock(Read(Compose), "valkey");

        service.Should().NotContain("ports:");
        service.Should().NotContain("6379:6379");
    }

    // 🔴 ADR-0131 決定 4 の 2: 経路 B は BFF の Pod だけに ingress を開け、egress を閉じる。
    [Fact]
    public void Route_b_restricts_reachability_to_the_bff()
    {
        var text = Read(RouteB);

        text.Should().Contain("kind: NetworkPolicy");
        text.Should().Contain("policyTypes: [Ingress, Egress]");
        text.Should().Contain("matchLabels: { kubernetes.io/metadata.name: microservices-platform }");
        text.Should().Contain("matchLabels: { app: bff-service }");
        text.Should().Contain("egress: []");
    }

    // [[IADR-0522]]: 起動スクリプトを実際に sh で走らせて fail-closed と「引数に載せない」を確かめる（Docker 不要）。
    // entrypoint は記録スタブに差し替える（引数と標準入力を書き出す）。/bin/sh が無い環境（Windows）では Skipped。
    [Fact]
    public void Startup_script_refuses_to_start_without_a_password()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("/bin/sh が無い環境では走らせない（CI の Linux で走る）");
            return;
        }

        using var dir = new TempDir();
        var (exitCode, stderr) = RunScript(dir, password: string.Empty);

        exitCode.Should().Be(1);
        stderr.Should().Contain("認証なしでは起動しない");
        File.Exists(Path.Combine(dir.Path, "args")).Should().BeFalse("パスワードが空なのに valkey-server を起動した");
    }

    [Fact]
    public void Startup_script_passes_the_password_through_stdin_not_argv()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("/bin/sh が無い環境では走らせない（CI の Linux で走る）");
            return;
        }

        using var dir = new TempDir();
        var password = "dummy-" + Guid.NewGuid().ToString("N");
        var (exitCode, stderr) = RunScript(dir, password);

        exitCode.Should().Be(0, stderr);
        File.ReadAllText(Path.Combine(dir.Path, "args")).Should().Be("valkey-server -\n");
        File.ReadAllText(Path.Combine(dir.Path, "stdin")).Should().Be($"requirepass \"{password}\"\n");
    }

    private static (int ExitCode, string Stderr) RunScript(TempDir dir, string password)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("/bin/sh が要る");
        // entrypoint の記録スタブ: 引数と標準入力をファイルへ書く。
        var stub = Path.Combine(dir.Path, "docker-entrypoint.sh");
        File.WriteAllText(stub, $"#!/bin/sh\necho \"$*\" > '{dir.Path}/args'\ncat > '{dir.Path}/stdin'\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ValkeyTestContainer.StartupScript);
        psi.Environment["PATH"] = dir.Path + ":" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["SESSION_STORE_PASSWORD"] = password;
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr);
    }

    /// <summary>compose のサービス 1 つ分（`  name:` から次のサービスの見出しまで）。</summary>
    private static string ServiceBlock(string compose, string name)
    {
        var start = compose.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"compose にサービス {name} が無い");
        var lines = compose[(start + 1)..].Split('\n');
        var body = lines.Skip(1).TakeWhile(l => l.Length == 0 || l.StartsWith("   ", StringComparison.Ordinal) || l.StartsWith("  #", StringComparison.Ordinal));
        return string.Join('\n', body);
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("リポジトリルート（deploy/docker-compose.yml を持つ）を解決できなかった。");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("valkey-def-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }
}
