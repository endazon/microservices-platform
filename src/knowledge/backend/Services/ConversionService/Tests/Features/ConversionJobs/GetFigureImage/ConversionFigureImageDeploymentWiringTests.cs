using AwesomeAssertions;

namespace ConversionService.Tests.Features.ConversionJobs.GetFigureImage;

// FR-12, UC-06, SC-07, ADR-0014, [[IADR-0558]] (#1941): **正の配備ファイル（helm・compose）で、図画像を読む側（conversion-service）に
// オブジェクトストレージが配線され、中継するだけの BFF には配線されていない**ことを固定する。
//
// 🔴 #1941 の壊れ方: 図画像を読む側（当時は BFF）にストレージが配線されておらず、`AddPlatformObjectStorage` が
//   `NullObjectStorageClient` へ縮退して配備では常に 404 だった。読み取りを conversion-service へ移したので、
//   conversion-service の配線が欠けると図画像は全件 503（BFF では 502）になる。BFF に配線を戻すと「BFF はストレージの
//   資格情報を持たない」（ADR-0014「サービス経由」）が崩れる。
[Trait("TestKind", "Unit")]
public class ConversionFigureImageDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";

    [Fact]
    public void helm_のconversionはオブジェクトストレージを配線している()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  conversion:");

        block.Should().Contain("database: conversion_svc", "対照: conversion の節を読めていないなら以下は何も検査していない");
        block.Should().MatchRegex(@"(?m)^    objectStorage:\s*true\s*$",
            "conversion-service が図画像を読む（無いと NullObjectStorageClient へ縮退し、図画像が全件 503 になる）");
    }

    [Fact]
    public void helm_のbffにはオブジェクトストレージを配線しない()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  bff:");

        block.Should().Contain("image: microservices-platform/bff", "対照: bff の節を読めていないなら以下は何も検査していない");
        block.Should().NotMatchRegex(@"(?m)^\s+objectStorage:\s*true",
            "BFF は図画像も本文も所有者のサービスから受け取る。ストレージの資格情報を配らない（ADR-0014）");
    }

    [Fact]
    public void compose_のconversion_serviceはオブジェクトストレージの接続を受け取る()
    {
        var block = Block(ReadRepoFile(Compose), "services:", "  conversion-service:");

        block.Should().Contain("Database=conversion_svc", "対照: conversion-service の節を読めていないなら以下は何も検査していない");
        block.Should().MatchRegex(@"(?m)^\s+<<:\s*\[[^\]]*\*objectstorage-env[^\]]*\]\s*$",
            "conversion-service が図画像を読む（無いと NullObjectStorageClient へ縮退する）");
    }

    [Fact]
    public void compose_のbffはオブジェクトストレージの接続を受け取らない()
    {
        var block = Block(ReadRepoFile(Compose), "services:", "  bff:");

        block.Should().Contain("src/platform/backend/Bff/Platform.Bff/Dockerfile", "対照: bff の節を読めていないなら以下は何も検査していない");
        block.Should().NotContain("*objectstorage-env", "BFF へストレージの資格情報を配らない（ADR-0014）");
        block.Should().NotContain("ObjectStorage__", "BFF へストレージの資格情報を配らない（ADR-0014）");
    }

    // `section` の行より後で `header` に一致する行から、同じ字下げの次の見出しまで（DocumentContentDeploymentWiringTests と同じ切り出し）。
    private static string Block(string text, string section, string header)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sectionAt = Array.IndexOf(lines, section);
        sectionAt.Should().BeGreaterThanOrEqualTo(0, $"'{section}' が見つからない");
        var start = Array.IndexOf(lines, header, sectionAt + 1);
        start.Should().BeGreaterThan(sectionAt, $"'{section}' の下に '{header.Trim()}' が見つからない");
        var indent = header.Length - header.TrimStart().Length;
        var end = start + 1;
        while (end < lines.Length)
        {
            var l = lines[end];
            var lineIndent = l.Length - l.TrimStart().Length;
            if (l.Trim().Length > 0 && !l.TrimStart().StartsWith('#') && lineIndent <= indent)
                break;
            end++;
        }
        return string.Join('\n', lines[start..end]);
    }

    private static string ReadRepoFile(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    // 解決できなければ止める（fail-closed）。読めなかったファイルを空として扱うと何も検査しないまま緑になる。
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("リポジトリのルート（deploy/docker-compose.yml を持つ）が見つからない。");
    }
}
