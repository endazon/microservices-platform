using System.Text;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests;

// FR-01, UC-04, IADR-0051: ファイルサーバーコネクタ（優先1）の単体テスト。
// 一時ディレクトリを実データソースとして、対応形式の列挙・増分検知・取得・縮退を検証する。
public sealed class FileSystemConnectorTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemConnector _connector = new(NullLogger<FileSystemConnector>.Instance);

    public FileSystemConnectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kp-fsc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private DataSource Source(Dictionary<string, string>? config = null)
        => DataSource.Create("share", "filesystem", "",
            config ?? new Dictionary<string, string> { ["rootPath"] = _root });

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Discover_FullScan_ReturnsSupportedFilesOnly()
    {
        Write("a.md", "a");
        Write("sub/b.txt", "b");
        Write("c.docx", "c");
        Write("ignore.bin", "x");      // 非対応拡張子
        Write("ignore.png", "y");      // 非対応拡張子

        var items = await _connector.DiscoverAsync(Source(), since: null, CancellationToken.None);

        items.Select(i => Path.GetFileName(i.Path))
            .Should().BeEquivalentTo("a.md", "b.txt", "c.docx");
    }

    [Fact]
    public async Task Discover_Incremental_ExcludesFilesAtOrBeforeWatermark()
    {
        Write("old.md", "old");
        var oldPath = Path.Combine(_root, "old.md");
        var watermark = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(oldPath, watermark.AddMinutes(-10).UtcDateTime);

        Write("new.md", "new");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "new.md"), watermark.AddMinutes(10).UtcDateTime);

        var items = await _connector.DiscoverAsync(Source(), since: watermark, CancellationToken.None);

        items.Select(i => Path.GetFileName(i.Path)).Should().ContainSingle().Which.Should().Be("new.md");
    }

    [Fact]
    public async Task Fetch_ReturnsBytesAndContentType()
    {
        Write("doc.md", "# Title");
        var items = await _connector.DiscoverAsync(Source(), null, CancellationToken.None);
        var item = items.Single();

        var raw = await _connector.FetchAsync(Source(), item, CancellationToken.None);

        Encoding.UTF8.GetString(raw.Bytes).Should().Be("# Title");
        raw.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Discover_MissingRoot_ReturnsEmptyWithoutThrowing()
    {
        var source = DataSource.Create("missing", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = Path.Combine(_root, "does-not-exist") });

        var items = await _connector.DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_RemoteUriWithoutRootPath_DegradesToEmpty()
    {
        // smb:// 等のリモート URI は rootPath 未指定なら縮退（マウントパス未提供）。
        var source = DataSource.Create("smb", "filesystem", "smb://server/share");

        var items = await _connector.DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
