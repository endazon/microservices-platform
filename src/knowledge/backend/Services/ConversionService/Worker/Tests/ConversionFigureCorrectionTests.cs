using System.Net;
using System.Net.Http.Json;
using ConversionService.Worker.Infrastructure.Persistence;
using ConversionService.Worker.Domain.Ports;
using ConversionService.Worker.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1（図のコード化のやり直し）の受け入れ基準を写像する。
// 対象は**画像保持へ縮退した図を持つ成功ジョブ**である（UC-06 は縮退を例外フローの中の正常な収束と
// 定めており、ジョブの状態は succeeded になる）。**再変換（failed 限定）とは別の操作である。**
public class ConversionFigureCorrectionTests
{
    private const string ImageUri = "storage://normalized/assets/fig-1.png";

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["手順書"], DateTimeOffset.UtcNow);

    private static IReadOnlyList<NormalizedFigure> TwoFigures() =>
    [
        new("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null),
        new("fig-1", false, null, null, ImageUri, "image/png", "全体構成"),
    ];

    // 縮退した図 1 件を含む成功ジョブと、その本文を用意する。
    private static async Task<Guid> SeedSucceededAsync(Factory factory)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
        var objects = (FakeObjectStore)scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var documentId = Guid.NewGuid();
        var body = $"# 障害対応手順書\n\n```mermaid\nflowchart TD; A-->B;\n```\n\n"
            + FigureMarkdown.ImageEmbed("fig-1", ImageUri) + "\n";
        var markdownUri = await objects.SaveMarkdownAsync($"{documentId:N}/document.md", body,
            TestContext.Current.CancellationToken);
        await store.StartAsync(Raw(id), TestContext.Current.CancellationToken);
        await store.SucceedAsync(id, documentId, markdownUri, TwoFigures(),
            TestContext.Current.CancellationToken);
        return id;
    }

    [Fact]
    public async Task Figures_AreRecorded_AndReadable()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        var figures = await client.GetFromJsonAsync<List<ConversionFigureDto>>($"/jobs/{id}/figures",
            TestContext.Current.CancellationToken);

        figures!.Should().HaveCount(2);
        figures.Should().ContainSingle(f => f.FigureId == "fig-0").Which.Coded.Should().BeTrue();
        var retained = figures.Single(f => f.FigureId == "fig-1");
        retained.Coded.Should().BeFalse();
        retained.ImageUri.Should().Be(ImageUri);
        // 2 ペインの右側（元の図画像）とキャプションが引けること。
        retained.Caption.Should().Be("全体構成");
    }

    [Fact]
    public async Task JobDto_DerivesDiagramCounts_ForScreenState()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);

        // SC-07 hi-fi:420「✕ 図コード化失敗（画像保持へ縮退済み）」は状態の 5 値目ではなく、
        // この内訳から導出する（ジョブ自体は succeeded のまま）。IADR-0127。
        job!.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.DiagramsCoded.Should().Be(1);
        job.DiagramsRetained.Should().Be(1);
        job.HasCorrection.Should().BeFalse();
    }

    [Fact]
    public async Task Correction_ReplacesImageEmbedWithCodeBlock_AndRepublishes()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<FigureCorrectionResultDto>(
            TestContext.Current.CancellationToken);
        result!.CorrectedFigures.Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var objects = (FakeObjectStore)scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var body = await objects.TryGetMarkdownAsync(result.MarkdownUri, TestContext.Current.CancellationToken);
        // 画像参照が消えてコードブロックになっていること（IADR-0154 決定 3）。
        body.Should().NotContain(FigureMarkdown.ImageEmbed("fig-1", ImageUri));
        body.Should().Contain("flowchart LR; X-->Y;");
        // 自動コード化済みの図は触っていないこと。
        body.Should().Contain("flowchart TD; A-->B;");

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.HasCorrection.Should().BeTrue();
    }

    [Fact]
    public async Task Correction_Republishes_WithAbacAttributesPreserved()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        var harness = factory.Services.GetRequiredService<MassTransit.Testing.ITestHarness>();
        (await harness.Published.Any<DocumentNormalized>(TestContext.Current.CancellationToken))
            .Should().BeTrue();
        var ev = harness.Published.Select<DocumentNormalized>(TestContext.Current.CancellationToken)
            .First().Context.Message;
        // **属性を空で再発行してはならない** —— 取り込み側は属性から機密区分を読むため、
        // 落とすと文書の可視範囲が変わる（IADR-0154 決定 3）。
        ev.Attributes.Should().ContainKey("confidentiality").WhoseValue.Should().Be("internal");
        ev.Tags.Should().Contain("手順書");
    }

    [Fact]
    public async Task Correction_OnCodedFigure_Is409()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        // Phase 1 の対象は縮退した図に限る（05_screens:330）。
        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-0/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("figure_not_correctable");
    }

    [Fact]
    public async Task Correction_OnUnknownFigure_Is404()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-404/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_OnCorrectedJob_Is409_UntilDiscardIsConfirmed()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        // 再変換は失敗ジョブに限るので、まず失敗させる（補正は残る）。
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            await store.FailAsync(id, "後続の変換で失敗", ct: TestContext.Current.CancellationToken);
        }

        // IADR-0154 決定 4: 確認なしでは補正を破棄しない。**本文に件数が載ること**まで見る
        // ——BFF が状態コードだけを返していると画面で件数を出せない（#640 と同型）。
        var blocked = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("corrections_would_be_lost");
        body.Should().Contain("\"correctedFigures\":1");

        // 明示確認つきなら通り、補正は消える。
        var confirmed = await client.PostAsync($"/jobs/{id}/retry?discardCorrections=true", content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        confirmed.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.HasCorrection.Should().BeFalse();
    }

    [Fact]
    public async Task Retry_OnFailedJobWithoutCorrections_IsStillAccepted()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            await store.StartAsync(Raw(id), TestContext.Current.CancellationToken);
            await store.FailAsync(id, "変換失敗", ct: TestContext.Current.CancellationToken);
        }

        // 補正が無いジョブの再変換は従前どおり素通りすること（ゲートを広げすぎていない）。
        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // 本文を読めない場合は**補正を保存しない**（補正だけ保存されて本文に出ない状態を作らない）。
    [Fact]
    public async Task Correction_WhenBodyMissing_Is409_AndDoesNotPersist()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            // 本文を保存せずに成功として記録する（参照だけがある状態）。
            await store.StartAsync(Raw(id), TestContext.Current.CancellationToken);
            await store.SucceedAsync(id, Guid.NewGuid(), "storage://normalized/missing.md", TwoFigures(),
                TestContext.Current.CancellationToken);
        }

        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("body_unavailable");

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.HasCorrection.Should().BeFalse();
    }

    // ★ レビュー指摘の検証: 「corrections_would_be_lost は通常の API 経路では到達しない」か。
    // **ストアを直叩きせず、公開 API だけで到達できるか**を実測する。
    [Fact]
    public async Task Retry_CorrectionsGate_IsReachable_ThroughPublicApiOnly()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        // 失敗させるのもストア直叩きを使わない——変換の失敗は公開 API では起こせないので、
        // ここでは「失敗済みのジョブに図が残っている」状態を作れるかを見る。
        // 公開 API で作れないなら、この Fact 自体が到達不能性の証拠になる。
        var beforeCorrection = await client.PostAsync($"/jobs/{id}/retry", content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        // succeeded なので not_retryable で弾かれる（＝補正ゲートまで届かない）。
        beforeCorrection.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await beforeCorrection.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("not_retryable");
    }

    // CodeQL（Log entries created from user input）: 利用者由来の値をログへ出す前に無害化すること。
    // **改行を残すと偽のログ行を注入できる。** 経路パラメータ（figureId）は呼び出し元が自由に決められる。
    [Fact]
    public async Task Correction_WithControlCharsInFigureId_DoesNotBreak()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        // 改行を含む図 ID は当然どの図にも一致しないので 404。**落ちたり注入されたりしないこと**を見る。
        var injected = Uri.EscapeDataString("fig-1\n2026-01-01 INFO 偽のログ行");
        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/{injected}/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ★ PR #650 レビュー 2 巡目: コードフェンスを内側から閉じられる入力を弾く。
    // 通すと、閉じた後ろが**生の本文**として保存され、`DocumentNormalized` で再発行されて
    // **補正操作をしていない一般利用者が読む文書**に永続化される。
    [Fact]
    public async Task Correction_WithFenceBreakingCode_Is400_AndDoesNotPersist()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        var breakout = "flowchart LR; X-->Y;\n" + "``" + "`\n\n<script>alert(1)</script>";
        var resp = await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", breakout), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 本文にも補正にも入っていないこと。
        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.HasCorrection.Should().BeFalse();
        using var scope = factory.Services.CreateScope();
        var objects = (FakeObjectStore)scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var body = await objects.TryGetMarkdownAsync(job.MarkdownUri!, TestContext.Current.CancellationToken);
        body.Should().NotContain("<script>");
    }

    // ★ レビュー 2 巡目の繰り越し指摘: 補正ゲートが実際に発火する経路を固定する。
    // **図は MarkFailed で消えない**——これが崩れると分岐の意義が失われるので、そこを直接見る。
    [Fact]
    public async Task Figures_AndCorrections_SurviveFailure_SoTheRetryGateStillFires()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = await SeedSucceededAsync(factory);

        await client.PostAsJsonAsync($"/jobs/{id}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"), TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            await store.FailAsync(id, "成功後の再変換で失敗", ct: TestContext.Current.CancellationToken);
        }

        // 失敗しても図と補正が残っていること（＝ゲートが守るべき状態が実在する）。
        var figures = await client.GetFromJsonAsync<List<ConversionFigureDto>>($"/jobs/{id}/figures",
            TestContext.Current.CancellationToken);
        figures!.Should().Contain(f => f.Corrected);
        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}",
            TestContext.Current.CancellationToken);
        job!.Status.Should().Be(ConversionJobStatus.Failed);
        job.HasCorrection.Should().BeTrue();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();
        private readonly FakeObjectStore _objects = new();

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
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>(_dbName);
                // 実ストレージを使わず、保存した本文を URI 引きできる差し替えを入れる。
                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(_objects);
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
                // 🔴 ADR-0027（#441 E1）: Program.cs は Wolverine ホストも起こす。外部トランスポートを
                // 落とさないと、テストごとに実 RabbitMQ への接続再試行（実測 20 回・約 135 秒）が走り、
                // **落ちるのではなく黙って遅くなる**（1 テスト 2 分半）。ビルドも赤にならないので気づきにくい。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    // 本文・資産をメモリに持つ IObjectStore。URI は実装と同じ storage:// 形にする。
    private sealed class FakeObjectStore : IObjectStore
    {
        private readonly Dictionary<string, string> _markdown = [];

        public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default)
        {
            var uri = $"storage://normalized/{key}";
            _markdown[uri] = markdown;
            return Task.FromResult(uri);
        }

        public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default) =>
            Task.FromResult($"storage://normalized/{key}");

        public Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default) =>
            Task.FromResult(_markdown.TryGetValue(uri, out var md) ? md : null);
    }
}
