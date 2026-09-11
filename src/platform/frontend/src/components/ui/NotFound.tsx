import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';

// Issue #126 / IADR-0009: 存在秘匿。404（不在または権限による秘匿）は同一の画面で応答し、
// 資源の存在有無を推測させない。
export function NotFound() {
  // ［2026-09-12 / UI/UX 改善］見た目をトークンへ寄せた（従前は素のインラインスタイル）。
  // 🔴 **要素と文言は変えない。** 未知パスと権限による秘匿が**同一の markup** で出ることを
  // `Layout.test.tsx` が outerHTML の一致で固定しており、ここの構造は存在秘匿の担保そのものである。
  return (
    <main className="px-n4 py-n8 text-center">
      <h1 className="mb-n2 text-[17px] font-medium text-fg">{i18n._(msg`見つかりませんでした`)}</h1>
      <p className="text-xs text-fg-muted">
        {i18n._(msg`お探しのページは存在しないか、アクセスできません。`)}
      </p>
    </main>
  );
}
