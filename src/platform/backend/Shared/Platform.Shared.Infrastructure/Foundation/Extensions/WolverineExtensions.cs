using System.Net.Security;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0027 移行チェックリスト 手順 3・4・5 を全サービス共通で満たすための Wolverine 設定拡張。
//
// 🔴 **本ファイルは 3 手順の唯一の実装箇所である**（submodule の別プロジェクトは射程外）。
// 手順 6 が「3〜5 を共通ヘルパへ封じ込め、個別サービスでの逸脱を静的検査で禁止する」と定めるため、
// scripts/check-backend-libraries.js の規則 5 が (a) 本ファイル以外での使用を fail させ、
// (b) **本ファイルから消えたことも** fail させる。封じ込めは「他所で書けない」だけでは半分で、
// 「ここに在り続ける」が要る（IADR-0233 決定 2）。
//
// 手順 3〜5 はいずれも「怠っても起動し、ビルドもテストも通り、実行時に静かに壊れる」種類の設定である。
// 手順 3 を怠ると pub/sub が competing consumer へ退行して片方だけが受信し、手順 4 を怠ると発行が
// プロセス内へ閉じ、手順 5 を怠ると internal 実装型に依存するハンドラが最初の受信時に落ちる。
//
// MassTransit 経路（MassTransitExtensions / PipelineExtensions）とは併存する別 API であり、
// 本ファイルは既存の登録経路を一切変更しない（部分移行に対する型制約の安全弁は C3 まで残す。
// **U5「型制約の緩和」という単位は起こさない** —— IADR-0234 決定 4 が IADR-0233 決定 4 を改めた）。
public static class WolverineExtensions
{
    // ADR-0027 §決定「遅延実行・再試行・スケジュール配信は Wolverine の耐久メッセージ機能で賄い」:
    // 自動再試行の間隔。**要素数がそのまま再試行回数**である（MassTransit 側の RetryIntervals と同じ約束）。
    //
    // 🔴 **MassTransit 側（MassTransitExtensions.RetryIntervals）と同値でなければならないが、参照しない。**
    // 共有すると「両方を同時に変える変異」が等価性試験をすり抜ける —— 片側を変えても他方が追随するため
    // **変異が当たらない試験**になる。値を 2 か所に置いたうえで一致を試験で縛る作法は
    // ConversionJobRetryPolicy.MaxAttempts の先例（IADR-0137 決定 3）に倣う。一致は
    // WolverineExtensionsTests が MassTransit 側をリフレクションで読んで束ねる。
    private static readonly TimeSpan[] RetryIntervals =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // 1 回の配信で行う試行の上限（初回 1 回 ＋ 自動再試行 RetryIntervals.Length 回）。
    // これに達した失敗はデッドレターへ送られる。**試行上限の単一情報源**である。
    public static int MaxAttempts => RetryIntervals.Length + 1;

    // #1640: 1 回の配信の中で試行の間に待つ時間の合計（再試行は同じ配信の中で回る。RabbitMQ の ack は最後の試行の後）。
    // 受け口の実行期限 × 試行上限 ＋ これ が、ブローカの consumer_timeout を超えないことの起動時の検査に使う
    // （`ConsumerHandlerTimeouts.EnsureRetryChainFits`）。
    public static TimeSpan TotalRetryCooldown =>
        RetryIntervals.Aggregate(TimeSpan.Zero, (sum, interval) => sum + interval);

    // 手順 3: リスニングキュー名にサービス名を前置する。
    //
    // 前置の目的は fan-out の保存である。同一イベントを 2 サービスが購読するとき（正本の
    // pipeline.json では DocumentUpdated を ingestion-service と wiki-service が購読する）、
    // キュー名が同じになると RabbitMQ は competing consumer となり **丁度 1 つだけが受信する**。
    // サービス名を前置すればキューは必ず分かれ、両方が受信する。
    //
    // 区切りは "." である（"-" ではない）。サービス名自体が kebab-case（wiki-service）であり、
    // "-" で繋ぐと "wiki-service-DocumentUpdated" のどこまでがサービス名か読めない。
    // 隣接プロジェクト ai-stock-trading も同じ問題に対して "." を採っている（同 IADR-0129 決定 1）。
    //
    // 🔴 **exchange 名には前置しない。** 前置すると発行側の exchange と食い違い「誰にも届かない」形になる。
    // ブローカ側の一括前置 API と規約ルーティングはいずれも ADR-0027 手順 3 が名指しで禁じており、
    // その 2 つの API 名は本ファイルに書かない —— check-backend-libraries.js 規則 4 が
    // **.cs 中のコメントも含めて**名前の出現自体を fail させる（「コメントに書いてから外す」経路を塞ぐ設計）。
    // 名前と経緯は docs/tech/tech-requirements.md にある。
    public static string PlatformQueueName(string serviceName, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return $"{serviceName}.{queueName}";
    }

    // 手順 3 の適用点。個別サービスは ListenToRabbitQueue を直接呼ばず、必ずこれを通す
    // （直接呼び出しは規則 5(a) が fail させる）。
    //
    // ⚠️ この行のシンボル名はコメントにも本文にも現れるが、規則 5(b) は**コメントを除いたコードに対して
    // 呼び出し構文で**照合するため、実装が消えれば検出される。**この二重の出現は意図的に残している** ——
    // 実リポジトリの状態そのものが「コメント越しのすり抜け」に対する恒常的な回帰試験になる。
    public static RabbitMqListenerConfiguration ListenToPlatformQueue(
        this WolverineOptions options, string serviceName, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ListenToRabbitQueue(PlatformQueueName(serviceName, queueName));
    }

    // ADR-0027 手順 3 の**発行側**。#992 / [[IADR-0314]]。
    //
    // 🔴 **これが無いと、Wolverine で発行したイベントはブローカへ 1 通も出て行かない。**
    // 手順 4 でプロセス内経路を切ったうえで外向きの経路を誰も宣言していなければ、Wolverine は
    // 宛先を決められず **`No routes can be determined for Envelope ...` を info ログへ 1 行出して捨てる**。
    // 例外は出ず、ヘルスチェックも緑のままである。
    //
    // 実測（2026-08-30・稼働 k3s）: `document-service` が `DocumentUpdated` を発行しても
    // ingestion / wiki / graph のどのキューにも入らず、**取り込みが一度も起動していなかった**。
    // 発行側の経路宣言は**本番コードに一度も存在したことがない**（`git log -S` で 0 件）。
    // 統合テストは器の側でこの宣言を足していたため緑であり、[[IADR-0014]] の
    // 「テストは緑・本番は壊れている」と同型であった。
    //
    // 🔴 **exchange 名にサービス名を前置しない**（上の注記と対）。前置すると購読側が束ねた
    // exchange と食い違い、やはり「誰にも届かない」形になる。
    //
    // ⚠️ 名前を `Publish...` で始めない。`check-event-topology.js` の発行元検出は
    // `Publish` に続く語 ＋ 型引数の形へ一致するため、**経路の宣言が「発行」として数えられてしまう**。
    // 実際の発行元はイベントを構築して送る 1 箇所であり、ここは配線である。
    public static WolverineOptions RoutePlatformEvent<TEvent>(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PublishMessage<TEvent>().ToRabbitExchange(PlatformExchangeName<TEvent>());
        return options;
    }

    // ADR-0027 手順 3 の**購読側の束ね**。#992 / [[IADR-0314]]。
    //
    // 自分のキュー（サービス名前置つき）をイベント型名の fan-out exchange へ束ねる。
    // **束ねるのは購読側である** —— 発行側に購読者の一覧を持たせると、購読サービスが増減するたびに
    // 発行側を直すことになり、pipeline.json の queue 上書き（[[IADR-0239]] 決定 4）にも追随できない。
    //
    // fan-out の保存は「キュー名が分かれていること」と「各キューが同じ exchange に束ねられていること」の
    // **両方**で成り立つ。片方だけでは、分かれたキューに何も届かない。
    public static RabbitMqTransportExpression BindPlatformQueue<TEvent>(
        this RabbitMqTransportExpression rabbit, string serviceName, string queueName)
    {
        ArgumentNullException.ThrowIfNull(rabbit);
        return rabbit.DeclareExchange(
            PlatformExchangeName<TEvent>(),
            ex => ex.BindQueue(PlatformQueueName(serviceName, queueName)));
    }

    // FR-14, IADR-0028 / #1801: 段宣言（`AddPlatformWolverineStep` の戻り値）を受ける購読側の束ね。
    //
    // 🔴 **`enabled: false` の段は束ねない（受信キューを宣言しない）。** 段のハンドラを外すだけでキューを
    // 束ねると、exchange へ届いたメッセージが**ハンドラの無いキュー**へ溜まり続ける。
    // 宣言が無い（null。規則 1 の既定登録）・`enabled: true` は従来どおり束ねる。
    //
    // 名前を `BindPlatformQueue<TEvent>` のまま多重定義にしているのは、`check-event-topology.js` が
    // 購読側の束ねをこの字面で検出するためである（名前を変えると「束ねていない」と誤判定される）。
    public static RabbitMqTransportExpression BindPlatformQueue<TEvent>(
        this RabbitMqTransportExpression rabbit, string serviceName, PipelineStepOptions? step)
    {
        ArgumentNullException.ThrowIfNull(rabbit);
        return step is { Enabled: false }
            ? rabbit
            : rabbit.BindPlatformQueue<TEvent>(serviceName, PlatformStepQueueName<TEvent>(step));
    }

    // FR-14, IADR-0028 / #1801: 段宣言を受ける手順 3 の適用点。`enabled: false` の段では**リスナーを立てない**
    // （null を返す）。上の束ねと対で使う —— 片方だけ抑えると「束ねたのに誰も読まない」か
    // 「読むのに何も届かない」形になる。
    public static RabbitMqListenerConfiguration? ListenToPlatformQueue<TEvent>(
        this WolverineOptions options, string serviceName, PipelineStepOptions? step)
    {
        ArgumentNullException.ThrowIfNull(options);
        return step is { Enabled: false }
            ? null
            : options.ListenToPlatformQueue(serviceName, PlatformStepQueueName<TEvent>(step));
    }

    // 段の受信キュー名（前置前）の単一情報源。`queue` 宣言があればそれ、無ければイベント型名
    // （IADR-0239 決定 4: queue 宣言を黙って無視しない）。従前は各 `Program.cs` が同じ式を複写していた。
    public static string PlatformStepQueueName<TEvent>(PipelineStepOptions? step) =>
        step?.Queue ?? typeof(TEvent).Name;

    // exchange 名の単一情報源。**メッセージ型名そのもの**であり、前置も接尾も付けない。
    // 発行側と購読側が同じ値を導くことが要点であり、値そのものに意味は無い。
    public static string PlatformExchangeName<TEvent>() => typeof(TEvent).Name;

    // NFR-16, NFR-18, IADR-0591（Refs GHSA-j4qv-2w99-xg5p）: ブローカへの接続（RabbitMq:ConnectionString）。各サービスは
    // `UseRabbitMq(new Uri(...))` ではなくこれを使う。
    //
    // 🔴 **RabbitMQ.Client は amqps の URI を受けると、サーバ証明書の名前の不一致を既定で受け入れる**
    // （`ConnectionFactory.Uri` の setter が `Ssl.AcceptablePolicyErrors = RemoteCertificateNameMismatch` を入れる。
    // RabbitMQ.Client 7.1.2 ＋ WolverineFx.RabbitMQ 6.24.4 で実測）。CA の検証は効くが、同じ CA が発行した別の名前の
    // 証明書で受けた相手を拒めない。ここで「受け入れてよい誤り」を空に戻し、CA の検証と名前の照合の両方を求める。
    // 平文（amqp）のときは TLS を使わないので何も変えない。
    public static RabbitMqTransportExpression UsePlatformRabbitMq(this WolverineOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var uri = new Uri(connectionString);
        return options.UseRabbitMq(factory =>
        {
            factory.Uri = uri;
            if (factory.Ssl.Enabled)
            {
                factory.Ssl.AcceptablePolicyErrors = SslPolicyErrors.None;
                factory.Ssl.ServerName = uri.Host;
            }
        });
    }

    // 手順 4・5: 全サービス共通のメッセージング既定値。各サービスの UseWolverine 内で 1 回呼ぶ。
    public static WolverineOptions UsePlatformMessagingDefaults(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 手順 4: 発行元プロセスに同じ型のハンドラがあると、Wolverine の既定の規約ルーティングが
        // 発行をプロセス内へ閉じてしまい、外部購読者へ出て行かなくなる。規約を切って明示配線に寄せる。
        options.Policies.DisableConventionalLocalRouting();

        // 手順 5: Wolverine は既定で ServiceLocationPolicy.NotAllowed であり（実測）、生成コードから
        // 直接構築できない internal 実装型に依存するハンドラは **最初のメッセージ受信時に** 落ちる。
        // 実行時コンパイル（IADR-0217）と組み合わさるため起動時には現れない。サービスロケーションを
        // 明示的に許可して、この「受信するまで分からない失敗」を消す。
        options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        // 再試行・デッドレター（ADR-0027 §決定。移行チェックリストの 8 手順には含まれない）:
        // MassTransit 側の UsePlatformRetry が与える挙動と等価な既定を全サービス共通で与える。
        //
        // 🔴 **Wolverine の既定は「再試行 0 回・初回失敗で即デッドレター」である**（実測）。
        // 手順 4・5 と同じく「設定しなくても起動し、ビルドもトポロジ検査も封じ込め検査も通る」種類の
        // 設定であり、移行したハンドラが黙って回復性を失う。だから移行の全辺より先にここへ置く。
        //
        // ⚠️ **.Then.MoveToErrorQueue() は挙動としては冗長である**（既定でも試行を使い切れば
        // デッドレターへ行くことを実測済み）。**意図の明示と、変異試験のための面**として残す ——
        // これが無いと「デッドレター送りを無効化する」変異を注入する場所が存在しない。
        //
        // NFR-21, ADR-0053 決定 1〜3, IADR-0592 (#2043): **デッドレターへ送るのと同じ継続で計器を上げる。**
        // 契機は「再試行を使い切ったこと」であり（決定 2）、キュー名を見ない（共有 1 本の `wolverine-dead-letter-queue`
        // にはキュー名の照合が効かない）。上げる場所はここ 1 箇所で、個別サービスでは上げない（決定 3）。
        // 計器は DI から引く（Meter は host の IMeterFactory が持ち、`AddPlatformObservability` が収集対象へ入れる）。
        options.Services.AddMetrics();
        options.Services.TryAddSingleton<DeadLetterMetrics>();
        options.Policies.OnAnyException()
            .RetryWithCooldown(RetryIntervals)
            .Then.MoveToErrorQueue()
            .And(RecordDeadLetterAsync, DeadLetterCounterDescription);

        return options;
    }

    // FR-06, ADR-0027 §決定, IADR-0604 (#2042): Wolverine の表（envelope・ノード・デッドレター等）を置くスキーマ。
    // サービスの業務表（EF の既定スキーマ）と分ける。値は全サービス共通である（DB はサービスごとに別なので衝突しない）。
    public const string OutboxSchemaName = "wolverine";

    // FR-06, ADR-0027 §決定「イベント発行の原子性は Wolverine の EF Core + PostgreSQL Transactional Outbox を標準とする」,
    // IADR-0604 (#2042): **Transactional Outbox の共通構成。`UsePlatformMessagingDefaults` と対で、DB 更新と対になる
    // イベントを出すサービスの `UseWolverine` 内で 1 回呼ぶ。**
    //
    // 🔴 **`connectionString` は発行と対になる DbContext と同じ DB を指さなければならない。**
    // EF 連携の発行口（`IDbContextOutbox<T>`）は、envelope の INSERT を **DbContext の接続とトランザクションで**
    // 実行する（DbContext のモデルに Wolverine の表を写していないとき。本構成はそれである）。表の在り処は
    // ここで渡した接続文字列が決めるので、別の DB を渡すと INSERT が表を見つけられず落ちる
    // （同じサーバの別 DB でも同じ。原子性は「同じ DB の同じトランザクション」でしか成り立たない）。
    //
    // 3 つの設定はいずれも「欠けても起動し、ビルドもテストも通り、障害時にだけ壊れる」種類である:
    // - 永続化（PersistMessagesWithPostgresql）が無いと、`IDbContextOutbox` は最初の発行で
    //   `This Wolverine application is not using Database backed message persistence` を投げる。
    // - EF 連携（UseEntityFrameworkCoreTransactions）が無いと、`IDbContextOutbox<T>` が DI に無い。
    // - 🔴 **耐久送出（UseDurableOutboxOnAllSendingEndpoints）が無いと、発行は Outbox の表へ書かれない。**
    //   Wolverine は送出先が耐久（Durable）のときだけ envelope を永続化する（`Envelope.PersistAsync` が
    //   `Sender.IsDurable` を見る）。既定の送出先は耐久でないため、**発行は DB のトランザクションと無関係に
    //   メモリ上で保持され、コミットの後・送出の前にプロセスが落ちると消える** —— Outbox を入れる前と同じ壊れ方である。
    //   これは全送出先へ効く（DB と対にならない発行も、送出前に自分の表へ書かれるので、ブローカ不達では消えなくなる）。
    //
    // 表は起動時に Wolverine 自身が作る（`AutoBuildMessageStorageOnStartup`。EF の Migration には入れない）。
    // 既定値は JasperFx のプロファイルに依存するため、明示する。DB ロールにスキーマ作成の権限が要る。
    public static WolverineOptions UsePlatformTransactionalOutbox(this WolverineOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        options.PersistMessagesWithPostgresql(connectionString, OutboxSchemaName);
        options.UseEntityFrameworkCoreTransactions();
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;

        return options;
    }

    // 継続の説明（Wolverine の診断表示に出る）。
    private const string DeadLetterCounterDescription = "計器: messaging.dead_letter（再試行を使い切った）";

    // ADR-0053 決定 2: 再試行を使い切った 1 通を数える。**例外を投げない** —— ここで投げると
    // デッドレターへの移送と同じ継続の中で失敗し、検知のための計器が回復の経路を壊す。
    internal static ValueTask RecordDeadLetterAsync(
        IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle, Exception exception)
    {
        runtime.Services.GetService<DeadLetterMetrics>()?.RecordRetryExhausted(
            MessageTypeOf(lifecycle.Envelope), exception.GetType().Name);
        return ValueTask.CompletedTask;
    }

    // タグの値。メッセージ本体の型の短い名前（`DocumentUpdated`）を優先し、本体が無ければエンベロープの型名で代える。
    internal static string MessageTypeOf(Envelope? envelope) =>
        envelope?.Message?.GetType().Name
        ?? envelope?.MessageType
        ?? "unknown";

    // readiness タグ。MapPlatformHealthChecks の /health/ready は
    // `Predicate = hc => hc.Tags.Contains("ready")` だけを条件にするため、この 1 語が
    // 「ready に載るか」を決める唯一の接点である。
    private const string ReadyTag = "ready";

    // 既定の登録名。
    public const string BrokerHealthCheckName = "wolverine-broker";

    // ADR-0027: Wolverine へ移した発行元のブローカ疎通を readiness へ載せる。
    //
    // 🔴 **これが無いと、移行した瞬間に readiness が黙って消える。**
    // MassTransit は AddMassTransit が "masstransit-bus"（tag ready）を**暗黙に**登録していた。
    // Wolverine 側は `AddWolverine` ＋ `UseRabbitMq` で**ヘルスチェックを 0 件しか登録しない**（実測）。
    // 気づかずに移すと /health/ready はブローカ不達でも 200 を返し、
    // **probe が自分の主張することを検査しなくなる**（k8s は publish できない pod へ流す）。
    //
    // 🔴 **opt-in である。AddPlatformHealthChecks へ自動登録しない。**
    // 自動にすると (1) ブローカを使わないサービス（認可・LLM ゲートウェイ・ダッシュボード）にまで付き、
    // (2)「チェック 0 件なら両方 200」という共通ヘルパの基準を偽にする。
    //
    // ⚠️ Wolverine の `IHealthCheck` 実装（アダプタ）は internal であり、ドロップイン代替は無い（実測）。
    // 公開されているのは `ITransport.BuildHealthCheck(IWolverineRuntime)` と、それが返す
    // `WolverineTransportHealthCheck.CheckHealthAsync(CancellationToken)` までである。ここで橋渡しする。
    public static IHealthChecksBuilder AddPlatformWolverineBroker(
        this IHealthChecksBuilder builder, string name = BrokerHealthCheckName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.AddCheck<WolverineBrokerHealthCheck>(
            name, failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag]);
    }
}

// ADR-0027: Wolverine のトランスポート健全性を ASP.NET の IHealthCheck へ橋渡しする。
//
// 🔴 **例外を握り潰して Healthy を返さない。** ブローカが落ちているときに 200 を返す readiness は、
// 無いのと同じであるうえに「在るように見える」ぶん悪い。到達不能・設定不備はいずれも Unhealthy にする。
internal sealed class WolverineBrokerHealthCheck(IWolverineRuntime runtime) : IHealthCheck
{
    // ADR-0027 / ADR-0028: 本プラットフォームがブローカとして使う protocol。
    // 組み込み（stub / local / tcp）は検査対象にしない —— それらはブローカ疎通を表さない。
    private static readonly string[] BrokerProtocols = ["rabbitmq", "kafka"];

    // 🔴 **`ITransport` 経由で BuildHealthCheck を呼んではならない —— null が返る。**
    //
    // `ITransport.BuildHealthCheck` は既定実装つきの仮想メソッドだが、`RabbitMqTransport` 側の
    // 同名メソッドは **non-virtual**（override ではなく shadow）である。よってインタフェース型で
    // 呼ぶと**具象の実装ではなく既定実装が走り、null が返る**（Wolverine 6.24.4 で実測）。
    //
    // これを踏むと「健全なブローカに対して恒久的に Unhealthy」——
    // **readiness が永久に赤いまま**という、無いより悪い状態になる。実際に一度書いて踏んだ。
    // 具象型に宣言されたメソッドを名指しで呼ぶ。見つからなければ健全と偽らず Unhealthy にする。
    private static async Task<TransportHealthResult?> CheckAsync(
        ITransport transport, IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var method = transport.GetType().GetMethod(
            nameof(ITransport.BuildHealthCheck),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (method?.Invoke(transport, [runtime]) is not WolverineTransportHealthCheck check)
        {
            return null;
        }

        return await check.CheckHealthAsync(cancellationToken);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // 🔴 **組み込みトランスポートを除外する denylist にしない。**
        // 素の Wolverine ホストは常に `stub` / `local` / `tcp` の 3 つを持つ（実測）。
        // 当初 `local` だけを除いたところ、`stub` と `tcp` にヘルスチェックを掛けて
        // NullReferenceException になった。版が増やせば同じ事故が再発する。
        // **ブローカの protocol を allowlist で挙げる**（ADR-0027 / ADR-0028: RabbitMQ と Kafka）。
        var transports = runtime.Options.Transports
            .Where(t => BrokerProtocols.Contains(t.Protocol))
            .ToArray();

        // ブローカのトランスポートが 1 つも無い＝繋ぐ設定がされていない。
        // 「繋ぎ先が無いので健全」と読むと、配線忘れが緑になる。
        if (transports.Length == 0)
        {
            return HealthCheckResult.Unhealthy(
                "Wolverine にブローカのトランスポート（"
                + string.Join(" / ", BrokerProtocols)
                + "）が設定されていません（ブローカ疎通を検査できません）。");
        }

        var unhealthy = new List<string>();
        var degraded = new List<string>();

        foreach (var transport in transports)
        {
            TransportHealthResult? result;
            try
            {
                result = await CheckAsync(transport, runtime, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unhealthy.Add($"{transport.Protocol}: {ex.Message}");
                continue;
            }

            if (result is null)
            {
                // 健全と読まない。観測できないことは「異常が無い」ことの証拠にならない。
                unhealthy.Add(
                    $"{transport.Protocol}: ヘルスチェックを取得できませんでした"
                    + "（Wolverine の版更新で BuildHealthCheck の形が変わった可能性があります）。");
                continue;
            }

            switch (result.Status)
            {
                case TransportHealthStatus.Unhealthy:
                    unhealthy.Add($"{result.Protocol}: {result.Message}");
                    break;
                case TransportHealthStatus.Degraded:
                    degraded.Add($"{result.Protocol}: {result.Message}");
                    break;
            }
        }

        if (unhealthy.Count > 0)
        {
            return HealthCheckResult.Unhealthy(string.Join(" / ", unhealthy));
        }

        return degraded.Count > 0
            ? HealthCheckResult.Degraded(string.Join(" / ", degraded))
            : HealthCheckResult.Healthy();
    }
}
