import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-06, UC-04, FR-01/FR-02: データソースの同期状態と種別の写像（純関数）。
//
// IADR-0127 決定 2: **契約から導出できる値だけ**で状態を表示する。`DataSourceDto` が持つのは
// `status`（active / disabled の 2 値）と `lastSyncedAt` だけであり、hi-fi の「⚠ 再試行中（3/5）」
// （連続失敗回数）と「次回同期」（ソース別スケジュール）はいずれも契約に無い。
// 実装しない理由は画面仕様書 docs/screens/SC-06_datasource-management.md に記録した。

/**
 * `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。
 *
 * **`warning`（琥珀）は今のところどの状態にも充てていない。** 計画（05_screens モック間相違の確定 ②）が
 * 琥珀へ与えた意味は「**同期異常**」であり、`DataSourceDto` が同期健全性を持つまで空けておく
 * （IADR-0127 決定 2）。union に残してあるのは、契約が来たときの充て先が決まっているためである。
 */
export type SyncTone = 'neutral' | 'success' | 'warning';

export interface SyncStateView {
  label: MessageDescriptor;
  tone: SyncTone;
  /** 同期済みのときだけ日時を添える（モックの「✓ 正常（07-24 03:00）」に対応）。 */
  showSyncedAt: boolean;
}

/**
 * 同期状態を表示の組（文言 ＋ tone）へ写像する。
 *
 * **`disabled` は中立（neutral）である。** 05_screens モック間相違の確定 ②（2026-07-24）は
 * 「SC-06 の**同期異常表示**の警告色＝琥珀（hi-fi を正）」と定める。すなわち琥珀が指すのは
 * **異常**であって、管理者が意図して無効化した**正常な設定状態**ではない。無効へ琥珀を充てると
 * 「⚠」が付いた正常状態が生まれ、計画が琥珀に与えた意味と表示の意味がずれる
 * （IADR-0127 決定 2）。**同期健全性が契約に載るまで琥珀は空けておく**——
 * 「異常」の語彙を先に使い切ると、契約が来たときに区別する色が無くなる。
 * 中立でも色だけに意味を持たせない点は変わらない（`StatusBadge` がアイコンとテキストを伴わせる）。
 */
export function syncStateView(
  status: string,
  lastSyncedAt: string | null | undefined,
): SyncStateView {
  if (status === 'disabled') {
    return { label: msg`無効`, tone: 'neutral', showSyncedAt: false };
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

/** 日時の表示。解釈できない値はそのまま出す（握り潰さない）。 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? value : new Date(parsed).toLocaleString();
}
