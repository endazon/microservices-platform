using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Knowledge.IntegrationTests.Messaging;

// ADR-0027, IADR-0245, #1038: **購読の準備完了を待ってから発行する**ための器。
//
// ■ なぜ要るのか
//   fan-out の統合テスト 2 件は「発行 → 終端の副作用」を 30 秒で待っていた。この 30 秒は
//   ①ブローカへの接続確立 ②コンシューマの購読開始 ③実処理（Postgres 書き込み）の**3 つを
//   ひとまとめに覆っていた**ため、全 70 件の並行実行で機械が混むと ①② が食い潰し、
//   **実装が正しいのに落ちた**（#1038。文書だけの差分で結果が変わったことが非決定性の証拠）。
//
//   🔴 **待ち時間を伸ばす直し方を採らない。** 伸ばせば落ちなくなるだろうが、
//   **本当に遅くなっているのか（＝製品側の劣化）を見逃す**。#1038 が明示的に禁じている。
//   代わりに **①② を独立した待ち合わせへ切り出し、③ の予算は 30 秒のまま据え置く。**
//   落ちた場所がそのまま「どの段が遅いか」の答えになる。
//
// ■ 器の実測（Docker 不要の範囲で確かめたもの）
//   - RabbitMQ のキューエンドポイントの Uri は `rabbitmq://queue/<キュー名>` である
//     （`RabbitMqTransport.Queues[name].Uri` を実際に生成して確認した）
//   - `WolverineTracker.WaitForListenerStatusAsync` は**期限切れで `TimeoutException` を投げる**
//     （実測: 存在しない Uri に 2 秒で待つと 2.0 秒後に throw）。**待って諦めて緑にはならない。**
//   - 未知のエンドポイントの `StatusFor` は `ListeningStatus.Unknown` を返す
//
// 🔴 **ここで測れるのは「購読が始まったか」までである。** 実ブローカ上での到達時間の分布は
// Docker のある環境（`integration.yml`）でしか測れない。**本器は測定を可能にするものであって、
// 測定そのものではない**（#1038 の受け入れ基準①③は依然として実走が要る）。
internal static class ListenerReadiness
{
    // ①② に与える予算。**③ の 30 秒とは別物**であり、混ぜてはならない。
    // ホスト 2 つ ＋ Testcontainers の Postgres / RabbitMQ が同時に立ち上がる状況を覆う。
    internal static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(120);

    internal static Uri QueueUri(string queueName) => new($"rabbitmq://queue/{queueName}");

    // 指定キューのリスナーが `Accepting` になるまで待ち、**掛かった時間を返す**。
    // 返り値は測定値であり、テスト側が失敗メッセージと出力へ載せる。
    internal static async Task<TimeSpan> WaitUntilAcceptingAsync(
        IServiceProvider services, string queueName, TimeSpan? budget = null)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();
        var uri = QueueUri(queueName);
        var limit = budget ?? StartupBudget;
        var elapsed = Stopwatch.StartNew();
        try
        {
            await runtime.Tracker.WaitForListenerStatusAsync(uri, ListeningStatus.Accepting, limit);
        }
        catch (TimeoutException)
        {
            // 🔴 握り潰さない。購読が始まっていなければ、この先の待ち合わせは
            // 「購読開始待ち」と「処理待ち」を区別できず、測定の意味が失われる。
            throw new TimeoutException(
                $"{uri} のリスナーが {limit.TotalSeconds:F0} 秒以内に Accepting にならなかった"
                + $"（実測 {elapsed.Elapsed.TotalSeconds:F1} 秒 / 現在の状態 {runtime.Tracker.StatusFor(uri)}）。"
                + " これは fan-out の退行ではなく**購読開始が遅い**ことを指す（#1038 の切り分け ①②）。");
        }
        return elapsed.Elapsed;
    }

    // ［2026-08-30 / #1038 #1059］**予算切れの瞬間に、ホストが実際に購読しているキュー名を採る。**
    //
    // 🔴 これは #1038 の最新の観測が「次の一手」として名指ししたものである ——
    // 現在の失敗メッセージは「実処理側の遅さか受信そのものの欠落である」までしか言えず、
    // **次に落ちたときも同じ場所から仮説を建て直すことになる**（既に 5 ラウンド繰り返している）。
    //
    //   キュー名が 2 本に分かれている → 前置は効いている。原因は配送／処理側
    //   キュー名が 1 本に潰れている   → **競合コンシューマ化**（本命仮説）
    //
    // **1 回の実走でこの 2 つが分かれる。**
    //
    // ⚠️ ここで例外を投げない。診断が assert の失敗理由を覆い隠すと、
    // **本来の失敗（fan-out が届かない）が別の例外にすり替わる。** 採れなかったことは
    // 「採れなかった」と書いて返す —— 黙って空にはしない（それは「購読ゼロ」と区別が付かない）。
    // ［2026-08-30 / #1073］**ホストがハンドラを探したアセンブリ名を失敗メッセージへ載せる。**
    //
    // 🔴 Wolverine の `ApplicationAssembly` は**プロセス全体で 1 つの静的値**であり、
    // 明示しない限り**そのプロセスで最初に起動したホスト**のアセンブリに固定される
    // （Wolverine 6.24.4 / GH-3521）。後発のホストは**相手のハンドラを拾い、自分のハンドラを拾わない**。
    // そのとき受信は `NotSupportedException`（依存未解決）で落ち、Wolverine は
    // **ack して捨てる** —— 例外も再配信もデッドレターも残らない。
    // 症状は「キュー名は正しい・購読は Accepting・30 秒待って何も起きない」であり、
    // **配送の欠落と見分けがつかない**。#1038 / #1059 / #1073 の 3 件・6 ラウンドは
    // この 1 行が無かったために費やされた。**原因を名乗るのはキュー名ではなくこちらである。**
    internal static string DescribeHandlerDiscovery(IServiceProvider services)
    {
        try
        {
            var runtime = services.GetRequiredService<IWolverineRuntime>();
            return runtime.Options.ApplicationAssembly?.GetName().Name ?? "(未設定)";
        }
        catch (Exception ex)
        {
            return $"(採れなかった: {ex.GetType().Name})";
        }
    }

    internal static string DescribeListeners(IServiceProvider services)
    {
        try
        {
            var runtime = services.GetRequiredService<IWolverineRuntime>();
            var listeners = runtime.Options.Transports
                .Where(t => t.Protocol == "rabbitmq")
                .SelectMany(t => t.Endpoints())
                .Where(e => e.IsListener)
                .Select(e => $"{e.Uri}={runtime.Tracker.StatusFor(e.Uri)}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            return listeners.Length == 0
                ? "購読キュー無し（rabbitmq のリスナーが 1 本も登録されていない）"
                : string.Join(" , ", listeners);
        }
        catch (Exception ex)
        {
            return $"購読キューを列挙できなかった（{ex.GetType().Name}: {ex.Message}）";
        }
    }
}
