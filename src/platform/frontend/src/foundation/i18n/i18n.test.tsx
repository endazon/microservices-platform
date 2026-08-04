import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { msg } from '@lingui/core/macro';
import { NotFound } from '@foundation/ui/NotFound';
import { notificationLabel } from '@foundation/ui/notifications';
import {
  DEFAULT_LOCALE,
  SUPPORTED_LOCALES,
  activate,
  catalogFor,
  detectLocale,
  i18n,
  initI18n,
  isSupportedLocale,
} from './index';

// ADR-0031（i18n = Lingui〔ja / en〕）/ IADR-0125 決定 3・4・7 の回帰。

afterEach(() => {
  cleanup();
  // 他のテストへロケールを漏らさない（setup.ts が ja に固定している前提を戻す）。
  activate('ja');
});

describe('detectLocale（IADR-0125 決定 7: 切替 UI は持たず、ブラウザ設定から決める）', () => {
  it.each([
    [['ja'], 'ja'],
    [['ja-JP'], 'ja'],
    [['en-US', 'ja'], 'en'],
    [['EN'], 'en'],
    // 未対応言語は既定（ja）へ倒す。
    [['fr-FR'], 'ja'],
    // 未対応 → 対応 の順なら対応言語を採る。
    [['fr-FR', 'en'], 'en'],
    [[], 'ja'],
  ])('%j -> %s', (languages, expected) => {
    expect(detectLocale(languages)).toBe(expected);
  });

  it('isSupportedLocale は ja / en だけを通す', () => {
    expect(SUPPORTED_LOCALES).toEqual(['ja', 'en']);
    expect(isSupportedLocale('ja')).toBe(true);
    expect(isSupportedLocale('en')).toBe(true);
    expect(isSupportedLocale('fr')).toBe(false);
    expect(isSupportedLocale(undefined)).toBe(false);
  });

  it('initI18n は検出したロケールを有効化する', () => {
    expect(initI18n(['en-GB'])).toBe('en');
    expect(i18n.locale).toBe('en');
    expect(initI18n(['fr'])).toBe(DEFAULT_LOCALE);
    expect(i18n.locale).toBe('ja');
  });
});

describe('ja / en の切替が実際に描画を変える', () => {
  it('NotFound の見出しと本文がロケールで変わる', () => {
    activate('ja');
    const { unmount } = render(<NotFound />);
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    expect(
      screen.getByText('お探しのページは存在しないか、アクセスできません。'),
    ).toBeInTheDocument();
    unmount();

    activate('en');
    render(<NotFound />);
    expect(screen.getByRole('heading', { name: 'Not found' })).toBeInTheDocument();
    expect(
      screen.getByText('The page does not exist, or you do not have access to it.'),
    ).toBeInTheDocument();
  });

  it('通知のラベル（INDEX 決定 21 のテキスト手掛かり）もロケールで変わる', () => {
    activate('ja');
    expect(notificationLabel('warning')).toBe('注意');
    activate('en');
    expect(notificationLabel('warning')).toBe('Warning');
  });

  it('未対応ロケールを渡しても既定へ倒れる（呼び出し側で分岐させない）', () => {
    // 型は Locale だが、実行時に外から来る値（将来の URL パラメータ等）を想定した防御である。
    expect(activate('de' as 'ja')).toBe(DEFAULT_LOCALE);
    expect(i18n.locale).toBe('ja');
  });
});

describe('カタログの整合（テストにカタログの写しを書かない）', () => {
  it('ja と en が同じメッセージ ID の集合を持つ', () => {
    const ja = Object.keys(catalogFor('ja')).sort();
    const en = Object.keys(catalogFor('en')).sort();
    expect(ja.length).toBeGreaterThan(0);
    expect(en).toEqual(ja);
  });

  it('どちらのカタログにも空の訳文が無い', () => {
    for (const locale of SUPPORTED_LOCALES) {
      for (const [id, value] of Object.entries(catalogFor(locale))) {
        expect(value, `${locale}: ${id}`).toBeTruthy();
      }
    }
  });

  it('ソースのメッセージがカタログに存在する（マクロ由来の ID で引く）', () => {
    // msg マクロが返す MessageDescriptor の id は抽出時と同じハッシュである。
    // 文字列リテラルを書き写さず、実装と同じ経路で ID を得て突き合わせる。
    const descriptor = msg`見つかりませんでした`;
    expect(descriptor.id).toBeTruthy();
    expect(Object.keys(catalogFor('en'))).toContain(descriptor.id);
  });
});

// IADR-0125 決定 3: マクロの babel 変換は **ビルド（vite.config.ts）とテスト（vitest.config.ts）の
// 両方**に効いていなければならない。片方だけだと「テストは通るのにビルドが壊れる」という
// 静かな破綻になる。テスト側が効いていることは本ファイルが `msg` を使えている事実そのものが示す。
// ビルド側は設定ファイルを読んで確かめる（ここでビルドを走らせるのは高価すぎる）。
describe('マクロ変換の設定がビルドとテストの両方に入っている', () => {
  const PLUGIN = '@lingui/babel-plugin-lingui-macro';

  it('vitest 側は本ファイルが msg マクロを解決できている時点で効いている', () => {
    // 変換が無ければ msg は実行時に例外を投げる（マクロは実行時関数ではない）。
    expect(msg`エラー`.id).toBeTruthy();
  });

  it('vite.config.ts にも同じ babel プラグインが入っている', () => {
    // jsdom 環境では import.meta.url が http スキームになるため相対解決に使えない。
    // 横断 Vitest はワークスペースルート（src/）で走るので cwd から辿る。
    const viteConfig = readFileSync(resolve(process.cwd(), 'platform/frontend/vite.config.ts'), 'utf8');
    expect(viteConfig).toContain(PLUGIN);
  });

  it('vitest.config.ts にも同じ babel プラグインが入っている', () => {
    const vitestConfig = readFileSync(resolve(process.cwd(), 'vitest.config.ts'), 'utf8');
    expect(vitestConfig).toContain(PLUGIN);
  });
});
