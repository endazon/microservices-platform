using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConversionService.Tests.Features.ConversionJobs;

// FR-12, UC-06, #2159: **並行する再変換に負けた側の応答。** 端点は要求の最初にジョブを読んで failed を確かめ、その後に確定の中で
// 遷移する。その間に別の再変換が先に確定したら、負けた側は 500 にも 202 にもならず、409 `not_retryable` を返し、発行しない。
// 応答の `status` は読み直した現在の値（queued）であり、要求の最初に読んだ failed ではない。
// 窓の再現: ストアの `PrepareRetryAsync` の手前で、別のスコープの再変換を最後まで走らせる（勝つ側）。
// 保存の時点の並行トークンの照合そのものは `ConversionRetryConcurrencyTests`（ストア）と実 PostgreSQL の結合試験が測る。
[Trait("TestKind", "Integration")]
public class ConversionRetryRaceEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // 再変換の手前で、別のスコープの再変換（勝つ側）を割り込ませるストア。他の操作は本物へ委ねる。
    private sealed class RacingStore(EfConversionJobStore inner, IServiceScopeFactory scopes) : IConversionJobStore
    {
        public async Task<RawDocumentFetched?> PrepareRetryAsync(Guid id, bool discardCorrections = false,
            CancellationToken ct = default)
        {
            using (var scope = scopes.CreateScope())
            {
                var winner = new EfConversionJobStore(scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>());
                (await winner.PrepareRetryAsync(id, discardCorrections, ct)).Should().NotBeNull("前提: 割り込んだ再変換は勝つ");
            }
            return await inner.PrepareRetryAsync(id, discardCorrections, ct);
        }

        public Task StartAsync(RawDocumentFetched ev, CancellationToken ct = default) => inner.StartAsync(ev, ct);
        public Task SucceedAsync(Guid id, Guid documentId, string markdownUri,
            IReadOnlyList<NormalizedFigure>? figures = null, bool hasBody = true, CancellationToken ct = default)
            => inner.SucceedAsync(id, documentId, markdownUri, figures, hasBody, ct);
        public Task FailAsync(Guid id, string error, bool deadLettered = false, CancellationToken ct = default)
            => inner.FailAsync(id, error, deadLettered, ct);
        public Task<IReadOnlyList<ConversionJobDto>> ListAsync(string? status, CancellationToken ct = default)
            => inner.ListAsync(status, ct);
        public Task<ConversionJobDto?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<ConversionFigureDto>?> ListFiguresAsync(Guid jobId, CancellationToken ct = default)
            => inner.ListFiguresAsync(jobId, ct);
        public Task<ConversionFigureDto?> GetFigureAsync(Guid jobId, string figureId, CancellationToken ct = default)
            => inner.GetFigureAsync(jobId, figureId, ct);
        public Task<RawDocumentFetched?> GetSourceEventAsync(Guid id, CancellationToken ct = default)
            => inner.GetSourceEventAsync(id, ct);
        public Task ApplyCorrectionAsync(Guid jobId, string figureId, string language, string code,
            string markdownUri, CancellationToken ct = default)
            => inner.ApplyCorrectionAsync(jobId, figureId, language, code, markdownUri, ct);
    }

    [Fact]
    public async Task 並行する再変換に負けた側は409で現在の状態を返し発行しない()
    {
        using var baseFactory = new ConversionJobEndpointTests.Factory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConversionJobStore>();
            services.AddScoped<IConversionJobStore>(sp => new RacingStore(
                new EfConversionJobStore(sp.GetRequiredService<ConversionJobDbContext>()),
                sp.GetRequiredService<IServiceScopeFactory>()));
        }));
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var store = new EfConversionJobStore(scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>());
            await store.StartAsync(Raw(id), Ct);
            await store.FailAsync(id, "失敗", ct: Ct);
        }

        var resp = await factory.CreateClient().PostAsync($"/jobs/{id}/retry", content: null, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "負けた側を 500 や 202 にしない");
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("error").GetString().Should().Be("not_retryable");
        body.RootElement.GetProperty("status").GetString().Should().Be(ConversionJobStatus.Queued,
            "状態は読み直した現在の値（要求の最初に読んだ failed ではない）");
        factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<RawDocumentFetched>()
            .Should().BeEmpty("負けた側は発行しない（勝った側は割り込みでストアを直接呼んだので、ここには出ない）");
    }
}
