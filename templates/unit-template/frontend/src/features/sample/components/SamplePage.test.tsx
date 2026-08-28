import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { createSampleUnitRoutes, sampleUnitNavItems } from '../../index';

// テンプレート: feature のテスト。**実装と同居させ** `*.{test,spec}.{ts,tsx}` で命名する。
//
// 描画は `@foundation/testing/renderUnitRoute` を使う（ADR-0031 / IADR-0124）。これは
// **実アプリと同じ id（`_shell`）を持つ検査用シェル**の下にユニットのルートを載せるハーネスで、
// 実アプリのルート木をそのまま使わないのは、それが合成点（`@features`）＝他ユニットまで
// 引き込み、ユニット単体のテストが他ユニットの存在に依存してしまうためである。
//
// BFF を叩く画面では `apiRequest` を差し替える（`vi.mock('@foundation/api/apiClient', …)`）。
// **`apiFetch` を差し替えても orval 生成コードの経路には効かない**——生成コードは
// mutator（`bffFetch`）→ `apiRequest` を通る（IADR-0135 決定 4）。本雛形は BFF 契約を
// 持たないため、その差し替えは書いていない。

describe('SamplePage（雛形）', () => {
  it('ルート factory が公開したパスで画面が描画される', async () => {
    await renderUnitRoute(createSampleUnitRoutes, { initialEntry: '/sample' });

    expect(await screen.findByRole('heading', { name: 'サンプル' })).toBeInTheDocument();
  });

  it('取得結果が空のとき、中立の表示になる（IADR-0009: 存在秘匿）', async () => {
    await renderUnitRoute(createSampleUnitRoutes, { initialEntry: '/sample' });

    // 「権限が無い」と「存在しない」を UI で区別しない。
    expect(await screen.findByText('該当するものはありません。')).toBeInTheDocument();
  });

  // IADR-0124 決定 5: **ナビはデータであり `<Link to>` の静的検査が効かない。**
  // よって「全ナビ項目の `to` がルート木に解決すること」を単体テストで固定する。
  //
  // 🔴 **捕まえるのは「ナビだけ足してルートを足し忘れた」側だけである。** 逆向き
  // （ルートだけ足してナビに載せ忘れた）は**テストにできない** —— `features/index.ts` が
  // 書いているとおり、ナビに出さない画面（一覧・検索からのみ到達する詳細など）は
  // 載せないのが正しく、「全ルートにナビ項目がある」は不変条件ではないからである。
  // **逆向きは人が見る。**（従前ここは「対の追加漏れ（ルートだけ／ナビだけ）を捕まえる」と
  // 書いていたが、走査元がナビ項目である以上ルート側の漏れには届かない。過大申告だった。）
  //
  // **ルートの配列からパスを読み出す形では書かない。** `route.path` は木を組んだ後に解決される
  // ため未定義で（実測。試験が空振りする）、`route.options.path` は実行時には取れるが
  // **型に無い**（`tsc` が落ちる）。実際に描画して到達を確かめるのが、型にも実装にも依存しない。
  it.each(sampleUnitNavItems.map((nav) => [nav.id, nav.to] as const))(
    'ナビ項目 %s の遷移先 %s が描画される',
    async (_id, to) => {
      await renderUnitRoute(createSampleUnitRoutes, { initialEntry: to });

      expect(await screen.findByRole('heading', { name: 'サンプル' })).toBeInTheDocument();
    },
  );

  it('全ナビ項目がグループを宣言している', () => {
    // 05_screens §共通シェル: 総称フォールバックが無いため、宣言漏れは
    // 「どのグループにも属さず静かに消える」ことを意味する（型でも強制しているが値でも固定する）。
    for (const nav of sampleUnitNavItems) {
      expect(nav.group).toBeTruthy();
    }
  });
});
