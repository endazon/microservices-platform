using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Knowledge.Bff.Endpoints.Usage;

// FR-10, SC-10, [[IADR-0343]] (#1103): 利用状況イベント 1 件ぶんの送出指示。
//
// 🔴 **HttpContext を持ち回らない。** 送出は要求の応答が終わったあとに別のスレッドで走るため、
// そのときには HttpContext は破棄され得る。必要な値（種別・検索語・資格情報）を**文字列として
// 写し取ってから**列へ載せる。
//
// `Query` は種別が `search` のときだけ意味を持つ（受け口は `answer` では捨てる）。
// **`answer` では null を渡す** —— 捨てられる値を経路とログに晒す理由が無い（決定 5）。
//
// NFR-02, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203): `IsSynthetic` は合成監視のトラフィックか。
// 🔴 **判定は呼び出し元（BFF の端点）が検証済み JWT の主体から行い、ここへは結果だけを運ぶ。**
// 受信ヘッダから決めてはならない —— 外から印を付けて実利用を費用・集計から隠せてしまう。
public sealed record UsageEventSignal(
    string EventType, string? Query, string? Authorization, bool IsSynthetic = false);

// FR-10, SC-10, [[IADR-0343]] 決定 3 (#1103): 発火の口。**同期・O(1)・例外を投げない。**
//
// 呼び出し元（検索・回答の各エンドポイント）は本処理の成功後にこれを 1 回呼ぶだけであり、
// 送出の往復を待たない —— NFR の検索 p95 1.5s に計測の往復を載せないためである。
public interface IUsageEventReporter
{
    void Report(UsageEventSignal signal);
}

// FR-10, SC-10, [[IADR-0343]] 決定 3: 送出待ちの有界列。Reporter（書き手）と Dispatcher（読み手）が
// 共有するため singleton で持つ。
//
// ★ **`FullMode` は `Wait` だが `TryWrite` しか使わない。** `DropWrite` にすると溢れても
// `TryWrite` が true を返すため、**捨てたことを数えられなくなる**（それでは計器の意味が無い）。
// `Wait` ＋ `TryWrite` なら満杯のとき false が返り、呼び出し元を待たせずに落ちたことが判る。
public sealed class UsageEventQueue
{
    // 送出待ちの上限。受け口が不調なとき、これを超えたぶんは `dropped` として数えて捨てる。
    public const int Capacity = 1024;

    private readonly Channel<UsageEventSignal> _channel = Channel.CreateBounded<UsageEventSignal>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelWriter<UsageEventSignal> Writer => _channel.Writer;
    public ChannelReader<UsageEventSignal> Reader => _channel.Reader;
}

// FR-10, SC-10, [[IADR-0343]] 決定 3・4 (#1103): 発火の実装。列へ載せるだけで送出はしない。
public sealed class UsageEventReporter(
    UsageEventQueue queue,
    UsageEventMetrics metrics,
    ILogger<UsageEventReporter> logger) : IUsageEventReporter
{
    public void Report(UsageEventSignal signal)
    {
        // NFR-02, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
        // 🔴 **合成監視のトラフィックはここで落とす。** `IUsageEventReporter.Report` は
        // 利用状況イベントの**唯一の発火の口**であり、ここを通さなければ `UsageEvents` に行は入らない。
        // 行が入らなければ SC-10 の利用状況も、ADR-0071 のしきい値（出現件数 3 件）を通る語も生じない
        // —— **検索傾向の側に独立した除外を置かなくてよい**のはこのためである（作業仕様書 §母集合 ②）。
        //
        // 🔴 **黙って捨てない。** 落とした件数を `excluded_synthetic` として数える。数えないと
        // 「合成だけが通っていて実利用は 0」でも計器が緑に見え、#1103 が直した「0 件が正常に見える」形を作り直す。
        if (signal.IsSynthetic)
        {
            metrics.RecordDispatch(signal.EventType, UsageEventMetrics.OutcomeExcludedSynthetic);
            return;
        }

        if (queue.Writer.TryWrite(signal))
            return;

        // 🔴 溢れは**黙って捨てない**。ここが埋まるのは受け口の不調が続いたときであり、
        // 「利用状況が伸びない」の原因として最初に見るべき数である。
        metrics.RecordDispatch(signal.EventType, UsageEventMetrics.OutcomeDropped);
        logger.LogError(
            "利用状況イベントの送出待ちが溢れたため 1 件を捨てた（capacity={Capacity}）。eventType={EventType}。"
            + "検索語と利用者は本文へ出さない。",
            UsageEventQueue.Capacity, signal.EventType);
    }
}
