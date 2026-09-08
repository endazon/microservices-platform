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

    /// <summary>
    /// コンテナ起動に失敗したときの原因（成功／未実行なら <c>null</c>）。
    /// 🔴 **握り潰さずに残す**（#1292）—— 従前は理由がログに 1 行も出ず、原因を特定できなかった。
    /// </summary>
    public Exception? StartupError { get; private set; }
    public string? ConnectionString => _external ?? _container?.GetConnectionString();

    // 外部エンドポイントが設定されているか。
    // 🔴 **読み方（空文字は「未設定」）は `ExternalEndpoints` 1 か所が持つ**（[[IADR-0414]] 決定 4 / #1336）。
    // 従前は 4 つの供給口が同じ判定を各自で持っており、**片方だけ空文字を通す状態が作れた。**
    public static string? ExternalEndpoint => ExternalEndpoints.Read(ExternalEndpointVariable);

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
        catch (Exception ex)
        {
            IsAvailable = false;
            StartupError = ex;

            // 🔴 **Docker があるのに起動できないのは skip すべき事情ではない**（#1292）。
            // 判定は `ContainerStartupFailure` が 1 つだけ持つ（理由もそこに書いた）。
            // **ここで規則を書かない** —— 2 つの fixture へ写すと片方だけ直した状態が作れる。
            var fail = ContainerStartupFailure.ToThrow(
                nameof(PostgresFixture), "PostgreSQL", ExternalEndpointVariable,
                ex, DockerRequired.IsAvailable());
            if (fail is not null) throw fail;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
