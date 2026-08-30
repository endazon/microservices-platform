using System.Reflection;
using AwesomeAssertions;
using JasperFx.CodeGeneration.Model;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// ADR-0027 移行チェックリスト 手順 3・4・5 が共通ヘルパで実際に効いていることを固定する。
//
// 🔴 **どの試験も「適用前の既定値」を先に assert する。** 手順 4・5 は「設定してもしなくても
// 起動し、ビルドもテストも通る」種類の設定であり、適用後の値だけを見ると **ヘルパが何もしなくても
// 既定値がたまたま目的の値なら緑になる**（＝変異が当たらない試験になる）。#883 で
// 「置換が無言で no-op になり、証明したい命題を何も証明していなかった」実例を踏んでいる。
public class WolverineExtensionsTests
{
    // 手順 4 が変える状態は internal プロパティにしか現れないため、リフレクションで観測する。
    // 名前が消えれば GetProperty が null を返し、下の assert が落ちる（版更新で静かに no-op 化しない）。
    private static PropertyInfo LocalRoutingDisabledProperty =>
        typeof(WolverineOptions).GetProperty(
            "LocalRoutingConventionDisabled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "WolverineOptions.LocalRoutingConventionDisabled が見つかりません。"
            + " Wolverine の版更新で手順 4 の観測点が変わった可能性があります（ADR-0027 手順 4）。");

    [Fact]
    public void 手順4_適用前は規約ローカルルーティングが有効である()
    {
        // 変異が当たることの前提条件。既定が既に true なら手順 4 の試験は何も証明しない。
        LocalRoutingDisabledProperty.GetValue(new WolverineOptions()).Should().Be(false);
    }

    [Fact]
    public void 手順4_共通ヘルパが規約ローカルルーティングを無効化する()
    {
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        LocalRoutingDisabledProperty.GetValue(options).Should().Be(true);
    }

    [Fact]
    public void 手順5_適用前の既定はサービスロケーション禁止である()
    {
        // 既定が NotAllowed だからこそ手順 5 が要る（internal 実装型に依存するハンドラが
        // 最初のメッセージ受信時に落ちる）。ここが AlwaysAllowed に変わったら手順 5 は不要になる。
        new WolverineOptions().ServiceLocationPolicy.Should().Be(ServiceLocationPolicy.NotAllowed);
    }

    [Fact]
    public void 手順5_共通ヘルパがサービスロケーションを常時許可へ変える()
    {
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        options.ServiceLocationPolicy.Should().Be(ServiceLocationPolicy.AlwaysAllowed);
    }

    [Fact]
    public void 手順3_リスニングキュー名にサービス名を前置する()
    {
        WolverineExtensions.PlatformQueueName("wiki-service", "DocumentUpdated")
            .Should().Be("wiki-service.DocumentUpdated");
    }

    // 実際に設定されたキュー名を Wolverine の公開 API から読む。
    //
    // 🔴 **純粋関数（PlatformQueueName）の試験だけでは足りない。** #897 の監査が実測で示したとおり、
    // 適用点が `ListenToRabbitQueue(queueName)` へ退化しても——つまり前置が丸ごと消えても——
    // ビルドも 13 件のテストも検査器もすべて緑のままだった。**封じ込めるべきは名前の作り方ではなく
    // 適用点である**（IADR-0233 決定 1）以上、適用点そのものを観測しなければ守れていない。
    private static string[] RabbitQueueNamesOf(WolverineOptions options) =>
        [.. options.Transports
            .Single(t => t.Protocol == "rabbitmq")
            .Endpoints()
            .Select(e => e.EndpointName)];

    [Fact]
    public void 手順3_適用点が実際に前置つきのキューを購読する()
    {
        var options = new WolverineOptions();
        options.UseRabbitMq();

        options.ListenToPlatformQueue("wiki-service", "DocumentUpdated");

        RabbitQueueNamesOf(options).Should().Contain("wiki-service.DocumentUpdated");
    }

    [Fact]
    public void 手順3_適用点を通せば同一イベントの2購読者が別々のキューになる()
    {
        // 手順 3 が防ぐ退行そのもの。キューが 1 つに潰れると competing consumer になり、
        // 丁度 1 つだけが受信する（＝業務イベントが片方へ届かない）。
        var options = new WolverineOptions();
        options.UseRabbitMq();

        options.ListenToPlatformQueue("ingestion-service", "DocumentUpdated");
        options.ListenToPlatformQueue("wiki-service", "DocumentUpdated");

        RabbitQueueNamesOf(options).Should().Contain(
            ["ingestion-service.DocumentUpdated", "wiki-service.DocumentUpdated"]);
    }

    [Fact]
    public void 手順3_同一イベントを購読する2サービスのキュー名が分かれる()
    {
        // 手順 3 の目的そのもの。正本の pipeline.json では DocumentUpdated を
        // ingestion-service（段 ingest）と wiki-service（段 wiki-sync）が購読する。
        // キュー名が同じになると competing consumer へ退行し、丁度 1 つだけが受信する。
        var ingest = WolverineExtensions.PlatformQueueName("ingestion-service", "DocumentUpdated");
        var wikiSync = WolverineExtensions.PlatformQueueName("wiki-service", "DocumentUpdated");

        ingest.Should().NotBe(wikiSync);
    }

    [Theory]
    [InlineData("", "DocumentUpdated")]
    [InlineData("  ", "DocumentUpdated")]
    [InlineData("wiki-service", "")]
    [InlineData("wiki-service", "  ")]
    public void 手順3_サービス名かキュー名が空なら例外にする(string serviceName, string queueName)
    {
        // 空のサービス名を黙って受けると "-DocumentUpdated" のような前置なしのキュー名ができ、
        // 手順 3 を満たしていないのに満たしたつもりになる。
        var act = () => WolverineExtensions.PlatformQueueName(serviceName, queueName);

        act.Should().Throw<ArgumentException>();
    }

    // ── 再試行・デッドレターの既定：MassTransit 側 UsePlatformRetry との等価性 ─────────────
    //
    // 🔴 **観測点は Wolverine が実行時に実際に引く判定関数そのものである。**
    // FailureRuleCollection.DetermineExecutionContinuation(例外, エンベロープ) は internal だが、
    // 「n 回目の試行で何をするか」を決める唯一の関数であり、ここを読まないと
    // **「規則が登録されたこと」しか測れない**（＝挙動の等価性を測ったことにならない）。
    // 「どちらも例外を投げる」程度の粗い assert では、再試行回数も間隔もデッドレター送りの
    // 条件も 1 つも固定できない。
    //
    // 名前が消えれば GetMethod が null を返し、下の assert が落ちる（版更新で静かに no-op 化しない）。
    private static MethodInfo DetermineContinuationMethod =>
        typeof(FailureRuleCollection).GetMethod(
            "DetermineExecutionContinuation",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "FailureRuleCollection.DetermineExecutionContinuation が見つかりません。"
            + " Wolverine の版更新で再試行判定の観測点が変わった可能性があります（ADR-0027 §決定）。");

    // 試行 attempts 回目に Wolverine が選ぶ継続の型名を返す。
    private static string ContinuationNameAt(WolverineOptions options, Exception exception, int attempts)
        => ContinuationAt(options, exception, attempts).GetType().Name;

    private static object ContinuationAt(WolverineOptions options, Exception exception, int attempts)
        => DetermineContinuationMethod.Invoke(
               options.Policies.Failures, [exception, new Envelope { Attempts = attempts }])
           ?? throw new InvalidOperationException($"試行 {attempts} 回目の継続が null でした。");

    // 再試行継続が持つ待ち時間。間隔ポリシーの観測点である。
    private static TimeSpan? DelayAt(WolverineOptions options, Exception exception, int attempts)
    {
        var continuation = ContinuationAt(options, exception, attempts);
        return (TimeSpan?)continuation.GetType()
            .GetProperty("Delay", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(continuation);
    }

    // 🔴 **MassTransit 側の数値を using MassTransit; なしで読む。**
    // 本テストプロジェクトは baseline に載っていないため、`using MassTransit;` を 1 行でも書くと
    // 不採用ライブラリの残件が 13 → 14 へ**増えて** check-backend-libraries.js 規則 1 が fail する。
    // 完全修飾名で書けば素通りするが、それは検査の回避であって遵守ではない（IADR-0233 決定 6）。
    // MassTransitExtensions は本テストが既に using している名前空間の型であり、ここで触れる
    // MaxAttempts は int、RetryIntervals は TimeSpan[] —— **MassTransit の型は 1 つも現れない。**
    private static TimeSpan[] MassTransitRetryIntervals =>
        (TimeSpan[])(typeof(MassTransitExtensions).GetField(
            "RetryIntervals", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MassTransitExtensions.RetryIntervals が見つかりません。"
                + " 等価性の基準側が変わった可能性があります（FR-12, SC-07）。"))
        .GetValue(null)!;

    [Fact]
    public void 再試行既定_適用前は初回の失敗で即デッドレターへ送られる()
    {
        // 変異が当たることの前提条件。**Wolverine の既定は「再試行 0 回」である**（実測）。
        // ここが既に間隔つき再試行なら、以降の試験はヘルパが何もしなくても緑になる。
        ContinuationNameAt(new WolverineOptions(), new InvalidOperationException("x"), 1)
            .Should().Be("MoveToErrorQueue");
    }

    [Fact]
    public void 再試行既定_試行上限までは間隔つきで再試行する()
    {
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        // 初回 ＋ 再試行 3 回 ＝ 4 試行。最後の 1 回を除く各試行が「間隔つき再試行」である。
        for (var attempt = 1; attempt < WolverineExtensions.MaxAttempts; attempt++)
        {
            ContinuationNameAt(options, new InvalidOperationException("x"), attempt)
                .Should().Be("RetryInlineContinuation", $"試行 {attempt} 回目は再試行である");
        }
    }

    [Fact]
    public void 再試行既定_試行上限に達して初めてデッドレターへ移る()
    {
        // 境界の鋭さを固定する。1 つ手前は再試行、上限とその先はデッドレター。
        // 「いつか落ちる」ではなく「**丁度ここで**落ちる」を測る。
        var options = new WolverineOptions();

        options.UsePlatformMessagingDefaults();

        ContinuationNameAt(options, new InvalidOperationException("x"), WolverineExtensions.MaxAttempts - 1)
            .Should().Be("RetryInlineContinuation", "上限の 1 つ手前はまだ再試行である");
        ContinuationNameAt(options, new InvalidOperationException("x"), WolverineExtensions.MaxAttempts)
            .Should().Be("MoveToErrorQueue", "上限に達した失敗はデッドレターへ送る");
        ContinuationNameAt(options, new InvalidOperationException("x"), WolverineExtensions.MaxAttempts + 1)
            .Should().Be("MoveToErrorQueue", "上限を超えても再試行へ戻らない");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(HttpRequestException))]
    public void 再試行既定_例外の種類によらず同じ判定になる(Type exceptionType)
    {
        // MassTransit の UseMessageRetry(r => r.Intervals(...)) は例外型で絞らない。
        // Wolverine 側も OnAnyException でなければ等価にならない（型を絞ると
        // 「大半の障害が再試行されない」形で静かに非等価になる）。
        var options = new WolverineOptions();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        options.UsePlatformMessagingDefaults();

        ContinuationNameAt(options, exception, 1).Should().Be("RetryInlineContinuation");
        ContinuationNameAt(options, exception, WolverineExtensions.MaxAttempts)
            .Should().Be("MoveToErrorQueue");
    }

    [Fact]
    public void 等価性_試行上限がMassTransit側と一致する()
    {
        // FR-12, SC-07: 移行の前後で「1 通が何回試されるか」が変わってはならない。
        WolverineExtensions.MaxAttempts.Should().Be(MassTransitExtensions.MaxAttempts);
    }

    [Fact]
    public void 等価性_再試行の間隔がMassTransit側と一致する()
    {
        // 実際に設定された間隔を Wolverine の判定関数から読み、MassTransit 側の間隔表と突き合わせる。
        // 🔴 **両側の値は独立に置いてある**（共有すると片側を変えた変異が素通りする）。
        var options = new WolverineOptions();
        var expected = MassTransitRetryIntervals;

        options.UsePlatformMessagingDefaults();

        expected.Should().HaveCount(WolverineExtensions.MaxAttempts - 1,
            "MassTransit 側は間隔の要素数がそのまま再試行回数である");
        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            DelayAt(options, new InvalidOperationException("x"), attempt)
                .Should().Be(expected[attempt - 1], $"試行 {attempt} 回目の待ち時間");
        }
    }

    [Fact]
    public void 等価性_試行上限の絶対値を固定する()
    {
        // 上の 2 件は両側の**一致**を測る。両側を同時に変えた場合は一致したまま通るため、
        // 絶対値をここで 1 度だけ釘付けする（初回 1 回 ＋ 再試行 3 回）。
        WolverineExtensions.MaxAttempts.Should().Be(4);
        MassTransitRetryIntervals.Should().Equal(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    // --- ADR-0027 手順 3（発行側の経路）/ #992・[[IADR-0312]] ---------------------------
    //
    // 🔴 **ここが無いと Wolverine の発行は 1 通もブローカへ出ない。** 手順 4 でプロセス内経路を
    // 切ったうえで外向きの経路を誰も宣言しなければ、Wolverine は宛先を決められず
    // `No routes can be determined for Envelope ...` を info ログへ 1 行出して捨てる。
    // 例外もヘルスチェックの赤も出ないため、**本番だけが静かに壊れる**（稼働 k3s で実測）。
    private sealed record ProbeEvent(Guid Id);

    private static string[] RabbitEndpointUrisOf(WolverineOptions options) =>
        [.. options.Transports
            .Single(t => t.Protocol == "rabbitmq")
            .Endpoints()
            .Select(e => e.Uri.ToString())];

    [Fact]
    public void 手順3発行側_適用前は外向きの経路が1つも無い()
    {
        // 変異が当たることの前提条件。既定で経路が生えるなら、この配線の試験は何も証明しない。
        var options = new WolverineOptions();
        options.UseRabbitMq();

        RabbitEndpointUrisOf(options).Should().NotContain(u => u.Contains(nameof(ProbeEvent)));
    }

    [Fact]
    public void 手順3発行側_共通ヘルパがイベント型名のexchangeへ経路を宣言する()
    {
        var options = new WolverineOptions();
        options.UseRabbitMq();

        options.RoutePlatformEvent<ProbeEvent>();

        RabbitEndpointUrisOf(options).Should().Contain(u => u.Contains("exchange/" + nameof(ProbeEvent)));
    }

    [Fact]
    public void 手順3_exchange名にサービス名を前置しない()
    {
        // 前置すると購読側が束ねた exchange と食い違い、やはり「誰にも届かない」形になる。
        // キュー名（前置する）との非対称は意図的である。
        WolverineExtensions.PlatformExchangeName<ProbeEvent>().Should().Be(nameof(ProbeEvent));
        WolverineExtensions.PlatformExchangeName<ProbeEvent>().Should().NotContain("-service");
    }

    [Fact]
    public void 手順3購読側_束ねる先が発行側のexchangeと一致する()
    {
        // 🔴 発行側と購読側が**同じ名前を導く**ことがこの配線の全てである。
        // 片方だけ変えると、キューは在り exchange も在るのに 1 通も届かない。
        var publisher = new WolverineOptions();
        publisher.UseRabbitMq();
        publisher.RoutePlatformEvent<ProbeEvent>();

        var subscriber = new WolverineOptions();
        subscriber.UseRabbitMq().BindPlatformQueue<ProbeEvent>("ingestion-service", nameof(ProbeEvent));

        RabbitEndpointUrisOf(publisher).Should().Contain(u => u.Contains("exchange/" + nameof(ProbeEvent)));
        RabbitEndpointUrisOf(subscriber).Should().Contain(u => u.Contains("exchange/" + nameof(ProbeEvent)));
    }

    [Fact]
    public void 手順3購読側_束ねてもキュー名の前置は保たれる()
    {
        // fan-out の保存は「キュー名が分かれている」と「同じ exchange に束ねられている」の
        // 両方で成り立つ。束ねる過程で前置が失われると competing consumer へ退行する。
        var options = new WolverineOptions();
        options.UseRabbitMq()
            .BindPlatformQueue<ProbeEvent>("ingestion-service", nameof(ProbeEvent))
            .BindPlatformQueue<ProbeEvent>("wiki-service", nameof(ProbeEvent));

        var uris = RabbitEndpointUrisOf(options);
        uris.Should().Contain(u => u.Contains("ingestion-service." + nameof(ProbeEvent)));
        uris.Should().Contain(u => u.Contains("wiki-service." + nameof(ProbeEvent)));
    }
}
