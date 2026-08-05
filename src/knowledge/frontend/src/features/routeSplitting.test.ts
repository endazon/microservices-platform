import { describe, it, expect, vi } from 'vitest';

// NFR, ADR-0031 / IADR-0134: ルート単位の遅延読み込み（分割境界の回帰ガード）。
//
// 何を守るか: **画面のモジュールが feature の index から静的 import へ戻らないこと**。
// 戻ると Rollup はその画面を初期チャンクへ載せ直すが、ビルドは通り、画面のテストも通り、
// 型検査も通る——**バンドルのサイズ表を人が読まない限り誰も気付かない**。
// 実測では画面 11 本の遅延化で初期チャンクが 632.98 kB → 533.66 kB へ下がっている
// （その先の 274.33 kB までは manualChunks の寄与。IADR-0134 §実測）。
//
// 検出のしかた: 画面モジュールを `vi.mock` で包む。**mock の factory は実際に import された
// ときにだけ評価される**ため、feature の index を import しただけで factory が走ったなら
// 静的 import が復活している。`component.preload`（unguarded）/ `wrapInSuspense`
// （guarded。ガードは初期チャンクに残すため）の宣言も併せて固定する。

const loaded = vi.hoisted(() => new Set<string>());

const trace =
  <T,>(id: string, importOriginal: () => Promise<T>) =>
  async (): Promise<T> => {
    loaded.add(id);
    return await importOriginal();
  };

vi.mock('./sc01-search/SearchChatPage', (o) => trace('sc01', o as () => Promise<unknown>)());
vi.mock('./sc02-results/SearchResultsPage', (o) => trace('sc02', o as () => Promise<unknown>)());
vi.mock('./sc03-document/DocumentDetailPage', (o) => trace('sc03', o as () => Promise<unknown>)());
vi.mock('./sc04-wiki/WikiAccessPage', (o) => trace('sc04', o as () => Promise<unknown>)());
vi.mock('./sc05-documents/DocumentManagementPage', (o) =>
  trace('sc05', o as () => Promise<unknown>)(),
);
vi.mock('./sc06-datasources/DataSourceManagementPage', (o) =>
  trace('sc06', o as () => Promise<unknown>)(),
);
vi.mock('./sc07-conversions/ConversionJobsPage', (o) =>
  trace('sc07', o as () => Promise<unknown>)(),
);
vi.mock('./sc08-analysis/AnalysisDashboardPage', (o) =>
  trace('sc08', o as () => Promise<unknown>)(),
);
vi.mock('./sc09-admin-abac/AdminAbacSettingsPage', (o) =>
  trace('sc09', o as () => Promise<unknown>)(),
);
vi.mock('./sc10-operations/OperationsDashboardPage', (o) =>
  trace('sc10', o as () => Promise<unknown>)(),
);
vi.mock('./sc11-config/ConfigViewerPage', (o) => trace('sc11', o as () => Promise<unknown>)());

/**
 * 画面（SC）と、その feature index の importer・ガードの有無。
 * importer をリテラルで持つのは、変数を含む動的 import が Vite の解析対象外になるためである。
 * ガードのある画面は RequireRole を初期チャンクに残す。
 */
const SCREENS = [
  ['SC-01', 'sc01', () => import('./sc01-search'), false],
  ['SC-02', 'sc02', () => import('./sc02-results'), false],
  ['SC-03', 'sc03', () => import('./sc03-document'), false],
  ['SC-04', 'sc04', () => import('./sc04-wiki'), false],
  ['SC-05', 'sc05', () => import('./sc05-documents'), true],
  ['SC-06', 'sc06', () => import('./sc06-datasources'), true],
  ['SC-07', 'sc07', () => import('./sc07-conversions'), true],
  ['SC-08', 'sc08', () => import('./sc08-analysis'), false],
  ['SC-09', 'sc09', () => import('./sc09-admin-abac'), true],
  ['SC-10', 'sc10', () => import('./sc10-operations'), true],
  ['SC-11', 'sc11', () => import('./sc11-config'), true],
] as const;

describe('route-level code splitting (NFR / IADR-0134)', () => {
  it('keeps every screen module out of the eagerly imported graph', async () => {
    // 合成点（ユニットの束ね役）を読む＝実アプリが初期チャンクへ載せるものと同じ範囲。
    const mod = await import('./index');
    // 「見えるはずのもの」を先に確かめる（読み込み自体が失敗していれば下の assert は空振りする）。
    expect(typeof mod.createKnowledgeRoutes).toBe('function');
    expect(mod.knowledgeNavItems.length).toBeGreaterThan(0);

    expect([...loaded]).toEqual([]);
  });

  it.each(SCREENS)(
    '%s is reachable only through a lazy boundary',
    async (_sc, id, importFeature, guarded) => {
      const shell = { id: '_shell' } as never;
      const feature: Record<string, unknown> = await importFeature();
      const factory = Object.entries(feature).find(([k]) => k.startsWith('create'))?.[1] as (
        s: never,
      ) => { options: Record<string, unknown> };
      const options = factory(shell).options;

      if (guarded) {
        // ガード（RequireRole）は初期チャンクに残すため component は素の関数である。
        // 遅延した画面が描画時に suspend するので Suspense 境界の宣言が要る。
        expect(options.wrapInSuspense).toBe(true);
      } else {
        // lazyRouteComponent は `.preload()` を生やす。router.load() がこれを呼ぶ。
        expect(typeof (options.component as { preload?: unknown }).preload).toBe('function');
      }
      // ここまでで画面モジュールはまだ読まれていない。
      expect(loaded.has(id)).toBe(false);
    },
  );
});
