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

  // NFR / IADR-0579（Refs GHSA-5h4q-4m2j-rjhm）: 許可は allowlist。フォーム部品・style・
  // 埋め込み・画面のクラスは通らない。
  it('drops form controls, style, embeds and presentational attributes', async () => {
    const html = await renderMarkdown(
      [
        '<form action="/x"><input name="q"><button>b</button><select><option>o</option></select><textarea>t</textarea></form>',
        '<style>p{color:red}</style>',
        '<p style="position:fixed" class="fixed inset-0" id="root" name="n" data-x="1" aria-hidden="true">本文</p>',
        '<iframe src="/x"></iframe><object data="/x"></object><embed src="/x"><svg><a href="/x">s</a></svg><math></math>',
      ].join('\n'),
      opts,
    );
    for (const tag of [
      '<form',
      '<input',
      '<button',
      '<select',
      '<option',
      '<textarea',
      '<style',
      '<iframe',
      '<object',
      '<embed',
      '<svg',
      '<math',
    ]) {
      expect(html).not.toContain(tag);
    }
    for (const attr of ['style=', 'class=', 'id=', 'name=', 'data-x', 'aria-hidden', 'action=']) {
      expect(html).not.toContain(attr);
    }
    // 陽性対照: 本文そのものは残る。
    expect(html).toContain('<p>本文</p>');
  });

  // 陽性対照: 表の揃え・番号付きリストの開始・脚注の印の class は残る（全部消す実装を緑にしない）。
  it('keeps the markup that markdown needs', async () => {
    const html = await renderMarkdown('| a | b |\n|:-:|--:|\n| 1 | 2 |\n\n3. x\n4. y\n\n脚注 [1]', {
      citePrefix: 'ask-current',
      citationCount: 1,
    });
    expect(html).toContain('align="center"');
    expect(html).toContain('<ol start="3">');
    expect(html).toContain('<sup class="cite"><a href="#ask-current-cite-1">[1]</a></sup>');
  });

  // URI は http(s) / mailto・断片・同一オリジンのパスだけ。
  it('allows only http(s), mailto, fragment and same-origin path urls', async () => {
    const html = await renderMarkdown(
      '[a](https://example.com/) [b](mailto:x@example.com) [c](#f) [d](/docs/1) [e](//evil.example/x) [f](data:text/html,x) [g](vbscript:x) [h](ftp://example.com/)',
      opts,
    );
    expect(html).toContain('href="https://example.com/"');
    expect(html).toContain('href="mailto:x@example.com"');
    expect(html).toContain('href="#f"');
    expect(html).toContain('href="/docs/1"');
    expect(html).not.toContain('//evil.example');
    expect(html).not.toContain('data:');
    expect(html).not.toContain('vbscript:');
    expect(html).not.toContain('ftp:');
  });

  // class は生成側が付けた脚注の印にだけ残る。本文に直書きした class="cite"（sup を含む）は落ちる。
  it('keeps class only on the footnote marker that the renderer produced', async () => {
    const html = await renderMarkdown(
      '<span class="cite">x</span><a class="cite" href="/a">y</a><sup class="cite"><a href="#ask-current-cite-1">*</a></sup> [1]',
      { citePrefix: 'ask-current', citationCount: 1 },
    );
    // 直書きの sup は class を失い、ただの sup とリンクになる。
    expect(html).toContain('<sup><a href="#ask-current-cite-1">*</a></sup>');
    // 生成側の印はちょうど 1 つ。
    expect(html.match(/class="cite"/g)).toEqual(['class="cite"']);
    expect(html).toContain('<sup class="cite"><a href="#ask-current-cite-1">[1]</a></sup>');
    // 内部の印（cite-<乱数>）は出力に残らない。
    expect(html).not.toMatch(/cite-[0-9a-f]{32}/);
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
