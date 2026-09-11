import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  applyStoredTheme,
  applyTheme,
  isThemeChoice,
  nextTheme,
  PREFERS_DARK_QUERY,
  readStoredTheme,
  resolveTheme,
  STORAGE_KEY,
  writeStoredTheme,
  type ThemeChoice,
} from './theme';

// ADR-0031 / 利用者裁定（2026-09-12）: 3 状態の巡回と、OS 設定による**向きの反転**を固定する。
// ここが崩れると「押しても見た目が変わらない」状態（`system`=ライトの利用者が `light` を選ぶ）が
// 静かに戻る。純関数なので描画を通さずに全経路を並べられる。

afterEach(() => {
  delete document.documentElement.dataset.theme;
  window.localStorage.clear();
  vi.restoreAllMocks();
});

describe('nextTheme（3 状態の巡回）', () => {
  it('OS がダークのとき system → light → dark → system と巡る', () => {
    expect(nextTheme('system', true)).toBe('light');
    expect(nextTheme('light', true)).toBe('dark');
    expect(nextTheme('dark', true)).toBe('system');
  });

  it('OS がライトのとき system → dark → light → system と巡る（向きが反転する）', () => {
    expect(nextTheme('system', false)).toBe('dark');
    expect(nextTheme('dark', false)).toBe('light');
    expect(nextTheme('light', false)).toBe('system');
  });

  it.each([true, false])(
    'system から 1 回押すと必ず見た目が変わる（prefersDark=%s の陰性対照）',
    (prefersDark) => {
      const current = resolveTheme('system', prefersDark);
      const after = resolveTheme(nextTheme('system', prefersDark), prefersDark);
      expect(after).not.toBe(current);
    },
  );

  it.each([true, false])('3 回押すと必ず system へ戻る（prefersDark=%s）', (prefersDark) => {
    let choice: ThemeChoice = 'system';
    for (let i = 0; i < 3; i += 1) choice = nextTheme(choice, prefersDark);
    expect(choice).toBe('system');
  });
});

describe('PREFERS_DARK_QUERY（組み立てたクエリが意図どおりか）', () => {
  it('(prefers-color-scheme: dark) と等しい', () => {
    // 語ごとに分けて組み立てている（theme.ts の注記）ので、結果をここで固定する。
    expect(PREFERS_DARK_QUERY).toBe('(prefers-color-scheme: dark)');
  });
});

describe('resolveTheme', () => {
  it('明示された選択はそのまま返す（OS 設定に左右されない）', () => {
    expect(resolveTheme('light', true)).toBe('light');
    expect(resolveTheme('dark', false)).toBe('dark');
  });

  it('system は OS 設定へ従う', () => {
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('system', false)).toBe('light');
  });
});

describe('applyTheme（<html data-theme>）', () => {
  it('明示された選択は data-theme として書く', () => {
    applyTheme('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    applyTheme('light');
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('system のときは属性そのものを消す（"system" とは書かない）', () => {
    applyTheme('dark');
    applyTheme('system');
    // styles.css のライト規則は :root:not([data-theme='dark']) なので、値を書くと 4 段のどれにも当たらない。
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });
});

describe('localStorage の読み書き', () => {
  it('保存した選択を読み戻せる', () => {
    writeStoredTheme('dark');
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('dark');
    expect(readStoredTheme()).toBe('dark');
  });

  it('未保存・不正値はいずれも system へ倒す', () => {
    expect(readStoredTheme()).toBe('system');
    window.localStorage.setItem(STORAGE_KEY, 'neon');
    expect(readStoredTheme()).toBe('system');
  });

  it('localStorage が例外を投げても落ちない（プライベートブラウズ・企業ポリシー）', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(readStoredTheme()).toBe('system');
    expect(() => writeStoredTheme('dark')).not.toThrow();
  });

  it('isThemeChoice は 3 つの値だけを通す', () => {
    expect(isThemeChoice('system')).toBe(true);
    expect(isThemeChoice('light')).toBe(true);
    expect(isThemeChoice('dark')).toBe(true);
    expect(isThemeChoice('auto')).toBe(false);
    expect(isThemeChoice(null)).toBe(false);
  });
});

describe('applyStoredTheme（起動時の入口）', () => {
  it('保存済みの選択を <html> へ当て、その選択を返す', () => {
    writeStoredTheme('light');
    expect(applyStoredTheme()).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('未保存なら system（属性なし）', () => {
    expect(applyStoredTheme()).toBe('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });
});
