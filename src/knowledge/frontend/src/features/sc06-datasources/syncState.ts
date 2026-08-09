import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-06, UC-04, FR-01/FR-02: データソースの同期状態と種別の写像（純関数）。
//
// IADR-0127 決定 2: **契約から導出できる値だけ**で状態を表示する。
// **［2026-08-08 / #537］同期健全性が契約に載ったので、予約していた琥珀の充て先が確定した。**
// `DataSourceDto` は `consecutiveFailureCount` / `retryLimit` / `lastSyncError` を持つ
// （利用者裁定 2026-08-05 質問票 第12回 Q14）。hi-fi の「⚠ 再試行中（3/5）」はこれで描ける。

/**
 * `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。
 *
 * **`warning`（琥珀）は同期健全性へ充てる**（05_screens モック間相違の確定 ②「SC-06 の**同期異常表示**の
 * 警告色＝琥珀」）。無効（`disabled`）は**中立のまま**である —— 管理者が意図して無効化した正常な
 * 設定状態に「⚠」を付けない（IADR-0127 決定 2 の判断はそのまま活きており、計画も「妥当であり
 * そのまま活かす」と明記している）。
 */
export type SyncTone = 'neutral' | 'success' | 'warning';

export interface SyncStateView {
  label: MessageDescriptor;
  tone: SyncTone;
  /** 同期済みのときだけ日時を添える（モックの「✓ 正常（07-24 03:00）」に対応）。 */
  showSyncedAt: boolean;
}

/** SC-06（Q14 / #537）: 契約が返す同期健全性のうち、状態表示に要る 2 項目。 */
export interface SyncHealth {
  consecutiveFailureCount?: number | null;
  retryLimit?: number | null;
}

/**
 * 同期状態を表示の組（文言 ＋ tone）へ写像する。
 *
 * 優先順位は **無効 → 継続失敗 → 再試行中 → 同期済み → 未同期** である。
 * 健全性を状態より先に見ないのは、**無効化されたソースは同期が回らない**ため、残っている失敗回数を
 * 異常として出し続けると「管理者が意図して止めた正常な状態」に ⚠ が付くからである
 * （IADR-0127 決定 2 が `disabled` を中立に置いた理由と同じ）。
 *
 * **しきい値は契約が返す `retryLimit` である**（計画 §SC-06「「継続失敗」のしきい値は再試行上限に
 * 達した時点とする」）。画面に定数を複写しない —— 複写すると同じ数が 2 箇所に立ち、
 * サーバ側を変えたときに黙って割れる。
 */
export function syncStateView(
  status: string,
  lastSyncedAt: string | null | undefined,
  health?: SyncHealth,
): SyncStateView {
  if (status === 'disabled') {
    return { label: msg`無効`, tone: 'neutral', showSyncedAt: false };
  }

  const failures = health?.consecutiveFailureCount ?? 0;
  const limit = health?.retryLimit ?? 0;

  // UC-04 例外フロー: 再試行上限に達した継続失敗。アラートが出ている状態と同じ判定である。
  if (failures > 0 && limit > 0 && failures >= limit) {
    return { label: msg`同期異常（${failures}/${limit}）`, tone: 'warning', showSyncedAt: false };
  }
  // hi-fi の「⚠ 再試行中（3/5）」。上限までは再試行が続くが、既に静かに壊れかけている。
  if (failures > 0 && limit > 0) {
    return { label: msg`再試行中（${failures}/${limit}）`, tone: 'warning', showSyncedAt: false };
  }

  if (lastSyncedAt) {
    return { label: msg`同期済み`, tone: 'success', showSyncedAt: true };
  }
  return { label: msg`未同期`, tone: 'neutral', showSyncedAt: false };
}

/** 05_screens §SC-06「種別はファイルサーバー・Wiki・SaaS・業務DB」。契約の値との対応。 */
export const SOURCE_TYPES = ['filesystem', 'wiki', 'saas', 'db'] as const;

export type SourceType = (typeof SOURCE_TYPES)[number];

const SOURCE_TYPE_LABELS: Record<SourceType, MessageDescriptor> = {
  filesystem: msg`ファイルサーバー`,
  wiki: msg`Wiki`,
  saas: msg`SaaS`,
  db: msg`業務DB`,
};

/**
 * 種別の表示名。**未知の値は生値をそのまま返す**——契約が 4 値でも、サーバが将来 5 つ目を返したときに
 * 画面が「空欄」ではなく実際の値を見せて異常へ気付ける状態にしておく。
 */
export function sourceTypeLabel(sourceType: string): MessageDescriptor | string {
  return (
    (SOURCE_TYPE_LABELS as Record<string, MessageDescriptor | undefined>)[sourceType] ?? sourceType
  );
}

// 日時の整形は foundation の単一実装を使う（#536 で SC-02 が 2 つ目の利用者になったため移した）。
// **同じ整形規則を 2 か所に置かない** —— `—`（値なし）の書き方が画面ごとに割れる。
export { formatDateTime } from '@foundation/ui/formatDateTime';
