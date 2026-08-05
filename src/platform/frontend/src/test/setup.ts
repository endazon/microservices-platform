import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/dom';
import { activate } from '../foundation/i18n';

// NFR, ADR-0031 / IADR-0134: 遅延ルート（`lazyRouteComponent`）を入れた分だけ、
// `findBy*` / `waitFor` の待ち時間に**モジュールの動的 import が乗る**。
// ガード（`RequireRole`）配下の画面は `router.load()` の事前読み込みが効かず、
// 描画が始まってから import が走るためである（IADR-0134 決定 2）。
// 既定の 1000 ms は**カバレッジ計測を有効にすると足りなくなる**——実測で
// `sc05-documents` の一覧テストが 9 回中 1 回落ちた（`pnpm run test` では再現せず
// `pnpm run test:coverage` でのみ再現。導入前の `68d91ce` は 6 回中 6 回 green）。
// 待ち時間を延ばしても**通るテストの所要時間は変わらない**（ポーリングは条件成立で止まる）。
configure({ asyncUtilTimeout: 5000 });

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
