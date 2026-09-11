import { i18n, type Messages } from '@lingui/core';
// ADR-0031（13_frontend-stack §ディレクトリ構成）/ IADR-0262 第 2 段 / ADR-0067 決定 2:
// カタログは計画のツリーがユニット直下に置く区分（`locales/  # ja / en（Lingui）`）に在る。
// **i18n の実行時部分（本モジュール）は `lib/` 側**であり（設定済みの再利用可能ライブラリ＝原典の `lib`）、
// 両者は別の区分なので相対で辿る（`@foundation/i18n` の公開面には出さない）。
// ［2026-08-30 / ADR-0067］従前ここは `app/` 側と書いていた。**`app/i18n/` → `lib/i18n/` へ移った**
// ——深さは同じなので下の相対 import は変わらない。
import { messages as ja } from '../../locales/ja/messages';
import { messages as en } from '../../locales/en/messages';

// ADR-0031（i18n = Lingui〔ja / en〕）/ IADR-0125 決定 3・7。
//
// カタログは `lingui compile` の生成物（`locales/<locale>/messages.ts`）をそのまま import する。
// `@lingui/vite-plugin`（.po の直接 import）を使わないのは、peer に rolldown を要求するためである。
// 生成物はコミットする（orval と同じ扱い。IADR-0121 決定 3）。

/** 対応ロケール。計画（13_frontend-stack §採用技術一覧）が定める ja / en の 2 つ。 */
export const SUPPORTED_LOCALES = ['ja', 'en'] as const;
export type Locale = (typeof SUPPORTED_LOCALES)[number];

/** 既定ロケール。既存文言が日本語であり、lingui.config.ts の sourceLocale と揃える。 */
export const DEFAULT_LOCALE: Locale = 'ja';

/**
 * 読み込み済みカタログ（ロケールごと）。基盤自身のカタログを起点に、`registerUnitMessages` で
 * 可変ユニットのカタログが**追加**されていく。`catalogFor()` はこれを返す。
 */
const CATALOGS: Record<Locale, Messages> = { ja: { ...ja }, en: { ...en } };

i18n.load({ ja, en });
// 既定ロケールをモジュール読み込み時に活性化する。**ブラウザ設定は見ない**——ここで
// navigator を読むと、テスト（jsdom の既定は en-US）と本番で描画される言語が変わり、
// 「テストだけ英語」という再現しにくい差が生まれる。検出は initI18n() が明示的に行う。
i18n.activate(DEFAULT_LOCALE);

/** 文字列が対応ロケールか。 */
export function isSupportedLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (SUPPORTED_LOCALES as readonly string[]).includes(value);
}

/**
 * ブラウザの言語設定から使うロケールを決める。
 *
 * IADR-0125 決定 7: **ロケール切替の UI は作らない**。計画（05_screens）で言語切替を要求しているのは
 * SC-13（Keycloak のログインテーマ）だけであり、§共通シェル に言語切替の要素は無い。
 * 無い UI を先回りで作らないため、実行時は下記の判定で決め、切替そのものは activate() を公開する。
 *
 * `ja-JP` のような地域つきタグは主言語部分（`ja`）で判定する。未対応言語は既定（ja）へ倒す。
 */
export function detectLocale(languages: readonly string[] = navigator.languages ?? []): Locale {
  for (const tag of languages) {
    const primary = String(tag).split('-')[0]?.toLowerCase();
    if (isSupportedLocale(primary)) return primary;
  }
  return DEFAULT_LOCALE;
}

/** ロケールを有効化する。未対応の値は既定へ倒す（呼び出し側で分岐させない）。 */
export function activate(locale: Locale = DEFAULT_LOCALE): Locale {
  const next: Locale = isSupportedLocale(locale) ? locale : DEFAULT_LOCALE;
  i18n.activate(next);
  return next;
}

/** 起動時の初期化。検出したロケールを有効化する。 */
export function initI18n(languages?: readonly string[]): Locale {
  return activate(detectLocale(languages));
}

/** テスト・診断用: 読み込まれているカタログ（ロケールごとのメッセージ表。ユニットの追加分を含む）。 */
export function catalogFor(locale: Locale): Messages {
  return CATALOGS[locale];
}

/**
 * 可変機能ユニットのカタログを**追加ロード**する（UI/UX 改善 2026-09-12・利用者裁定 #3）。
 *
 * 呼ぶのは**合成点（`features/index.ts`）だけ**である。合成点はルート・ナビ・パンくずと同様に
 * 「ユニットを知る唯一の場所」であり（IADR-0124 決定 1）、本モジュール（`lib/i18n`）が
 * `@ai-stock-trading` を import する形は ESLint（`no-restricted-imports`）が禁じている——
 * foundation が可変ユニットを知ってはならない。
 *
 * - 与えられたロケールは `i18n.load(locale, messages)` で**追加**する。`@lingui/core` の `load` は
 *   既存カタログへ `Object.assign` でマージする（実測: `_load` は `Object.assign(maybeMessages, messages)`）
 *   ので、基盤のカタログは消えない。ID はメッセージ本文のハッシュ（`msg` マクロが両者で同じ算法）
 *   なので、同じ本文は同じ ID になり、衝突しても同じ訳文である。
 * - **与えられていないロケール（例: en）には、ユニットの最初のロケールの文言を流す。**
 *   AST は「Lingui を導入するが英訳はしない」裁定（2026-09-12）で ja カタログしか持たない。
 *   何も流さないと、本番ビルド（`msg` マクロが `message` を落とし ID だけを残す）で
 *   en ロケールの AST 画面に**ハッシュがそのまま出る**。ja を流せば「AST の画面は en でも日本語で出る」
 *   という裁定どおりの見え方になる。
 *   🔴 **流すのは、そのロケールに未登録の ID だけである。** ユニットの ja と基盤の en が同じ本文
 *   （例: 「保存」）を持つとき、ID が一致するため無条件に流すと**基盤の英訳が日本語で上書きされる**。
 *
 * ロード後に `activate` し直す必要は無い（`i18n._` は呼び出し時点の表を引く）。
 */
export function registerUnitMessages(messagesByLocale: Partial<Record<Locale, Messages>>): void {
  const provided = SUPPORTED_LOCALES.filter((locale) => messagesByLocale[locale] !== undefined);
  const fallback = provided.length > 0 ? messagesByLocale[provided[0]] : undefined;

  for (const locale of SUPPORTED_LOCALES) {
    const own = messagesByLocale[locale];
    const source = own ?? fallback;
    if (source === undefined) continue;
    const additions: Messages = own
      ? { ...own }
      : Object.fromEntries(Object.entries(source).filter(([id]) => !(id in CATALOGS[locale])));
    if (Object.keys(additions).length === 0) continue;
    i18n.load(locale, additions);
    Object.assign(CATALOGS[locale], additions);
  }
}

export { i18n };
