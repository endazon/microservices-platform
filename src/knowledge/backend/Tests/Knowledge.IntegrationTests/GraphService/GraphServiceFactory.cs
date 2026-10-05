using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;

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
// （graph-delete 段 = #1016 / graph-sync 段 = #911）。渡さないと**防壁へ到達する前に
// ホスト起動が失敗する**。
//
// ［2026-08-28 / #1022］**失敗の仕方は変わった。** 従前は Program.cs の既定値
// `amqp://guest:guest@rabbitmq:5672` へ繋ぎに行って `BrokerInitializationException`
// （＝接続失敗）になっていたが、既定値を撤去したので今は
// `InvalidOperationException: RabbitMq:ConnectionString が未設定である`（＝構成未注入）で落ちる。
// **必須にしておく理由は変わらない** —— 変わったのは診断の読みやすさだけである
// （ADR-0027 / #441 E1 の実測は `Fixtures/IntegrationTestFactory.cs` に記録されている）。
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

    // ［2026-10-04 / #1733・[[IADR-0496]]］ホストが登録した日次のクラスタ検出は、起動の待ちの後に**本当に走る**
    // （記録が無ければ期限切れ。実 PostgreSQL ではリースも本物で取れる）。試験の DB へ黙って書かせないよう、
    // 単体側の `TestWebApplicationFactory` と同じく起動の待ちを 1 日にして眠らせる。
    // `UseSetting` はホスト構成へ書くので、オプションの束縛（解決時）から見える。
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ClusterDetection:StartupDelay", "1.00:00:00");
    }
}
