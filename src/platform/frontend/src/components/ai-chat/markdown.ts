import { citationAnchorId } from './citations';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（2026-09-12「Markdown 描画＋コピー」
// 「出典と本文の対応印」）: AI 回答（Markdown）を安全な HTML へ描く。
//
// ■ 遅延読み込み（`vendor-markdown`）
//   `AiChatPanel` は共通シェル（Layout）が静的に import するため、ここが `marked` を静的に import すると
//   **全利用者の初期ロードに Markdown 処理系が乗る**（IADR-0134 の初期ロード ratchet に直撃する）。
//   よって `marked` と `dompurify` は**最初の描画時に動的 import** する。`marked` は
//   `vite.config.ts` の `vendor-markdown` 規則で 1 本のチャンクに束ねる（`dompurify` は SC-04 の Wiki
//   本文 sanitize と共有される遅延チャンクのまま。`vendor-markdown` へ入れると Wiki 閲覧だけの
//   利用者にも `marked` が届く）。
//
// ■ sanitize は多層防御である（SC-04 の `sanitizeWikiHtml` と同じ判断）
//   回答は LLM の生成物であり、RAG の文脈には取り込み文書（外部データソース由来）が含まれる。
//   `marked` は HTML を素通しするので、**そのまま `innerHTML` へは入れない**。DOMPurify の既定
//   （script / style / iframe / イベント属性 / `javascript:`）に加え、メディア（img / picture / source /
//   video / audio）を落とす——**外部 URL の資産をブラウザに取りに行かせない**（08_data-egress-policy）。
//
// ■ 外部リンク
//   `http(s)` の絶対 URL は `target="_blank" rel="noopener noreferrer"` で開く（利用者の入力途中の
//   画面を捨てない。`noopener` は開いた側からの `window.opener` 操作を塞ぐ）。DOMPurify は `target` を
//   既定で落とすので、sanitize **後**のフックで付ける（sanitize 前に付けても剥がれる）。
//   `#…` の断片リンク（脚注）と相対パスは触らない。
//
// ■ 対応印（脚注方式）
//   本文中の `[n]`（`n` は出典の番号）を `<sup><a href="#<prefix>-cite-n">[n]</a></sup>` にする。
//   marked の inline 拡張で行う（**文字列置換ではない**——コードスパン `` `[1]` `` やリンク `[1](url)`・
//   参照リンク `[1]: url` を誤って結ばないため）。出典の件数を超える番号は結ばない
//   （行き先の無いアンカーを作らない）。
//
// ■ ストリーミング中の部分文字列
//   marked は閉じていない強調・フェンス・表でも例外を投げず、読めるところまで描く
//   （`markdown.test.ts` が固定する）。呼び出し側はトークンが届くたびに描き直してよい。

export interface RenderMarkdownOptions {
  /** 対応印のアンカー接頭辞（回答ごとに一意）。 */
  citePrefix: string;
  /** 出典の件数。`[n]` は `1 ≤ n ≤ citationCount` のときだけ脚注へ結ぶ。既定 0（結ばない）。 */
  citationCount?: number;
}

/** 落とすタグ（`sanitizeWikiHtml` と同じ集合）。 */
const FORBID_TAGS = ['img', 'picture', 'source', 'video', 'audio'];

const CITE_RE = /^\[(\d{1,3})\](?![([:])/;

interface Renderer {
  render(markdown: string, options: RenderMarkdownOptions): string;
}

let rendererPromise: Promise<Renderer> | null = null;

/**
 * 処理系を 1 度だけ読み込む。`Marked` のインスタンスは 1 つで、対応印の文脈（接頭辞・件数）は
 * `parse` の直前に差し替える——`parse` は同期（`async: false`）なので、呼び出しの途中で
 * 別の回答の文脈に入れ替わることは無い。
 */
function loadRenderer(): Promise<Renderer> {
  if (rendererPromise) return rendererPromise;
  rendererPromise = Promise.all([import('marked'), import('dompurify')]).then(
    ([{ Marked }, { default: DOMPurify }]) => {
      const cite = { prefix: '', count: 0 };
      const marked = new Marked({
        gfm: true,
        breaks: true,
        extensions: [
          {
            name: 'cite',
            level: 'inline',
            start: (src) => src.indexOf('['),
            tokenizer(src) {
              const m = CITE_RE.exec(src);
              if (!m) return undefined;
              return { type: 'cite', raw: m[0], number: Number(m[1]) };
            },
            renderer(token) {
              const n = token['number'] as number;
              // markup（脚注の印）であり文言ではない。
              /* eslint-disable lingui/no-unlocalized-strings */
              if (n < 1 || n > cite.count) return `[${n}]`;
              return `<sup class="cite"><a href="#${citationAnchorId(cite.prefix, n)}">[${n}]</a></sup>`;
              /* eslint-enable lingui/no-unlocalized-strings */
            },
          },
        ],
      });

      const purifier = DOMPurify();
      purifier.addHook('afterSanitizeAttributes', (node) => {
        if (node.tagName !== 'A') return;
        const href = node.getAttribute('href');
        if (!href || !/^https?:\/\//i.test(href)) return;
        node.setAttribute('target', '_blank');
        node.setAttribute('rel', ['noopener', 'noreferrer'].join(' '));
      });

      return {
        render(markdown, options) {
          cite.prefix = options.citePrefix;
          cite.count = options.citationCount ?? 0;
          const html = marked.parse(markdown, { async: false });
          return String(
            purifier.sanitize(html, {
              USE_PROFILES: { html: true },
              FORBID_TAGS,
              // `target` は既定で落ちる。上のフックが sanitize 後に付け直すため、ここでは許可しない
              //（許可すると LLM の出力に書かれた `target` がそのまま通る）。
            }),
          );
        },
      };
    },
  );
  return rendererPromise;
}

/** Markdown を sanitize 済み HTML へ描く。処理系は初回だけ読み込む。 */
export async function renderMarkdown(
  markdown: string,
  options: RenderMarkdownOptions,
): Promise<string> {
  const renderer = await loadRenderer();
  return renderer.render(markdown, options);
}
