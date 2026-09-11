import { useEffect, useRef, useState } from 'react';
import { cn } from '@platform/ui';
import { renderMarkdown } from './markdown';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（Markdown 描画・対応印）:
// AI 回答の本文。`renderMarkdown`（marked ＋ DOMPurify。遅延読み込み）の出力を描く。
//
// ■ 処理系が届くまでの表示
//   初回は `marked` の動的 import を待つ。その間は**素の本文を `whitespace-pre-wrap` で出す**
//   （待たせて空にしない——ストリーミングの最初のトークンが届いているのに何も見えない状態を作らない）。
//   処理系が届いたら以降は毎回同期で描き直せる。
//
// ■ 順序の保証
//   トークンが届くたびに描き直すため、描画は非同期の呼び出しが重なる。**古い呼び出しの結果で
//   新しい結果を上書きしない**よう、呼び出しごとの連番で最新だけを採る。
//
// ■ `prose` 風の最小クラス
//   `@tailwindcss/typography` は入れない（1 部品のために全画面の CSS を増やさない）。見出し・箇条書き・
//   表・コード・引用・リンクの余白と色だけを、意味トークンの名前付きユーティリティで与える。

export interface MarkdownAnswerProps {
  markdown: string;
  /** 対応印のアンカー接頭辞（回答ごとに一意。出典リスト側と同じ値を渡す）。 */
  citePrefix: string;
  /** 出典の件数。`[n]` はこの範囲だけ脚注へ結ぶ。 */
  citationCount?: number;
  className?: string;
}

/* eslint-disable lingui/no-unlocalized-strings --
   クラス名の束であり文言ではない（`className` 属性なら規則が除外する文字列を、定数へ切り出しただけ）。 */
const PROSE =
  'text-sm leading-relaxed text-fg break-words ' +
  '[&_p]:my-n2 [&_p:first-child]:mt-0 [&_p:last-child]:mb-0 ' +
  '[&_h1]:mt-n3 [&_h1]:mb-n2 [&_h1]:text-base [&_h1]:font-semibold ' +
  '[&_h2]:mt-n3 [&_h2]:mb-n2 [&_h2]:text-sm [&_h2]:font-semibold ' +
  '[&_h3]:mt-n2 [&_h3]:mb-n1 [&_h3]:text-sm [&_h3]:font-medium ' +
  '[&_ul]:my-n2 [&_ul]:list-disc [&_ul]:pl-5 [&_ol]:my-n2 [&_ol]:list-decimal [&_ol]:pl-5 ' +
  '[&_li]:my-n1 ' +
  '[&_a]:text-accent [&_a]:underline ' +
  '[&_code]:rounded-sm [&_code]:bg-surface-muted [&_code]:px-1 [&_code]:py-0.5 [&_code]:text-[12px] ' +
  '[&_pre]:my-n2 [&_pre]:overflow-x-auto [&_pre]:rounded-sm [&_pre]:bg-surface-muted [&_pre]:p-n3 ' +
  '[&_pre_code]:bg-transparent [&_pre_code]:p-0 ' +
  '[&_blockquote]:my-n2 [&_blockquote]:border-l-2 [&_blockquote]:border-divider [&_blockquote]:pl-n3 [&_blockquote]:text-fg-muted ' +
  '[&_table]:my-n2 [&_table]:w-full [&_table]:border-collapse [&_table]:text-xs ' +
  '[&_th]:border-b [&_th]:border-divider [&_th]:px-n2 [&_th]:py-n1 [&_th]:text-left [&_th]:font-medium [&_th]:text-fg-muted ' +
  '[&_td]:border-b [&_td]:border-divider [&_td]:px-n2 [&_td]:py-n1 ' +
  '[&_hr]:my-n3 [&_hr]:border-divider ' +
  '[&_sup.cite]:ml-0.5 [&_sup.cite_a]:no-underline';
/* eslint-enable lingui/no-unlocalized-strings */

export function MarkdownAnswer({
  markdown,
  citePrefix,
  citationCount = 0,
  className,
}: MarkdownAnswerProps) {
  const [html, setHtml] = useState<string | null>(null);
  const seq = useRef(0);

  useEffect(() => {
    const mine = ++seq.current;
    let alive = true;
    void renderMarkdown(markdown, { citePrefix, citationCount }).then((out) => {
      if (alive && mine === seq.current) setHtml(out);
    });
    return () => {
      alive = false;
    };
  }, [markdown, citePrefix, citationCount]);

  if (html === null) {
    return <div className={cn(PROSE, 'whitespace-pre-wrap', className)}>{markdown}</div>;
  }
  // sanitize 済み（`renderMarkdown` が DOMPurify を通す）。生の Markdown をここへ入れる経路は無い。
  return <div className={cn(PROSE, className)} dangerouslySetInnerHTML={{ __html: html }} />;
}
