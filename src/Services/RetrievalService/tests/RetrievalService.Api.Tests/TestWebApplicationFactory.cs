using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qdrant.Client;
using RetrievalService.Api.Abstractions;
using RetrievalService.Api.Infrastructure;

namespace RetrievalService.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Qdrant:Host"] = "localhost",
                ["Qdrant:Port"] = "6334",
                ["Services:LlmGateway"] = "http://localhost:5007"
            }));
        builder.ConfigureServices(services =>
        {
            // Qdrant クライアントとベクトルストアをインメモリ実装へ差し替え
            services.RemoveAll<QdrantClient>();
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore, InMemoryVectorStore>();

            // 埋め込みサービスをスタブへ差し替え
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService, StubEmbeddingService>();
        });
    }
}

// テスト用スタブ — 常にゼロベクトルを返す
public class StubEmbeddingService : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[1536]);
}
