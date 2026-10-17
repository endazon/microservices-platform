import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-05, FR-06（#1923・[[IADR-0561]]）: 文書の**データソースの鍵**の表示写像（純関数）。
//
// 🔴 **鍵の導出の正は BFF の `DocumentListSources`**（絞り込みと件数はそちらが当てる。画面は `sourceCounts` の鍵を受け取って描くだけ）。
//   鍵は `ds:<データソース名>` → `col:<属性 source>` → `report`（`kind` と `periodKey`）→ `manual` の順で 1 つに決まる。

const DATA_SOURCE_PREFIX = 'ds:';
const COLLECTED_PREFIX = 'col:';

/** 属性 `project` を持たない文書の鍵（BFF の `DocumentListSources.NoProject` と同じ）。 */
// eslint-disable-next-line lingui/no-unlocalized-strings -- BFF のクエリと件数の鍵（表示文言ではない。表示は「プロジェクトなし」）
export const NO_PROJECT = '(none)';

const FIXED_LABELS: Record<string, MessageDescriptor> = {
  report: msg`確定報告書`,
  manual: msg`手入力`,
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
