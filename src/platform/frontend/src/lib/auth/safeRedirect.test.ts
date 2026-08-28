import { describe, it, expect } from 'vitest';
import { toInternalPath } from './safeRedirect';

// ADR-0031 / IADR-0124 決定 9: オープンリダイレクト対策の判定を固定する。
// 認証導線の遷移先は 2 経路とも**外部由来**（`/login?from=` は URL に載る・OIDC の `state.returnTo` は
// 認可サーバを往復する）であり、攻撃者が任意の値を入れられる前提で扱う。
//
// 本スイートは**実装のヘルパを直接呼ぶ**。テスト内に条件を書き写すと、実装が緩んでも
// テストが自分の写しを検査してしまい何も検知できない（本 PR が繰り返し戒めた「静かに壊れる」型）。

const ORIGIN = 'https://platform.example.co.jp';

describe('toInternalPath: 内部パスは保持する', () => {
  it.each([
    ['/search', '/search'],
    ['/admin/ops', '/admin/ops'],
    ['/ask', '/ask'],
    ['/', '/'],
    // クエリ・フラグメントも遷移先の一部として保持する（SC-02 の ?q= 等）。
    ['/search?q=%E7%B5%8C%E8%B2%BB', '/search?q=%E7%B5%8C%E8%B2%BB'],
    ['/docs/abc#section', '/docs/abc#section'],
    // バックスラッシュがパーセントエンコードされていれば、ただのパス文字である。
    ['/%5Cevil.com', '/%5Cevil.com'],
  ])('keeps %s', (input, expected) => {
    expect(toInternalPath(input, ORIGIN)).toBe(expected);
  });
});

describe('toInternalPath: 外部への遷移を弾く', () => {
  it.each([
    // スキーム相対（プロトコル相対）URL。
    ['//evil.com'],
    ['///evil.com'],
    ['//evil.com/path'],
    // スキーム付き絶対 URL。
    ['https://evil.com'],
    ['http://evil.com'],
    ['https://evil.com/path?a=1'],
    // 危険なスキーム。
    ['javascript:alert(1)'],
    ['data:text/html,<script>alert(1)</script>'],
    // バックスラッシュ。WHATWG URL 仕様（＝ブラウザ）はバックスラッシュをスラッシュと同一視するため、
    // `/\evil.com` は「'/' で始まり '//' で始まらない」のに https://evil.com へ解決される。
    // 素朴な前方一致だけでは通過してしまう経路であり、本テストがその退行を止める。
    ['/\\evil.com'],
    ['/\\\\evil.com'],
    ['\\\\evil.com'],
    ['\\/evil.com'],
    // 先頭が '/' でない相対パス（意図しない基準からの解決を避ける）。
    ['evil.com'],
    ['../admin'],
  ])('rejects %s', (input) => {
    expect(toInternalPath(input, ORIGIN)).toBeNull();
  });
});

describe('toInternalPath: 文字列でない値・欠落を弾く', () => {
  it.each([
    ['undefined', undefined],
    ['null', null],
    ['number', 42],
    ['array', ['/search']],
    ['object', { toString: () => '/search' }],
    ['boolean', true],
    ['empty string', ''],
  ])('rejects %s', (_label, input) => {
    expect(toInternalPath(input, ORIGIN)).toBeNull();
  });
});

describe('toInternalPath: origin の既定', () => {
  it('falls back to the running page origin when no origin is given', () => {
    // jsdom の既定 origin（http://localhost:3000）に対して内部パスが通ること。
    expect(toInternalPath('/search')).toBe('/search');
    expect(toInternalPath('//evil.com')).toBeNull();
    expect(toInternalPath('/\\evil.com')).toBeNull();
  });
});
