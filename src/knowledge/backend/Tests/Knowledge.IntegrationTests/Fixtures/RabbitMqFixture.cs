using Testcontainers.RabbitMq;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0003（Superseded by ADR-0027・注記は #580）: 統合テスト用 RabbitMQ コンテナ
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;
    public bool IsAvailable { get; private set; }
    public string? ConnectionString => _container?.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new RabbitMqBuilder("rabbitmq:3.13-alpine")
                .WithUsername("guest")
                .WithPassword("guest")
                .Build();
            await _container.StartAsync();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
