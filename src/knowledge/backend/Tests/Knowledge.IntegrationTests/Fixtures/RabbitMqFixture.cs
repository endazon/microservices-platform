using Testcontainers.RabbitMq;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0003（Superseded by ADR-0027・注記は #580）: 統合テスト用 RabbitMQ コンテナ
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // #455 W3: ブローカの供給経路を 2 つ持つ。
    //
    //   1. Testcontainers（既定）—— CI（integration.yml）はこちらを使う。
    //   2. 外部エンドポイント —— Docker API が無い環境で使う。
    //
    // 2 が要るのは、Docker Engine API を持たないコンテナランタイム（Rancher Desktop の
    // containerd バックエンド等）では Testcontainers が起動できず、実ブローカ結合テストが
    // **全件 skip されたまま緑になる**ためである。ブローカを外から与えられれば、
    // テスト本体（発行・購読・fan-out・ローカル配送の検出）はそのまま実走できる。
    //
    // 🔴 **fail-closed**: この変数が設定されているのに接続できない場合、skip はしない。
    // IsAvailable を true のままにし、接続失敗をテストの失敗として表に出す。
    // 「設定したのに黙って skip」は、W3 が塞ごうとしている無音の穴そのものである。
    public const string ExternalEndpointVariable = "PLATFORM_TEST_RABBITMQ";

    private RabbitMqContainer? _container;
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
            _container = new RabbitMqBuilder("rabbitmq:3.13-alpine")
                .WithUsername("guest")
                .WithPassword("guest")
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
            var fail = ContainerStartupFailure.ToThrow(
                nameof(RabbitMqFixture), "RabbitMQ", ExternalEndpointVariable,
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
