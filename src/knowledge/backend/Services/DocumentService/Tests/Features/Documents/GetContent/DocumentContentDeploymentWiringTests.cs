using AwesomeAssertions;

namespace DocumentService.Tests.Features.Documents.GetContent;

// FR-06, UC-03, SC-03, ADR-0014, [[IADR-0541]] (#1905): **正の配備ファイル（helm・compose）で、本文を読む側
// （document-service）にオブジェクトストレージが配線されている**ことを固定する。
//
// 🔴 #1905 の壊れ方: 本文を読む側（当時は BFF）にストレージが配線されておらず、`AddPlatformObjectStorage` が
//   `NullObjectStorageClient` へ縮退して案内文を 200 で返していた。本文の読み取りを document-service へ移したので、
//   document-service の配線が 1 行欠けると SC-03 の本文は全件 502 になる（`FAILED_PRECONDITION`）。
// BFF → document-service の gRPC 宛先（D-1）は `EastWestGrpcDeploymentWiringTests` が固定する（ここで重ねない）。
[Trait("TestKind", "Unit")]
public class DocumentContentDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";

    [Fact]
    public void helm_のdocumentはオブジェクトストレージを配線している()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  document:");

        block.Should().Contain("database: document_svc", "対照: document の節を読めていないなら以下は何も検査していない");
        block.Should().MatchRegex(@"(?m)^    objectStorage:\s*true\s*$",
            "document-service が本文を読む（無いと NullObjectStorageClient へ縮退し、SC-03 の本文が全件 502 になる）");
    }

    [Fact]
    public void compose_のdocument_serviceはオブジェクトストレージの接続を受け取る()
    {
        var compose = ReadRepoFile(Compose);
        var block = Block(compose, "services:", "  document-service:");

        block.Should().Contain("Database=document_svc", "対照: document-service の節を読めていないなら以下は何も検査していない");
        block.Should().MatchRegex(@"(?m)^\s+<<:\s*\[[^\]]*\*objectstorage-env[^\]]*\]\s*$",
            "document-service が本文を読む（無いと NullObjectStorageClient へ縮退する）");
        var anchor = Block(compose, "x-objectstorage-env: &objectstorage-env", "x-objectstorage-env: &objectstorage-env");
        foreach (var key in new[] { "ObjectStorage__Endpoint", "ObjectStorage__AccessKey", "ObjectStorage__SecretKey", "ObjectStorage__Bucket" })
            anchor.Should().MatchRegex($@"(?m)^\s+{key}:\s*\S+", $"{key} が無いとクライアントは構成されない");
    }

    // `section` の行より後（`section` 自身を見出しにするときは同じ行）で `header` に一致する行から、同じ字下げの次の見出しまで。
    private static string Block(string text, string section, string header)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sectionAt = Array.IndexOf(lines, section);
        sectionAt.Should().BeGreaterThanOrEqualTo(0, $"'{section}' が見つからない");
        var start = section == header ? sectionAt : Array.IndexOf(lines, header, sectionAt + 1);
        start.Should().BeGreaterThanOrEqualTo(sectionAt, $"'{section}' の下に '{header.Trim()}' が見つからない");
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
