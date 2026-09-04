import { describe, it, expect } from 'vitest';
import { citationKind } from './citations';

// SC-01, UC-01 基本フロー 5: 出典（Wiki／原本リンク）付きで結果を返す。
// 出典の種別は**権限内の Wiki 台帳に文書 ID が載っているか**で判定する（画面仕様書 SC-01 §出典の種別判定。
// #1200 / IADR-0367 決定 1）。`sourceUri` や実行時 config は見ない。
describe('citationKind (SC-01)', () => {
  const WIKI_DOC = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  const OTHER_DOC = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  const ledger: ReadonlySet<string> = new Set([WIKI_DOC]);

  // P-1: 台帳に載る文書は Wiki ページの出典（📖 / SC-04）。
  it('classifies a document that the wiki ledger lists as a wiki citation', () => {
    expect(citationKind(WIKI_DOC, ledger)).toBe('wiki');
  });

  // P-2: 台帳に無い文書は正規化文書の出典（📄 / SC-03）。
  it('classifies a document the ledger does not list as a document citation', () => {
    expect(citationKind(OTHER_DOC, ledger)).toBe('document');
    expect(citationKind(OTHER_DOC, new Set())).toBe('document');
  });

  // P-3: 台帳が未取得・取得失敗なら Wiki 由来を推測しない（到達できない導線へ送らない）。
  it('never infers a wiki citation while the ledger is unavailable', () => {
    expect(citationKind(WIKI_DOC, undefined)).toBe('document');
  });
});
