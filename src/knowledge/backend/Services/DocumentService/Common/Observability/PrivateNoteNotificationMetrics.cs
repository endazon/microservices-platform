using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DocumentService.Common.Observability;

// FR-22, NFR-19, ADR-0045 決定 8, IADR-0215 決定 5-b（2026-08-28 追記 / #600）:
// **通知の発火側（送出）の結末を計器に載せる。**
//
// ★ **ADR-0045 決定 8「静かに落ちない」の発火側の対である。** 送出側（NotificationService）は
// メールの 4 結末を計器へ載せているが、**受け口へ届かなかった通知はどこにも数えられていなかった** ——
// 送出は fail-open（業務処理を止めない）であり、失敗はエラーログにしか残らないためである。
// **落ちたことが運用ダッシュボードに出ない fail-open は「静かに落ちる」と同じである。**
//
// ★ **3 つの結末を 1 本のカウンタの属性で分ける**（NotificationDeliveryMetrics と同じ形）。
// 結末ごとに計器を分けると「送れたぶん」だけを見て安心できてしまう。同じ計器の内訳にすれば
// 分母と分子が並ぶ。
//
// 🔴 **利用者識別子（subject）を属性にしない。** カーディナリティが非有界であり、個人の利用行動の
// 記録に踏み込む（NotificationDeliveryMetrics / LlmUsageMetrics と同じ規律。ADR-0044 決定 1）。
// 属性は種別と結末だけである。
public sealed class PrivateNoteNotificationMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象。IngestTagMetrics と同じ器）。
    public const string MeterName = "microservices-platform.document-service";

    public const string DispatchCounterName = "notification.dispatch.total";

    public const string KindTag = "notification.kind";
    public const string OutcomeTag = "notification.outcome";

    /// <summary>受け口が 2xx を返した（＝アプリ内通知として永続化された）。</summary>
    public const string OutcomeSent = "sent";

    /// <summary>受け口へ届いたが拒否された（非 2xx）。ペイロードか配備の不整合を疑う。</summary>
    public const string OutcomeRejected = "rejected";

    /// <summary>受け口へ届かなかった（未配備・通信エラー・タイムアウト・想定外の例外）。</summary>
    public const string OutcomeUnreachable = "unreachable";

    private readonly Counter<long> _dispatch;

    public PrivateNoteNotificationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _dispatch = meter.CreateCounter<long>(
            DispatchCounterName, unit: "{notification}",
            description: "個人資料まわりの通知の送出結果の件数。notification.outcome = sent / rejected / "
                       + "unreachable。**送れなかったぶんが必ずここに載る**（fail-open が静かに落ちない"
                       + "ための計器）。");
    }

    // kind の値域は開放されている（IADR-0215 決定 2）が、**発火側が送る値は
    // PrivateNoteNotificationKinds の 5 値に閉じている**（呼び出し元が本サービス内だけであるため）。
    // 基数は有界であり、丸めない。
    public void RecordDispatch(string kind, string outcome)
        => _dispatch.Add(1, new TagList { { KindTag, kind }, { OutcomeTag, outcome } });
}
