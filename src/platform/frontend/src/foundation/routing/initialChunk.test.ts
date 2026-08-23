import { describe, it, expect, vi } from 'vitest';

// NFR, ADR-0031 / IADR-0134: 初期チャンクに残すもの（共通シェル・認証・UI プリミティブ）の回帰ガード。
//
// 何を守るか: **全画面が使うものを遅延側へ移さないこと**。移しても型検査もテストもビルドも通り、
// バンドルはむしろ小さく見える——変異試験で、共通シェル（Layout）を lazyRouteComponent へ
// 移しても 551 件のテストが全部緑のままだったことを実測した（IADR-0134 §変異試験 M5a）。
// 実害は初期表示の往復が 1 つ増えること、および描画が suspend してシェルごと空白になることである。
//
// 検出のしかたは routeSplitting.test.ts（knowledge）の裏返しで、同じ `vi.mock` の性質
// （factory は実際に import されたときにだけ評価される）を使う。**あちらは「読まれないこと」、
// こちらは「読まれること」**を固定する。

const loaded = vi.hoisted(() => new Set<string>());

const trace =
  <T>(id: string, importOriginal: () => Promise<T>) =>
  async (): Promise<T> => {
    loaded.add(id);
    return await importOriginal();
  };

vi.mock('@foundation/ui/Layout', (o) => trace('Layout', o as () => Promise<unknown>)());
vi.mock('@foundation/ui/NotFound', (o) => trace('NotFound', o as () => Promise<unknown>)());
vi.mock('@foundation/auth/RequireAuth', (o) => trace('RequireAuth', o as () => Promise<unknown>)());
vi.mock('@foundation/auth/RequireRole', (o) => trace('RequireRole', o as () => Promise<unknown>)());
vi.mock('@foundation/auth/AuthProvider', (o) =>
  trace('AuthProvider', o as () => Promise<unknown>)(),
);
vi.mock('@platform/ui', (o) => trace('@platform/ui', o as () => Promise<unknown>)());

/** 初期チャンクに残すもの（issue #512 の指定）と、そう決めた理由。 */
const MUST_BE_EAGER = [
  ['Layout', '共通シェル。全画面の外枠であり、遅延させると初期表示が 1 往復増える'],
  ['NotFound', '存在秘匿の描画。未知パスと権限による秘匿の両方で即座に要る（IADR-0009）'],
  ['RequireAuth', '認証ガード。ルート解決の前段で判定する'],
  ['RequireRole', 'ロールガード。**画面チャンクを取りに行く前**に判定させるため初期側に置く'],
  ['AuthProvider', '認証（BFF セッション）の入口。全ルートの前提'],
  ['@platform/ui', 'UI プリミティブ。全画面が使うので分けても往復が増えるだけ'],
] as const;

describe('initial chunk contents (NFR / IADR-0134)', () => {
  it.each(MUST_BE_EAGER)('loads %s eagerly (%s)', async (id) => {
    // アプリの合成ルート＝実アプリが初期チャンクへ載せる範囲そのもの。
    const mod = await import('../../App');
    // 「見えるはずのもの」を先に確かめる（読み込みに失敗していれば以下は空振りする）。
    expect(typeof mod.App).toBe('function');

    expect(loaded.has(id)).toBe(true);
  });
});
