import { lazy, Suspense } from 'react';
import type { Control } from 'react-hook-form';
import type { AnalysisFormValues } from '../types/analysisFormSchema';

// ADR-0031 §採用技術一覧「フォーム = React Hook Form / Zod / @hookform/resolvers /
// **RHF DevTools**」/ NFR・IADR-0134（初期ロード予算）/ #788。
//
// ■ 採用はするが、本番の初期ロードへは入れない
//   DevTools は**開発時のインスペクタ**である。静的 import すると本番のエントリチャンクへ載り、
//   IADR-0134 の初期ロード ratchet を利用者に何の得も無いまま押し上げる。
//   `React.lazy`（＝動的 import）で分けたうえ、**`import.meta.env.DEV` の下でしか描画しない**。
//   production ビルドでは条件が定数 `false` に畳まれるため、チャンクは**一度も取得されない**。
//
// ■ なぜ「入れない」で済ませないか
//   計画は DevTools を「採用」と定めている（§採用技術一覧 のフォーム欄）。宣言だけして使わないと、
//   Knip（第 5 段 / #493）が未使用依存として検出する状態を残すことになる。
//   **使い方の側で条件を満たす**のが素直である。

const DevTool = lazy(async () => {
  const mod = await import('@hookform/devtools');
  return { default: mod.DevTool };
});

export function FormDevTools({ control }: { control: Control<AnalysisFormValues> }) {
  if (!import.meta.env.DEV) return null;
  return (
    <Suspense fallback={null}>
      {/* DevTool の `control` は自身の型定義（`Control<FieldValues>`）を持つ。値としては同一なので
          ここで型だけを合わせる——DevTools の型定義に画面側の型を合わせにいかない。 */}
      <DevTool control={control as unknown as Control} />
    </Suspense>
  );
}
