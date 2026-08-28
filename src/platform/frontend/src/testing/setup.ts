import '@testing-library/jest-dom/vitest';
import { activate } from '@foundation/i18n';

// NFR, ADR-0031 / IADR-0134: 遅延ルートが持ち込む `findBy*` の待ち時間の延長
// （`asyncUtilTimeout`）は**ここに置かない**。本ファイルは `src/vitest.config.ts` の
// `setupFiles` ＝**全ユニット横断の setup** であり、ここで延ばすと AST・`@platform/ui`・
// 純関数のテストまで「1 秒で落ちるべき退行が 5 秒待って落ちる」経路になる。
// 延長が要るのは**ガード配下の画面を描画するテストだけ**なので、その唯一の入口である
// `@foundation/testing/renderUnitRoute` で局所化している。

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

// ADR-0031 §採用技術一覧（チャート = Apache ECharts）/ #788: ECharts は **SVG レンダラ**でも
// 文字幅の実測に canvas の 2D コンテキストを使う（zrender の既定 `platformApi.measureText`）。
// jsdom は `HTMLCanvasElement.prototype.getContext` を実装しておらず、図を描くテストが走るたびに
// 「Not implemented」を stderr へ大量に吐いてテスト出力が読めなくなる（実測: SC-10 の 1 ファイルで 21 行）。
//
// **`canvas` パッケージは入れない** —— ネイティブビルドを要する重い依存であり、
// 得られるのは「文字幅が正確になる」ことだけである。図の**判断の部分**（系列・順序・欠測の扱い）は
// 純関数（`dashboardCharts.ts`）のテストが固定しており、描画の画素は検証対象ではない。
// よって幅を返すだけの最小のスタブを置く（`window.scrollTo` を no-op にしたのと同じ作法）。
if (typeof HTMLCanvasElement !== 'undefined') {
  HTMLCanvasElement.prototype.getContext = (() => ({
    measureText: (text: string) => ({ width: text.length * 6 }),
    fillText: () => {},
    fillRect: () => {},
    clearRect: () => {},
    save: () => {},
    restore: () => {},
    beginPath: () => {},
    closePath: () => {},
    setTransform: () => {},
    translate: () => {},
    scale: () => {},
    drawImage: () => {},
  })) as unknown as HTMLCanvasElement['getContext'];
}
