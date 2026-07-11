using Testcontainers.PostgreSql;

namespace Knowledge.IntegrationTests.Fixtures;

// UC-03, UC-04, UC-05: 統合テスト用 PostgreSQL コンテナ
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public bool IsAvailable { get; private set; }
    public string? ConnectionString => _container?.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("integration_test")
                .WithUsername("kp")
                .WithPassword("kp")
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
