import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { NotificationDto } from '@foundation/api/generated/bff.schemas';
import { formatDateTime } from '@foundation/ui/formatDateTime';

// FR-22, UC-11, IADR-0215 決定 2: **アプリ内通知の表示文言をここで組み立てる。**
//
// ★ 契約（`NotificationDto`）には**タイトル・本文に相当する項目が 1 つも無い**。
// 計画 FR-22 の受け入れ基準「本文が件数と期限のみで構成される。資料のタイトル・本文・検索語・
// 回答内容を含まない」を、実装の規律ではなく**契約の形**で守らせているためである
// （ADR-0037 決定 6 / IADR-0215 決定 2）。**その帰結として、文言はクライアント側の資源になる。**
// この不変条件そのものは notificationContract.test.ts が固定する。
//
// **`@platform/ui` には表示文言を入れない**（IADR-0125 決定 1）ので、文言はすべてここに置く。
// 入力は種別（`kind`）・件数（`count`）・閾値（`thresholdPercent`）・期限（`deadline`）だけであり、
// **資料名・トークン名・本文の断片はどの文言にも現れない。**

/** `StatusBadge` / `Alert` の tone。**色だけで意味を持たせない**ため、必ずラベルと対で使う。 */
export type NotificationTone = 'neutral' | 'warning' | 'danger';

/**
 * 通知の文言。**件数・閾値・期限だけ**から組み立てる。
 *
 * `default` 枝は「契約が種別を増やしたが SPA がまだ再デプロイされていない」場合のためである
 * （**起こり得ないケースへの防御ではない** —— 契約とクライアントは別々に配布される）。
 * その場合も**共通シェルを壊さず、件数だけを出す**。
 */
export function notificationText(notification: NotificationDto): string {
  const count = notification.count ?? 0;
  const deadline = formatDateTime(notification.deadline);
  const threshold = notification.thresholdPercent ?? 0;

  switch (notification.kind) {
    // ①-a ADR-0037 決定 6: 論理削除済み資料の週次通知（残り日数を含める）。
    case 'private-note-purge-weekly':
      return i18n._(
        msg`削除済みの個人資料が ${count} 件あります。最短で ${deadline} に完全削除されます`,
      );
    // ①-b ADR-0037 決定 6: 完全削除の 7 日前の別建て通知。
    case 'private-note-purge-imminent':
      return i18n._(msg`個人資料 ${count} 件が ${deadline} に完全削除されます（7 日前の通知）`);
    // ①-c ADR-0037 決定 6: 完全削除の事後通知（期限は無い）。
    case 'private-note-purge-done':
      return i18n._(msg`個人資料 ${count} 件を完全削除しました`);
    // ② FR-19 / ADR-0037 決定 17: 保存容量の警告（80% / 95% で各 1 回。件数も期限も持たない）。
    case 'storage-quota-warning':
      return i18n._(msg`保存容量が上限の ${threshold}% に達しました`);
    // ③ ADR-0037 決定 18: 同期トークンの期限予告（7 日前。当日通知は設けない）。
    case 'sync-token-expiry':
      return i18n._(
        msg`同期トークン ${count} 件が ${deadline} に期限切れになります（7 日前の通知）`,
      );
    default:
      return i18n._(msg`新しいお知らせが ${count} 件あります`);
  }
}

/**
 * 通知の tone。**色は意味の担い手ではない**（`notificationToneLabel` のテキストが担う）。
 *
 * 容量警告だけ閾値で分けるのは、95% が「新規作成が止まる 100% の直前」だからである
 * （ADR-0037 決定 17: 100% で新規作成のみ拒否）。
 */
export function notificationTone(notification: NotificationDto): NotificationTone {
  switch (notification.kind) {
    case 'private-note-purge-imminent':
    case 'sync-token-expiry':
      return 'warning';
    case 'storage-quota-warning':
      return (notification.thresholdPercent ?? 0) >= 95 ? 'danger' : 'warning';
    default:
      return 'neutral';
  }
}

/**
 * tone のテキストラベル。**色だけで意味を持たせない**ための 3 点セット（色 ＋ アイコン ＋ テキスト）の
 * うちテキストを担う（INDEX 決定 21 / IADR-0125 決定 1）。アイコンは `StatusBadge` が tone から固定で付ける。
 */
export function notificationToneLabel(tone: NotificationTone): string {
  switch (tone) {
    case 'danger':
      return i18n._(msg`重要`);
    case 'warning':
      return i18n._(msg`注意`);
    default:
      return i18n._(msg`お知らせ`);
  }
}
