#!/usr/bin/env node
// FR-17, SC-18, ADR-0039 §導入の条件 1（#917 / IADR-0274）:
// **ノード 200 / 辺 500 を対話操作（ズーム・パン・ノード選択・種別フィルタ切替）込みで扱えるか**の
// 実測ハーネス。ADR-0039 は「判断は見込みではなく実測で行う」を明示しており、本スクリプトが
// その実測の再現手段である（IADR-0274 §条件 1 に実測値を記録した）。
//
// ■ 何を測るか（SC-18 の対話要素に対応させる）
//   1. init+layout : echarts.init + setOption から最初の 'finished'（力学レイアウト収束・初回描画）まで
//   2. pan         : マウスドラッグ 30 ステップ中の平均フレーム間隔（roam according to SC-18）
//   3. zoom        : ホイール 20 回中の平均フレーム間隔
//   4. select      : ノード click ディスパッチ → select 反映（'finished'）まで
//   5. filter      : 辺の種別フィルタ相当（辺 500 → 250 の setOption）→ 'finished' まで
//
// ■ 作法
//   - 実行環境の SPA と同じ供給源（src/ workspace の echarts@6.1.0）を esbuild で束ね、
//     外部 CDN を一切引かない self-contained な HTML を組み立てて headless Chromium で開く
//     （08_data-egress-policy と同じ制約下で測る）。
//   - レンダラは SVG / Canvas の両方を測る（SPA 既定は SVG。EChart.tsx 参照）。
//
// 使い方: node perf/graph-render/measure.mjs
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = resolve(here, '../../src');

// pnpm の仮想ストアのパスは版で変わるため、ワークスペースの依存グラフ経由で解決する
// （esbuild は vite の依存・playwright-core は @playwright/test の依存）。
const srcRequire = createRequire(join(srcRoot, 'package.json'));
const { build } = createRequire(srcRequire.resolve('vite'))('esbuild');
const feRequire = createRequire(join(srcRoot, 'platform/frontend/package.json'));
const { chromium } = createRequire(feRequire.resolve('@playwright/test'))('playwright-core');

const entry = `
import * as echarts from 'echarts/core';
import { GraphChart } from 'echarts/charts';
import { LegendComponent, TooltipComponent } from 'echarts/components';
import { SVGRenderer, CanvasRenderer } from 'echarts/renderers';
echarts.use([GraphChart, LegendComponent, TooltipComponent, SVGRenderer, CanvasRenderer]);
window.echarts = echarts;
`;

const bundle = await build({
  stdin: { contents: entry, resolveDir: join(srcRoot, 'knowledge/frontend'), loader: 'js' },
  bundle: true,
  format: 'iife',
  write: false,
  minify: true,
});
const js = bundle.outputFiles[0].text;

// SC-18 実データ相当: ノード 200（1 割を個人資料＝roundRect）/ 辺 500・型 5 種の線種を出し分け。
const page = `<!doctype html><meta charset="utf-8">
<style>html,body,#g{margin:0;width:1280px;height:800px}</style>
<div id="g"></div>
<script>${js}</script>
<script>
const N = 200, E = 500;
const rand = (() => { let s = 42; return () => (s = (s * 1664525 + 1013904223) >>> 0) / 2 ** 32; })();
const nodes = Array.from({ length: N }, (_, i) => ({
  id: 'D-' + i,
  name: '文書タイトル ' + i,
  symbol: i % 10 === 0 ? 'roundRect' : 'circle',
  symbolSize: i === 0 ? 30 : 14,
  itemStyle: i % 10 === 0 ? { borderType: 'dashed', borderWidth: 2 } : {},
  label: { show: false },
}));
const styles = [
  { type: 'solid', width: 1 }, { type: 'solid', width: 2 },
  { type: 'solid', width: 4 }, { type: 'dashed', width: 2 }, { type: 'dotted', width: 2 },
];
const links = Array.from({ length: E }, (_, i) => ({
  source: 'D-' + Math.floor(rand() * N),
  target: 'D-' + Math.floor(rand() * N),
  lineStyle: styles[i % 5],
  symbol: i % 5 === 0 ? ['none', 'none'] : ['none', 'arrow'],
}));
const option = (ls) => ({
  animation: false,
  tooltip: { show: true },
  series: [{
    type: 'graph', layout: 'force', roam: true, data: nodes, links: ls,
    force: { repulsion: 60, gravity: 0.1, edgeLength: 40, layoutAnimation: false },
    emphasis: { focus: 'adjacency' }, selectedMode: 'single',
  }],
});
window.run = (renderer) => new Promise((done) => {
  const el = document.getElementById('g');
  el.innerHTML = '';
  const m = {};
  const chart = echarts.init(el, null, { renderer });
  const t0 = performance.now();
  chart.on('finished', function onInit() {
    chart.off('finished', onInit);
    m.initMs = performance.now() - t0;
    requestAnimationFrame(() => interact(chart, m, done));
  });
  chart.setOption(option(links));
});
async function frames(n, step) {
  const gaps = [];
  let prev = performance.now();
  for (let i = 0; i < n; i++) {
    step(i);
    await new Promise((r) => requestAnimationFrame(r));
    const now = performance.now();
    gaps.push(now - prev);
    prev = now;
  }
  return gaps.reduce((a, b) => a + b, 0) / gaps.length;
}
function mouse(type, x, y, extra = {}) {
  const el = document.getElementById('g').querySelector('svg,canvas') ?? document.getElementById('g');
  el.dispatchEvent(new (type === 'wheel' ? WheelEvent : MouseEvent)(type, {
    clientX: x, clientY: y, bubbles: true, ...extra,
  }));
}
async function interact(chart, m, done) {
  // pan: ドラッグ 30 ステップ
  mouse('mousedown', 600, 400, { button: 0 });
  m.panFrameMs = await frames(30, (i) => mouse('mousemove', 600 + i * 8, 400 + i * 4, { buttons: 1 }));
  mouse('mouseup', 840, 520, { button: 0 });
  // zoom: ホイール 20 回
  m.zoomFrameMs = await frames(20, (i) => mouse('wheel', 640, 400, { deltaY: i % 2 ? -120 : 120 }));
  // select: dispatchAction（ヒットテストではなく選択反映の費用を測る）
  {
    const t = performance.now();
    await new Promise((r) => { chart.on('finished', function f() { chart.off('finished', f); r(); });
      chart.dispatchAction({ type: 'select', seriesIndex: 0, dataIndex: 42 }); });
    m.selectMs = performance.now() - t;
  }
  // filter: 辺 500 → 250（型フィルタ相当の setOption）
  {
    const half = links.filter((_, i) => i % 2 === 0);
    const t = performance.now();
    await new Promise((r) => { chart.on('finished', function f() { chart.off('finished', f); r(); });
      chart.setOption(option(half)); });
    m.filterMs = performance.now() - t;
  }
  chart.dispose();
  done(m);
}
</script>`;

const dir = mkdtempSync(join(tmpdir(), 'sc18-perf-'));
const htmlPath = join(dir, 'index.html');
writeFileSync(htmlPath, page);

const browser = await chromium.launch();
try {
  const round = (o) => Object.fromEntries(Object.entries(o).map(([k, v]) => [k, Math.round(v * 10) / 10]));
  for (const renderer of ['svg', 'canvas']) {
    const results = [];
    for (let i = 0; i < 3; i++) {
      const pg = await browser.newPage({ viewport: { width: 1280, height: 800 } });
      await pg.goto(pathToFileURL(htmlPath).href);
      results.push(await pg.evaluate((r) => window.run(r), renderer));
      await pg.close();
    }
    const median = {};
    for (const k of Object.keys(results[0])) {
      median[k] = results.map((r) => r[k]).sort((a, b) => a - b)[1];
    }
    console.log(`[${renderer}] bundle=${(js.length / 1000).toFixed(1)}kB(minified,graph-face-only)`,
      JSON.stringify(round(median)));
  }
} finally {
  await browser.close();
  rmSync(dir, { recursive: true, force: true });
}
