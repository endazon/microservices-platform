using System.Globalization;

namespace NotificationService.Domain;

// FR-22, ADR-0037 決定 6, IADR-0215 決定 2: メール本文の組み立て。
//
// ★ **メールは本システムの ABAC の外側へ出る。** よって本文に入れてよいのは
// **種別・件数・閾値・期限だけ**であり、資料のタイトル・本文・検索語・回答内容は入れない。
// 材料が `EmailOutboxEntry` の 4 つしか無いので、**入れようとしても入れる値が無い** ——
// 文言の規律ではなく型の形で守っている（アプリ内通知の DTO と同じ作法）。
//
// **文言は機能仕様書 §文言の組み立て規則 の ja に揃える。** フロントは Lingui カタログから
// 組み立てるが、メールは受信者の言語設定を知らないため、送出側のテンプレートを使う。
public static class NotificationMailBody
{
    public static string Compose(EmailOutboxEntry entry)
    {
        var count = entry.Count?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var threshold = entry.ThresholdPercent?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var deadline = entry.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";

        return entry.Kind switch
        {
            NotificationKinds.PrivateNotePurgeWeekly =>
                $"削除済みの個人資料が {count} 件あります。最短で {deadline} に完全削除されます",
            NotificationKinds.PrivateNotePurgeImminent =>
                $"個人資料 {count} 件が {deadline} に完全削除されます（7 日前の通知）",
            NotificationKinds.PrivateNotePurgeDone =>
                $"個人資料 {count} 件を完全削除しました",
            NotificationKinds.StorageQuotaWarning =>
                $"保存容量が上限の {threshold}% に達しました",
            NotificationKinds.SyncTokenExpiry =>
                $"同期トークン {count} 件が {deadline} に期限切れになります（7 日前の通知）",
            // ★ 既定枝を持つ。`Kind` は閉じた enum ではないので、後段が種別を増やしても
            //   本文が空になったり例外になったりしない（IADR-0215 決定 2 と同じ理由）。
            _ => $"新しいお知らせが {count} 件あります",
        };
    }
}
