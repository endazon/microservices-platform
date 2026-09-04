import { describe, it, expect } from 'vitest';
import { sanitizeWikiHtml, wikiDocLinkTarget } from './sanitizeWikiHtml';

// SC-04, UC-07, FR-13, ADR-0073 決定 2 / IADR-0367 決定 3 (#1200): Wiki.js が描画した本文の sanitize。
// **落とすもの**と**残すもの**を対で固定する —— 落とす側だけだと「全部消す実装」でも緑になる。

const DOC_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

describe('sanitizeWikiHtml (SC-04)', () => {
  // ★ 陽性対照: Wiki.js が描画した見出し・強調・リストはそのまま残る（本文の描画は Wiki.js の仕事）。
  it('keeps the rendered markup that Wiki.js produced', () => {
    const out = sanitizeWikiHtml(
      '<h2 id="s1">申請の手順</h2><p><b>領収書</b>を添付する。</p><ul><li>a</li></ul>',
    );
    expect(out).toContain('<h2 id="s1">申請の手順</h2>');
    expect(out).toContain('<b>領収書</b>');
    expect(out).toContain('<li>a</li>');
  });

  // ★ 陰性対照: スクリプト・イベント属性・javascript: は DOMPurify の既定で落ちる。
  it('drops scripts, event handlers and javascript: urls', () => {
    const out = sanitizeWikiHtml(
      '<p onclick="x()">t</p><script>alert(1)</script><a href="javascript:alert(1)">j</a>',
    );
    expect(out).not.toContain('<script');
    expect(out).not.toContain('onclick');
    expect(out).not.toContain('javascript:');
    expect(out).toContain('<p>t</p>');
  });

  // 決定 3: メディアは落とす（Wiki.js の資産は SPA から到達できず、外部資産を取りに行かせない）。
  it('drops media elements (assets are not proxied and must not be fetched from elsewhere)', () => {
    const out = sanitizeWikiHtml(
      '<p>x</p><img src="https://evil.example/t.png" alt="t"><video src="/v.mp4"></video><audio src="/a.mp3"></audio>',
    );
    expect(out).not.toContain('<img');
    expect(out).not.toContain('<video');
    expect(out).not.toContain('<audio');
    expect(out).toContain('<p>x</p>');
  });

  // 決定 3: 新規タブを開かない（`target` を落とす）。リンクそのものは残る。
  it('strips target attributes but keeps the links', () => {
    const out = sanitizeWikiHtml(
      '<a href="https://example.co.jp/x" target="_blank" rel="noopener">x</a>',
    );
    expect(out).not.toContain('target=');
    expect(out).toContain('href="https://example.co.jp/x"');
  });

  // 決定 3: Wiki.js のページ間リンクは SPA 側の到達先（`/wiki?doc=`）へ書き換える。
  it('rewrites Wiki.js page links (with or without locale) to the SPA deep link', () => {
    const out = sanitizeWikiHtml(
      `<a href="/ja/doc/${DOC_ID}">a</a><a href="/doc/${DOC_ID.toUpperCase()}#sec">b</a><a href="/ja/other/page">c</a>`,
    );
    expect(out).toContain(`href="/wiki?doc=${DOC_ID}">a</a>`);
    expect(out).toContain(`href="/wiki?doc=${DOC_ID}">b</a>`);
    // ★ 陽性対照: 正準パスでないリンクは触らない。
    expect(out).toContain('href="/ja/other/page">c</a>');
  });
});

describe('wikiDocLinkTarget', () => {
  it('matches only the canonical doc/<guid> path', () => {
    expect(wikiDocLinkTarget(`/ja/doc/${DOC_ID}`)).toBe(`/wiki?doc=${DOC_ID}`);
    expect(wikiDocLinkTarget(`/doc/${DOC_ID}/`)).toBe(`/wiki?doc=${DOC_ID}`);
    expect(wikiDocLinkTarget(`/en-us/doc/${DOC_ID}?x=1`)).toBe(`/wiki?doc=${DOC_ID}`);
    expect(wikiDocLinkTarget('/ja/doc/not-a-guid')).toBeNull();
    expect(wikiDocLinkTarget(`https://wiki.example/ja/doc/${DOC_ID}`)).toBeNull();
    expect(wikiDocLinkTarget('/ja/home')).toBeNull();
  });
});
