import { describe, it, expect } from 'vitest';
import { renderMarkdown } from './markdown';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（Markdown 描画・出典と本文の対応印）:
// AI 回答の Markdown を**安全な** HTML へ描く。sanitize は多層防御（SC-04 の `sanitizeWikiHtml` と同じ判断）。
describe('renderMarkdown', () => {
  const opts = { citePrefix: 'ask-current', citationCount: 2 };

  // 基本フロー: 見出し・箇条書き・強調・表が HTML になる。
  it('renders GFM (headings, lists, emphasis, tables)', async () => {
    const html = await renderMarkdown(
      '## 締め日\n\n- **毎月25日**\n\n| a | b |\n| - | - |\n| 1 | 2 |',
      opts,
    );
    expect(html).toContain('<h2>締め日</h2>');
    expect(html).toContain('<li><strong>毎月25日</strong></li>');
    expect(html).toContain('<table>');
  });

  // 裁定 6（脚注方式）: 本文の `[n]` を出典の行へ結ぶ。件数を超える番号は結ばない（行き先の無いアンカーを作らない）。
  it('links [n] to the citation anchor only within the citation count', async () => {
    const html = await renderMarkdown(
      '締め日は25日です[1]。休業日は前営業日[2]。根拠なし[3]',
      opts,
    );
    expect(html).toContain('<sup class="cite"><a href="#ask-current-cite-1">[1]</a></sup>');
    expect(html).toContain('<a href="#ask-current-cite-2">[2]</a>');
    expect(html).not.toContain('cite-3');
    expect(html).toContain('[3]');
  });

  // 対応印は**文字列置換ではない**: コードスパン・リンク・参照定義の `[1]` を誤って結ばない。
  it('does not turn code spans, links or reference definitions into footnotes', async () => {
    const html = await renderMarkdown(
      '`[1]` と [1](https://example.com/x) と\n\n[1]: https://example.com/y',
      opts,
    );
    expect(html).toContain('<code>[1]</code>');
    expect(html).toContain('href="https://example.com/x"');
    expect(html).not.toContain('cite-1');
  });

  // 08_data-egress-policy / 多層防御: script・イベント属性・`javascript:`・メディアを落とす。
  it('strips scripts, event handlers, javascript: urls and media', async () => {
    const html = await renderMarkdown(
      '<script>alert(1)</script><img src="https://evil.example/x.png"><a href="javascript:alert(1)" onclick="x()">x</a>',
      opts,
    );
    expect(html).not.toContain('<script');
    expect(html).not.toContain('<img');
    expect(html).not.toContain('javascript:');
    expect(html).not.toContain('onclick');
  });

  // 外部リンクは新しいタブで開き、opener を渡さない。断片リンク（脚注）と相対パスは触らない。
  it('opens absolute http(s) links in a new tab with rel="noopener noreferrer"', async () => {
    const html = await renderMarkdown(
      '[外部](https://example.com/) と [内部](/docs/1) と [1]',
      opts,
    );
    expect(html).toMatch(
      /<a href="https:\/\/example\.com\/" target="_blank" rel="noopener noreferrer">外部<\/a>/,
    );
    expect(html).toContain('<a href="/docs/1">内部</a>');
    expect(html).toContain('<a href="#ask-current-cite-1">[1]</a>');
  });

  // LLM の出力に書かれた `target` はそのまま通さない（フックが付け直す分だけが残る）。
  it('does not pass through a target attribute written in the source', async () => {
    const html = await renderMarkdown('<a href="/docs/1" target="_top">x</a>', opts);
    expect(html).toBe('<p><a href="/docs/1">x</a></p>\n');
  });

  // ストリーミング中の部分文字列: 閉じていない強調・フェンス・表・脚注でも例外を投げない。
  it('never throws on partial markdown while streaming', async () => {
    for (const partial of [
      '**太字',
      '```js\nconst a',
      '| a | b |\n| -',
      '締め日は[',
      '[1',
      '- 項目\n  - ',
    ]) {
      await expect(renderMarkdown(partial, opts)).resolves.toEqual(expect.any(String));
    }
  });
});
