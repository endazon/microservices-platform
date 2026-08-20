#!/usr/bin/env node
'use strict';
/*
 * check-doc-type-vocabulary.js — #675 / NFR / IADR-0166 / IADR-0167
 *
 * `docs/` 配下の文書 frontmatter の `type` が、**テンプレートが書く値**に収まっていることを検査する。
 *
 * ★ 値域はハードコードしない。**`docs/templates/*.md` の `type:` を実行時に読んで組み立てる。**
 *   正本がテンプレートである（IADR-0167 決定 1）以上、**値域をここへ写すと二重定義になり片方が古くなる。**
 *   テンプレートに種別を足せば、その値は自動で許される。
 *
 * ★ 起点（#675）: #675 は「正本が三つ巴（テンプレの `spec` / 種別表の `work` / 実データの `work-spec`）」
 *   と書いたが**誤りだった**。`.claude/commands/new-spec.md` の種別表は**コマンドの引数の語彙**であり、
 *   `type` の値ではない（引数 `work` → `spec_template.md` → `type: spec`）。**二層を 1 軸に潰していた。**
 *
 * ★ 本当の欠陥は「別の種別が同じ `type` になる」ことだった:
 *     operations / runbook → どちらも operations-spec
 *     work       / how-to  → どちらも spec
 *   これは **#667 の `EXEMPT_TYPES`（`runbook` / `how-to` を `status` 検査から外す）が、
 *   テンプレートが書かない手書きの値に依存していた**ことを意味する。
 *   `/new-spec runbook` で作れば `type: operations-spec` になり、除外が効かない。
 *   → `runbook_template.md` / `how_to_template.md` を新設して塞いだ（IADR-0167 決定 2）。
 *
 * ★★ 再発防止の軸を 1 度取り違えた。**最初は「2 枚のテンプレートが同じ `type` を書いていないか」**を
 *   検査したが、**それは今回の欠陥ではない。** 欠陥は **「1 枚のテンプレートを 2 つの種別が共用している」**
 *   形であり、是正前の状態を組み立てて当ててみたところ**衝突 0 件で素通りした**（実測）。
 *   **直したはずのものを捕まえない検査器を書いていた。**
 *   → **`.claude/commands/new-spec.md` の対応表を読み、種別 → テンプレート → `type` を解決して、
 *      2 つの種別が同じ `type` に落ちないことを検査する**形へ書き直した。
 *
 * ★ 据え置き（ラチェット）: `docs/tech` の `tech` / `tech-note` / `tech-architecture` と
 *   `docs/superpowers` の `design`。**種別の設計そのものを決める必要があり、`type` の語彙の問題ではない**
 *   （IADR-0167 決定 5）。**件数を固定し、増えたら落ちる。**
 *
 * 実行: node scripts/check-doc-type-vocabulary.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const DOCS = path.join(REPO, 'docs');
const TEMPLATES = path.join(DOCS, 'templates');
// ADR-0048 決定 1: IADR・作業仕様書・superpowers は `.ai-context/` へ移設された。
// `docs/` と両方を走査しないと、移設後は IADR / 作業仕様書 / superpowers の type が検査されなくなる。
const AI_CONTEXT = path.join(REPO, '.ai-context');

// frontmatter の切り出しは #667 の検査器と共有する（同じ解析器であることを明示的に担保する）。
const { frontMatter } = require('./check-doc-status-vocabulary.js');

/** 据え置き（ラチェット）。**増えたら落ちる。**（IADR-0167 決定 5） */
const BASELINE = {
  // docs/tech は「リポ単位（原則 1 つ）」と定めながら実体は 5 件あり、
  // 技術要件書・アーキテクチャ・区分表・実装ガイド・PoC 記録が同居している。
  // tech-requirements へ一律に寄せると、内容と名前が食い違う。
  tech: 2,
  'tech-note': 1,
  'tech-architecture': 1,
  // .ai-context/superpowers（旧 docs/superpowers）は #59 / #72 由来の旧構造で、docs/README.md の種別表に無い。
  design: 1,
};

function field(fm, key) {
  const m = new RegExp(`^${key}:\\s*(.+?)\\s*$`, 'm').exec(fm);
  return m ? m[1] : null;
}

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

/** テンプレートから値域を組み立てる。`templates` は { name, text } の配列。 */
function buildVocabulary(templates) {
  const typeOfTemplate = new Map();
  for (const { name, text } of templates) {
    const fm = frontMatter(text);
    if (fm === null) continue;
    const type = field(fm, 'type');
    if (type === null) continue;
    typeOfTemplate.set(name, type);
  }
  return { allowed: new Set(typeOfTemplate.values()), typeOfTemplate };
}

/**
 * `.claude/commands/new-spec.md` の対応表から「種別（引数） → テンプレート」を読む。
 * 表は `| \`work\` | 作業仕様書 | \`spec_template.md\` | \`docs/specs/\` | … |` の形。
 */
function parseKindTable(commandText) {
  return [...commandText.matchAll(/^\|\s*`([a-z][a-z-]*)`\s*\|[^|]*\|\s*`([a-z_]+\.md)`\s*\|/gm)].map(
    (m) => ({ kind: m[1], template: m[2] }),
  );
}

/**
 * **種別 → テンプレート → `type` を解決し、2 つの種別が同じ `type` に落ちないことを見る。**
 *
 * ★ これが #675 の欠陥そのものである。是正前は
 *     operations / runbook → operations_spec_template.md → operations-spec
 *     work       / how-to  → spec_template.md            → spec
 *   となっており、**`type` から種別を決められなかった。**
 *   「2 枚のテンプレが同じ type を書く」形では**この欠陥は現れない**（1 枚を共用しているため）。
 */
function kindCollisions(kinds, typeOfTemplate) {
  const byType = new Map();
  for (const { kind, template } of kinds) {
    const type = typeOfTemplate.get(template);
    if (type === undefined) continue;
    if (!byType.has(type)) byType.set(type, []);
    byType.get(type).push(kind);
  }
  return [...byType.entries()]
    .filter(([, ks]) => ks.length > 1)
    .map(([type, ks]) => ({ type, kinds: ks.slice().sort() }));
}

/** 検査本体（純関数）。`docs` は { relPath, text } の配列。 */
function findIssues(docs, allowed) {
  const violations = [];
  const counts = {};
  let scanned = 0;
  let noType = 0;

  for (const { relPath, text } of docs) {
    if (relPath.startsWith('docs/templates/')) continue;
    const fm = frontMatter(text);
    if (fm === null) {
      noType++;
      continue;
    }
    const type = field(fm, 'type');
    if (type === null) {
      noType++;
      continue;
    }
    scanned++;
    if (allowed.has(type)) continue;
    if (Object.prototype.hasOwnProperty.call(BASELINE, type)) {
      counts[type] = (counts[type] ?? 0) + 1;
      continue;
    }
    violations.push({ file: relPath, type });
  }
  return { violations, counts, scanned, noType };
}

/** 据え置きが増えていないか（減るのは可）。 */
function ratchetViolations(counts) {
  const out = [];
  for (const [key, limit] of Object.entries(BASELINE)) {
    const n = counts[key] ?? 0;
    if (n > limit) {
      out.push(
        `据え置き type "${key}" が ${limit} 件 → ${n} 件へ増えた。` +
          '据え置きは減らすためのものであり、新規に足してはいけない',
      );
    }
  }
  return out;
}

function selfTest() {
  const assert = require('assert');
  const t0 = (name, fm) => ({ name, text: `---\n${fm}\n---\n` });
  const d = (relPath, fm) => ({ relPath, text: `---\n${fm}\n---\n\n本文\n` });
  let passed = 0;
  const t = (name, fn) => {
    fn();
    passed++;
    process.stdout.write(`  ok  ${name}\n`);
  };

  const TPL = [t0('spec_template.md', 'type: spec'), t0('runbook_template.md', 'type: runbook')];

  t('値域はテンプレートから組み立てる（ハードコードしない）', () => {
    const v = buildVocabulary(TPL);
    assert.deepStrictEqual([...v.allowed].sort(), ['runbook', 'spec']);
  });

  t('種別表を読める（引数 → テンプレートの対応）', () => {
    const table = '| `work` | 作業仕様書 | `spec_template.md` | `docs/specs/` | 単位 |\n' +
      '| `how-to` | 手順ガイド | `how_to_template.md` | `docs/how-to/` | 単位 |\n';
    assert.deepStrictEqual(parseKindTable(table), [
      { kind: 'work', template: 'spec_template.md' },
      { kind: 'how-to', template: 'how_to_template.md' },
    ]);
  });

  t('★★ 1 枚のテンプレートを 2 種別が共用していたら衝突として検出する（#675 の欠陥そのもの）', () => {
    // 是正前の形: work と how-to がどちらも spec_template.md を使い、type が spec で衝突していた。
    const kinds = [
      { kind: 'work', template: 'spec_template.md' },
      { kind: 'how-to', template: 'spec_template.md' },
    ];
    const c = kindCollisions(kinds, buildVocabulary(TPL).typeOfTemplate);
    assert.strictEqual(c.length, 1, JSON.stringify(c));
    assert.strictEqual(c[0].type, 'spec');
    assert.deepStrictEqual(c[0].kinds, ['how-to', 'work']);
  });

  t('★★ 別テンプレートへ分ければ衝突しない（是正後の形）', () => {
    const kinds = [
      { kind: 'work', template: 'spec_template.md' },
      { kind: 'runbook', template: 'runbook_template.md' },
    ];
    assert.deepStrictEqual(kindCollisions(kinds, buildVocabulary(TPL).typeOfTemplate), []);
  });

  t('種別表に無いテンプレートは衝突判定に混ぜない', () =>
    assert.deepStrictEqual(
      kindCollisions([{ kind: 'x', template: '存在しない.md' }], buildVocabulary(TPL).typeOfTemplate),
      [],
    ));

  t('値域内なら違反 0 件', () => {
    const r = findIssues([d('docs/specs/a.md', 'type: spec')], buildVocabulary(TPL).allowed);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.scanned, 1);
  });

  t('テンプレートが書かない type を検出する（変異試験）', () => {
    const r = findIssues([d('docs/specs/a.md', 'type: work-spec')], buildVocabulary(TPL).allowed);
    assert.strictEqual(r.violations.length, 1, JSON.stringify(r.violations));
    assert.strictEqual(r.violations[0].type, 'work-spec');
  });

  t('templates は対象外', () => {
    const r = findIssues([d('docs/templates/x.md', 'type: 何でも')], buildVocabulary(TPL).allowed);
    assert.strictEqual(r.scanned, 0);
  });

  t('type を持たない文書は数えるだけで違反にしない', () => {
    const r = findIssues([d('docs/specs/a.md', 'status: done')], buildVocabulary(TPL).allowed);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.noType, 1);
  });

  t('★ 本文中の type: を frontmatter と取り違えない（#667 起票文と同じ型の誤り）', () => {
    const text = '---\ntype: spec\n---\n\n```\ntype: work-spec\n```\n';
    const r = findIssues([{ relPath: 'docs/specs/a.md', text }], buildVocabulary(TPL).allowed);
    assert.deepStrictEqual(r.violations, [], 'コードブロック中の type を拾っている');
  });

  t('据え置きは違反にせず数える', () => {
    const r = findIssues([d('docs/tech/a.md', 'type: tech')], buildVocabulary(TPL).allowed);
    assert.deepStrictEqual(r.violations, []);
    assert.strictEqual(r.counts.tech, 1);
  });

  t('据え置きが上限を超えると落ちる（ラチェット・変異試験）', () => {
    assert.deepStrictEqual(ratchetViolations({ ...BASELINE }), []);
    for (const k of Object.keys(BASELINE)) {
      const over = ratchetViolations({ ...BASELINE, [k]: BASELINE[k] + 1 });
      assert.strictEqual(over.length, 1, `据え置き "${k}" の超過を検出できない`);
    }
  });

  t('据え置きは減ってよい', () => assert.deepStrictEqual(ratchetViolations({}), []));

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

function main(argv) {
  if (argv.includes('--self-test')) {
    selfTest();
    return 0;
  }

  const templates = walk(TEMPLATES).map((abs) => ({
    name: path.basename(abs),
    text: fs.readFileSync(abs, 'utf8'),
  }));
  const { allowed, typeOfTemplate } = buildVocabulary(templates);

  // 種別表（/new-spec の引数 → テンプレート）を読み、2 種別が同じ type に落ちないことを見る。
  const commandPath = path.join(REPO, '.claude/commands/new-spec.md');
  let kinds = [];
  try {
    kinds = parseKindTable(fs.readFileSync(commandPath, 'utf8'));
  } catch {
    console.error('[check-doc-type-vocabulary] .claude/commands/new-spec.md を読めませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }
  // 門 C: 対応表を 1 行も読めない（表の書式が変わった等）。
  if (kinds.length === 0) {
    console.error('[check-doc-type-vocabulary] new-spec.md の種別表から 1 行も読めませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }
  const collisions = kindCollisions(kinds, typeOfTemplate);

  // 門 A: テンプレートから値域を作れない（テンプレが読めない・type を持たない）。
  // これを通すと「値域が空 ＝ 全部違反」ではなく「allowed が空でも scanned が 0 なら緑」に化ける。
  if (allowed.size === 0) {
    console.error('[check-doc-type-vocabulary] docs/templates から type を 1 件も読めませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const docs = [...walk(DOCS), ...walk(AI_CONTEXT)].map((abs) => ({
    relPath: path.relative(REPO, abs).split(path.sep).join('/'),
    text: fs.readFileSync(abs, 'utf8'),
  }));
  const { violations, counts, scanned, noType } = findIssues(docs, allowed);

  // 門 B: 走査 0 件（IADR-0130 / #664）。
  if (scanned === 0) {
    console.error('[check-doc-type-vocabulary] type を持つ文書を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const ratchet = ratchetViolations(counts);
  const collisionMsgs = collisions.map(
    (c) =>
      `種別 ${c.kinds.map((k) => `\`${k}\``).join(' と ')} がどちらも type "${c.type}" に落ちている。` +
      'type から種別を決められないため、片方に専用テンプレートを用意すること（IADR-0167 決定 2）',
  );

  if (violations.length === 0 && ratchet.length === 0 && collisionMsgs.length === 0) {
    console.log(
      `[check-doc-type-vocabulary] OK: ${scanned} 件の文書の type が、` +
        `テンプレート ${allowed.size} 種類の値域に収まっています` +
        `（type 無し ${noType} 件は検査していません）。種別 ${kinds.length} 個の衝突なし。` +
        `据え置き: ${Object.entries(BASELINE)
          .map(([k]) => `${k} ${counts[k] ?? 0}`)
          .join(' / ')}。`,
    );
    return 0;
  }
  for (const m of collisionMsgs) console.error(`[check-doc-type-vocabulary] ${m}`);
  if (violations.length > 0) {
    console.error(`[check-doc-type-vocabulary] 値域外の type ${violations.length} 件:`);
    for (const v of violations) console.error(`  - ${v.file}: type: ${v.type}`);
    console.error('  値域は docs/templates/*.md が書く type です（IADR-0167 決定 1）。');
  }
  for (const r of ratchet) console.error(`[check-doc-type-vocabulary] ${r}`);
  return 1;
}

module.exports = { buildVocabulary, parseKindTable, kindCollisions, findIssues, ratchetViolations, BASELINE };

if (require.main === module) process.exit(main(process.argv.slice(2)));
