import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';

// SC-05, FR-06（#1923・[[IADR-0561]]）: 文書の**データソースの鍵**とタブ（機械の収集か否か）の表示写像（純関数）。
//
// 🔴 **鍵の導出の正は BFF の `DocumentListSources`**（絞り込みと件数はそちらが当てる）。ここは列の表示のために
//   同じ規則を写したもので、`documentSource.test.ts` が BFF の試験と同じ例で固定する。規則を変えるときは両方を変える。
//   1. `ds:<dataSourceName>` → 2. `col:<属性 source>` → 3. `report`（`kind` と `periodKey`）→ 4. `manual`。

const DATA_SOURCE_PREFIX = 'ds:';
const COLLECTED_PREFIX = 'col:';
const REPORT_SOURCE = 'report';
const MANUAL_SOURCE = 'manual';
/** 属性 `project` を持たない文書の鍵（BFF の `DocumentListSources.NoProject` と同じ）。 */
// eslint-disable-next-line lingui/no-unlocalized-strings -- BFF のクエリと件数の鍵（表示文言ではない。表示は「プロジェクトなし」）
export const NO_PROJECT = '(none)';

/** タブ。`curated` は人の文書・報告書・データソースの取り込み、`collected` は機械の収集、`''` はすべて。 */
export type DocumentOrigin = 'curated' | 'collected' | '';

const nonBlank = (value: string | null | undefined): value is string =>
  typeof value === 'string' && value.trim() !== '';

/** 鍵の上限（BFF の `DocumentListSources.MaxKeyLength` と同じ。UTF-16 の符号単位で数え、サロゲートペアは割らない）。 */
const MAX_KEY_LENGTH = 300;

function bounded(key: string): string {
  if (key.length <= MAX_KEY_LENGTH) return key;
  const last = key.charCodeAt(MAX_KEY_LENGTH - 1);
  return key.slice(0, last >= 0xd800 && last <= 0xdbff ? MAX_KEY_LENGTH - 1 : MAX_KEY_LENGTH);
}

export function documentSourceKey(doc: Pick<DocumentDto, 'dataSourceName' | 'attributes'>): string {
  if (nonBlank(doc.dataSourceName)) return bounded(DATA_SOURCE_PREFIX + doc.dataSourceName);
  const attributes = doc.attributes ?? {};
  if (nonBlank(attributes.source)) return bounded(COLLECTED_PREFIX + attributes.source);
  if ('kind' in attributes && 'periodKey' in attributes) return REPORT_SOURCE;
  return MANUAL_SOURCE;
}

function isCollectedSource(key: string): boolean {
  return key.startsWith(COLLECTED_PREFIX);
}

const FIXED_LABELS: Record<string, MessageDescriptor> = {
  [REPORT_SOURCE]: msg`確定報告書`,
  [MANUAL_SOURCE]: msg`手入力`,
};

/**
 * 鍵の表示。固定の 2 つは訳した文言、ほかはデータソース・出典の名前をそのまま出す（名前は訳さない）。
 * 収集は「収集: 出典名」、データソースは「データソース名」とし、色ではなく文字で見分けられるようにする。
 */
export function sourceLabel(key: string, translate: (descriptor: MessageDescriptor) => string) {
  const fixed = FIXED_LABELS[key];
  if (fixed) return translate(fixed);
  if (key.startsWith(COLLECTED_PREFIX)) {
    const name = key.slice(COLLECTED_PREFIX.length);
    return translate(msg`収集: ${name}`);
  }
  if (key.startsWith(DATA_SOURCE_PREFIX)) return key.slice(DATA_SOURCE_PREFIX.length);
  return key;
}

/** タブの件数（データソース別の件数から導く。どれも ABAC 後・絞り込み前）。 */
export function originCounts(sourceCounts: Record<string, number> | undefined) {
  if (!sourceCounts) return undefined;
  let collected = 0;
  let curated = 0;
  for (const [key, count] of Object.entries(sourceCounts)) {
    if (isCollectedSource(key)) collected += count;
    else curated += count;
  }
  return { collected, curated, all: collected + curated };
}

/** 原本の所在をリンクにしてよいか（`http(s)` だけ。`storage://` や `javascript:` は押させない）。 */
export function linkableOriginalUri(uri: string | null | undefined): string | null {
  return typeof uri === 'string' && /^https?:\/\//i.test(uri) ? uri : null;
}
