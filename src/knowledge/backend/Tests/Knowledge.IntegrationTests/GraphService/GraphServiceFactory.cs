using Knowledge.IntegrationTests.Fixtures;

namespace Knowledge.IntegrationTests.GraphService;

// FR-17, SC-09, ADR-0033 決定 1 / #941: GraphService を実 PostgreSQL で起こすファクトリ。
//
// **`Fixtures/IntegrationTestFactory.cs` へ足していない。** 同ファイルは並行作業の交差点であり、
// 本 issue の追加はここ 1 ファイルに閉じる。基底（`IntegrationTestFactoryBase`）は共有のままなので
// 配線の作法は 1 箇所に保たれる。
//
// 🔴 **RabbitMQ を渡さない。** GraphService は Program.cs でメッセージングを一切構成しない（実測:
// `AddPlatformObservability` / `AddPlatformAuth` / `AddDbContext` / `AddHttpClient` のみ）。
// `AuthorizationServiceFactory` と同じく 1 引数で立てる。
//
// 🔴 **`EnsureCreatedAsync` を呼んではならない**（テスト側の注記も参照）。GraphService の Program.cs は
// 起動時に `MigrateAsync` を実行するので、**このファクトリでホストを起こすとスキーマは
// マイグレーション出力そのものになる**。`EnsureCreated` はモデルから直接スキーマを作るため、
// 「マイグレーションが `ON DELETE RESTRICT` を正しく出力しているか」を測れなくなる（#941）。
public sealed class GraphServiceFactory : IntegrationTestFactoryBase<
    global::GraphService.Api.GraphServiceTestMarker,
    global::GraphService.Api.Foundation.Persistence.GraphDbContext>
{
    public GraphServiceFactory(PostgresFixture pg) : base(pg, null) { }
}
