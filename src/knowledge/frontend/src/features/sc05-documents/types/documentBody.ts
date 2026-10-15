import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-05, SC-03, FR-06, ADR-0070 決定 3（#1923 段 2）: 文書の**本文の状態**の表示写像（純関数）。
//
// 台帳（`DocumentDto`）だけで出せる事実である —— `hasBody`（原本が本文を持っていたか）と `markdownUri`（本文の参照）。
// 🔴 **索引の状態（索引済み・埋め込み待ち・未索引）ではない。** 索引は取り込み側（IngestionService）が持ち、文書ごとに引く口は無い。
//   列の見出しも「本文」とし、索引の状態と読み違えさせない。
// 「本文なし（原本を参照）」は SC-02 / SC-03 と**同じ文言・同じ導出**（`hasBody === false` のときだけ）。

export type DocumentBodyState = 'present' | 'none' | 'unset';

export interface DocumentBodyView {
  label: MessageDescriptor;
  tone: 'neutral' | 'success';
}

const VIEWS: Record<DocumentBodyState, DocumentBodyView> = {
  present: { label: msg`本文あり`, tone: 'success' },
  // 本文を持たない原本（テキスト層の無い PDF など）。異常ではないので警告色を当てない。
  none: { label: msg`本文なし（原本を参照）`, tone: 'neutral' },
  // 本文の参照がまだ無い（画面から作った直後など）。
  unset: { label: msg`本文未設定`, tone: 'neutral' },
};

export function documentBodyState(doc: {
  hasBody?: boolean;
  markdownUri?: string | null;
}): DocumentBodyState {
  // `hasBody` の既定は true（項目を持たない旧い応答は「本文あり」として読む。DTO と同じ向き）。
  if (doc.hasBody === false) return 'none';
  return doc.markdownUri ? 'present' : 'unset';
}

export function documentBodyView(doc: {
  hasBody?: boolean;
  markdownUri?: string | null;
}): DocumentBodyView {
  return VIEWS[documentBodyState(doc)];
}
