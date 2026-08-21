using DocumentService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Persistence;
using AuthorizationService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Knowledge.IntegrationTests.Fixtures;

// UC-03, UC-04, UC-05: 統合テスト用 WebApplicationFactory 基底クラス
// TestContainers の Postgres/RabbitMQ を使いサービスを実際に起動する
public abstract class IntegrationTestFactoryBase<TProgram, TDbContext> : WebApplicationFactory<TProgram>
    where TProgram : class
    where TDbContext : DbContext
{
    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture? _rabbit;

    protected IntegrationTestFactoryBase(PostgresFixture postgres, RabbitMqFixture? rabbit = null)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Integration");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString ?? "Host=localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            };
            // ［2026-08-21 / #455 Phase 0 U0a］🔴 **繋ぎ先が無いことを黙って許さない。**
            //
            // RabbitMqFixture はコンテナ起動失敗を catch して IsAvailable=false にするだけなので、
            // ConnectionString は null のまま残る。本番配線を使うようにした結果、上書きを省くと
            // Program.cs の既定値 amqp://guest:guest@rabbitmq:5672（compose 前提のホスト名）へ
            // **静かにフォールバック**し、原因不明の DNS / 接続タイムアウトとして現れる。
            // 従前は cfg.Host(null) が即座に例外で落ちていたので、失敗の分かりやすさが退行していた。
            //
            // フィクスチャを渡された以上、繋ぎ先はそのコンテナでなければならない。無ければ止める。
            if (_rabbit is not null)
            {
                overrides["RabbitMq:ConnectionString"] = _rabbit.ConnectionString
                    ?? throw new InvalidOperationException(
                        "RabbitMqFixture の接続文字列が null である（コンテナの起動に失敗した可能性が高い）。"
                        + " 本番配線は RabbitMq:ConnectionString を読むため、ここで止めないと"
                        + " 既定の amqp://guest:guest@rabbitmq:5672 へ繋ぎに行き、"
                        + " 原因の分からない接続タイムアウトになる。dockerd と Testcontainers を確認すること。");
            }
            cfg.AddInMemoryCollection(overrides);
        });

        builder.ConfigureServices(services =>
        {
            // DbContext: Npgsql で TestContainers Postgres を使う
            ReplaceDbContextWithNpgsql<TDbContext>(services, _postgres.ConnectionString ?? "Host=localhost");

            // ［2026-08-21 / #455 Phase 0 U0］**サービス自身の配線をそのまま使う。**
            //
            // 従前ここは RemoveAllMassTransitServices() で Program.cs の AddMassTransit() を
            // アセンブリ単位で全削除し、テストが自前でバスを組み直していた。その結果
            // AddPlatformPipelineStep（段の宣言照合）・UsePlatformRetry（リトライ / DLQ）・
            // AddPlatformIntrospection が **1 行も通っていなかった**。
            //
            // 🔴 最も重い帰結: 登録される Consumer が RegisterConsumers の明示列挙だけになるため、
            // DocumentUpdated の 2 購読者（IngestionService + WikiService）が同時に生きている
            // 状態を作れなかった。移行手順 3（リスニングキュー名にサービス名を前置する）を誤って
            // competing consumer 化しても、**試験する場所が無い**。
            //
            // 接続先は Program.cs が Configuration["RabbitMq:ConnectionString"] から読むので、
            // 上の ConfigureAppConfiguration の上書きだけで Testcontainers を向く。
            // 段の宣言（pipeline.json）が無い環境では AddPlatformPipelineStep が
            // 「Pipeline config absent; step enabled by default」で登録するため、
            // 本番配線のままコンシューマが有効になる（PipelineExtensions.cs）。
            //
            // ⚠️ Pipeline:ConfigPath は依然として設定していない。したがって pipeline.json の
            // 段宣言・queue 上書きは**まだ通っていない**。残る穴として別作業で塞ぐ。

            // Issue #33: Bus 起動レース対策。既定では MassTransitHostedService が Bus を
            // バックグラウンド起動するため、CreateClient() 直後の Publish が Consumer の
            // キューバインド完了前に走り、メッセージが破棄され得る。WaitUntilStarted=true で
            // レシーブエンドポイントのバインド完了までホスト起動を待機させ、購読確立後に
            // Publish されることを保証する。
            services.AddOptions<MassTransitHostOptions>().Configure(o =>
            {
                o.WaitUntilStarted = true;
                o.StartTimeout = TimeSpan.FromSeconds(30);
                o.StopTimeout = TimeSpan.FromSeconds(10);
            });

            // FR-09, IADR-0044: 多層防御で書き込み/管理エンドポイントに RequireAuthorization が
            // 付与されたため、実 Keycloak が無い統合環境では TestAuthHandler で platform-admin として
            // 認証し、既定認証スキームを差し替える。実 JWT → ロール展開の検証は単体テストが担う。
            services.AddAuthentication(IntegrationTestAuthHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, IntegrationTestAuthHandler>(
                    IntegrationTestAuthHandler.SchemeName, _ => { });

            AdditionalServices(services);
        });
    }

    protected virtual void AdditionalServices(IServiceCollection services) { }



    private static void ReplaceDbContextWithNpgsql<T>(IServiceCollection services, string connStr)
        where T : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<T>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                                .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(T)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<T>(opt => opt.UseNpgsql(connStr));
    }
}

// ── 各サービス固有ファクトリ ────────────────────────────

// global:: でローカル namespace（Knowledge.IntegrationTests.*）を隠さないようにする
public sealed class DocumentServiceFactory : IntegrationTestFactoryBase<
    global::DocumentService.Api.DocumentServiceTestMarker, DocumentDbContext>
{
    public DocumentServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

public sealed class DataSourceServiceFactory : IntegrationTestFactoryBase<
    global::DataSourceService.Api.DataSourceServiceTestMarker, DataSourceDbContext>
{
    public DataSourceServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

public sealed class AuthorizationServiceFactory : IntegrationTestFactoryBase<
    global::AuthorizationService.Api.AuthorizationServiceTestMarker, AuthorizationDbContext>
{
    public AuthorizationServiceFactory(PostgresFixture pg) : base(pg, null) { }

    // FR-09, ADR-0004: 管理系エンドポイント（/authz/policies 等）は AdminOnly を要求する。
    // 認証スキームの差し替え（platform-admin）は基底クラスが全サービス共通で行う。
}

public sealed class WikiServiceFactory : IntegrationTestFactoryBase<
    global::WikiService.Api.WikiServiceTestMarker, WikiDbContext>
{
    public WikiServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}
