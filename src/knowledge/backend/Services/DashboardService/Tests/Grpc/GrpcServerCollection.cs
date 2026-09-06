namespace DashboardService.Tests.Grpc;

// NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0407]] (#1255):
// gRPC の**実 Kestrel 器**（`GrpcKestrelFactory`）を持つコレクション。
//
// 🔴 **器はプロセスで 1 つでなければならない。** `GrpcTestConfiguration` は h2c ポートを
// プロセスで 1 度だけ選ぶ（クラス並列で 2 つ選ぶと衝突するため意図的に 1 つに固定してある）。
// したがって器をクラスごとに作る（`IClassFixture`）と、2 つ目の Kestrel が同じポートへ
// bind できずに起動へ失敗する（AuthorizationService 側が #1255 で実測した形）。
[CollectionDefinition(Name)]
public sealed class GrpcServerCollection : ICollectionFixture<GrpcKestrelFactory>
{
    public const string Name = "dashboard-grpc-server";
}
