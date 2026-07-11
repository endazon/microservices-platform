using Testcontainers.RabbitMq;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0003: 統合テスト用 RabbitMQ コンテナ
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;
    public bool IsAvailable { get; private set; }
    public string? ConnectionString => _container?.GetConnectionString();

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
