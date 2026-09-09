namespace NotificationService.Tests.Grpc;

// NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0419]] (#1255):
// gRPC の**実 Kestrel 器**（`GrpcKestrelFactory`）を持つコレクション。
//
// 🔴 **器はプロセスで 1 つでなければならない。** `GrpcTestConfiguration` は h2c ポートを
// プロセスで 1 度だけ選ぶ（クラス並列で 2 つ選ぶと衝突するため意図的に 1 つに固定してある）。
// したがって器をクラスごとに作る（`IClassFixture`）と、2 つ目の Kestrel が同じポートへ
// bind できずに起動へ失敗する（AuthorizationService / RetrievalService が実測した形）。
//
// **今の gRPC テストクラスは 1 つだけだが、それでもコレクションで持つ** ——
// 面が 2 つ目になったときに器の持ち方を書き換える必要が無く、
// 「1 つのときは通ったのに 2 つ目で落ちる」という形の事故を先に閉じておける。
[CollectionDefinition(Name)]
public sealed class GrpcServerCollection : ICollectionFixture<GrpcKestrelFactory>
{
    public const string Name = "notification-grpc-server";
}
