using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConversionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブ照会・人手補正エンドポイントが状況一覧（絞り込み含む）・
// 個別取得・再変換（202/404/409）を提供することを検証する。RabbitMQ は使わず MassTransit テストハーネスに、
// DB は EF Core InMemory provider に差し替える。各テストは Factory を都度生成し状態を独立させる。
public class ConversionJobEndpointTests
{
    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // 変換ジョブをストア（スコープ）経由で投入する。InMemory DB は Factory の provider 内で共有され、
    // 後続の HTTP リクエスト（別スコープ）から参照できる。
    private static async Task SeedAsync(Factory factory, Func<IConversionJobStore, Task> seed)
    {
        using var scope = factory.Services.CreateScope();
        await seed(scope.ServiceProvider.GetRequiredService<IConversionJobStore>());
    }

    [Fact]
    public async Task GetList_ReturnsSeededJobs_AndFiltersByStatus()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();

        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(ok));
            await store.SucceedAsync(ok, Guid.NewGuid(), "storage://ok.md");
            await store.StartAsync(Raw(bad));
            await store.FailAsync(bad, "変換失敗");
        });

        var all = await client.GetFromJsonAsync<List<ConversionJobDto>>("/jobs",
            TestContext.Current.CancellationToken);
        all!.Should().HaveCount(2);

        var failed = await client.GetFromJsonAsync<List<ConversionJobDto>>("/jobs?status=failed",
            TestContext.Current.CancellationToken);
        failed!.Should().ContainSingle(j => j.Id == bad).Which.Error.Should().Be("変換失敗");
    }

    [Fact]
    public async Task GetById_ReturnsJob_Or404()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, store => store.StartAsync(Raw(id)));

        (await client.GetAsync($"/jobs/{id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_KnownFailedJob_Returns202()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(id));
            await store.FailAsync(id, "失敗");
        });

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // HTTP 契約（失敗ジョブは受理＝202）のみを検証する。queued への遷移は、再発行イベントが
        // テストハーネスのコンシューマに即時消費されて processing 等へ進み得るため、ここで status を
        // 断定するとレース（フレーク）になる。queued 化は決定的な store 単体テスト
        // （PrepareRetry_requeues_and_returns_original_event）で担保する。
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 🔴 発行 ②（不可視の発行元）が **Wolverine のバスへ**出ていることを固定する。
        // MassTransit のまま残すと、購読側は Wolverine なので**再変換は受理されたまま
        // 永久に実行されず、キュー深さのアラームも鳴らない**（IADR-0245 方向 1）。
        // **202 だけを見ていると、この壊れ方は全く見えない。**
        var published = factory.Services.GetRequiredService<RecordingMessageBus>()
            .PublishedOf<RawDocumentFetched>();
        published.Should().ContainSingle("再変換は RawDocumentFetched をちょうど 1 通発行する")
            .Which.FetchId.Should().Be(id);
    }

    [Fact]
    public async Task Retry_NonFailedJob_Returns409()
    {
        // UC-06: 失敗以外（ここでは succeeded）は再変換不可＝409。UI 制御に頼らず API 側で状態を強制する。
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(id));
            await store.SucceedAsync(id, Guid.NewGuid(), "storage://ok.md");
        });

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.Status.Should().Be(ConversionJobStatus.Succeeded); // 状態は変わらない
    }

    // FR-12, UC-06, SC-07（計画 05_screens §SC-07 2026-08-04 確定）: 「同一ジョブの再変換は直列化し、
    // 実行中（processing）の再変換要求は拒否する」。BFF の認可を管理者限定へ絞った（IADR-0128 決定1）後も
    // 後段の状態強制が変わっていないことを固定する回帰テスト。本文の error=not_retryable まで検証する
    // （状態コードだけだと 404 との取り違えや理由の入れ替わりを見逃す）。
    [Fact]
    public async Task Retry_ProcessingJob_Returns409NotRetryable()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        // StartAsync は processing（試行 1）にする。以降イベントを発行しないため processing のまま。
        await SeedAsync(factory, store => store.StartAsync(Raw(id)));

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should().Be("not_retryable");

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.Status.Should().Be(ConversionJobStatus.Processing); // 状態は変わらない
    }

    // FR-12, SC-07（05_screens:324・裁定 Q13）: 照会 API の応答にデッドレター標識と試行上限が載ること
    // （AC-1/AC-2/AC-3）を HTTP 越しに固定する。契約（DTO）に持たせても射影や JSON 化で落ちれば
    // 画面には届かないため、経路の端で見る。
    //
    // 再変換で標識が落ちること（AC-8）はここでは見ない —— Retry_KnownFailedJob_Returns202 と同じ理由で、
    // 再発行イベントがハーネスのコンシューマに即時消費されて状態が進み得るためレースになる
    // （実際に本 PR で solution 一括実行時のみ落ちるフレークとして観測した）。標識の消滅は決定的な
    // store 単体テスト（PrepareRetry_clears_dead_letter_marker）で担保する。
    [Fact]
    public async Task GetById_ExposesDeadLetterMarkerAndMaxAttempts()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(id));
            await store.FailAsync(id, "本文変換 恒久失敗", deadLettered: true);
        });

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.Status.Should().Be(ConversionJobStatus.Failed); // 4 値モデルは変わらない
        job.DeadLettered.Should().BeTrue();
        job.MaxAttempts.Should().Be(ConversionJobRetryPolicy.MaxAttempts);
    }

    [Fact]
    public async Task Retry_UnknownJob_Returns404()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync($"/jobs/{Guid.NewGuid()}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        // Factory ごとに一意な InMemory DB 名で他テストと隔離する。
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Otlp:Endpoint"] = "http://localhost:4317"
                }));
            builder.ConfigureServices(services =>
            {
                // Npgsql DbContext を InMemory へ差し替える（起動時 MigrateAsync は IsRelational=false で回避）。
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>(_dbName);

                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える（再変換の Publish 用）。
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
                // 🔴 ADR-0027（#441 E1）: Program.cs は Wolverine ホストも起こす。外部トランスポートを
                // 落とさないと、テストごとに実 RabbitMQ への接続再試行（実測 20 回・約 135 秒）が走り、
                // **落ちるのではなく黙って遅くなる**（1 テスト 2 分半）。ビルドも赤にならないので気づきにくい。
                services.DisableAllExternalWolverineTransports();

                // 🔴 ADR-0027（#441 E1）: **再変換の発行（発行 ②）を観測するための差し替え。**
                // この発行は `bus.PublishAsync(ev)` の形で**型名が発行行に現れず、
                // `check-event-topology.js` から見えない** —— 変異 A の実測では、ここを
                // MassTransit のまま残しても検査の終了コード・報告行・baseline のバイト列が
                // 1 つも動かなかった。**静的検査では捕まらないので、試験で固定するしかない。**
                services.RemoveAll<IMessageBus>();
                services.AddSingleton<RecordingMessageBus>();
                services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RecordingMessageBus>());
            });
        }
    }
}
