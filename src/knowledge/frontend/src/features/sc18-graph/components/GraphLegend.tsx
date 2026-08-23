import { Trans } from '@lingui/react/macro';

// SC-18 (#917): 凡例。**グラフ領域の近傍に常時置く**（利用者裁定・質問票 第11回 Q3）。
//
// 🔴 区別はアイコン・形・線種のテキスト説明が担い、**色だけで意味を持たせない**。
// 中核 5 種は描き分けを説明し、推奨追加の 4 種は**凡例のみ**でよい（05_screens §SC-18。
// 検討中の 2 種〔contradicts / responds-to〕は辞書に seed されないため載せない）。

/** 線種の見本（CSS の border で描く。SVG を持ち込まない）。 */
function LineSample({ style, width }: { style: 'solid' | 'dashed' | 'dotted'; width: number }) {
  return (
    <span
      aria-hidden="true"
      className="inline-block w-8 align-middle"
      style={{ borderTopStyle: style, borderTopWidth: width, borderTopColor: 'currentcolor' }}
    />
  );
}

export function GraphLegend() {
  return (
    <details open className="rounded border border-[--color-border] p-2 text-xs">
      <summary className="cursor-pointer font-semibold">
        <Trans>凡例（アイコンと形で区別・色は補助）</Trans>
      </summary>
      <div className="mt-2 grid gap-3 sm:grid-cols-2">
        <ul className="space-y-1" data-testid="legend-nodes">
          <li>
            <span
              aria-hidden="true"
              className="mr-1 inline-flex h-4 w-4 items-center justify-center rounded-full border border-current text-[10px]"
            >
              📄
            </span>
            <Trans>円 = 組織文書</Trans>
          </li>
          <li>
            <span
              aria-hidden="true"
              className="mr-1 inline-flex h-4 w-4 items-center justify-center rounded border border-dashed border-current text-[10px]"
            >
              👤
            </span>
            <Trans>角丸四角（破線の輪郭）= 個人資料（自分のみ）</Trans>
          </li>
          <li>
            <span
              aria-hidden="true"
              className="mr-1 inline-flex h-4 w-4 items-center justify-center rounded-full border border-dotted border-current opacity-75 text-[10px]"
            >
              📄
            </span>
            <Trans>点線の輪郭 = 孤立文書（表示中の辺なし）</Trans>
          </li>
          <li>
            <span
              aria-hidden="true"
              className="mr-1 inline-flex h-4 w-4 items-center justify-center rounded-full border-2 border-current text-[10px]"
            >
              📄
            </span>
            <Trans>太い輪郭・大きい表示 = 起点</Trans>
          </li>
        </ul>
        <ul className="space-y-1" data-testid="legend-edges">
          <li>
            <LineSample style="solid" width={1} /> <Trans>related: 実線・細（向きなし）</Trans>
          </li>
          <li>
            <LineSample style="solid" width={2} /> → <Trans>cites: 実線・矢印</Trans>
          </li>
          <li>
            <LineSample style="solid" width={4} /> →{' '}
            <Trans>supersedes: 太実線・矢印（旧→新）</Trans>
          </li>
          <li>
            <LineSample style="dashed" width={2} /> → <Trans>derived-from: 破線・矢印</Trans>
          </li>
          <li>
            <LineSample style="dotted" width={2} /> → <Trans>embeds: 点線・矢印</Trans>
          </li>
          <li>
            <LineSample style="dashed" width={2} />{' '}
            <Trans>AI 提案由来の辺は破線（承認済みのみ表示）</Trans>
          </li>
          <li className="text-[--color-fg-muted]">
            <Trans>その他の型（implements / refines / depends-on / part-of）は細い実線で表示</Trans>
          </li>
        </ul>
      </div>
    </details>
  );
}
