using DocumentService.Infrastructure.Persistence;
using DataSourceService.Infrastructure.Persistence;
using AuthorizationService.Infrastructure.Persistence;
using WikiService.Infrastructure.Persistence;
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
        // 🔴 ADR-0027（#441 E1 のマージ後に integration.yml が検出）:
        // **Wolverine は接続先をホスト構築時に読む。** MassTransit は `UsingRabbitMq` のラムダ内で
        // **遅延して**読むため、下の ConfigureAppConfiguration の上書きで間に合っていた。
        // Wolverine のオプション構成は `builder.Build()` の時点で走るので**間に合わず**、
        // 既定の amqp://guest:guest@rabbitmq:5672 へ繋ぎに行って
        // `BrokerInitializationException: Unable to initialize the Broker rabbitmq in time` になった。
        // 🔴 **［2026-08-28 / #1022］その既定値は撤去した** —— 今は
        // `InvalidOperationException: RabbitMq:ConnectionString が未設定である` で落ちる。
        // **「接続失敗」と「構成未注入」が型で読み分けられる**ようになったが、
        // `UseSetting` が要る理由（読まれる時点）は 1 バイトも変わっていない。
        //
        // **`UseSetting` はホスト構成へ書くので、CreateBuilder が構成を組む時点から見える** ——
        // Pipeline:ConfigPath と同じ理由である。ConfigureAppConfiguration 側の上書きは
        // 残したまま（MassTransit 経路のサービスがまだ在るため）、**両方の読み取り時点を満たす。**
        //
        // 🔴 **これは「統合テストの config 上書きは効く」を一般化できない実例が 2 件目である。**
        // 1 件目は Pipeline:ConfigPath（下記）。**読まれる時点で決まる。**
        //
        // ［2026-08-28 / #1032］🔴 **そして 3 件目が ConnectionStrings:DefaultConnection である**
        // （下の `UseSetting`）。#1012 が `Program.cs` を
        // `GetConnectionString("DefaultConnection") ?? throw` にしたとき、この器は同キーを
        // **`ConfigureAppConfiguration` の overrides でしか与えておらず**、`develop` の
        // `integration.yml` で **28 件が `InvalidOperationException` で落ちた**（Total 70 /
        // Passed 41 / Failed 28 / Skipped 1。`DocumentService/Program.cs:41` が発生源）。
        // **本ファイルのこのコメントが警告していた罠を、警告した本人が踏んだ形である。**
        // 対処は器の与え方であって `?? throw` を弱めることではない —— 弱めれば
        // 「未注入が既定の資格情報で接続成功へ倒れる」#1012 の欠陥がそのまま戻る。
        if (_rabbit is not null)
        {
            // ［2026-08-28 / #1022］🔴 **fail-closed をここへ引き上げた。**
            // #1022 で `RabbitMq:ConnectionString` も `?? throw` になったため、
            // 下の overrides に置いていた guard では**間に合わない**（Program.cs の
            // トップレベル文が先に読む）。フィクスチャを渡された以上、繋ぎ先はそのコンテナで
            // なければならない —— 無ければここで、**「構成未注入」ではなく「フィクスチャの失敗」
            // として**止める。この 2 つを読み分けられることが #1022 の要件である。
            builder.UseSetting("RabbitMq:ConnectionString", _rabbit.ConnectionString
                ?? throw new InvalidOperationException(
                    "RabbitMqFixture の接続文字列が null である（コンテナの起動に失敗した可能性が高い）。"
                    + " 本番配線は RabbitMq:ConnectionString を読み、未設定なら起動時に落ちる（#1022）。"
                    + " ここで止めないと『構成の注入漏れ』と区別が付かない失敗になる。"
                    + " dockerd と Testcontainers を確認すること。"));
        }

        // ［2026-08-28 / #1032］**接続文字列はビルダ構築時に見えていなければならない。**
        // `Program.cs` は `builder.Configuration.GetConnectionString("DefaultConnection")` を
        // **トップレベル文で即座に**読む（#1012 の fail-fast）。`ConfigureAppConfiguration` で
        // 足した値が見えるのは**その後**であり、読み取りに間に合わない。
        // `UseSetting` はホスト構成へ書くので `CreateBuilder` が構成を組む時点から見える。
        // **下の overrides にも同じキーを残してある** —— `RabbitMq:ConnectionString` と同じ扱いで、
        // 両方の読み取り時点を満たすためである（消しても現状は動くが、遅い時点で読む配線が
        // 足されたときに静かに割れるのを避ける）。**在時点で効いているのはこちらである。**
        builder.UseSetting(
            "ConnectionStrings:DefaultConnection", _postgres.ConnectionString ?? "Host=localhost");

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
            // Program.cs の既定値へ**静かにフォールバック**し、原因不明の DNS / 接続タイムアウトとして
            // 現れた。🔴 **［2026-08-28 / #1022］その既定値はもう無い**（`?? throw` へ置き換えた）ので、
            // 上書きを省くと今度は「構成未注入」として落ちる。**どちらにせよフィクスチャを渡された以上、
            // 繋ぎ先はそのコンテナでなければならない** —— guard は上の UseSetting 側にある。
            // ［2026-08-21 / #455 Phase 0 U0d］**段宣言（pipeline.json）を本番と同じ経路で通す。**
            //
            // 従前ここは Pipeline:ConfigPath を設定しておらず、AddPlatformPipelineStep は
            // pipeline.Steps.Count == 0 の経路（「宣言が無いので既定で登録」）しか通っていなかった。
            // その結果、同メソッドが持つ 4 つの起動時 fail-fast——
            //   規則 2: 宣言があるのに段が未宣言 → 起動失敗
            //   規則 3: 宣言の consumer 完全名が実装と不一致 → 起動失敗
            //   規則 4: 宣言の input が IConsumer<TIn> の TIn と不一致 → 起動失敗
            //   規則 5: enabled:false → 購読・キューを作らない
            // を**出荷される pipeline.json に対して検査するテストが無かった**。コンシューマの
            // クラス名や namespace を変えると、本番は起動時に落ちるのに、テストは緑のままだった。
            //
            // 🔴 **［2026-08-22 訂正 / #892］従前ここには「4 つの fail-fast を検査するテストが
            // 1 件も無かった」と書いていた。誤りである。** 規則 2〜5 は ConversionService.Worker.Tests の
            // PipelineStepRegistrationTests が**合成した宣言に対して**既に検査していた（2026-07-08 の
            // #111 で追加。本コメントを書いた時点で 6 週間前から存在した）。無かったのは
            // 「**出荷される pipeline.json に対する**検査」である。
            //
            // docs 側（tech-requirements.md / docs/tests/FR-14_composability.md）は #892 で訂正済み
            // だったが、**本コメントだけが引き直されずに残っていた**。規則 10 の破れである ——
            // 是正のたびに、その変更で新たに誤りになる自分の記述を**全走査で**引き直すこと。
            //
            // 🔴 **本番が読む正本を指す。テストへ複製しない。** 複製すると本番の宣言を変えても
            // テストの複製は古いままになり、「宣言と実装の一致」を検査するはずのテストが
            // **古い宣言との一致**を検査するようになる（検査しているつもりで何も守らない）。
            // ［2026-08-28 / #1022］guard は上の UseSetting へ引き上げた（読み取りに間に合わないため）。
            // ここは遅い時点で読む配線のために同じ値を重ねるだけである。
            if (_rabbit?.ConnectionString is { Length: > 0 } rabbitConnection)
            {
                overrides["RabbitMq:ConnectionString"] = rabbitConnection;
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
    global::DocumentService.DocumentServiceTestMarker, DocumentDbContext>
{
    public DocumentServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

public sealed class DataSourceServiceFactory : IntegrationTestFactoryBase<
    global::DataSourceService.DataSourceServiceTestMarker, DataSourceDbContext>
{
    public DataSourceServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

public sealed class AuthorizationServiceFactory : IntegrationTestFactoryBase<
    global::AuthorizationService.AuthorizationServiceTestMarker, AuthorizationDbContext>
{
    public AuthorizationServiceFactory(PostgresFixture pg) : base(pg, null) { }

    // FR-09, ADR-0004: 管理系エンドポイント（/authz/policies 等）は AdminOnly を要求する。
    // 認証スキームの差し替え（platform-admin）は基底クラスが全サービス共通で行う。
}

public sealed class WikiServiceFactory : IntegrationTestFactoryBase<
    global::WikiService.WikiServiceTestMarker, WikiDbContext>
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
    global::ConversionService.Worker.Infrastructure.Persistence.ConversionJobDbContext>
{
    public ConversionServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}
