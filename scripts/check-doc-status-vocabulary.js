#!/usr/bin/env node
'use strict';
/*
 * check-doc-status-vocabulary.js — #667 / NFR / IADR-0130
 *
 * `docs/` 配下の仕様書 frontmatter の `status` が、規約が定めた値域に収まっていることを検査する。
 *
 * 値域の正本は **`docs/README.md` の運用ルール 6**（本ファイルではない）:
 *   draft / in-progress / completed / done / superseded
 * ADR（`docs/adr/`）は別系統で `docs/templates/adr_template.md` の状態欄が定める:
 *   Proposed / Accepted / Deprecated / Superseded
 *
 * ★ 起点（#667）: 規約は最初から存在したが、**誰も追随していなかった**。
 *   実測で `fixed` 20 / `review` 8 / `implemented` 3 / `active` 3 / `published` 2 / `approved` 1 の
 *   計 37 件が語彙外であり、**そのうち 16 件は計画リポの語彙（`fixed`）を実装リポへ持ち込んだもの**
 *   だった。**規約を書き足すのではなく、既にある規約を機械で閉じる。**
 *
 * ★ 対象外（`docs/README.md` 運用ルール 6 の追記と同じ判断）:
 *   - **種別表に無い `type`**（`tech-note` / `design` 等）
 *   - **`how-to` / `how-to-guide` / `runbook`** —— 仕様ではなく作業手順の案内であり、
 *     「その仕様書が記述する実装の状態」という定義が当てはまらない
 *   - `docs/templates/` —— 雛形。`draft` / `Proposed` を書くのが正しい
 *   **除外は黙って飛ばさず件数をログに出す**（「検査しているつもりで何も見ていない」を作らない）。
 *
 * ★ 据え置き（ラチェット）:
 *   - `review` …… 2026-07 の作業仕様書 8 件。**状態の進行を推測しないため触らない**（#667 判断 2）
 *   - `docs/specs/` の `completed` …… 43 件。README 6 は `docs/specs/` を `done` と名指しているが、
 *     **`completed` も値域内の語**であり、書き換えは語彙の是正ではなく好みの統一になる
 *   いずれも **件数を固定し、増えたら落ちる**（減るのは可）。
 *
 * 実行: node scripts/check-doc-status-vocabulary.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const DOCS = path.join(REPO, 'docs');

/** 仕様書の値域（正本は docs/README.md 運用ルール 6）。 */
const SPEC_STATUSES = new Set(['draft', 'in-progress', 'completed', 'done', 'superseded']);
/**
 * ADR の値域（正本は docs/templates/adr_template.md:18 の `<!-- Proposed / Accepted / Deprecated / Superseded -->`）。
 * ★ **本リポに `.claude/rules/adr.md` は無い。** 同名の規約は計画リポ側にあり、計画 ADR にしか適用されない
 *   （最初そう書いて check-doc-links が破損リンクとして落とした。**存在しない正本を指していた**）。
 */
const ADR_STATUSES = new Set(['Proposed', 'Accepted', 'Deprecated', 'Superseded']);

/** 値域の検査から外す `type`（docs/README.md の種別表に無い、または仕様ではない）。 */
const EXEMPT_TYPES = new Set(['how-to', 'how-to-guide', 'runbook', 'tech-note', 'design']);

/** 据え置き（ラチェット）。**増えたら落ちる。** */
const BASELINE = {
  // #667 判断 2: 状態の進行を推測しない。
  review: 8,
  // README 6 は docs/specs を `done` と名指すが `completed` も値域内。好みの統一はしない。
  'specs-completed': 43,
};

/** ディレクトリを再帰的に走査して .md を集める。 */
function walk(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith('.md')) out.push(p);
  }
  return out;
}

/**
 * frontmatter を切り出す。**`---` で始まらない文書は frontmatter を持たない**（README.md 等）。
 * 行フィルタで `status:` を拾うと**本文やコードブロック中の `status:` まで数える** ——
 * #667 の起票文がまさにその誤りだった（246 行 対 243 ファイル）。**必ず frontmatter に限る。**
 */
function frontMatter(text) {
  if (!text.startsWith('---')) return null;
  const end = text.indexOf('\n---', 3);
  return end < 0 ? null : text.slice(3, end);
}

function field(fm, key) {
  const m = new RegExp(`^${key}:\\s*(.+?)\\s*$`, 'm').exec(fm);
  return m ? m[1] : null;
}

/**
 * 検査本体（純関数）。`docs` は { relPath, text } の配列。
 * 返り値の `scanned` は **frontmatter を持つ文書の数**であり、0 件なら門が発火する。
 */
function findIssues(docs) {
  const violations = [];
  const counts = { review: 0, 'specs-completed': 0 };
  let scanned = 0;
  let exempt = 0;
  let noFrontMatter = 0;

  for (const { relPath, text } of docs) {
    if (relPath.startsWith('docs/templates/')) continue;
    const fm = frontMatter(text);
    if (fm === null) {
      noFrontMatter++;
      continue;
    }
    const status = field(fm, 'status');
    if (status === null) {
      noFrontMatter++;
      continue;
    }
    const type = field(fm, 'type');
    if (type !== null && EXEMPT_TYPES.has(type)) {
      exempt++;
      continue;
    }
    scanned++;

    const isAdr = relPath.startsWith('docs/adr/');
    const allowed = isAdr ? ADR_STATUSES : SPEC_STATUSES;
    if (allowed.has(status)) {
      if (!isAdr && status === 'completed' && relPath.startsWith('docs/specs/')) {
        counts['specs-completed']++;
      }
      continue;
    }
    // 据え置きの語は違反にせず数える（件数のラチェットで見る）。
    if (!isAdr && Object.prototype.hasOwnProperty.call(BASELINE, status)) {
      counts[status]++;
      continue;
    }
    violations.push({
      file: relPath,
      status,
      type,
      allowed: isAdr ? 'ADR 語彙' : '仕様書の値域',
    });
  }
  return { violations, counts, scanned, exempt, noFrontMatter };
}

/** 据え置きが増えていないか（減るのは可）。 */
function ratchetViolations(counts) {
  const out = [];
  for (const [key, limit] of Object.entries(BASELINE)) {
    const n = counts[key] ?? 0;
    if (n > limit) {
      out.push(
        `据え置き "${key}" が ${limit} 件 → ${n} 件へ増えた。` +
          '据え置きは減らすためのものであり、新規に足してはいけない',
      );
    }
  }
  return out;
}

function selfTest() {
  const assert = require('assert');
  const d = (relPath, fm) => ({ relPath, text: `---\n${fm}\n---\n\n本文\n` });
  let passed = 0;
  const t = (name, fn) => {
    fn();
    passed++;
    process.stdout.write(`  ok  ${name}\n`);
  };

  t('値域内なら違反 0 件', () => {
    const r = findIssues([
      d('docs/specs/a.md', 'type: work-spec\nstatus: done'),
      d('docs/functional/b.md', 'type: functional-spec\nstatus: completed'),
      d('docs/adr/IADR-0001_x.md', 'type: impl-adr\nstatus: Accepted'),
    ]);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.scanned, 3);
  });

  t('値域外の語を検出する（変異試験）', () => {
    const r = findIssues([d('docs/specs/a.md', 'type: work-spec\nstatus: fixed')]);
    assert.strictEqual(r.violations.length, 1, JSON.stringify(r.violations));
    assert.strictEqual(r.violations[0].status, 'fixed');
  });

  t('ADR に仕様書の語を書くと検出する（語彙の取り違え・変異試験）', () => {
    const r = findIssues([d('docs/adr/IADR-0001_x.md', 'type: impl-adr\nstatus: done')]);
    assert.strictEqual(r.violations.length, 1, JSON.stringify(r.violations));
  });

  t('仕様書に ADR の語を書くと検出する（逆向き・変異試験）', () => {
    const r = findIssues([d('docs/specs/a.md', 'type: work-spec\nstatus: Accepted')]);
    assert.strictEqual(r.violations.length, 1, JSON.stringify(r.violations));
  });

  t('大小文字だけ違う語を素通ししない（superseded と Superseded を混ぜない）', () => {
    // 仕様書側は小文字 superseded、ADR 側は大文字 Superseded。取り違えは違反である。
    assert.strictEqual(
      findIssues([d('docs/specs/a.md', 'type: work-spec\nstatus: Superseded')]).violations.length,
      1,
    );
    assert.strictEqual(
      findIssues([d('docs/adr/IADR-1_x.md', 'type: impl-adr\nstatus: superseded')]).violations.length,
      1,
    );
  });

  t('対象外の type は検査せず、件数を数える', () => {
    const r = findIssues([
      d('docs/how-to/x.md', 'type: how-to-guide\nstatus: published'),
      d('docs/operations/y.md', 'type: runbook\nstatus: active'),
      d('docs/tech/z.md', 'type: tech-note\nstatus: fixed'),
    ]);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.exempt, 3);
    assert.strictEqual(r.scanned, 0);
  });

  t('templates は対象外', () => {
    const r = findIssues([d('docs/templates/spec_template.md', 'type: spec\nstatus: draft')]);
    assert.strictEqual(r.scanned, 0);
  });

  t('frontmatter を持たない文書は数えるだけで違反にしない', () => {
    const r = findIssues([{ relPath: 'docs/README.md', text: '# 見出し\n\nstatus: fixed\n' }]);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.noFrontMatter, 1);
    assert.strictEqual(r.scanned, 0);
  });

  t('★ 本文中の status: を frontmatter と取り違えない（#667 起票文の誤り）', () => {
    const text = '---\ntype: work-spec\nstatus: done\n---\n\n```\nstatus: fixed\n```\n';
    const r = findIssues([{ relPath: 'docs/specs/a.md', text }]);
    assert.deepStrictEqual(r.violations, [], 'コードブロック中の status を拾っている');
  });

  t('据え置きは違反にせず数える', () => {
    const r = findIssues([d('docs/specs/a.md', 'type: work-spec\nstatus: review')]);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.counts.review, 1);
  });

  t('据え置きが上限を超えると落ちる（ラチェット・変異試験）', () => {
    assert.deepStrictEqual(ratchetViolations({ review: BASELINE.review }), []);
    const over = ratchetViolations({ review: BASELINE.review + 1 });
    assert.strictEqual(over.length, 1, JSON.stringify(over));
    assert.match(over[0], /増えた/);
  });

  t('据え置きは減ってよい', () =>
    assert.deepStrictEqual(ratchetViolations({ review: 0, 'specs-completed': 0 }), []));

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

function main(argv) {
  if (argv.includes('--self-test')) {
    selfTest();
    return 0;
  }

  const docs = walk(DOCS).map((abs) => ({
    relPath: path.relative(REPO, abs).split(path.sep).join('/'),
    text: fs.readFileSync(abs, 'utf8'),
  }));

  const { violations, counts, scanned, exempt, noFrontMatter } = findIssues(docs);

  // #664 / IADR-0130: 0 件走査で緑を返さない。
  if (scanned === 0) {
    console.error('[check-doc-status-vocabulary] status を持つ仕様書を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const ratchet = ratchetViolations(counts);
  if (violations.length === 0 && ratchet.length === 0) {
    console.log(
      `[check-doc-status-vocabulary] OK: ${scanned} 件の仕様書の status が値域に収まっています` +
        `（対象外の種別 ${exempt} 件 / frontmatter 無し ${noFrontMatter} 件は検査していません）。` +
        `据え置き: review ${counts.review} / docs/specs の completed ${counts['specs-completed']}。`,
    );
    return 0;
  }
  if (violations.length > 0) {
    console.error(`[check-doc-status-vocabulary] 値域外の status ${violations.length} 件:`);
    for (const v of violations) {
      console.error(`  - ${v.file}: status: ${v.status}（type: ${v.type ?? '無し'}）は${v.allowed}に無い`);
    }
    console.error('  値域は docs/README.md 運用ルール 6（仕様書）/ docs/templates/adr_template.md（IADR）が定めます。');
  }
  for (const r of ratchet) console.error(`[check-doc-status-vocabulary] ${r}`);
  return 1;
}

module.exports = { findIssues, ratchetViolations, frontMatter, SPEC_STATUSES, ADR_STATUSES, BASELINE };

if (require.main === module) process.exit(main(process.argv.slice(2)));
