using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先4: 業務DB）, IADR-0055, Issue #219:
// 業務DB コネクタ（参照専用 SQL・行→文書化）の単体テスト。
// 実 DB を要さないハンドロール ADO.NET フェイク（DbConnection/DbCommand/DbDataReader）で、
// 行→SourceItem マッピング・更新列によるインメモリ増分・本文取得・縮退・DB エラー伝播を検証する。
// ※ 実 SQL の正しさ（派生表ラップ・WHERE id=@id）は実 PostgreSQL の統合テスト（DockerRequired.SkipUnlessAvailable()・follow-up）で確認する。
// ※ SQLite は SQLitePCLRaw の未修正 CVE-2025-6965（NU1903）を持ち込むため採用しない。
public sealed class DatabaseConnectorTests
{
    private static DataSource DbSource()
        => DataSource.Create("erp", "db", "Host=erp;Database=orders;Username=readonly",
            new Dictionary<string, string> { ["query"] = "SELECT id AS id, updated AS updated, body AS content FROM articles" });

    private static DatabaseConnector Connector(FakeDbConnection connection)
        => new(new FakeFactory(connection), NullLogger<DatabaseConnector>.Instance);

    [Fact]
    public async Task Discover_FullScan_MapsAllRows()
    {
        var conn = FakeDbConnection.WithReaderRows(
            [
                new() { ["id"] = "a", ["updated"] = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
                new() { ["id"] = "b", ["updated"] = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc) },
            ]);

        var items = await Connector(conn).DiscoverAsync(DbSource(), since: null, CancellationToken.None);

        items.Select(i => i.Path).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Discover_Incremental_ExcludesRowsAtOrBeforeWatermark()
    {
        var conn = FakeDbConnection.WithReaderRows(
            [
                new() { ["id"] = "old", ["updated"] = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
                new() { ["id"] = "new", ["updated"] = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc) },
            ]);
        var since = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);

        var items = await Connector(conn).DiscoverAsync(DbSource(), since, CancellationToken.None);

        items.Select(i => i.Path).Should().ContainSingle().Which.Should().Be("new");
    }

    [Fact]
    public async Task Discover_ParsesIso8601StringUpdatedColumn()
    {
        // プロバイダによっては updated が ISO8601 文字列で返る（SQLite 等）。DateTimeOffset へ正規化できること。
        var conn = FakeDbConnection.WithReaderRows(
            [new() { ["id"] = "s", ["updated"] = "2026-07-09T00:00:00Z" }]);

        var items = await Connector(conn).DiscoverAsync(DbSource(), null, CancellationToken.None);

        items.Should().ContainSingle().Which.ModifiedAt.Should().Be(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Discover_SkipsRowsWithNullUpdated_WithoutFailingWholeSync()
    {
        var conn = FakeDbConnection.WithReaderRows(
            [
                new() { ["id"] = "bad", ["updated"] = DBNull.Value },
                new() { ["id"] = "good", ["updated"] = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc) },
            ]);

        var items = await Connector(conn).DiscoverAsync(DbSource(), null, CancellationToken.None);

        // updated が NULL の行はスキップし、同期全体は成功する（good のみ列挙）。
        items.Select(i => i.Path).Should().ContainSingle().Which.Should().Be("good");
    }

    [Fact]
    public async Task Fetch_MissingRow_ReturnsEmptyContent()
    {
        var conn = FakeDbConnection.WithScalar(DBNull.Value); // Discover と Fetch の間に消えた等

        var raw = await Connector(conn).FetchAsync(
            DbSource(), new SourceItem("gone", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        raw.Bytes.Should().BeEmpty("該当なしは例外にせず空本文へ縮退する");
    }

    [Fact]
    public async Task Fetch_ReturnsScalarContentAsBytes_AndParameterizesId()
    {
        var conn = FakeDbConnection.WithScalar("# Body");

        var raw = await Connector(conn).FetchAsync(
            DbSource(), new SourceItem("a", DateTimeOffset.UtcNow, 0), CancellationToken.None);

        Encoding.UTF8.GetString(raw.Bytes).Should().Be("# Body");
        raw.ContentType.Should().Be("text/markdown");
        // SQL インジェクション回避: id はパラメータ（@id）で渡す。
        conn.LastCommand!.Parameters.Count.Should().Be(1);
        ((DbParameter)conn.LastCommand.Parameters[0]!).ParameterName.Should().Be("@id");
        ((DbParameter)conn.LastCommand.Parameters[0]!).Value.Should().Be("a");
    }

    [Fact]
    public async Task Resolve_QuotesPasswordWithSpecialCharacters_IntoConnectionString()
    {
        // `;` や `'` を含むパスワードを単純連結すると接続文字列のパースが崩れる。
        // DbConnectionStringBuilder によるクオート合成で正しく組み立てられることを検証（claude-review #224 リグレッション）。
        var conn = FakeDbConnection.WithReaderRows([]);
        var factory = new FakeFactory(conn);
        var connector = new DatabaseConnector(factory, NullLogger<DatabaseConnector>.Instance);
        var source = DataSource.Create("erp", "db", "Host=erp;Database=orders;Username=readonly",
            new Dictionary<string, string>
            {
                ["query"] = "SELECT id AS id, updated AS updated, body AS content FROM articles",
                ["password"] = "p@ss;w'ord",
            });

        await connector.DiscoverAsync(source, null, CancellationToken.None);

        // 合成後の接続文字列を再パースして、特殊文字入りパスワードが欠落・破損なく往復すること。
        var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = factory.LastConnectionString };
        parsed["Password"].Should().Be("p@ss;w'ord");
        parsed["Host"].Should().Be("erp");
    }

    [Fact]
    public async Task Discover_MissingQuery_ReturnsEmpty_WithoutOpeningConnection()
    {
        var conn = FakeDbConnection.WithReaderRows([]);
        var source = DataSource.Create("erp", "db", "Host=erp"); // Config["query"] 無し

        var items = await Connector(conn).DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
        conn.Opened.Should().BeFalse("設定不備なら DB 接続をしない");
    }

    [Fact]
    public async Task Discover_MissingConnectionUri_ReturnsEmpty()
    {
        var conn = FakeDbConnection.WithReaderRows([]);
        var source = DataSource.Create("erp", "db", "",
            new Dictionary<string, string> { ["query"] = "SELECT 1" });

        var items = await Connector(conn).DiscoverAsync(source, null, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_DbError_Throws()
    {
        var conn = FakeDbConnection.ThatThrows(new FakeDbException("boom"));

        var act = () => Connector(conn).DiscoverAsync(DbSource(), null, CancellationToken.None);

        // DB エラーは例外 → オーケストレータが watermark 非前進・継続失敗アラートに載せる（IADR-0051 決定3a）。
        await act.Should().ThrowAsync<DbException>();
    }

    // ========================= ハンドロール ADO.NET フェイク =========================

    private sealed class FakeFactory(FakeDbConnection connection) : IDbConnectionFactory
    {
        public string? LastConnectionString { get; private set; }

        public DbConnection Create(string connectionString)
        {
            LastConnectionString = connectionString;
            return connection;
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeDbConnection : DbConnection
    {
        private List<Dictionary<string, object>>? _rows;
        private object? _scalar;
        private Exception? _throw;
        public bool Opened { get; private set; }
        public FakeDbCommand? LastCommand { get; private set; }

        public static FakeDbConnection WithReaderRows(List<Dictionary<string, object>> rows) => new() { _rows = rows };
        public static FakeDbConnection WithScalar(object scalar) => new() { _scalar = scalar };
        public static FakeDbConnection ThatThrows(Exception ex) => new() { _throw = ex };

        public override void Open() => Opened = true;
        public override Task OpenAsync(CancellationToken ct) { Opened = true; return Task.CompletedTask; }
        public override void Close() { }
        protected override DbCommand CreateDbCommand()
        {
            LastCommand = new FakeDbCommand(_rows, _scalar, _throw);
            return LastCommand;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => Opened ? ConnectionState.Open : ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotSupportedException();
    }

    private sealed class FakeDbCommand(
        List<Dictionary<string, object>>? rows, object? scalar, Exception? throwEx) : DbCommand
    {
        private readonly FakeParameterCollection _parameters = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throwEx is not null ? throw throwEx : scalar;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throwEx is not null ? throw throwEx : new FakeDbDataReader(rows ?? []);
    }

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<object> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) _items.Add(v); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains(value);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf(value);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) => _items.Insert(index, value);
        public override void Remove(object value) => _items.Remove(value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { }
        protected override DbParameter GetParameter(int index) => (DbParameter)_items[index];
        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = "";
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = "";
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeDbDataReader(List<Dictionary<string, object>> rows) : DbDataReader
    {
        private int _index = -1;
        private Dictionary<string, object> Current => rows[_index];

        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override object this[string name] => Current[name];
        public override object this[int ordinal] => Current.Values.ElementAt(ordinal);
        public override int FieldCount => rows.Count > 0 ? rows[0].Count : 0;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override int RecordsAffected => 0;
        public override string GetName(int ordinal) => Current.Keys.ElementAt(ordinal);
        public override int GetOrdinal(string name) => Current.Keys.ToList().IndexOf(name);
        public override object GetValue(int ordinal) => this[ordinal];
        public override bool IsDBNull(int ordinal) => this[ordinal] is null;
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();

        // 未使用の型別アクセサ（コネクタは this[name] のみ使用）。
        public override bool GetBoolean(int ordinal) => (bool)this[ordinal];
        public override byte GetByte(int ordinal) => (byte)this[ordinal];
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => (char)this[ordinal];
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "";
        public override DateTime GetDateTime(int ordinal) => (DateTime)this[ordinal];
        public override decimal GetDecimal(int ordinal) => (decimal)this[ordinal];
        public override double GetDouble(int ordinal) => (double)this[ordinal];
        public override Type GetFieldType(int ordinal) => this[ordinal].GetType();
        public override float GetFloat(int ordinal) => (float)this[ordinal];
        public override Guid GetGuid(int ordinal) => (Guid)this[ordinal];
        public override short GetInt16(int ordinal) => (short)this[ordinal];
        public override int GetInt32(int ordinal) => (int)this[ordinal];
        public override long GetInt64(int ordinal) => (long)this[ordinal];
        public override string GetString(int ordinal) => (string)this[ordinal];
        public override int GetValues(object[] values) => 0;
    }
}
