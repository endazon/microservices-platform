import type { ReactNode } from 'react';
import { msg } from '@lingui/core/macro';
import { useLingui } from '@lingui/react';
import { BookOpen, FileText, UserRound } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { Tag, cn } from '@platform/ui';
import { citationAnchorId } from './citations';
import type { AskCitation, CitationKind } from './citations';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（出典と本文の対応印）: 出典の一覧。
//
// 05_screens §SC-01「区別の表示方法」: **アイコンとラベルを併用し、色だけで意味を持たせない**
// （INDEX 決定 21）。アイコンは装飾（`aria-hidden`）とし、意味はタグの文字が担う。
// 計画は glyph（📄 / 📖 / 👤）で描いているが、実装は lucide-react のアイコン（FileText / BookOpen /
// UserRound）にする——絵文字はプラットフォームごとに字形が変わり、SC-18 のノード・SC-19 の一覧と
// 同じ印を揃えられない（利用者裁定 2026-09-12）。
//
// ■ 種別と行き先は呼び出し側が決める
//   種別の判定（Wiki 台帳）と行き先（SC-03 / SC-04 の typed route）は knowledge ユニットの知識であり、
//   foundation はそれを持たない。`kindOf` と `renderTitle` で受け、省略時は `document` の文字だけを描く
//   （右レールはこの形——共通シェルは可変ユニットのルートを知らない）。
//
// ■ 対応印
//   各行の先頭に脚注番号 `[n]` を出し、行の `id` を `citationAnchorId(citePrefix, n)` にする。
//   本文側（`MarkdownAnswer`）の `[n]` がここへ結ぶ。

export interface CitationListProps {
  citations: readonly AskCitation[];
  /** 対応印のアンカー接頭辞（本文側と同じ値）。 */
  citePrefix: string;
  /** 出典の種別。省略時は `document`。 */
  kindOf?: (citation: AskCitation) => CitationKind;
  /** 表題の描き方（リンクにする等）。省略時は文字だけ。 */
  renderTitle?: (citation: AskCitation) => ReactNode;
  className?: string;
}

const KIND_ICONS: Record<CitationKind, LucideIcon> = {
  document: FileText,
  wiki: BookOpen,
  personal: UserRound,
};

// Wiki ページも組織文書である（モック: 📖 の行にも「組織文書」のラベル）。個人資料だけがラベルを変える。
const KIND_LABELS = {
  document: msg`組織文書`,
  wiki: msg`組織文書`,
  personal: msg`個人資料`,
} as const;

export function CitationList({
  citations,
  citePrefix,
  kindOf,
  renderTitle,
  className,
}: CitationListProps) {
  const { i18n } = useLingui();
  return (
    <ol className={cn('flex flex-col gap-1.5 text-sm', className)}>
      {citations.map((c) => {
        const kind = kindOf?.(c) ?? 'document';
        const Icon = KIND_ICONS[kind];
        return (
          <li
            key={c.chunkId}
            id={citationAnchorId(citePrefix, c.number)}
            className="flex flex-wrap items-center gap-2 scroll-mt-n3"
          >
            <span className="text-xs text-fg-muted">[{c.number}]</span>
            <Icon className="size-4 shrink-0 text-fg-muted" aria-hidden />
            {renderTitle ? renderTitle(c) : <span className="text-fg">{c.documentTitle}</span>}
            <Tag tone={kind === 'personal' ? 'accent' : 'outline'}>{i18n._(KIND_LABELS[kind])}</Tag>
            <span className="text-xs text-fg-muted">{c.snippet}</span>
          </li>
        );
      })}
    </ol>
  );
}
