using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Knowledge.Bff.Endpoints.Usage;

// FR-10, SC-10, ADR-0006, ADR-0044 決定 1, [[IADR-0343]] 決定 4 (#1103):
// **利用状況イベントの送出の結末を計器に載せる。**
//
// ★ 送出は fail-open（計測の失敗で検索・回答を落とさない）である。**落ちたことがどこにも
// 数えられない fail-open は「静かに落ちる」と同じ**であり、#1103 が直そうとしている
// 「0 件が正常に見える」形をそのまま作り直してしまう。`PrivateNoteNotificationMetrics` と
// 同じ理由・同じ形である。
//
// ★ **4 つの結末を 1 本のカウンタの属性で分ける。** 結末ごとに計器を分けると「送れたぶん」
// だけを見て安心できてしまう。同じ計器の内訳にすれば分母と分子が並ぶ。
//
// 🔴 **利用者識別子も検索語も属性にしない。** 前者はカーディナリティが非有界で個人の利用行動の
// 記録に踏み込む（ADR-0044 決定 1・計画 §SC-10 Q27 の理由 ②）。後者は本文そのものである
// （ADR-0006 §結果「ログに本文・機密情報を出力しない」）。属性は種別と結末だけである。
public sealed class UsageEventMetrics
{
    // Meter 名は BFF のサービス名と一致させる（Program.cs の AddMeter と OTLP の収集対象）。
    public const string MeterName = "microservices-platform.bff";

    public const string DispatchCounterName = "usage.event.dispatch.total";

    public const string EventTypeTag = "usage.event.type";
    public const string OutcomeTag = "usage.event.outcome";

    /// <summary>受け口が 2xx を返した（＝ UsageEvents に 1 行入った）。</summary>
    public const string OutcomeSent = "sent";

    /// <summary>受け口へ届いたが拒否された（非 2xx）。資格情報か配備の不整合を疑う。</summary>
    public const string OutcomeRejected = "rejected";

    /// <summary>受け口へ届かなかった（未配備・通信エラー・タイムアウト・想定外の例外）。</summary>
    public const string OutcomeUnreachable = "unreachable";

    /// <summary>送出待ちの列が溢れて捨てた（受け口の不調が続いたとき最初に現れる）。</summary>
    public const string OutcomeDropped = "dropped";

    /// <summary>
    /// NFR-02, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視のトラフィックだったため
    /// **意図的に発火しなかった**（`UsageEvents` に行を作らなかった）。
    /// 🔴 **`dropped` と混ぜない** —— あちらは不調による取りこぼしで、こちらは設計どおりの除外である。
    /// 混ぜると「除外が効いている」と「受け口が壊れている」を同じ数で読むことになる。
    /// **この系列が伸び、かつ `sent` が伸びないときは「合成だけが通っていて実利用が 0」である。**
    /// </summary>
    public const string OutcomeExcludedSynthetic = "excluded_synthetic";

    private readonly Counter<long> _dispatch;

    public UsageEventMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _dispatch = meter.CreateCounter<long>(
            DispatchCounterName, unit: "{event}",
            description: "利用状況イベント（POST /dashboard/events）の送出結果の件数。"
                       + "usage.event.outcome = sent / rejected / unreachable / dropped / excluded_synthetic。"
                       + "**送れなかったぶんが必ずここに載る**（fail-open が静かに落ちないための計器）。"
                       + "excluded_synthetic は合成監視のため意図的に発火しなかったぶん（ADR-0076 決定 4）。");
    }

    // 種別の値域は受け口の契約（UsageEventType）に閉じた 2 値であり、基数は有界である。
    public void RecordDispatch(string eventType, string outcome)
        => _dispatch.Add(1, new TagList { { EventTypeTag, eventType }, { OutcomeTag, outcome } });
}
