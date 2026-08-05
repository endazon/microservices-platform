import '@testing-library/jest-dom/vitest';
import { activate } from '../foundation/i18n';

// ADR-0031 / IADR-0125 決定 3: テストのロケールを ja に固定する。jsdom の navigator.language は
// 既定で en-US であり、ブラウザ検出に委ねると「テストだけ英語で描画される」ことになる。
// 個々のテストは activate('en') で一時的に切り替えてよい（i18n.test.tsx がそうしている）。
activate('ja');

// ADR-0031 / IADR-0124: TanStack Router はナビゲーションのたびにスクロール復元を行う。
// jsdom は window.scrollTo を実装しておらず「Not implemented」を毎回 stderr へ出すため、
// テスト出力が読めなくなる。挙動の検証対象ではないので無害な no-op を置く。
if (typeof window !== 'undefined') {
  window.scrollTo = () => {};
}
