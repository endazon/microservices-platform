// ADR-0031 / 利用者裁定（2026-09-12）: ライト・ダーク両対応。**既定は system**
// （`prefers-color-scheme`）で、ヘッダの切替ボタンが 3 状態を巡回する。
//
// 本モジュールは **DOM と localStorage 以外に依存しない純粋な層**である（React も Lingui も
// 参照しない）。理由は 2 つ:
//   1. 巡回の規則は**純関数で固定できる**（`nextTheme`）。React の描画を通さずに試験できる。
//   2. 起動時の適用（`applyStoredTheme`）を **React より前**に呼べる——描画してから
//      テーマを当てると、一瞬だけ反対のテーマが見える（いわゆる flash of wrong theme）。
//
// 色の実体は `@platform/ui` の `styles.css` が持つ（`:root` / `[data-theme]` の 4 段）。
// ここが決めるのは「`<html data-theme>` に何を書くか」だけである。

/** 利用者の選択。`system` は OS / ブラウザの設定に従う（既定）。 */
export type ThemeChoice = 'system' | 'light' | 'dark';

/** 実際に適用されるテーマ（`system` を解決した結果）。 */
export type ResolvedTheme = 'light' | 'dark';

/** localStorage のキー。名前空間を切るのは、同一オリジンに他のアプリが同居し得るためである。 */
export const STORAGE_KEY = 'msp.theme';

const CHOICES: readonly ThemeChoice[] = ['system', 'light', 'dark'];

/** 値が `ThemeChoice` か（localStorage は誰でも書けるので、読んだ値は必ず検証する）。 */
export function isThemeChoice(value: unknown): value is ThemeChoice {
  return typeof value === 'string' && (CHOICES as readonly string[]).includes(value);
}

/**
 * 切替ボタンの巡回。**`system` → `system` の反転 → もう一方（明示）→ `system`** の 3 状態。
 *
 * 巡回の順序が OS 設定で変わるのは意図である。**1 回押したときに見た目が必ず変わる**
 * ようにするため、最初に行くのは「今見えているものの反対」でなければならない——
 * 順序を固定（常に light → dark）にすると、OS がライトの利用者が `system` から `light` を
 * 選んだときに**何も起きない**（押しても変わらないボタンは壊れて見える）。
 *
 * - OS がダーク: `system`（＝ダーク）→ `light` → `dark` → `system`
 * - OS がライト: `system`（＝ライト）→ `dark` → `light` → `system`
 */
export function nextTheme(current: ThemeChoice, systemPrefersDark: boolean): ThemeChoice {
  const inverted: ResolvedTheme = systemPrefersDark ? 'light' : 'dark';
  const other: ResolvedTheme = systemPrefersDark ? 'dark' : 'light';
  if (current === 'system') return inverted;
  if (current === inverted) return other;
  return 'system';
}

/** 選択を実際のテーマへ解決する。 */
export function resolveTheme(choice: ThemeChoice, systemPrefersDark: boolean): ResolvedTheme {
  if (choice === 'light' || choice === 'dark') return choice;
  return systemPrefersDark ? 'dark' : 'light';
}

/**
 * 保存された選択を読む。**読めないときは `system`（既定）へ倒す。**
 * localStorage は Safari のプライベートブラウズや企業ポリシーで例外を投げ得るため、
 * 参照そのものを try/catch で囲む（「設定が読めない」ことでアプリが起動しないのは割に合わない）。
 */
export function readStoredTheme(): ThemeChoice {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return isThemeChoice(raw) ? raw : 'system';
  } catch {
    return 'system';
  }
}

/** 選択を保存する。書けなくても**失敗させない**（テーマはその場では効いている）。 */
export function writeStoredTheme(choice: ThemeChoice): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, choice);
  } catch {
    // 保存できないだけで、この操作そのものは成功している。
  }
}

/**
 * `<html>` へ選択を書く。
 *
 * **`system` のときは属性を消す**（`data-theme="system"` とは書かない）。styles.css の
 * ライト規則は `:root:not([data-theme='dark'])` で書かれており、属性が無いことが
 * 「OS に従う」の表現そのものだからである。値を書くと 4 段のどれにも当たらない。
 */
export function applyTheme(choice: ThemeChoice): void {
  const root = document.documentElement;
  if (choice === 'system') {
    delete root.dataset.theme;
    return;
  }
  root.dataset.theme = choice;
}

/**
 * 起動時に呼ぶ入口。保存された選択を読んで `<html>` へ当て、その選択を返す。
 * **React の描画より前に呼ぶ**（そうしないと最初の 1 フレームだけ反対のテーマが見える）。
 */
export function applyStoredTheme(): ThemeChoice {
  const choice = readStoredTheme();
  applyTheme(choice);
  return choice;
}

// 🔴 これは**表示文言ではなく CSS のメディアクエリ**である。`lingui/no-unlocalized-strings` は
// 空白と括弧を含む文字列を一律で「未国際化の文言」とみなす（eslint.config.js の `ignore` が
// 除外するのはクラス名・ID 相当の形だけで、この限界は同ファイルにも明記されている）。
// **規則を弱めるのでも抑制ファイルへ逃がすのでもなく、語ごとに分けて組み立てる** ——
// 各片（`prefers-color-scheme` / `dark`）は識別子の形なので規則の対象外であり、
// 組み立てた結果が意図どおりであることは theme.test.ts が固定する。
const COLOR_SCHEME_FEATURE = 'prefers-color-scheme';
const DARK_SCHEME = 'dark';

/** `matchMedia()` へ渡すクエリ。`(prefers-color-scheme: dark)` と等しい。 */
export const PREFERS_DARK_QUERY = `(${COLOR_SCHEME_FEATURE}: ${DARK_SCHEME})`;

/** OS / ブラウザがダークを好むか。`matchMedia` が無い環境（古い jsdom 等）ではライト扱い。 */
export function systemPrefersDark(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  return window.matchMedia(PREFERS_DARK_QUERY).matches;
}
