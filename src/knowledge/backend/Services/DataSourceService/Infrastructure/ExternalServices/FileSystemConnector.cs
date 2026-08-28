using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先1: ファイルサーバー）, IADR-0051:
// ファイルサーバー（SMB/NFS 等はマウント済みローカルパスとして扱う）コネクタ。
// ルート配下の対応拡張子ファイルを再帰列挙し、更新日時で増分検知する（初回はフルスキャン）。
public sealed class FileSystemConnector(ILogger<FileSystemConnector> logger) : IDataSourceConnector
{
    public string SourceType => "filesystem";

    // 対応フォーマット→content-type。変換サービス（Pandoc/正規化）が扱える形式に限定する。
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = "text/markdown",
            [".txt"] = "text/plain",
            [".html"] = "text/html",
            [".htm"] = "text/html",
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        };

    public Task<IReadOnlyList<SourceItem>> DiscoverAsync(
        DataSource source, DateTimeOffset? since, CancellationToken ct)
    {
        var root = ResolveRoot(source);
        if (root is null || !Directory.Exists(root))
        {
            // ルート未存在・アクセス不可は例外にせず空列挙で縮退する（定期同期サイクルを止めない）。
            logger.LogWarning(
                "FileSystemConnector: ルート '{Root}' が存在しないため空列挙します（source {Id}）", root, source.Id);
            return Task.FromResult<IReadOnlyList<SourceItem>>([]);
        }

        var items = new List<SourceItem>();
        // フォルダ単位のアクセスエラー（UnauthorizedAccessException 等）でサイクル全体を失敗させないよう、
        // ディレクトリを手動再帰し、アクセス不可のサブフォルダは握りつぶして列挙を継続する（IADR-0051 の縮退方針）。
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            List<string> subDirs;
            List<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger.LogWarning(ex,
                    "FileSystemConnector: フォルダ '{Dir}' の列挙に失敗したためスキップします（source {Id}）", dir, source.Id);
                continue;
            }

            foreach (var sub in subDirs)
                stack.Push(sub);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!ContentTypes.ContainsKey(Path.GetExtension(file)))
                    continue; // 非対応形式は列挙しない
                try
                {
                    var info = new FileInfo(file);
                    var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    // 増分: 前回同期時刻（含む同時刻）以前は除外。since=null は初回フルスキャン。
                    if (since is { } watermark && modifiedAt <= watermark)
                        continue;
                    items.Add(new SourceItem(file, modifiedAt, info.Length));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    logger.LogWarning(ex,
                        "FileSystemConnector: ファイル '{File}' の情報取得に失敗したためスキップします", file);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<SourceItem>>(items);
    }

    public async Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(item.Path, ct);
        var contentType = ContentTypes.GetValueOrDefault(
            Path.GetExtension(item.Path), "application/octet-stream");
        return new RawContent(bytes, contentType);
    }

    // ルート解決: Config["rootPath"] を優先。無ければ ConnectionUri（file:// または素のローカルパス）から解決する。
    // smb:// 等のリモート URI は、マウント済みローカルパスを Config["rootPath"] で与える運用を前提とし、
    // 直接解決はしない（null＝空列挙で縮退）。
    private static string? ResolveRoot(DataSource source)
    {
        if (source.Config.TryGetValue("rootPath", out var rootPath) && !string.IsNullOrWhiteSpace(rootPath))
            return rootPath;

        var uri = source.ConnectionUri;
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return parsed.LocalPath;

        // スキーム付き（smb:// 等）はマウントパス未指定として縮退。素のパスはそのまま用いる。
        return uri.Contains("://", StringComparison.Ordinal) ? null : uri;
    }
}
