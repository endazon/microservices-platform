using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先4: 業務DB）, IADR-0055:
// 業務DB を「参照専用の設定駆動 SQL」で取り込むコネクタ。管理者が Config["query"] に列を
// id(テキスト)/updated(日時)/content(テキスト) に別名付けした SELECT を与え、
// 「行→文書」化する。コネクタは SELECT のみ発行し（書き込みしない）、参照専用 DB ユーザーを前提とする。
//
// 契約:
//   Discover: SELECT id, updated FROM ( {query} ) AS src を実行し updated>since をインメモリで増分。
//   Fetch:    SELECT content FROM ( {query} ) AS src WHERE id = @id（パラメータ化）→ 本文バイト＋content-type。
//   認証/権限: 接続情報は ConnectionUri（パスワードを含めない）＋Config["password"]（GET 応答でマスク）。
//     IADR-0295 決定 3: **「パスワードを含めない」は強制されるようになった** —— 従前この契約を
//     守らせる検証はどこにも無く、ConnectionUri は応答へ素で出ていた。現在は資格情報つきの
//     ConnectionUri を書き込み時に 400 で拒否し（ConnectionUriPolicy）、既存行は応答で伏せる。
//   失敗: DB エラーは例外送出 → オーケストレータ（IADR-0051 決定3a）が watermark 非前進・継続失敗アラートに載せる。
//   縮退: ConnectionUri／query 未設定は空列挙。
public sealed class DatabaseConnector(IDbConnectionFactory connectionFactory, ILogger<DatabaseConnector> logger)
    : IDataSourceConnector
{
    public string SourceType => "db";

    private const string DefaultContentType = "text/markdown";

    public async Task<IReadOnlyList<SourceItem>> DiscoverAsync(
        DataSource source, DateTimeOffset? since, CancellationToken ct)
    {
        var (connectionString, query) = Resolve(source);
        if (connectionString is null || query is null)
        {
            logger.LogWarning(
                "DatabaseConnector: ConnectionUri または Config[\"query\"] が未設定のため空列挙します（source {Id}）",
                source.Id);
            return [];
        }

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // 参照専用: SELECT のみ。管理者クエリを派生表として包み、列挙に必要な id/updated だけを取り出す。
        command.CommandText = $"SELECT id, updated FROM ( {query} ) AS src";

        var items = new List<SourceItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var id = reader["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var updatedValue = reader["updated"];
            if (updatedValue is null or DBNull)
            {
                // updated が NULL の行は増分判定できない。同期全体を失敗させず、当該行だけスキップし警告する
                // （query 側で updated を非 NULL に保つ想定。claude-review #224）。
                logger.LogWarning(
                    "DatabaseConnector: updated が NULL の行をスキップします（id={RowId}, source {Id}）", id, source.Id);
                continue;
            }
            var updated = ToDateTimeOffset(updatedValue);
            // 増分: 前回同期時刻（含む同時刻）以前は除外。since=null は初回全件。
            if (since is { } watermark && updated <= watermark)
                continue;
            items.Add(new SourceItem(id, updated, 0));
        }
        return items;
    }

    public async Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct)
    {
        var (connectionString, query) = Resolve(source);
        if (connectionString is null || query is null)
            throw new InvalidOperationException("DatabaseConnector: ConnectionUri または Config[\"query\"] が未設定です。");

        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // パラメータ化（SQL インジェクション回避）。id はテキスト前提。
        command.CommandText = $"SELECT content FROM ( {query} ) AS src WHERE id = @id";
        var param = command.CreateParameter();
        param.ParameterName = "@id";
        param.DbType = DbType.String;
        param.Value = item.Path;
        command.Parameters.Add(param);

        var content = await command.ExecuteScalarAsync(ct);
        // Discover と Fetch の間に行が消えた等で該当なし（null/DBNull）の場合は空本文へ縮退する
        // （同期全体は止めない。空文書は下流で無害）。
        var text = content is null or DBNull ? string.Empty : content.ToString() ?? string.Empty;
        var contentType = Config(source, "contentType", DefaultContentType);
        return new RawContent(Encoding.UTF8.GetBytes(text), contentType);
    }

    // ---- helpers ---------------------------------------------------------

    // 接続文字列（ConnectionUri＋任意 password）とクエリを解決する。いずれか欠落は (null, null)。
    private static (string? ConnectionString, string? Query) Resolve(DataSource source)
    {
        var baseConn = source.ConnectionUri;
        var query = Config(source, "query", string.Empty);
        if (string.IsNullOrWhiteSpace(baseConn) || string.IsNullOrWhiteSpace(query))
            return (null, null);

        var password = Config(source, "password", string.Empty);
        if (string.IsNullOrWhiteSpace(password))
            return (baseConn, query);

        // 単純連結だとパスワードの `;`/`'` 等でパースが崩れるため、DbConnectionStringBuilder で
        // 適切にクオートして合成する（プロバイダ非依存。claude-review #224）。
        var builder = new DbConnectionStringBuilder { ConnectionString = baseConn };
        builder["Password"] = password;
        return (builder.ConnectionString, query);
    }

    private static string Config(DataSource source, string key, string fallback)
        => source.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    // プロバイダ差（DateTime / DateTimeOffset / ISO8601 文字列）を吸収して DateTimeOffset(UTC) へ正規化する。
    // DateTime の Kind: Local は UTC へ変換、Unspecified（例 timestamp without time zone）は UTC 格納を前提とする
    // （IADR-0055。ローカル時刻格納の場合は timestamptz 化を推奨）。
    private static DateTimeOffset ToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime { Kind: DateTimeKind.Local } dt => new DateTimeOffset(dt).ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new FormatException($"updated 列を日時へ変換できません: {value.GetType().Name}"),
    };
}
