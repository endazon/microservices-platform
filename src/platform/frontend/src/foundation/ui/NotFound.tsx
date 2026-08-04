import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';

// Issue #126 / IADR-0009: 存在秘匿。404（不在または権限による秘匿）は同一の画面で応答し、
// 資源の存在有無を推測させない。
export function NotFound() {
  return (
    <main style={{ padding: '2rem', textAlign: 'center' }}>
      <h1>{i18n._(msg`見つかりませんでした`)}</h1>
      <p>{i18n._(msg`お探しのページは存在しないか、アクセスできません。`)}</p>
    </main>
  );
}
