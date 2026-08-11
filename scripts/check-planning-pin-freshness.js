#!/usr/bin/env node
'use strict';
/*
 * check-planning-pin-freshness.js — 計画 pin と planning 既定ブランチの乖離のうち、
 * **実装の着手可否に効くもの**を検知する（issue #589 / IADR-0170）。
 *
 * 背景:
 *   IADR-0119 の着手条件は「前提 ADR が Accepted になること」である。計画側で裁定が
 *   反映されても、本リポの pin を進めるまで実装側には何も伝わらない。**待ち時間の実体は
 *   「回答待ち」ではなく「回答に気づいていない時間」だった**（#572 施策 7 の訂正）。
 *   同型は #548 / #560 / #589 の 3 回とも「人が気づいて起票」で処理されている。
 *   **自動化するのは検知ではなく、その手作業（起票）そのものである。**
 *
 * 方針:
 *   - **落とさない（fail-open）。** pin を進める判断は人・AI が行う。赤にすると pin が
 *     古い間ずっと CI が止まる（#589 の指定）。通知手段は「赤」ではなく issue にする。
 *   - **「検査していない」と「乖離なし」を読み分けられるようにする。** planning が未 populate
 *     なら、その旨を出して exit 0 する。**黙って緑を返さない**（#546 / #664 / #674 の型）。
 *   - **鳴りすぎると読まれなくなる。** 着手可否に効く差分だけを鳴らす（判断 3）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ。scripts/ に依存解決の経路が無い）。
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { warn, notice } = require('./lib/ci-annotate.js');

const REPO_ROOT = path.join(__dirname, '..');
const PLANNING = 'planning';

// --- 判断 3: 「着手可否に効く」の定義 ---------------------------------------
//
// 鳴らす対象を絞る。全差分で鳴らすと、pin が古い間ずっと同じ内容が鳴り続け、読まれなくなる。
// 実測（2026-08-11・pin 2cf0795 → planning HEAD 14aed71）では、3 コミット 22 ファイルの
// 差分のうち、この規則で拾うのは ADR の status 変化 1 件と要求の変更 1 件である。

/** ADR ファイル（status の変化を見る）。 */
const ADR_RE = /^projects\/[^/]+\/07_adr\/ADR-\d+[^/]*\.md$/;

/** 受け入れ基準が載る計画書（変更そのものを見る）。 */
const GATE_DOC_RE = /^projects\/[^/]+\/(02_requirements|05_screens)\/[^/]+\.md$/;

/**
 * 変更ファイル一覧を「着手可否に効くもの」と「効かないもの」へ仕分ける。純関数。
 *
 * @param {string[]} files planning リポジトリ内の相対パス
 * @returns {{adr: string[], gateDocs: string[], ignored: string[]}}
 */
function classifyChanges(files) {
  const adr = [];
  const gateDocs = [];
  const ignored = [];
  for (const f of files) {
    if (ADR_RE.test(f)) adr.push(f);
    else if (GATE_DOC_RE.test(f)) gateDocs.push(f);
    else ignored.push(f);
  }
  return { adr, gateDocs, ignored };
}

/** frontmatter の `status:` を 1 件読む。読めなければ null。 */
function statusOf(text) {
  if (typeof text !== 'string') return null;
  const m = text.match(/^status:\s*(.+?)\s*$/m);
  return m ? m[1] : null;
}

/**
 * ADR の status 変化を求める。純関数。
 *
 * ★ **「変化した」だけでなく「Proposed → Accepted」かどうかを別に持つ。**
 *   着手ゲートが外れるのはこの遷移であり、他の変化（Accepted → Superseded 等）とは
 *   意味が違う。まとめると、鳴らす理由が読めなくなる。
 *
 * @param {{file: string, before: string|null, after: string|null}[]} pairs
 */
function adrStatusChanges(pairs) {
  const out = [];
  for (const { file, before, after } of pairs) {
    const b = statusOf(before);
    const a = statusOf(after);
    if (b === a) continue;
    out.push({
      file,
      before: b,
      after: a,
      // 着手ゲートが外れる遷移か
      unblocks: b === 'Proposed' && a === 'Accepted',
    });
  }
  return out;
}

/**
 * 検知結果を組み立てる。純関数（git を触らない）。
 *
 * @param {{pin: string, head: string, files: string[], adrPairs: object[]}} input
 */
function findIssues(input) {
  const { pin, head, files, adrPairs } = input;
  if (pin === head) return { drifted: false, reasons: [] };
  const { adr, gateDocs, ignored } = classifyChanges(files);
  const statusChanges = adrStatusChanges(adrPairs);
  const reasons = [];
  for (const c of statusChanges) {
    reasons.push({
      kind: c.unblocks ? 'adr-unblocked' : 'adr-status-changed',
      file: c.file,
      detail: `${c.before} → ${c.after}`,
    });
  }
  for (const f of gateDocs) {
    reasons.push({ kind: 'gate-doc-changed', file: f, detail: '受け入れ基準が変わった可能性' });
  }
  return {
    drifted: true,
    reasons,
    counts: { adr: adr.length, gateDocs: gateDocs.length, ignored: ignored.length },
  };
}

// --- git 側（副作用あり） ---------------------------------------------------

/**
 * planning サブモジュールが populate 済みか（`check-doc-links.js` の作法を踏襲）。
 * CI が submodule なしで checkout すると空プレースホルダになるため、存在チェックだけでは足りない。
 */
function planningPopulated(root = REPO_ROOT) {
  try {
    return fs.existsSync(path.join(root, PLANNING, 'projects'));
  } catch (e) {
    return false;
  }
}

function git(args, opts = {}) {
  return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, ...opts }).trim();
}

/** 本リポが pin している planning の commit。 */
function pinnedCommit(root = REPO_ROOT) {
  try {
    const line = git(['-C', root, 'ls-tree', 'HEAD', PLANNING]);
    const m = line.match(/^\d+ commit ([0-9a-f]{40})\t/);
    return m ? m[1] : null;
  } catch (e) {
    return null;
  }
}

/** planning の既定ブランチ HEAD。fetch できなければ null（fail-open）。 */
function remoteHead(root = REPO_ROOT, { fetch = true } = {}) {
  const dir = path.join(root, PLANNING);
  try {
    if (fetch) {
      // 失敗しても続行する（オフライン・認証なしの環境がある）。
      try {
        git(['-C', dir, 'fetch', '--quiet', 'origin'], { timeout: 60_000 });
      } catch (e) {
        /* fail-open */
      }
    }
    for (const ref of ['origin/HEAD', 'origin/main', 'origin/master']) {
      try {
        return git(['-C', dir, 'rev-parse', ref]);
      } catch (e) {
        /* 次の候補へ */
      }
    }
    return null;
  } catch (e) {
    return null;
  }
}

/** pin..head の変更ファイル一覧と、ADR の前後本文を集める。 */
function collect(root, pin, head) {
  const dir = path.join(root, PLANNING);
  const files = git(['-C', dir, 'diff', '--name-only', pin, head])
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
  const { adr } = classifyChanges(files);
  const adrPairs = adr.map((f) => {
    const show = (rev) => {
      try {
        return git(['-C', dir, 'show', `${rev}:${f}`]);
      } catch (e) {
        return null; // 追加・削除された ADR
      }
    };
    return { file: f, before: show(pin), after: show(head) };
  });
  return { files, adrPairs };
}

// --- 自己試験 ---------------------------------------------------------------

function selfTest() {
  let failed = 0;
  const t = (name, cond) => {
    if (!cond) {
      failed++;
      console.error(`  NG  ${name}`);
    } else {
      console.log(`  ok  ${name}`);
    }
  };

  // 判断 3: 仕分け
  const c = classifyChanges([
    'projects/microservices-platform/07_adr/ADR-0023_edge-cert.md',
    'projects/microservices-platform/02_requirements/01_requirements.md',
    'projects/microservices-platform/05_screens/01_screens.md',
    'draft/feedback/20260807_x.md',
    'tools/impl-sync/sync-impl-adr.js',
    'projects/microservices-platform/INDEX.md',
    'projects/microservices-platform/07_adr/README.md',
  ]);
  t('仕分け: ADR を拾う', c.adr.length === 1);
  t('仕分け: 要求・画面を拾う', c.gateDocs.length === 2);
  t('仕分け: draft / tools / INDEX は拾わない', c.ignored.length === 4);
  t('仕分け: 07_adr/README.md は ADR ではない', !c.adr.includes('projects/microservices-platform/07_adr/README.md'));

  // status の読み取り
  t('status を読む', statusOf('---\ntitle: x\nstatus: Accepted\n---\n') === 'Accepted');
  t('status が無ければ null', statusOf('---\ntitle: x\n---\n') === null);
  t('本文が無ければ null', statusOf(null) === null);

  // status 変化
  const ch = adrStatusChanges([
    { file: 'a.md', before: 'status: Proposed\n', after: 'status: Accepted\n' },
    { file: 'b.md', before: 'status: Accepted\n', after: 'status: Superseded\n' },
    { file: 'c.md', before: 'status: Accepted\n', after: 'status: Accepted\n' },
  ]);
  t('status 変化を 2 件拾う（変化なしは拾わない）', ch.length === 2);
  t('Proposed → Accepted だけを unblocks とする', ch.filter((x) => x.unblocks).length === 1);
  t('Accepted → Superseded は unblocks ではない', ch.find((x) => x.file === 'b.md').unblocks === false);

  // ★ 新規追加された ADR（before が null）でも落ちない
  const added = adrStatusChanges([{ file: 'n.md', before: null, after: 'status: Accepted\n' }]);
  t('新規 ADR: before が null でも拾う', added.length === 1 && added[0].before === null);
  t('新規 ADR は unblocks ではない（Proposed からの遷移ではない）', added[0].unblocks === false);

  // findIssues
  t(
    'pin == head なら乖離なし',
    findIssues({ pin: 'x', head: 'x', files: ['projects/p/07_adr/ADR-1_a.md'], adrPairs: [] }).drifted === false,
  );
  const r = findIssues({
    pin: 'a',
    head: 'b',
    files: ['projects/p/02_requirements/01_requirements.md', 'draft/x.md'],
    adrPairs: [],
  });
  t('要求の変更で鳴る', r.drifted && r.reasons.some((x) => x.kind === 'gate-doc-changed'));
  const r2 = findIssues({
    pin: 'a',
    head: 'b',
    files: ['draft/x.md', 'tools/y.js'],
    adrPairs: [],
  });
  // ★ 乖離はあるが、着手可否に効く変更は無い ＝ 理由 0 件。
  t('draft / tools だけの差分では理由が 0 件', r2.drifted && r2.reasons.length === 0);

  console.log(failed === 0 ? '[check-planning-pin-freshness] self-test OK' : `NG ${failed} 件`);
  return failed === 0 ? 0 : 1;
}

// --- main -------------------------------------------------------------------

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) process.exit(selfTest());
  const noFetch = argv.includes('--no-fetch');

  // 判断 4: 「検査していない」と「乖離なし」を読み分ける。
  if (!planningPopulated()) {
    console.log(
      '[check-planning-pin-freshness] planning サブモジュールが未 populate のため検査していません' +
        '（PR CI は submodule を取得しない）。乖離が無いことを意味しません。',
    );
    process.exit(0);
  }
  const pin = pinnedCommit();
  const head = remoteHead(REPO_ROOT, { fetch: !noFetch });
  if (!pin || !head) {
    console.log(
      `[check-planning-pin-freshness] 比較対象を取得できないため検査していません（pin=${pin ?? '不明'} / HEAD=${head ?? '不明'}）。`,
    );
    process.exit(0);
  }
  if (pin === head) {
    console.log(`[check-planning-pin-freshness] OK: pin は planning の既定ブランチ HEAD と一致しています（${pin.slice(0, 7)}）。`);
    process.exit(0);
  }

  const { files, adrPairs } = collect(REPO_ROOT, pin, head);
  // 0 件走査の門: pin != head なのに差分が 1 件も無いのは、配管が壊れている合図である
  // （#664 / IADR-0130 の作法。ここは fail-open の例外とし、黙って緑を返さない）。
  if (files.length === 0) {
    console.error(
      `[check-planning-pin-freshness] pin (${pin.slice(0, 7)}) と HEAD (${head.slice(0, 7)}) が異なるのに差分が 0 件でした。` +
        ' 比較の配管が壊れている可能性があります。',
    );
    process.exit(1);
  }

  const result = findIssues({ pin, head, files, adrPairs });
  const lines = [
    `計画 pin が ${pin.slice(0, 7)} のままで、planning の既定ブランチは ${head.slice(0, 7)} です。`,
  ];
  if (result.reasons.length === 0) {
    console.log(
      `[check-planning-pin-freshness] pin は古いですが、着手可否に効く変更はありません` +
        `（${files.length} 件の差分はすべて draft / tools / 索引）。`,
    );
    process.exit(0);
  }
  for (const r of result.reasons) {
    lines.push(`  [${r.kind}] ${r.file} — ${r.detail}`);
  }
  const unblocked = result.reasons.filter((r) => r.kind === 'adr-unblocked');
  if (unblocked.length) {
    lines.push(`  ★ ${unblocked.length} 件の ADR が Proposed → Accepted になっています（IADR-0119 の着手条件）。`);
  }
  lines.push('  pin を進めるか判断してください（本検査は落としません）。');
  const message = lines.join('\n');
  // 落とさない。ただし GitHub Actions では注釈として出す（緑のログに埋もれさせない）。
  // `warn` が Actions / ローカルの出し分けを持つので、ここで二重に出さない。
  warn(message, { prefix: '[check-planning-pin-freshness] ' });
  // ★ 呼び出し側（ワークフロー）が issue を起票できるよう、機械可読な出力も残す。
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `drifted=true\nreasons=${result.reasons.length}\n`);
  }
  // ★ issue の本文に使う素のテキスト。**stdout を tee で拾わない** ——
  //   Actions 上の stdout は `::warning::…`（注釈の書式）であり、そのまま issue へ貼ると読めない。
  //   1 回の実行で「注釈」と「素のテキスト」を別々の出口へ出す（2 回走らせて結果がずれるのを避ける）。
  if (process.env.PIN_REPORT_PATH) {
    fs.writeFileSync(process.env.PIN_REPORT_PATH, `${message}\n`);
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  classifyChanges,
  statusOf,
  adrStatusChanges,
  findIssues,
  planningPopulated,
  pinnedCommit,
  remoteHead,
  collect,
  ADR_RE,
  GATE_DOC_RE,
};
