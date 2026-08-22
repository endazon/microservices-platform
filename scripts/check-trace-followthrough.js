#!/usr/bin/env node
'use strict';
/*
 * check-trace-followthrough.js
 *
 * NFR / Issue #975: **新規に足した記録（`.ai-context/specs/` / `.ai-context/adr/`）が、
 * 同じ変更で書き換えた `docs/**\/*.md` の trace ブロックへ入っているか**を見る。
 *
 * 背景（#885 → #906 → #975）:
 *   #885 は同じ狙いの検査を設計し、**精度 1/38 ≒ 2.6% で棄却した**（IADR-0235）。
 *   ただし測られたのは **pair-rule**（変更された各文書について記録の有無を見る）であり、
 *   束ね PR で「記録 × 文書」の直積に潰れるのが偽陽性の主因だった。
 *   #906 で **record-rule**（各記録について、変更されたいずれかの文書に在るか）を測ると
 *   数字が桁で違うことが分かり、#975 で精度と再現率の両方を測って裁定した。
 *
 * 実測（有効母集合 = trace ブロック導入 #872 以降の 25 コミット）:
 *
 *                        pair-rule      record-rule
 *     flag 総数              167             21
 *     本物                    17             16
 *     精度                  10.2%          76.2%
 *     再現率                 100%          94.1%
 *
 *   🔴 偽陽性 5 件は**すべて 1 本の束ね PR（#878）**に出ており、
 *      **それ以外の 12 PR では 13 flag すべてが本物（精度 100%）**である。
 *
 * 🔴 **これは warn であってゲートではない**（IADR-0254 決定 2）:
 *   偽陽性のとき人ができるのは「**無関係な ID を trace ブロックへ足す**」ことだけで、
 *   それは**この検査器が守ろうとしているメタデータそのものを汚す**。
 *   自分の目的を壊す失敗様式なので、除外の手段を設計するまで赤にしない。
 *   ただし **warn は無視される**（#984 で実測）ので、記録名と文書名を名指しし、件数を出す。
 *
 * 検出しないこと:
 *   - 記録と文書が意味的に関係あるか（機械には判定できない。読み手が決める）
 *   - **1 つでも載っていれば黙る** —— 「A には載せたが B にも要る」形は見逃す（実測 17 件中 1 件）
 *   - 既存文書への遡及（同一範囲の同居だけを見る）
 *
 * 実行:
 *   node scripts/check-trace-followthrough.js            # 既定の範囲を自動判定
 *   node scripts/check-trace-followthrough.js --range origin/develop..HEAD
 *   node scripts/check-trace-followthrough.js --self-test
 *
 * 終了コード: 0=報告のみ（違反があっても 0） / 1=検査自体が成立しなかった（走査 0 件・git 不能）
 */

const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const { findTraceBlocks, parseTraceBlockBody } = require(path.join(__dirname, 'lib', 'trace-blocks.js'));
// #683 / IADR-0183 クラス A: 本検査は**コミット済みの履歴**（範囲の diff と終端の文書）を読む。
// 未コミットのまま走らせると CI と結果が食い違う（「偽の緑」になる）。
const { MODE, warnIfResultMayDifferFromCi } = require(path.join(__dirname, 'lib', 'worktree-state.js'));

function git(args, opts = {}) {
  return execFileSync('git', ['-C', REPO_ROOT, ...args], {
    encoding: 'utf8',
    maxBuffer: 1 << 28,
    ...opts,
  });
}
function tryGit(args) {
  try {
    return git(args);
  } catch {
    return null;
  }
}
function revExists(ref) {
  return tryGit(['rev-parse', '--verify', '--quiet', ref]) !== null;
}

/**
 * 検査範囲を決める。check-commit-messages.js と同じ順序（同じ運用で同じ範囲を見るため）。
 */
function resolveRange(explicit) {
  if (explicit) return explicit;
  if (process.env.TRACE_FOLLOWTHROUGH_RANGE) return process.env.TRACE_FOLLOWTHROUGH_RANGE;
  const baseRef = process.env.GITHUB_BASE_REF;
  if (baseRef) {
    if (revExists(`origin/${baseRef}`)) return `origin/${baseRef}..HEAD`;
    if (revExists(baseRef)) return `${baseRef}..HEAD`;
  }
  if (revExists('origin/develop')) return 'origin/develop..HEAD';
  if (revExists('develop')) return 'develop..HEAD';
  return null;
}

const RECORD_RE = /^\.ai-context\/(specs|adr)\/(.+)\.md$/;
const DOC_RE = /^docs\/.+\.md$/;

/** 記録ファイルのパスから、trace ブロックへ書かれる ID を作る。 */
function recordIdOf(relPath) {
  const m = RECORD_RE.exec(relPath);
  if (!m) return null;
  const base = m[2].split('/').pop();
  if (base === 'README') return null;
  const iadr = /^(IADR-\d{4})/.exec(base);
  // 実装 ADR は番号で引かれる。作業仕様書はファイル名（拡張子なし）で引かれる。
  return iadr ? iadr[1] : base;
}

/**
 * `git diff --name-status` の出力から、追加された記録と変更された docs を取り出す。
 * **純粋関数にしてある**（git を呼ばずに試験できるようにするため）。
 */
function classifyChanges(nameStatusText) {
  const addedRecords = [];
  const changedDocs = [];
  for (const line of String(nameStatusText).split('\n')) {
    if (!line.trim()) continue;
    const parts = line.split('\t');
    const status = parts[0];
    const p = parts[parts.length - 1];
    if (!p) continue;
    if (status.startsWith('A')) {
      const id = recordIdOf(p);
      if (id) addedRecords.push({ path: p, id });
    }
    if ((status.startsWith('M') || status.startsWith('A')) && DOC_RE.test(p)) changedDocs.push(p);
  }
  return { addedRecords, changedDocs };
}

/** 文書本文から trace ブロックが挙げている ID の集合を作る。lib のパーサを使う（自前の正規表現を持たない）。 */
function traceIdsOf(docText) {
  const ids = new Set();
  const { blocks } = findTraceBlocks(String(docText));
  for (const block of blocks || []) {
    const { entries } = parseTraceBlockBody(block.body);
    for (const e of entries || []) for (const v of e.items || []) ids.add(v);
  }
  return ids;
}

/**
 * record-rule の判定。**純粋関数**。
 * docs: Map<path, Set<id>>
 * 返り値: 追随が見当たらなかった記録の配列
 */
function computeFindings(addedRecords, docs) {
  // 🔴 適用条件を**計算側**に置く。報告側だけに置いていたため、
  //    docs を 1 つも変更していない範囲に対して「どの文書にも無い」と全記録を挙げていた
  //    （main() は早期 return で隠れていたが、inspectRange を直接使うと嘘をつく状態だった）。
  //    「記録の追加と docs の改稿が同居するときだけ見る」は判定の一部であって、表示の都合ではない。
  if (addedRecords.length === 0 || docs.size === 0) return [];
  const findings = [];
  for (const rec of addedRecords) {
    const carriedBy = [];
    for (const [docPath, ids] of docs) if (ids.has(rec.id)) carriedBy.push(docPath);
    if (carriedBy.length === 0) findings.push({ ...rec, docs: [...docs.keys()] });
  }
  return findings;
}

/** 走査が成立しなかった状態を判定する（0 件で静かに緑にしない。#664 の門）。 */
function isRangeUnusable(range) {
  return !range;
}

/**
 * 範囲の終端（`A..B` の B。省略時は HEAD）を返す。
 * 🔴 文書は**終端から読む**。HEAD 固定にすると過去の範囲を検証できず、
 *    検査器が自分の実測を再現できなくなる（#975 の測定と突き合わせられない）。
 */
// 🔴 正規表現を使わない。当初 `/^(.*?)\.\.\.?(.*)$/` と書いたつもりが、
//    エスケープが落ちて `/^(.*?)...?(.*)$/`（＝任意の 3 文字）になり、
//    SHA の先頭 3 文字を区切りと誤認して範囲を壊していた。**自己試験が無かったので気づかなかった。**
//    区切りは `..` だけなので、素直に最後の `..` で切る。
function tipOf(range) {
  const i = String(range).lastIndexOf('..');
  const tip = i < 0 ? String(range).trim() : String(range).slice(i + 2).trim();
  return tip || 'HEAD';
}

function inspectRange(range) {
  const normalized = range.replace('...', '..');
  const nameStatus = git(['diff', '--name-status', normalized]);
  const { addedRecords, changedDocs } = classifyChanges(nameStatus);
  const tip = tipOf(normalized);
  const docs = new Map();
  for (const d of changedDocs) {
    const text = tryGit(['show', `${tip}:${d}`]);
    docs.set(d, traceIdsOf(text === null ? '' : text));
  }
  return { addedRecords, changedDocs, findings: computeFindings(addedRecords, docs) };
}

// ---- 自己試験 --------------------------------------------------------------------

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n++;
    console.log(`  ok  ${name}`);
  };

  ok('記録 ID: 実装 ADR は番号、作業仕様書はファイル名', () => {
    assert.strictEqual(recordIdOf('.ai-context/adr/IADR-0250_foo-bar.md'), 'IADR-0250');
    assert.strictEqual(recordIdOf('.ai-context/specs/20260822_issue-1_x.md'), '20260822_issue-1_x');
    assert.strictEqual(recordIdOf('.ai-context/adr/README.md'), null);
    assert.strictEqual(recordIdOf('docs/tests/TEST_STRATEGY.md'), null);
  });

  ok('分類: 追加された記録と変更された docs だけを拾う', () => {
    const text = [
      'A\t.ai-context/specs/20260822_issue-1_x.md',
      'M\tdocs/tests/A.md',
      'A\tdocs/tests/B.md',
      'D\tdocs/tests/C.md',
      'M\t.ai-context/specs/20260101_old.md',
      'M\tsrc/foo.cs',
    ].join('\n');
    const r = classifyChanges(text);
    assert.deepStrictEqual(r.addedRecords.map((x) => x.id), ['20260822_issue-1_x']);
    assert.deepStrictEqual(r.changedDocs, ['docs/tests/A.md', 'docs/tests/B.md']);
    // 既存記録の**変更**は対象外（新規追加だけを見る）。
    assert.strictEqual(r.addedRecords.length, 1);
  });

  ok('record-rule: どれか 1 つの文書に載っていれば黙る', () => {
    const rec = [{ path: 'p', id: 'IADR-0001' }];
    const docs = new Map([
      ['docs/a.md', new Set(['IADR-0001'])],
      ['docs/b.md', new Set()],
    ]);
    assert.deepStrictEqual(computeFindings(rec, docs), []);
  });

  ok('★ record-rule: どの文書にも無ければ挙げる', () => {
    const rec = [{ path: 'p', id: 'IADR-0001' }];
    const docs = new Map([
      ['docs/a.md', new Set(['IADR-0002'])],
      ['docs/b.md', new Set()],
    ]);
    const f = computeFindings(rec, docs);
    assert.strictEqual(f.length, 1);
    assert.strictEqual(f[0].id, 'IADR-0001');
  });

  ok('★ 束ね PR では pair-rule が直積へ潰れ、record-rule は潰れない（#885 の失敗形の回帰）', () => {
    // 記録 1 × 文書 3。記録は 1 つ目の文書にだけ載っている。
    const rec = [{ path: 'p', id: 'IADR-0228' }];
    const docs = new Map([
      ['docs/a.md', new Set(['IADR-0228'])],
      ['docs/b.md', new Set()],
      ['docs/c.md', new Set()],
    ]);
    // pair-rule なら b/c の 2 件を挙げる。record-rule は 0 件。
    let pair = 0;
    for (const [, ids] of docs) if (!ids.has('IADR-0228')) pair++;
    assert.strictEqual(pair, 2, 'pair-rule は 2 件挙げる（比較用）');
    assert.deepStrictEqual(computeFindings(rec, docs), [], 'record-rule は挙げない');
  });

  ok('trace ブロックの読み取りは lib のパーサを使う（自前の正規表現を持たない）', () => {
    const doc = [
      '---',
      'title: x',
      '---',
      '<!-- trace:',
      'ids: [FR-01]',
      'iadrs: [IADR-0001, IADR-0002]',
      'specs: [20260822_issue-1_x]',
      '-->',
      '',
      '# H1',
    ].join('\n');
    const ids = traceIdsOf(doc);
    assert.ok(ids.has('IADR-0002'));
    assert.ok(ids.has('20260822_issue-1_x'));
    assert.ok(ids.has('FR-01'));
    assert.ok(!ids.has('IADR-0003'));
  });

  ok('trace ブロックが無い文書は「何も持たない」として扱う（例外にしない）', () => {
    assert.strictEqual(traceIdsOf('# H1 だけの文書').size, 0);
    assert.strictEqual(traceIdsOf('').size, 0);
  });

  ok('★ 範囲を決められないときは検査不成立として扱う（0 件で緑にしない）', () => {
    assert.strictEqual(isRangeUnusable(null), true);
    assert.strictEqual(isRangeUnusable('origin/develop..HEAD'), false);
  });

  ok('★ docs を 1 つも変更していない範囲では何も挙げない（適用条件は計算側にある）', () => {
    const rec = [{ path: 'p', id: 'IADR-0001' }];
    // 🔴 実際に踏んだ誤り: docs が空だと「どの文書にも無い」が真になり、全記録を挙げていた。
    assert.deepStrictEqual(computeFindings(rec, new Map()), []);
    // 記録が無ければ当然 0 件。
    assert.deepStrictEqual(computeFindings([], new Map([['docs/a.md', new Set()]])), []);
  });

  ok('★ 範囲の終端を取り違えない（SHA を区切りと誤認しない。実際に踏んだ）', () => {
    assert.strictEqual(tipOf('origin/develop..HEAD'), 'HEAD');
    assert.strictEqual(tipOf('origin/develop...HEAD'), 'HEAD');
    // 🔴 これが壊れていた。SHA には区切りに見える文字が無いのに、
    //    正規表現のエスケープが落ちて先頭 3 文字を区切りと誤認していた。
    const sha = '5c3e99112af3ef19d40560b489bb4a33f808de1e';
    assert.strictEqual(tipOf(`${sha}^..${sha}`), sha);
    assert.strictEqual(tipOf('abc..def'), 'def');
    // 区切りが無いときはその ref 自体を終端とみなす。
    assert.strictEqual(tipOf('HEAD'), 'HEAD');
    assert.strictEqual(tipOf(''), 'HEAD');
  });

  console.log(`\n✓ ${n} 件 OK`);
  return 0;
}

// ---- 実行 ------------------------------------------------------------------------

function main(argv) {
  if (argv.includes('--self-test')) return selfTest();

  warnIfResultMayDifferFromCi('check-trace-followthrough.js', MODE.HEAD, REPO_ROOT);

  const i = argv.indexOf('--range');
  const range = resolveRange(i >= 0 ? argv[i + 1] : null);
  if (isRangeUnusable(range)) {
    console.error(
      '[check-trace-followthrough] 検査範囲を決められませんでした（origin/develop も develop も無い）。' +
        ' --range か TRACE_FOLLOWTHROUGH_RANGE を指定してください。',
    );
    return 1; // 🔴 0 件走査で静かに緑にしない
  }

  let result;
  try {
    result = inspectRange(range);
  } catch (e) {
    console.error(`[check-trace-followthrough] 差分を取得できませんでした（${range}）: ${e.message}`);
    return 1;
  }

  const { addedRecords, changedDocs, findings } = result;
  if (addedRecords.length === 0 || changedDocs.length === 0) {
    console.log(
      `[check-trace-followthrough] 対象なし（範囲 ${range}: 新規記録 ${addedRecords.length} 件 /` +
        ` 変更 docs ${changedDocs.length} 件）。記録の追加と docs の改稿が同居するときだけ見ます。`,
    );
    return 0;
  }

  if (findings.length === 0) {
    console.log(
      `[check-trace-followthrough] OK: 新規記録 ${addedRecords.length} 件はいずれも、` +
        `変更した ${changedDocs.length} 文書のどれかの trace ブロックから引かれています。`,
    );
    return 0;
  }

  // 🔴 warn。赤くしない（決定 2）。ただし名指しする（決定 3）。
  console.log(
    `[check-trace-followthrough] ⚠ 追随が見当たらない記録が ${findings.length} 件あります` +
      `（新規記録 ${addedRecords.length} 件 / 変更 docs ${changedDocs.length} 件）:`,
  );
  for (const f of findings) {
    console.log(`    ${f.id}`);
    console.log(`      記録: ${f.path}`);
    console.log(`      この変更で書き換えた文書: ${f.docs.join(', ')}`);
  }
  console.log('');
  console.log('  改稿の根拠になった記録なら、当該文書の trace ブロック（specs: / iadrs:）へ足してください。');
  console.log('  🔴 無関係なら足さないこと。無関係な ID を入れると trace ブロックの意味が壊れます。');
  console.log('  束ね PR では無関係な組み合わせが増えます（実測: 束ね PR での精度は 37.5%、それ以外は 100%）。');
  console.log('  この検査は報告だけで、CI を赤くしません（IADR-0254 決定 2）。');
  return 0;
}

module.exports = {
  recordIdOf,
  classifyChanges,
  traceIdsOf,
  computeFindings,
  isRangeUnusable,
  resolveRange,
  tipOf,
  inspectRange,
};

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}
