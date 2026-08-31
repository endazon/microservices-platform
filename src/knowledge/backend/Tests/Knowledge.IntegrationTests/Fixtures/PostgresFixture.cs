using Testcontainers.PostgreSql;

namespace Knowledge.IntegrationTests.Fixtures;

// UC-03, UC-04, UC-05: 統合テスト用 PostgreSQL コンテナ
public sealed class PostgresFixture : IAsyncLifetime
{
    // ［2026-08-30 / #1073］**ブローカと同じ外部供給経路を DB にも置く。**
    //
    // `RabbitMqFixture` は #455 W3 で `PLATFORM_TEST_RABBITMQ` を持ったが、DB 側は持たなかった。
    // その非対称のため、Docker Engine API を持たない環境（Rancher Desktop の containerd 等）では
    // **ブローカを外から与えても fan-out 統合テストは 1 行も走らない** —— 器が
    // `postgres.IsAvailable` で早期 return するからである。#1073 の原因調査は
    // 「CI でしか再現できない」状態に置かれ、5 ラウンド仮説を建て直すことになった。
    //
    // 🔴 **fail-closed**: 設定されているのに接続できない場合、skip はしない（Rabbit 側と同じ）。
    // IsAvailable を true のままにし、接続失敗をテストの失敗として表に出す。
    public const string ExternalEndpointVariable = "PLATFORM_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private string? _external;
    public bool IsAvailable { get; private set; }
    public string? ConnectionString => _external ?? _container?.GetConnectionString();

    // 外部エンドポイントが設定されているか（空文字は「未設定」として扱う）。
    public static string? ExternalEndpoint =>
        Environment.GetEnvironmentVariable(ExternalEndpointVariable) is { Length: > 0 } value
            ? value
            : null;

    public async ValueTask InitializeAsync()
    {
        _external = ExternalEndpoint;
        if (_external is not null)
        {
            // コンテナは起こさない。到達性はテスト本体の接続で確かめる（上の fail-closed）。
            IsAvailable = true;
            return;
        }

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

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
