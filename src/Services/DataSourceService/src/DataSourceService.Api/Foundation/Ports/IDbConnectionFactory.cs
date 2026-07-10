using System.Data.Common;

namespace DataSourceService.Api.Foundation.Ports;

// FR-01, UC-04, IADR-0055: 業務DB コネクタが用いる DB 接続の生成を抽象化するポート。
// 本番は PostgreSQL（Npgsql）、テストは SQLite で差し替える。ADO.NET 基底クラス（DbConnection）を返し、
// コネクタはプロバイダ非依存に SELECT を実行する。
public interface IDbConnectionFactory
{
    // 接続文字列から未オープンの DbConnection を生成する（オープン/破棄は呼び出し側の責務）。
    DbConnection Create(string connectionString);
}
