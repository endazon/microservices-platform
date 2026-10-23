using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NotificationService.Common.Observability;

// FR-22, SC-10, ADR-0045 決定 8, IADR-0215 決定 4: 通知の送出結果を計器に載せる。
//
// ★ **4 つの結末（sent / deferred / dropped / failed）を 1 本のカウンタの属性で分ける。**
// 計器を結末ごとに分けると「送れたぶん」だけを見て安心できてしまう。同じ計器の内訳にすれば、
// **運用ダッシュボード（SC-10）で分母と分子が並ぶ**。
//
// **利用者識別子は属性にしない。** カーディナリティが非有界であり、個人の利用行動の記録に
// 踏み込む（LlmUsageMetrics と同じ規律。ADR-0044 決定 1）。属性は種別と結末だけである。
public sealed class NotificationDeliveryMetrics
{
    public const string MeterName = "microservices-platform.notification-service";

    // アプリ内通知の永続化件数。**「通知が届いた」の定義はここまでである**（IADR-0215 決定 3）。
    public const string InAppCounterName = "notification.inapp.total";

    // メール送出の結末。属性 notification.outcome で 4 値に分かれる。
    public const string EmailCounterName = "notification.email.total";

    // 日次上限に触れた事象そのもの。**繰り越した件数とは別に、上限到達を 1 事象として数える**
    // （ADR-0045 決定 8。「上限に当たっている」ことがダッシュボードで先に見える）。
    public const string LimitReachedCounterName = "notification.email.limit_reached";

    // FR-22, IADR-0600 決定 3: **一過性の失敗の再試行を使い切って `failed` にした件数。**
    // 結末としては notification.email.total{outcome=failed} にも 1 件載る。別の計器にするのは、
    // 「恒久的な失敗（SMTP 未構成・宛先未解決）が出続けている」と「リレーが上限を超えて落ち続けた」を
    // ダッシュボードで分けて読むためである —— 前者は常時出る（未構成のあいだ）ので、同じ線に混ぜると
    // 後者が埋もれる。
    public const string RetryExhaustedCounterName = "notification.email.retry_exhausted";

    public const string KindTag = "notification.kind";
    public const string OutcomeTag = "notification.outcome";

    private readonly Counter<long> _inApp;
    private readonly Counter<long> _email;
    private readonly Counter<long> _limitReached;
    private readonly Counter<long> _retryExhausted;

    public NotificationDeliveryMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _inApp = meter.CreateCounter<long>(
            InAppCounterName, unit: "{notification}",
            description: "永続化されたアプリ内通知の件数（種別別）。メールの成否に影響されない。");
        _email = meter.CreateCounter<long>(
            EmailCounterName, unit: "{notification}",
            description: "メール送出の結末の件数。notification.outcome = sent / deferred / dropped / failed。"
                       + "**送れなかったぶんが必ずここに載る**（静かに落ちないための計器）。");
        _limitReached = meter.CreateCounter<long>(
            LimitReachedCounterName, unit: "{event}",
            description: "日次送信上限に触れた事象の件数。0 でない値は上限の見直しが要ることの信号である。");
        _retryExhausted = meter.CreateCounter<long>(
            RetryExhaustedCounterName, unit: "{notification}",
            description: "一過性の送信失敗の再試行を上限まで使い切り failed にした件数（種別別）。"
                       + "0 でない値は送信基盤（SMTP リレー）の障害が再試行の窓より長く続いたことの信号である。");
    }

    // kind の値域は開放されている（IADR-0215 決定 2。受け口 `NotificationIngress/Accept` は値域検証を
    // 置かない）ため、このタグは**受け口の呼び出し元が基数を増やせる**。既知集合へ丸めず受容する ——
    // 受け口はゲートウェイ非公開の内部経路で、長さ 100 字上限・制御文字除去済みであり、丸めると
    // 新種別の追加のたびに本クラスの追随が要る（値域を開いた同決定の理由がここにも当たる）。
    // 波 2 クロス監査の指摘 🔵B の記録（`.ai-context/specs/20260828_wave2-audit-followup.md`）。
    public void RecordInApp(string kind)
        => _inApp.Add(1, new TagList { { KindTag, kind } });

    public void RecordEmailOutcome(string kind, string outcome)
        => _email.Add(1, new TagList { { KindTag, kind }, { OutcomeTag, outcome } });

    public void RecordLimitReached()
        => _limitReached.Add(1);

    public void RecordRetryExhausted(string kind)
        => _retryExhausted.Add(1, new TagList { { KindTag, kind } });
}
