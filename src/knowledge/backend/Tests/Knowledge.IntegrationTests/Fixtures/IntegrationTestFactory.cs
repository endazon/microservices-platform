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
//
// ［2026-08-21 / #455 Phase 0 U0b］**DbContext を要求しない基底**である。
// Worker には DbContext を持たないものがある（IngestionService.Worker）ため、
// 「DbContext を差し替える」責務を派生（IntegrationTestFactoryBase<TProgram, TDbContext>）へ
// 分けた。DbContext を持つサービスはそちらを使う。既存の 5 ファクトリの宣言は変わらない。
public abstract class IntegrationTestFactoryBase<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
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

        // ［2026-08-21 / #455 Phase 0 U0d］**ここは ConfigureAppConfiguration では効かない。**
        //
        // Program.cs は builder.AddPlatformPipelineConfig() で
        // builder.Configuration["Pipeline:ConfigPath"] を**ビルダ構築中に即座に読む**。一方
        // ConfigureAppConfiguration で足した値が見えるのはもっと後であり、**読み取りに間に合わない**。
        // 実測: overrides へ入れた版では pipeline.Steps が空のままだった（＝宣言が 1 行も読まれない）。
        //
        // 🔴 RabbitMq:ConnectionString が ConfigureAppConfiguration で効いていたのは、
        // あちらが UsingRabbitMq のラムダ内で**遅延して**読まれるからである。
        // **「統合テストの config 上書きは効く」を一般化してはならない —— 読まれる時点で決まる。**
        //
        // UseSetting はホスト構成へ書くので、CreateBuilder が構成を組む時点から見える。
        //
        // 🔴 **解決できなければ RepoFile.Find が例外で止める（fail-closed）。** これは
        // Pipeline:ConfigPath 固有の理由による —— AddPlatformPipelineConfig は
        // `if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return builder;` と
        // **黙って何もせずに返る**ため、存在しないパスを渡すと**宣言が 1 行も読まれないまま
        // 全テストが緑になる**。「設定したつもりで何も検査していない」状態が成功と見分けられなくなる。
        // （#891 で解決処理を RepoFile へ集約した。この理由は deploy/ の YAML を読む 5 箇所には
        //   当てはまらないので、共通メッセージへ混ぜず呼び出し側に残している。）
        builder.UseSetting("Pipeline:ConfigPath", RepoFile.Find(
            Path.Combine("deploy", "helm", "microservices-platform", "files", "pipeline.json"),
            because: "Pipeline:ConfigPath に存在しないパスを渡すと AddPlatformPipelineConfig が黙って何もせず、"
                + " 段宣言が読まれないまま全テストが緑になる（検査したつもりで何も検査していない状態）。"
                + " ここで止めるのはそれを防ぐためである。"));
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
            // ［2026-08-21 / #455 Phase 0 U0d］**段宣言（pipeline.json）を本番と同じ経路で通す。**
            //
            // 従前ここは Pipeline:ConfigPath を設定しておらず、AddPlatformPipelineStep は
            // pipeline.Steps.Count == 0 の経路（「宣言が無いので既定で登録」）しか通っていなかった。
            // その結果、同メソッドが持つ 4 つの起動時 fail-fast——
            //   規則 2: 宣言があるのに段が未宣言 → 起動失敗
            //   規則 3: 宣言の consumer 完全名が実装と不一致 → 起動失敗
            //   規則 4: 宣言の input が IConsumer<TIn> の TIn と不一致 → 起動失敗
            //   規則 5: enabled:false → 購読・キューを作らない
            // を**検査するテストが 1 件も無かった**。コンシューマのクラス名や namespace を変えると
            // 本番は起動時に落ちるのに、テストは緑のままだった。
            //
            // 🔴 **本番が読む正本を指す。テストへ複製しない。** 複製すると本番の宣言を変えても
            // テストの複製は古いままになり、「宣言と実装の一致」を検査するはずのテストが
            // **古い宣言との一致**を検査するようになる（検査しているつもりで何も守らない）。
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
            // DbContext: Npgsql で TestContainers Postgres を使う（持たないサービスでは何もしない）
            ReplaceDbContext(services, _postgres.ConnectionString ?? "Host=localhost");

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
            // ［2026-08-21 / U0d］段宣言（pipeline.json）も通るようになった。上の
            // ConfigureAppConfiguration が Pipeline:ConfigPath を本番の正本へ向けているため、
            // AddPlatformPipelineStep は宣言のある経路（規則 2〜5）を通る。
            //
            // ［2026-08-21 / U0e］`queue` 上書きの経路も試験するようになった。正本 pipeline.json の
            // 5 段はいずれも `queue` を持たないため（実測）、QueueOverrideFanOutTests が
            // **本番ファイルから実行時に派生**させたフィクスチャを使う（本ファクトリの既定は正本のまま）。

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

    /// <summary>DbContext を Testcontainers の Postgres へ差し替える。既定は何もしない。</summary>
    /// <remarks>
    /// DbContext を持たないサービス（IngestionService.Worker）でも同じ基底を使えるようにするため、
    /// 差し替えは派生の責務にしている。持つサービスは
    /// <see cref="IntegrationTestFactoryBase{TProgram, TDbContext}"/> を使う。
    /// </remarks>
    protected virtual void ReplaceDbContext(IServiceCollection services, string connStr) { }
}

// DbContext を持つサービス用の基底。差し替えの実装だけを足す。
public abstract class IntegrationTestFactoryBase<TProgram, TDbContext> : IntegrationTestFactoryBase<TProgram>
    where TProgram : class
    where TDbContext : DbContext
{
    protected IntegrationTestFactoryBase(PostgresFixture postgres, RabbitMqFixture? rabbit = null)
        : base(postgres, rabbit) { }

    protected override void ReplaceDbContext(IServiceCollection services, string connStr)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                                .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TDbContext>(opt => opt.UseNpgsql(connStr));
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

// ── Worker（#455 Phase 0 U0b） ────────────────────────────
//
// ［2026-08-21 / #887］**IngestionServiceFactory は使われるようになった**
// （Messaging/DocumentUpdatedFanOutTests.cs が WikiServiceFactory と同時に立てる）。
// 🔴 **ConversionServiceFactory は依然としてどのテストからも参照されていない。**
// 死蔵していることが見えなくなるので、この注記は残す。使うテストを書いたら消すこと。

// IngestionService は DocumentUpdated の購読者 2 つのうちの 1 つである（もう 1 つは WikiService）。
// 移行手順 3 を誤って competing consumer 化すると片方だけが受け取るので、2 つを同時に
// 立てられることが試験の前提になる。**その試験は #887 で書いた**
// （Messaging/DocumentUpdatedFanOutTests.cs）。
//
// 🔴 **DbContext を持たないので 1 引数版の基底を使う**（AddDbContext は 0 件。実測）。
public sealed class IngestionServiceFactory : IntegrationTestFactoryBase<
    global::IngestionService.Worker.IngestionServiceTestMarker>
{
    public IngestionServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

// ConversionService は ConversionJobDbContext を持つので 2 引数版を使う。
// 🔴 **未使用**（上の注記を参照）。
public sealed class ConversionServiceFactory : IntegrationTestFactoryBase<
    global::ConversionService.Worker.ConversionServiceTestMarker,
    global::ConversionService.Worker.Foundation.Persistence.ConversionJobDbContext>
{
    public ConversionServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}
