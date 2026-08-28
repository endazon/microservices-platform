using Knowledge.IntegrationTests.Fixtures;

namespace Knowledge.IntegrationTests.GraphService;

// FR-17, SC-09, ADR-0033 決定 1 / #941: GraphService を実 PostgreSQL で起こすファクトリ。
//
// **`Fixtures/IntegrationTestFactory.cs` へ足していない。** 同ファイルは並行作業の交差点であり、
// 本 issue の追加はここ 1 ファイルに閉じる。基底（`IntegrationTestFactoryBase`）は共有のままなので
// 配線の作法は 1 箇所に保たれる。
//
// 🔴 **`EnsureCreatedAsync` を呼んではならない**（テスト側の注記も参照）。GraphService の Program.cs は
// 起動時に `MigrateAsync` を実行するので、**このファクトリでホストを起こすとスキーマは
// マイグレーション出力そのものになる**。`EnsureCreated` はモデルから直接スキーマを作るため、
// 「マイグレーションが `ON DELETE RESTRICT` を正しく出力しているか」を測れなくなる（#941）。
//
// 🔴 **ブローカは省略できない引数である**（ADR-0027, IADR-0289 決定 2 / #941）。
// GraphService は `builder.Host.UseWolverine(...)` で **ホスト構築時に**
// `RabbitMq:ConnectionString` を読み、`UseRabbitMq(...).AutoProvision()` で接続する
// （graph-delete 段 = #1016 / graph-sync 段 = #911）。渡さないと Program.cs の既定値
// `amqp://guest:guest@rabbitmq:5672` へ繋ぎに行き、**防壁へ到達する前にホスト起動が失敗する**
// （`BrokerInitializationException`。ADR-0027 / #441 E1 の実測が
// `Fixtures/IntegrationTestFactory.cs` に記録されている）。
//
// 🔴 **既定値も null 許容も置かない。** 本ファイルの初版は
// 「GraphService はメッセージングを一切構成しない（実測）」という注記つきで `base(pg, null)` と
// 書いており、**その注記は 5 日後に偽になった**（#1016 / #911）。注記は腐るが型は腐らない ——
// 引数を必須にして、同じ退行をコンパイルエラーとして止める（IADR-0289 決定 2）。
public sealed class GraphServiceFactory : IntegrationTestFactoryBase<
    global::GraphService.GraphServiceTestMarker,
    global::GraphService.Infrastructure.Persistence.GraphDbContext>
{
    public GraphServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}
