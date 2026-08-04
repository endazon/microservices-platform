import '@testing-library/jest-dom/vitest';

// ADR-0031 / IADR-0124: TanStack Router はナビゲーションのたびにスクロール復元を行う。
// jsdom は window.scrollTo を実装しておらず「Not implemented」を毎回 stderr へ出すため、
// テスト出力が読めなくなる。挙動の検証対象ではないので無害な no-op を置く。
if (typeof window !== 'undefined') {
  window.scrollTo = () => {};
}
