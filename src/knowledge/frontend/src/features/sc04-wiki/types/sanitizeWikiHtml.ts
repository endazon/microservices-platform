import DOMPurify from 'dompurify';

// SC-04, UC-07, FR-13, ADR-0073 決定 2 / IADR-0367 決定 3 (#1200): Wiki.js が描画した本文（HTML）の sanitize。
//
// `WikiPageView.content` は **Wiki.js が Markdown から描画した HTML** であり、ゲートウェイ（WikiService）が
// ABAC 通過時にそのままプロキシする。SPA は Markdown を再レンダリングしない（ADR-0073 決定 2 の逐語）。
// ただし**そのまま `innerHTML` へは入れない** —— 本文の原典は取り込み文書（外部データソース由来）であり、
// Wiki.js 側の sanitize は管理 UI のトグル 1 つで外れる。ここで sanitize するのは**多層防御**である
// （SC-03 が「HTML 化はサニタイズ方針の決定を伴う」として Markdown を生で出している論点を、ここでは決めて塞ぐ）。
//
// ■ 落とすもの
//   - DOMPurify 既定（`script` / `style` / `iframe` / `object` / `embed` / `form` / イベント属性 / `javascript:`）
//   - **メディア（`img` / `picture` / `source` / `video` / `audio`）**: Wiki.js の資産は SPA オリジンから
//     到達できず（資産のプロキシは無い）、壊れた画像を並べるより落とす。**外部 URL の資産をブラウザに
//     取りに行かせない**（08_data-egress-policy の趣旨）ことも兼ねる。
//   - `target` 属性: 新規タブを開かない（Wiki.js 本体 UI への外部遷移を画面から無くした ADR-0073 決定 1 と同じ向き）。
// ■ 書き換えるもの
//   - Wiki.js の正準パス `/(<locale>/)?doc/<documentId>` を指すリンクは `/wiki?doc=<documentId>` へ。
//     台帳の `WikiPath` は `doc/<guid>`（`WikiPage.PathFor`）、Wiki.js の URL は `/ja/doc/<guid>` である。
//     ページ間リンクが SPA 内（`?doc=` → by-doc）で解決し、UC-07 基本フロー 1「開く」が本文中からも成り立つ。
//     それ以外の `href` は触らない。

const WIKI_DOC_LINK_RE =
  /^\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?doc\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?(?:[?#].*)?$/i;

const FORBID_TAGS = ['img', 'picture', 'source', 'video', 'audio'];
const FORBID_ATTR = ['target'];

/** Wiki.js のページ間リンクなら SPA 側の到達先を返す。それ以外は `null`。 */
export function wikiDocLinkTarget(href: string): string | null {
  const m = WIKI_DOC_LINK_RE.exec(href);
  return m ? `/wiki?doc=${m[1].toLowerCase()}` : null;
}

let purifier: ReturnType<typeof DOMPurify> | null = null;

/** 既定インスタンスにフックを足すと他の呼び出し側へ漏れるため、専用インスタンスを 1 つ持つ。 */
function getPurifier(): ReturnType<typeof DOMPurify> {
  if (purifier) return purifier;
  const instance = DOMPurify();
  instance.addHook('afterSanitizeAttributes', (node) => {
    if (node.tagName !== 'A') return;
    const href = node.getAttribute('href');
    if (!href) return;
    const target = wikiDocLinkTarget(href);
    if (target) node.setAttribute('href', target);
  });
  purifier = instance;
  return instance;
}

export function sanitizeWikiHtml(html: string): string {
  return String(
    getPurifier().sanitize(html, {
      USE_PROFILES: { html: true },
      FORBID_TAGS,
      FORBID_ATTR,
    }),
  );
}
