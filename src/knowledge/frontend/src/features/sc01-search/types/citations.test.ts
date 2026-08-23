import { describe, it, expect } from 'vitest';
import { citationKind } from './citations';

// SC-01, UC-01 基本フロー 5: 出典（Wiki／原本リンク）付きで結果を返す。
// 出典の種別は `sourceUri` と実行時 config の `wikiBaseUrl` から判定する（画面仕様書 SC-01 §出典の種別判定）。
describe('citationKind (SC-01)', () => {
  const wiki = 'https://wiki.example.co.jp';

  // P-1: wikiBaseUrl 配下は Wiki ページの出典（📖 / SC-04）。
  it('classifies a source under the wiki base url as a wiki citation', () => {
    expect(citationKind(`${wiki}/ja/keihi`, wiki)).toBe('wiki');
  });

  // P-2: 別ホストは正規化文書の出典（📄 / SC-03）。
  it('classifies a source on another host as a document citation', () => {
    expect(citationKind('https://files.example.co.jp/keihi.docx', wiki)).toBe('document');
    expect(citationKind('storage://normalized/keihi.md', wiki)).toBe('document');
  });

  // P-3: sourceUri が無い出典も文書として扱う（documentId は常にあるため SC-03 へ辿れる）。
  it('falls back to a document citation when the source uri is absent', () => {
    expect(citationKind(null, wiki)).toBe('document');
    expect(citationKind(undefined, wiki)).toBe('document');
  });

  // P-4: wikiBaseUrl 未設定の環境では Wiki 由来を推測しない（到達できない導線へ送らない）。
  it('never infers a wiki citation when no wiki base url is configured', () => {
    expect(citationKind(`${wiki}/ja/keihi`, undefined)).toBe('document');
    expect(citationKind(`${wiki}/ja/keihi`, '')).toBe('document');
  });
});
