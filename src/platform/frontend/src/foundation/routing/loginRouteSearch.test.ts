import { describe, it, expect } from 'vitest';
import { loginRoute } from './shell';

// ADR-0031 / IADR-0124 決定 9: `/login?from=` はブラウザの URL に載る＝利用者が自由に書き換えられる。
// ここは**実装のルート定義そのもの**（`loginRoute.options.validateSearch`）を呼び、
// 検証条件が実際に配線されていることを固定する。
// （`RequireAuth.test.tsx` は遷移先の**保持**を見るためにテスト専用ルートを組むが、
//  そちらは `validateSearch` を素通しにしているので、この配線の検査にはならない。）

/** 実装の validateSearch を取り出して呼ぶ（テスト側で条件を書き写さない）。 */
function validate(raw: Record<string, unknown>): { from?: string } {
  const fn = loginRoute.options.validateSearch;
  if (typeof fn !== 'function') throw new Error('loginRoute に validateSearch が配線されていない');
  return (fn as (r: Record<string, unknown>) => { from?: string })(raw);
}

describe('loginRoute.validateSearch: 正当な内部パスは保持する', () => {
  it.each(['/search', '/admin/ops', '/docs/abc', '/search?q=a'])('keeps %s', (from) => {
    expect(validate({ from })).toEqual({ from });
  });
});

describe('loginRoute.validateSearch: 外部への遷移を弾く（オープンリダイレクト対策）', () => {
  it.each([
    ['//evil.com'],
    ['https://evil.com'],
    ['http://evil.com'],
    ['javascript:alert(1)'],
    // バックスラッシュはブラウザの URL 解釈でスラッシュと同一視される（IADR-0124 §実測）。
    ['/\\evil.com'],
    ['\\\\evil.com'],
    ['evil.com'],
  ])('drops %s (falls back to no from)', (from) => {
    expect(validate({ from })).toEqual({});
  });
});

describe('loginRoute.validateSearch: 文字列でない値・欠落は落とす', () => {
  it.each([
    ['missing', {}],
    ['undefined', { from: undefined }],
    ['number', { from: 42 }],
    ['array', { from: ['/search'] }],
    ['empty string', { from: '' }],
  ])('drops %s', (_label, raw) => {
    expect(validate(raw as Record<string, unknown>)).toEqual({});
  });
});
