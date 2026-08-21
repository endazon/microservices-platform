#!/usr/bin/env node
/**
 * NFR / #455 子 C（Wolverine 移行 手順 1・6）: イベント型 → 発行元 / 購読先の対応表を走査で作り、
 * baseline に固定する。計画 ADR-0027（Wolverine）/ ADR-0030。
 *
 * ## なぜ移行より先に要るのか
 *
 * 計画は Wolverine 移行の手順 1 を「移行**前**に対応表を機械的に作る（後の検査の基準になる）」と定め、
 * 同時に「**うち 2 つの退行はビルド・ユニットテスト・トポロジ検査をすべて通過したまま、例外もログも
 * 出さずに業務イベントを失う**」と警告している。**赤で分からない種類の退行**なので、移行前に正解表を
 * 凍結しておかないと、移行後に「これで合っているのか」を判定する基準が無い。
 *
 * 最も重要な固定対象は **`DocumentUpdated` の購読が 2 件である**こと（IngestionService ＋ WikiService）。
 * 手順 3（リスニングキュー名にサービス名を前置する）を誤ると 2 つの購読者が同一キューを競合し、
 * **片方だけがメッセージを受け取る**。本検査はその減少を exit 1 で止める。
 *
 * ## 設計の要点
 *
 * 1. **列挙を持たない。** イベント型は `*.Contracts/Events/*.cs` の走査で発見する。
 * 2. **両方向の ratchet。** 減っても増えても違反にする（増加を黙って許すと baseline が形骸化する）。
 * 3. **0 件走査で緑を返さない。** イベント 0 件・購読 0 件なら exit 1（#797 / IADR-0130）。
 * 4. **購読 0 件のイベントは notice で必ず出す。** 「検査して 0 件」と「見ていない」を区別する。
 * 5. **MassTransit と Wolverine の両方の記法を読む。** 移行中は同居し得る。
 *    **移行しても表が変わらないことが、移行が正しいことの証拠**になる。
 */

'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..');
const BASELINE = path.join(__dirname, 'event-topology-baseline.json');
const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'coverage']);

/** 別プロジェクトの submodule。契約の名前空間が違うので走査に混ぜない。 */
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);

/** ファイルを再帰的に集める（拡張子で絞る）。 */
function walk(dir, ext, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!SKIP_DIRS.has(e.name)) walk(full, ext, out);
    } else if (e.name.endsWith(ext)) {
      out.push(full);
    }
  }
  return out;
}

const toPosix = (p) => p.split(path.sep).join('/');

/** テストプロジェクト配下か。フィクスチャの発行を実配線と数えないための除外。 */
function isTestPath(rel) {
  return /(^|\/)tests?\//i.test(rel) || /\.Tests?\//.test(rel);
}

/** submodule ユニット配下か。 */
function isExcludedUnit(rel) {
  const m = /^src\/([^/]+)\//.exec(rel);
  return m ? EXCLUDED_UNITS.has(m[1]) : false;
}

/**
 * パスから「担い手」の名前を決める。
 * サービスなら `<unit>/<Service>`、共有なら `<unit>/Shared/<Project>`、BFF なら `<unit>/Bff/<Project>`。
 */
function ownerOf(rel) {
  const p = toPosix(rel);
  let m = /^src\/([^/]+)\/backend\/Services\/([^/]+)\//.exec(p);
  if (m) return `${m[1]}/${m[2]}`;
  m = /^src\/([^/]+)\/backend\/(Shared|Bff|Tests)\/([^/]+)\//.exec(p);
  if (m) return `${m[1]}/${m[2]}/${m[3]}`;
  m = /^src\/([^/]+)\//.exec(p);
  return m ? m[1] : 'unknown';
}

/** イベント契約の型名を発見する（`*.Contracts/Events/*.cs` のファイル名を型名とみなす）。 */
function discoverEvents(repoRoot = REPO_ROOT) {
  const files = walk(path.join(repoRoot, 'src'), '.cs')
    .map((f) => toPosix(path.relative(repoRoot, f)))
    .filter((f) => !isExcludedUnit(f))
    .filter((f) => /\/[^/]*\.Contracts\/Events\/[^/]+\.cs$/.test(f));
  return [...new Set(files.map((f) => path.basename(f, '.cs')))].sort();
}

/**
 * 発行元を見つける。MassTransit / Wolverine とも `Publish` 系の呼び出しで表す。
 * `Publish(new Foo(...))` / `Publish<Foo>(...)` / `PublishAsync(new Foo(...))` を拾う。
 */
function findPublishers(content, events) {
  const hit = new Set();
  for (const ev of events) {
    const re = new RegExp(String.raw`Publish[A-Za-z]*\s*(?:<\s*${ev}\s*>|\(\s*new\s+${ev}\b)`);
    if (re.test(content)) hit.add(ev);
  }
  return hit;
}

/**
 * 購読先を見つける。**MassTransit と Wolverine の両方の記法**を読む。
 *   - MassTransit: `IConsumer<Foo>`
 *   - Wolverine 規約: `Handle(Foo ...)` / `Consume(Foo ...)`（メソッド引数の第 1 型）
 */
function findSubscribers(content, events) {
  const hit = new Set();
  for (const ev of events) {
    const massTransit = new RegExp(String.raw`IConsumer\s*<\s*${ev}\s*>`);
    const wolverine = new RegExp(String.raw`\b(?:Handle|Consume)\s*\(\s*(?:\[[^\]]*\]\s*)?${ev}\s+\w`);
    if (massTransit.test(content) || wolverine.test(content)) hit.add(ev);
  }
  return hit;
}

/** 対応表を作る。{ [event]: { publishers: [...], subscribers: [...] } } */
function buildTopology(repoRoot = REPO_ROOT) {
  const events = discoverEvents(repoRoot);
  const table = Object.fromEntries(events.map((e) => [e, { publishers: new Set(), subscribers: new Set() }]));
  if (events.length === 0) return { events, topology: {} };

  const files = walk(path.join(repoRoot, 'src'), '.cs')
    .map((f) => toPosix(path.relative(repoRoot, f)))
    .filter((f) => !isExcludedUnit(f))
    .filter((f) => !isTestPath(f));

  for (const rel of files) {
    let content;
    try {
      content = fs.readFileSync(path.join(repoRoot, rel), 'utf8');
    } catch {
      continue;
    }
    const owner = ownerOf(rel);
    for (const ev of findPublishers(content, events)) table[ev].publishers.add(owner);
    for (const ev of findSubscribers(content, events)) table[ev].subscribers.add(owner);
  }

  const topology = {};
  for (const ev of events) {
    topology[ev] = {
      publishers: [...table[ev].publishers].sort(),
      subscribers: [...table[ev].subscribers].sort(),
    };
  }
  return { events, topology };
}

function loadBaseline() {
  try {
    return JSON.parse(fs.readFileSync(BASELINE, 'utf8'));
  } catch {
    return null;
  }
}

/** baseline と突き合わせる。増減の**両方向**を違反にする。 */
function diffAgainstBaseline(topology, baseline) {
  const violations = [];
  const base = baseline && baseline.topology ? baseline.topology : {};
  const evs = [...new Set([...Object.keys(topology), ...Object.keys(base)])].sort();

  for (const ev of evs) {
    const cur = topology[ev];
    const old = base[ev];
    if (!old) {
      violations.push(`イベント「${ev}」が baseline に無い。新設したなら baseline を更新すること。`);
      continue;
    }
    if (!cur) {
      violations.push(`イベント「${ev}」が消えた（baseline には在る）。契約を削ったなら baseline を更新すること。`);
      continue;
    }
    for (const kind of ['publishers', 'subscribers']) {
      const a = new Set(old[kind] || []);
      const b = new Set(cur[kind] || []);
      const lost = [...a].filter((x) => !b.has(x));
      const added = [...b].filter((x) => !a.has(x));
      const label = kind === 'publishers' ? '発行元' : '購読先';
      if (lost.length > 0) {
        violations.push(
          `「${ev}」の${label}が減った: ${lost.join(', ')}\n` +
            `      **これは「例外もログも出さずにイベントを失う」退行そのものである。**` +
            `意図した削除なら baseline を更新すること。`,
        );
      }
      if (added.length > 0) {
        violations.push(
          `「${ev}」の${label}が増えた: ${added.join(', ')}\n` +
            `      baseline の更新を忘れている（増加を黙って許すと baseline が形骸化する）。`,
        );
      }
    }
  }
  return violations;
}

// ---------------------------------------------------------------- self-test

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n += 1;
    console.log(`  ok  ${name}`);
  };
  const EV = ['DocumentUpdated', 'IngestionCompleted'];

  ok('MassTransit の IConsumer<T> を購読として拾う', () => {
    const s = findSubscribers('class C : IConsumer<DocumentUpdated>, IPipelineStep {}', EV);
    assert.deepStrictEqual([...s], ['DocumentUpdated']);
  });

  ok('Wolverine 規約の Handle(T) / Consume(T) も購読として拾う', () => {
    assert.ok(findSubscribers('public Task Handle(DocumentUpdated e) => Task.CompletedTask;', EV).has('DocumentUpdated'));
    assert.ok(findSubscribers('public void Consume(DocumentUpdated msg) { }', EV).has('DocumentUpdated'));
    assert.ok(findSubscribers('public void Handle([NotNull] DocumentUpdated m) { }', EV).has('DocumentUpdated'));
  });

  ok('Publish(new T) / Publish<T> を発行として拾う', () => {
    assert.ok(findPublishers('await bus.Publish(new DocumentUpdated(id));', EV).has('DocumentUpdated'));
    assert.ok(findPublishers('await bus.PublishAsync<DocumentUpdated>(msg);', EV).has('DocumentUpdated'));
  });

  ok('型名の部分一致で誤検出しない', () => {
    assert.strictEqual(findSubscribers('class C : IConsumer<DocumentUpdatedV2> {}', EV).size, 0);
    assert.strictEqual(findPublishers('Publish(new DocumentUpdatedV2());', EV).size, 0);
  });

  ok('担い手はパスから決まる（サービス / 共有 / BFF）', () => {
    assert.strictEqual(ownerOf('src/knowledge/backend/Services/WikiService/src/W/X.cs'), 'knowledge/WikiService');
    assert.strictEqual(
      ownerOf('src/platform/backend/Shared/Platform.Shared.Infrastructure/X.cs'),
      'platform/Shared/Platform.Shared.Infrastructure',
    );
  });

  ok('テストプロジェクトは走査から外れる（フィクスチャの発行を実配線と数えない）', () => {
    assert.ok(isTestPath('src/knowledge/backend/Tests/Knowledge.IntegrationTests/X.cs'));
    assert.ok(isTestPath('src/platform/backend/Bff/Platform.Bff.Tests/X.cs'));
    assert.ok(!isTestPath('src/knowledge/backend/Services/WikiService/src/W/X.cs'));
  });

  ok('購読が減ると違反になる（イベントを失う退行）', () => {
    const base = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x', 'y'] } } };
    const v = diffAgainstBaseline({ DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } }, base);
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('購読先が減った'));
  });

  ok('購読が増えても違反になる（baseline の更新を強制する）', () => {
    const base = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } } };
    const v = diffAgainstBaseline({ DocumentUpdated: { publishers: ['a'], subscribers: ['x', 'z'] } }, base);
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('購読先が増えた'));
  });

  console.log(`[check-event-topology] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const { events, topology } = buildTopology();

  // 0 件走査で緑を返さない（#797 / IADR-0130）。
  if (events.length === 0) {
    console.error('[check-event-topology] イベント契約が 0 件だった。走査が壊れている可能性がある。');
    process.exit(1);
  }
  const totalSubs = Object.values(topology).reduce((a, t) => a + t.subscribers.length, 0);
  if (totalSubs === 0) {
    console.error('[check-event-topology] 購読が 1 件も見つからなかった。走査が壊れている可能性がある。');
    process.exit(1);
  }

  if (argv.includes('--update')) {
    fs.writeFileSync(
      BASELINE,
      `${JSON.stringify(
        {
          $comment:
            'NFR / #455 子 C: Wolverine 移行 手順 1 の対応表。移行前の正解表であり、' +
            '移行後もこの表が変わらないことが移行の正しさの証拠になる。増減の両方向を違反にする。',
          topology,
        },
        null,
        2,
      )}\n`,
    );
    console.log(`[check-event-topology] baseline を更新した: ${path.relative(REPO_ROOT, BASELINE)}`);
    return;
  }

  const baseline = loadBaseline();
  if (!baseline) {
    console.error(
      `[check-event-topology] baseline が読めない: ${path.relative(REPO_ROOT, BASELINE)}\n` +
        '  初回は --update で作成すること。',
    );
    process.exit(1);
  }

  // 購読 0 件のイベントを notice で必ず出す（「検査して 0 件」と「見ていない」を区別する）。
  const orphans = Object.entries(topology)
    .filter(([, t]) => t.subscribers.length === 0)
    .map(([ev, t]) => `${ev}（発行元: ${t.publishers.length > 0 ? t.publishers.join(', ') : 'なし'}）`);
  if (orphans.length > 0) {
    console.log(
      `notice: 購読が 0 件のイベントが ${orphans.length} 件ある（違反ではない）: ${orphans.join(' / ')}`,
    );
  }

  const violations = diffAgainstBaseline(topology, baseline);
  if (violations.length > 0) {
    console.error(`[check-event-topology] ${violations.length} 件の違反:`);
    for (const v of violations) console.error(`\n  - ${v}`);
    process.exit(1);
  }

  console.log(`[check-event-topology] OK: イベント ${events.length} 件 / 購読 ${totalSubs} 件が baseline と一致。`);
  for (const [ev, t] of Object.entries(topology)) {
    console.log(
      `  ${ev}: 発行 [${t.publishers.join(', ') || '-'}] → 購読 [${t.subscribers.join(', ') || '-'}]`,
    );
  }
}

if (require.main === module) main();

module.exports = { buildTopology, discoverEvents, findPublishers, findSubscribers, diffAgainstBaseline, ownerOf, isTestPath };
