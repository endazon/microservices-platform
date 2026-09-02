// FR-20, ADR-0037 決定 1, IADR-0331 決定 3: Obsidian プラグインのビルド。
//
// Obsidian が読むのは `main.js`（CommonJS・`obsidian` モジュールは本体が与えるので external）と
// `manifest.json` の 2 つである。`styles.css` は持たない（本段の UI は設定タブと通知だけで、
// 独自の見た目を足さない）。
//
// 同じ同期モジュールを Obsidian 本体なしで実 HTTP に当てるため、Node 向けの `cli.mjs` も
// 同時に束ねる（実測の証跡は Obsidian の GUI ではなくこの CLI で取る。IADR-0331 決定 6）。
//
// 08_data-egress-policy: 成果物に外部 CDN・Web フォント・analytics を含めない。ここで何かを
// 取りに行く設定は無いが、「設定したつもり」で終わらせず、CI が
// `node scripts/check-static-egress.js --require src/obsidian-plugin/dist` で成果物を走査する。
import { build } from 'esbuild';
import { copyFile, mkdir } from 'node:fs/promises';

const outDir = 'dist';
await mkdir(outDir, { recursive: true });

await build({
  entryPoints: ['src/main.ts'],
  bundle: true,
  format: 'cjs',
  platform: 'browser',
  target: 'es2022',
  outfile: `${outDir}/main.js`,
  external: ['obsidian', 'electron', '@codemirror/*', '@lezer/*'],
  sourcemap: false,
  legalComments: 'none',
  logLevel: 'info',
});

await build({
  entryPoints: ['src/cli/pull.ts'],
  bundle: true,
  format: 'esm',
  platform: 'node',
  target: 'node22',
  outfile: `${outDir}/cli.mjs`,
  sourcemap: false,
  legalComments: 'none',
  logLevel: 'info',
});

await copyFile('manifest.json', `${outDir}/manifest.json`);
