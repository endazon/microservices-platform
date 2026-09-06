namespace AuthorizationService.Tests.Grpc;

// NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0401]] (#1255):
// gRPC の**実 Kestrel 器**（`GrpcKestrelFactory`）を持つコレクション。
//
// 🔴 **器はプロセスで 1 つでなければならない。** `GrpcTestConfiguration` は h2c ポートを
// プロセスで 1 度だけ選ぶ（クラス並列で 2 つ選ぶと衝突するため意図的に 1 つに固定してある）。
// したがって器をクラスごとに作る（`IClassFixture`）と、2 つ目の Kestrel が同じポートへ
// bind できずに起動へ失敗する —— 参照実装のときは gRPC のテストクラスが 1 つしか無かったので
// 露見しなかった。名簿の面（`UserDirectory`）が増えた本 PR で顕在化する。
//
// LlmGateway の `SharedMeterCollection` と同型である（あちらは Meter の直列化も兼ねる）。
// 本サービスの gRPC テストは Meter へ発行しないので、**共有するのは器だけ**である。
[CollectionDefinition(Name)]
public sealed class GrpcServerCollection : ICollectionFixture<GrpcKestrelFactory>
{
    public const string Name = "authz-grpc-server";
}
