import { Suspense, lazy } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Bot } from 'lucide-react';
import { Button, LoadingState } from '@platform/ui';
import { useAiChatStore } from './aiChatStore';

// 05_screens §共通シェル: 「**AIチャットパネル（右レール）**」。IADR-0121 決定 1 の第 4 段（#788）。
// ［2026-09-12 / UI/UX 改善 A-8］hi-fi モック `.rail-r` の構造へ合わせた（裁定 6 / IADR-0439）。
// ［2026-09-12 / #1437 作業 4］**本体を `AiChatRail.tsx` へ切り出し、`React.lazy` で遅延にした**
// （IADR-0443）。
//
// ■ 列そのものをこの部品が描く
//   共通シェル（Layout）は 3 カラム grid の 3 列目（244px）にこの部品を置くだけである。
//   閉じているときも列は残し、ランチャーだけを出す（開閉で本文の幅が揺れない）。
//   **だからランチャーは初期チャンクに残す** —— ここまで遅延にすると、初期描画で 3 列目が
//   空のまま 1 往復ぶん待たされる。
//
// ■ 🔴 このファイルは初期チャンクである（`Layout` が静的 import する）。
//   **`@platform/ui` の `Tooltip` 系をここへ書かない**（IADR-0443）——
//   Tooltip / Dialog は `@base-ui/react` を静的に引いており、初期側から触れた瞬間に
//   `vendor-baseui`（実測 114 kB）が初期ロードへ戻る。名前つきの部品が要るなら `AiChatRail.tsx` 側へ置く。

/**
 * 右レール本体。**開いたときに初めて読み込む**（既定は閉じている）。
 *
 * 既定の輸出を持たせず `.then()` で名前つきを取り出すのは、`@platform/ui` と同じく
 * 「公開面は名前で引く」に揃えるためである（既定の輸出は import 側で名前を自由に付け替えられる）。
 */
const AiChatRail = lazy(() => import('./AiChatRail').then((m) => ({ default: m.AiChatRail })));

export function AiChatPanel() {
  const { t } = useLingui();
  const open = useAiChatStore((s) => s.open);
  const openPanel = useAiChatStore((s) => s.openPanel);

  if (!open) {
    return (
      <div className="flex flex-col items-stretch border-l border-divider bg-surface-muted p-n3">
        <Button type="button" variant="ghost" size="sm" aria-expanded={false} onClick={openPanel}>
          <Bot className="size-4" aria-hidden />
          <Trans>AI チャットを開く</Trans>
        </Button>
      </div>
    );
  }
  return (
    // 待ちは**三部品の「待ち」**で表す（IADR-0125 決定 1。輪だけでなく見える文言を伴う）。
    // 器（列の枠と背景）は fallback 側にも持たせる —— ここを素の `null` にすると、
    // 遅延チャンクが届くまで 3 列目の枠線と面が消えて骨格が揺れる。
    <Suspense
      fallback={
        <div className="flex flex-col items-stretch border-l border-divider bg-surface-muted">
          <LoadingState label={t`AI チャットを読み込み中…`} />
        </div>
      }
    >
      <AiChatRail />
    </Suspense>
  );
}
