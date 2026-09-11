import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-05, UC-03, FR-06: 文書の**公開ライフサイクル**（`DocumentDto.status`）の表示写像（純関数）。
//
// 契約の値域は `draft` / `normalized` / `published` / `archived` である
// （`bff.schemas.ts` の `DocumentDto.status`: 「公開ライフサイクル。**変換結果ではない**」）。
//
// 🔴 **ABAC 属性の `lifecycle`（`draft` / `active` / `archived`。`lib/abac/lifecycle.ts`）とは別物である。**
//    あちらは**文書へ付ける属性**、こちらは**サーバが持つ状態**であり、値域も一致しない。
//    本画面の一覧が示すのは後者（公開・アーカイブの操作が効く相手）である。
//
// **未知の値は握り潰さない**——`—`／「不明」へ丸めず生値をそのまま出す。契約が 4 値と定めていても、
// サーバが 5 つ目を返したときに画面が異常へ気付ける状態にしておく（SC-07 `jobStatus.ts` と同じ作法）。

/** 契約が定める公開ライフサイクルの 4 値。 */
export const DOCUMENT_STATUSES = ['draft', 'normalized', 'published', 'archived'] as const;

export type DocumentStatus = (typeof DOCUMENT_STATUSES)[number];

/** `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。 */
export type DocumentStatusTone = 'neutral' | 'success';

export interface DocumentStatusView {
  /** 表示文言。**未知の状態は生値を出すため `string` になる**（翻訳しない）。 */
  label: MessageDescriptor | string;
  tone: DocumentStatusTone;
}

const VIEWS: Record<DocumentStatus, DocumentStatusView> = {
  draft: { label: msg`下書き`, tone: 'neutral' },
  normalized: { label: msg`正規化済み`, tone: 'neutral' },
  published: { label: msg`公開中`, tone: 'success' },
  // 🔴 **アーカイブ済みに警告色を当てない。** 管理者が意図して退避させた正常な状態であり、
  // 異常ではない（SC-06 が `disabled` を中立に置いたのと同じ判断）。区別はテキストが担う。
  archived: { label: msg`アーカイブ済み`, tone: 'neutral' },
};

function isKnown(status: string): status is DocumentStatus {
  return (DOCUMENT_STATUSES as readonly string[]).includes(status);
}

export function documentStatusView(status: string): DocumentStatusView {
  return isKnown(status) ? VIEWS[status] : { label: status, tone: 'neutral' };
}

/** 未公開状態のみ公開できる（アーカイブ済みの誤再公開を防ぐ。サーバも 409 で拒否する）。 */
export function canPublish(status: string): boolean {
  return status === 'draft' || status === 'normalized';
}
