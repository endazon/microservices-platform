using System.Data.Common;
using DataSourceService.Api.Foundation.Ports;
using Npgsql;

namespace DataSourceService.Api.Composable.Adapters;

// FR-01, IADR-0055: 業務DB コネクタの第一プロバイダ実装（PostgreSQL / Npgsql）。
// 他プロバイダ（SQL Server/MySQL 等）は別実装を追加する（follow-up）。
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    public DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);
}
