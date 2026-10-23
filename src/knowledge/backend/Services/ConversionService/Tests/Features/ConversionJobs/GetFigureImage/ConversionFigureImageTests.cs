using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Amazon.S3;
using AwesomeAssertions;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.GetFigureImage;

// FR-12, UC-06, SC-07, ADR-0014, [[IADR-0540]], [[IADR-0558]] (#1941): **縮退した図の元画像は、図の所有者（変換サービス）が読む。**
// BFF はストレージを持たず、この口を利用者の資格情報つきで中継する（ADR-0109 決定 1）。
// 以前は BFF がストレージを直接読み、配備では未配線のまま `NullObjectStorageClient` へ縮退して常に 404 だった。
//
// 本クラスは本番の `Program.cs` を起こし、`IObjectStorageClient` だけをメモリの偽物へ差し替えて
// **画像の状態（在る・参照が storage:// でない・オブジェクトが無い・ストレージ未構成）の写し**を測る。門は
// `ConversionJobAuthorizationTests` が測る（ここでは管理者の利用者トークンを既定で載せる）。
[Trait("TestKind", "Integration")]
public class ConversionFigureImageTests
{
    private const string ImageUri = "storage://normalized/assets/fig-1.png";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x42];

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // コード化済み（fig-0）・画像保持へ縮退（fig-1）・参照が storage:// でない（fig-2）の 3 つを持つ成功ジョブ。
    private static async Task<Guid> SeedAsync(Factory factory)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
        await store.StartAsync(Raw(id), TestContext.Current.CancellationToken);
        await store.SucceedAsync(id, Guid.NewGuid(), "storage://normalized/doc.md",
        [
            new NormalizedFigure("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null),
            new NormalizedFigure("fig-1", false, null, null, ImageUri, "image/png", "全体構成"),
            new NormalizedFigure("fig-2", false, null, null, "https://example.invalid/fig-2.png", "image/png", null),
        ], ct: TestContext.Current.CancellationToken);
        return id;
    }

    private static Task<HttpResponseMessage> GetAsync(Factory factory, Guid id, string figureId) =>
        factory.CreateClient().GetAsync($"/jobs/{id}/figures/{figureId}/image", TestContext.Current.CancellationToken);

    private static async Task<string?> ReasonAsync(HttpResponseMessage resp)
    {
        var problem = await resp.Content.ReadFromJsonAsync<Dictionary<string, object?>>(TestContext.Current.CancellationToken);
        problem!["type"]!.ToString().Should().Be("urn:microservices-platform:problem:figure-image-unavailable");
        return problem["reason"]?.ToString();
    }

    // AC-1: 画像のバイト列と、図の台帳にある Content-Type を返す。
    [Fact]
    public async Task DegradedFigure_ReturnsImageBytesAndContentType()
    {
        using var factory = new Factory();
        factory.Storage.Objects[ImageUri] = Png;
        var id = await SeedAsync(factory);

        using var resp = await GetAsync(factory, id, "fig-1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(Png);
    }

    // 未知のジョブ・未知の図・コード化済みの図（画像を持たない）は区別しない 404（問題詳細なし。IADR-0154 決定 2）。
    [Theory]
    [InlineData("fig-0")]
    [InlineData("fig-unknown")]
    public async Task FigureWithoutImage_Returns404WithoutProblem(string figureId)
    {
        using var factory = new Factory();
        var id = await SeedAsync(factory);

        using var resp = await GetAsync(factory, id, figureId);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Content.Headers.ContentLength.Should().Be(0);
        factory.Storage.Reads.Should().BeEmpty("画像を持たない図でストレージを読まない");
    }

    [Fact]
    public async Task UnknownJob_Returns404()
    {
        using var factory = new Factory();

        using var resp = await GetAsync(factory, Guid.NewGuid(), "fig-1");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 参照が storage:// でない画像は取りに行かない（任意の宛先への取得口を作らない）。理由つきの 404。
    [Fact]
    public async Task NonStorageReference_Returns404NotStored_WithoutFetching()
    {
        using var factory = new Factory();
        var id = await SeedAsync(factory);

        using var resp = await GetAsync(factory, id, "fig-2");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReasonAsync(resp)).Should().Be("not_stored");
        factory.Storage.Reads.Should().BeEmpty();
    }

    // オブジェクトが無い（NoSuchKey だけ。IADR-0540 決定 1）は理由つきの 404。
    [Fact]
    public async Task MissingObject_Returns404Missing()
    {
        using var factory = new Factory();
        var id = await SeedAsync(factory);

        using var resp = await GetAsync(factory, id, "fig-1");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReasonAsync(resp)).Should().Be("missing");
    }

    // 🔴 ストレージが構成されていない（#1941 の配備の BFF と同じ状態）は 503。「画像が無い」（404）に化けさせない。
    [Fact]
    public async Task StorageNotConfigured_Returns503_NotNotFound()
    {
        using var factory = new Factory();
        factory.Storage.Configured = false;
        factory.Storage.Objects[ImageUri] = Png;
        var id = await SeedAsync(factory);

        using var resp = await GetAsync(factory, id, "fig-1");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await resp.Content.ReadFromJsonAsync<Dictionary<string, object?>>(TestContext.Current.CancellationToken);
        problem!["type"]!.ToString().Should().Be("urn:microservices-platform:problem:figure-image-unreachable");
    }

    // バケットが無い（NoSuchBucket も 404）は構成の不調であり、「オブジェクトが無い」ではない → 例外のまま上げる
    // （実サーバでは 500、BFF では 502）。TestServer は未処理の例外を呼び出し側へそのまま投げる。
    [Fact]
    public async Task NoSuchBucket_IsNotTreatedAsMissing()
    {
        using var factory = new Factory();
        factory.Storage.Objects[ImageUri] = Png;
        factory.Storage.ReadThrows = new AmazonS3Exception("bucket") { ErrorCode = "NoSuchBucket", StatusCode = HttpStatusCode.NotFound };
        var id = await SeedAsync(factory);

        var act = () => GetAsync(factory, id, "fig-1");

        (await act.Should().ThrowAsync<AmazonS3Exception>()).Which.ErrorCode.Should().Be("NoSuchBucket");
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();
        public FakeStorage Storage { get; } = new();

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
                services.RemoveAll<IObjectStorageClient>();
                services.AddSingleton<IObjectStorageClient>(Storage);
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
                // 🔴 ADR-0027（#441 E1）: 外部トランスポートを落とさないと実 RabbitMQ への再試行で黙って遅くなる。
                services.DisableAllExternalWolverineTransports();
                // FR-12, ADR-0027 §決定, IADR-0609 (#2169): Program.cs は Wolverine の永続化（Transactional Outbox）を
                // PostgreSQL へ向ける。器の DB は InMemory で接続先も到達不能なので、永続化の起動（表の作成・耐久エージェント）を切る。
                services.DisableAllWolverineMessagePersistence();
                // NFR-09, IADR-0465: 門は本物の JwtBearer で判定する（検証鍵だけ差し替え）。
                TestUserTokens.UseStaticJwtBearer(services);
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            TestUserTokens.AuthenticateAsAdmin(client);
        }
    }

    // 画像をメモリに持つ IObjectStorageClient。無いキーは S3 と同じく NoSuchKey を投げる。
    private sealed class FakeStorage : IObjectStorageClient
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public ConcurrentQueue<string> Reads { get; } = new();
        public bool Configured { get; set; } = true;
        public Exception? ReadThrows { get; set; }

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default)
        {
            Reads.Enqueue(uri);
            if (ReadThrows is not null) throw ReadThrows;
            return Objects.TryGetValue(uri, out var bytes)
                ? Task.FromResult(bytes)
                : throw new AmazonS3Exception("The specified key does not exist.")
                { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound };
        }

        public bool CanResolve(string? uri) => Configured && StorageUri.IsStorageUri(uri);

        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default) =>
            Task.FromResult($"storage://normalized/{key}");

        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default) =>
            Task.FromResult($"storage://normalized/{key}");

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => uri;
    }
}
