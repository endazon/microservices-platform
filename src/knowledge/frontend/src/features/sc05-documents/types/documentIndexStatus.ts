import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-05, FR-06, FR-02 (#1993): 文書の**索引の状態**（`DocumentListPageDto.indexStatuses` の値）の表示写像（純関数）。
//
// 値は BFF が取り込み側（索引の点）から 1 ページ分まとめて引いたもの: 索引済み・埋め込み待ち・未索引の 3 値と、
// 引けなかったときの「不明」。計画は分類を定めていない（実装の判断。一覧の索引の状態の実装 ADR）。
// 🔴 **応答に鍵が無い文書（旧い BFF・窓の外）も「不明」**。未索引へ倒さない（索引の障害が「全部未索引」に見えないように）。
// **未知の値は握り潰さない** —— 生値をそのまま出す（`documentStatus.ts` と同じ作法）。
// INDEX 決定 21: 色だけで意味を持たせない —— `StatusBadge` が tone ごとの固定アイコンとテキストを型で強制する。

const DOCUMENT_INDEX_STATUSES = ['indexed', 'embedding_pending', 'not_indexed', 'unknown'] as const;

type DocumentIndexStatus = (typeof DOCUMENT_INDEX_STATUSES)[number];

export interface DocumentIndexStatusView {
  /** 表示文言。**未知の値は生値を出すため `string` になる**（翻訳しない）。 */
  label: MessageDescriptor | string;
  tone: 'neutral' | 'success' | 'warning';
}

const VIEWS: Record<DocumentIndexStatus, DocumentIndexStatusView> = {
  indexed: { label: msg`索引済み`, tone: 'success' },
  // 全文では引けるが意味検索には出ない（埋め込みが使えるようになれば自動で埋め直される）。気付かせたいので警告色。
  embedding_pending: { label: msg`埋め込み待ち`, tone: 'warning' },
  // 取り込み前・取り込みの失敗・索引の対象外（露出の除外・アーカイブ）を含む。異常とは限らないので警告色を当てない。
  not_indexed: { label: msg`未索引`, tone: 'neutral' },
  unknown: { label: msg`不明`, tone: 'neutral' },
};

function isKnown(status: string): status is DocumentIndexStatus {
  return (DOCUMENT_INDEX_STATUSES as readonly string[]).includes(status);
}

/** 文書の索引の状態の表示。`statuses` に鍵が無ければ「不明」。 */
export function documentIndexStatusView(
  statuses: Readonly<Record<string, string>> | undefined,
  documentId: string,
): DocumentIndexStatusView {
  const status = statuses?.[documentId];
  if (status === undefined) return VIEWS.unknown;
  return isKnown(status) ? VIEWS[status] : { label: status, tone: 'neutral' };
}
