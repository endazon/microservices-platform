using System.Reflection;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine;
using Wolverine.ErrorHandling;
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

    // exchange 名の単一情報源。**メッセージ型名そのもの**であり、前置も接尾も付けない。
    // 発行側と購読側が同じ値を導くことが要点であり、値そのものに意味は無い。
    public static string PlatformExchangeName<TEvent>() => typeof(TEvent).Name;

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
        options.Policies.OnAnyException()
            .RetryWithCooldown(RetryIntervals)
            .Then.MoveToErrorQueue();

        return options;
    }
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
