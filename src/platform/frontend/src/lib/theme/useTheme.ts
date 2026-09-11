import { useCallback, useEffect, useState } from 'react';
import {
  applyTheme,
  nextTheme,
  PREFERS_DARK_QUERY,
  readStoredTheme,
  resolveTheme,
  systemPrefersDark as readSystemPrefersDark,
  writeStoredTheme,
  type ResolvedTheme,
  type ThemeChoice,
} from './theme';

export interface UseThemeResult {
  /** 利用者の選択（`system` / `light` / `dark`）。 */
  choice: ThemeChoice;
  /** 実際に適用されているテーマ。 */
  resolved: ResolvedTheme;
  /** OS / ブラウザがダークを好むか。 */
  prefersDark: boolean;
  /** 選択を直接指定する。 */
  setTheme: (choice: ThemeChoice) => void;
  /** 3 状態を 1 つ進める（`nextTheme` の巡回）。 */
  cycleTheme: () => void;
}

/**
 * ADR-0031 / 利用者裁定（2026-09-12）: テーマの状態を持つフック。
 *
 * **OS 設定の変化を購読する。** `system` を選んでいる利用者は、OS のダークモードを
 * 切り替えた瞬間に画面が追従することを期待する（CSS の `prefers-color-scheme` は自動で
 * 追従するが、**切替ボタンの表示と巡回の向き**は JS 側が知っている必要がある）。
 *
 * 初期化時に保存済みの選択を `<html>` へ当て直す。`main.tsx` の起動時適用
 * （`applyStoredTheme()`）と二重になるが、**冪等**であり、フック単独で使った場合
 * （Storybook・テスト）にも正しく効く。
 */
export function useTheme(): UseThemeResult {
  const [choice, setChoice] = useState<ThemeChoice>(() => readStoredTheme());
  const [prefersDark, setPrefersDark] = useState<boolean>(() => readSystemPrefersDark());

  useEffect(() => {
    applyTheme(choice);
  }, [choice]);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const media = window.matchMedia(PREFERS_DARK_QUERY);
    const onChange = (event: MediaQueryListEvent) => setPrefersDark(event.matches);
    media.addEventListener('change', onChange);
    // 購読を張るまでの間に変わっている可能性があるので、張った直後に読み直す。
    setPrefersDark(media.matches);
    return () => media.removeEventListener('change', onChange);
  }, []);

  const setTheme = useCallback((next: ThemeChoice) => {
    setChoice(next);
    writeStoredTheme(next);
    applyTheme(next);
  }, []);

  const cycleTheme = useCallback(() => {
    setChoice((current) => {
      const next = nextTheme(current, readSystemPrefersDark());
      writeStoredTheme(next);
      applyTheme(next);
      return next;
    });
  }, []);

  return {
    choice,
    resolved: resolveTheme(choice, prefersDark),
    prefersDark,
    setTheme,
    cycleTheme,
  };
}
