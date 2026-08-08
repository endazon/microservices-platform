#!/usr/bin/env node
'use strict';
/*
 * check-landed-subjects.js
 * NFR / issue #579: **統合ブランチへ実際に載ったスカッシュ着地件名**を規約へ照合する。
 *
 * ## なぜ 3 本目の検査が要るのか（#579 の実測）
 *
 * 規約が見ている文字列は 2 つあるが、**develop へ載る文字列はそのどちらでもない**ことがある。
 *
 *   | 検査                        | 見ているもの        |
 *   | --------------------------- | ------------------- |
 *   | `pr-title.yml`              | **PR タイトル**     |
 *   | `ci.yml` の `commit-messages` | **`base..HEAD`**  |
 *   | —                           | **実際に載る件名**  ← 誰も見ていなかった |
 *
 * `.claude/rules/traceability.md` は「PR タイトル ＝ スカッシュ後件名」を前提にしているが、
 * **マージ時に件名を書き直せる**のでその前提は破れる。実際 PR #568 で破れ、
 * `feat(FR-12,SC-07)` → `feat(FR-12)` と **`SC-07` が落ちた状態で恒久履歴へ載った**
 * （force push 禁止のため事後修正できない）。
 *
 * ## この検査が「できないこと」を先に言う（IADR-0145 決定 3）
 *
 * **#568 で実際に起きた事故（PR タイトルにあった ID がマージ時に落ちる）は、本検査では検出できない。**
 * 落ちた後の件名 `feat(FR-12): ...` は**それ自体は完全に規約適合**であり、
 * 「落ちた」と判定するには PR タイトルとの突合が要る。**PR タイトルはリポジトリの中に無い。**
 * したがって本検査が見るのは「載った件名それ自体が規約に反していないか」だけである。
 *
 * 検出できるのは次の 2 型で、どちらも**恒久履歴に入ってから**気づく事後検知である:
 *   1. 書式違反（`Claude/issue 71 ...` のようなブランチ名由来の既定件名がそのまま載る型）
 *   2. 実在しない起点 ID（`feat(SC-99)`。#579 で新設した実在性検査を着地面へも当てる）
 *
 * ## ラチェット（既存履歴で無関係な PR を赤にしない）
 *
 * 規約導入前の件名が既に恒久履歴に在る。これを毎 PR で赤にしても直せない（force push 禁止）ので、
 * `scripts/landed-subject-baseline.json` へ**現に違反している着地コミット**を控え、
 * **新規混入だけを落とす**。baseline に在るのに違反しなくなった項目も fail させ、
 * **baseline が必ず縮む向き**にする（`backend-library-baseline.json` と同じ作法）。
 *
 * ## 浅いクローンの扱い（fail-open だが黙らない）
 *
 * 着地件名は履歴を遡らないと見えない。浅いクローンでは母集合そのものが取れず、
 * 「違反 0 件」が**検査していないことの言い換え**になる。そこで走査の前に前提を見る
 * （`scanPrecondition`）。`ci.yml` の `scripts-tests` は `fetch-depth: 0` なので実効する。
 *
 * **「ハッシュが解決できない」を 2 つに割るのが要点である**（当初これを一緒にして穴を作り、
 * 変異試験で踏んだ ——baseline へ `0000000` を 1 行足すだけで検査全体が exit 0 の緑になった）:
 *   - **浅いクローン**（`rev-parse --is-shallow-repository` = true） → **skip**（notice で可視化）
 *   - **履歴は完全なのに解決できない** → **baseline の打ち間違い**なので **fail**
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-landed-subjects.js              # HEAD から到達する着地件名を検査
 *   node scripts/check-landed-subjects.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-landed-subjects.js --update     # baseline を実測で置き直す
 *   node scripts/check-landed-subjects.js --ref <ref>  # 走査の起点を変える（既定 HEAD）
 *
 * CI への載せ方（.github/workflows/ は編集不可のため既存の呼び出し口へ相乗りする。IADR-0140 決定 2）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 */
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');
const {
  validateSubject,
  validateIdExistence,
  loadExistingIadrIds,
  loadExistingPlanAdrIds,
  loadExistingPlanIds,
  isBotAuthorName,
  isSkippable,
} = require('./check-commit-messages.js');

const BASELINE_PATH = path.join(__dirname, 'landed-subject-baseline.json');
const US = '\x1f';
const RS = '\x1e';

/** スカッシュ着地件名の判定。GitHub 既定は「PR タイトル + 半角スペース + (#番号)」。 */
const LANDED_RE = /\(#\d+\)\s*$/;

function git(args) {
  try {
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], maxBuffer: 1 << 28 });
  } catch (e) {
    return null;
  }
}

/** ref から到達する着地コミットを {hash, subject, author, email} で返す。取れなければ null。 */
function collectLanded(ref = 'HEAD') {
  const raw = git(`log ${ref} --no-merges --pretty=format:%H${US}%s${US}%an${US}%ae${RS}`);
  if (raw === null) return null;
  return raw
    .split(RS)
    .map((r) => r.replace(/^\n/, '').trim())
    .filter(Boolean)
    .map((line) => {
      const [hash, subject = '', author = '', email = ''] = line.split(US);
      return { hash, subject, author, email };
    })
    .filter((c) => LANDED_RE.test(c.subject));
}

/**
 * 1 件の着地件名を検査し、違反理由の配列を返す（空なら適合）。
 * bot 著者・Revert / [skip ci] は規約対象外（`check-commit-messages.js` と同じ除外）。
 */
function reasonsFor(commit, ids) {
  if (isBotAuthorName(commit.author) || isBotAuthorName(commit.email)) return [];
  if (isSkippable(commit.subject)) return [];
  return validateSubject(commit.subject).concat(
    validateIdExistence(commit.subject, ids.iadrIds, ids.planAdrIds, ids.planIds)
  );
}

function readBaseline(file = BASELINE_PATH) {
  const raw = fs.readFileSync(file, 'utf8');
  const json = JSON.parse(raw);
  if (!Array.isArray(json.known)) throw new Error('baseline に known 配列がない');
  return json;
}

/**
 * 実測と baseline を突き合わせ、{ added, fixed, violations } を返す。
 *   - added:  baseline に無い新しい違反（**fail**。これが本検査の目的）
 *   - fixed:  baseline に在るのに違反しなくなったもの（**fail**。baseline を縮ませる）
 */
function diffAgainstBaseline(violations, baseline) {
  const known = new Set(baseline.known.map((e) => e.hash));
  const seen = new Set();
  const added = [];
  for (const v of violations) {
    const hit = [...known].find((h) => v.hash.startsWith(h) || h.startsWith(v.hash));
    if (hit) seen.add(hit);
    else added.push(v);
  }
  const fixed = [...known].filter((h) => !seen.has(h));
  return { added, fixed, violations };
}

/**
 * 走査可能かを判定する。返り値は `{ ok }` / `{ skip, why }` / `{ broken, missing }` の 3 値。
 *
 * **2 つの「ハッシュが解決できない」を区別する**（当初これを一緒にして穴を作った）:
 *   - **浅いクローン** → 母集合そのものが取れないので **skip**（notice で可視化する）。
 *   - **履歴は完全なのに解決できない** → **baseline のハッシュが誤っている**。ここを skip にすると、
 *     **baseline へ打ち間違いを 1 つ入れるだけで検査全体が黙って無効化される**
 *     （変異試験で実際に踏んだ。`0000000` を足すと exit 0 で緑になった）。**fail させる。**
 */
function scanPrecondition(baseline, ref = 'HEAD') {
  const shallow = git('rev-parse --is-shallow-repository');
  if (shallow !== null && shallow.trim() === 'true') {
    return { skip: true, why: '浅いクローンのため履歴を遡れない' };
  }
  const missing = baseline.known
    .map((e) => e.hash)
    .filter((h) => git(`merge-base --is-ancestor ${h} ${ref} && echo ok`) === null);
  if (missing.length) return { broken: true, missing };
  return { ok: true };
}

function loadIds() {
  return {
    iadrIds: loadExistingIadrIds(),
    planAdrIds: loadExistingPlanAdrIds(),
    planIds: loadExistingPlanIds(),
  };
}

/** 実測して違反の配列を返す。history が取れないときは null。 */
function scan(ref = 'HEAD') {
  const landed = collectLanded(ref);
  if (landed === null) return null;
  const ids = loadIds();
  const out = [];
  for (const c of landed) {
    const reasons = reasonsFor(c, ids);
    if (reasons.length) out.push({ hash: c.hash, subject: c.subject, reasons });
  }
  return { landed, violations: out };
}

// --- 自己試験 -------------------------------------------------------------------
//
// **実データは baseline と一致しているので、検出力は変異でしか示せない。**
// 純関数（reasonsFor / diffAgainstBaseline）へ直接当てる。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const ids = loadIds();
  // 人間の著者（bot 除外に掛からないこと）。**識別子は ASCII にする** ——
  // 当初 `const人` と書いて空白が無く 1 つの識別子として解析され、宣言ごと壊れた。
  const human = { author: 'endazon', email: 'a@example.com' };

  // M1: ブランチ名由来の既定件名がそのまま載る型（#95 で実在した）
  t(
    'M1: ブランチ名由来の既定件名を検出する',
    reasonsFor({ ...human, subject: 'Claude/issue 71 20260705 1545 (#95)' }, ids).length > 0
  );

  // M2: 起点 ID の無い件名
  t(
    'M2: 起点 ID の無い feat 件名を検出する',
    reasonsFor({ ...human, subject: 'feat: 起点 ID が無い (#123)' }, ids).length > 0
  );

  // M3: 実在しない計画 ID（#579 で新設した実在性検査が着地面でも効くこと）
  if (ids.planIds) {
    t(
      'M3: 実在しない画面 ID を検出する（feat(SC-99)）',
      reasonsFor({ ...human, subject: 'feat(SC-99): 存在しない画面 (#123)' }, ids).length > 0
    );
  }

  // M4（負例）: 正当な件名は落ちない
  t(
    'M4 負例: 正当な着地件名は違反 0',
    reasonsFor({ ...human, subject: 'feat(FR-12,SC-07): 正当な件名 (#568)' }, ids).length === 0,
    reasonsFor({ ...human, subject: 'feat(FR-12,SC-07): 正当な件名 (#568)' }, ids)
  );

  // M5（負例）: bot 著者は規約対象外
  t(
    'M5 負例: dependabot の件名は対象外',
    reasonsFor(
      { author: 'dependabot[bot]', email: 'x@y', subject: 'build(deps): bump foo from 1 to 2 (#1)' },
      ids
    ).length === 0
  );

  // M6（負例）: Revert / [skip ci]
  t(
    'M6 負例: Revert は対象外',
    reasonsFor({ ...human, subject: 'Revert "feat: 何か" (#2)' }, ids).length === 0
  );

  // 着地件名の判定そのもの（(#NNN) で終わらないものは母集合に入らない）
  t(
    '母集合: (#NNN) で終わらない件名は着地件名ではない',
    !LANDED_RE.test('feat(FR-01): ローカルコミット') && LANDED_RE.test('feat(FR-01): 着地 (#12)')
  );

  // ラチェット: 新規違反は added、直った baseline 項目は fixed
  {
    const baseline = { known: [{ hash: 'aaaaaaa', subject: '旧', reason: 'テスト' }] };
    const d1 = diffAgainstBaseline([{ hash: 'aaaaaaa1111', subject: '旧', reasons: ['x'] }], baseline);
    t('ラチェット: baseline に在る違反は added にならない', d1.added.length === 0 && d1.fixed.length === 0, d1);

    const d2 = diffAgainstBaseline(
      [
        { hash: 'aaaaaaa1111', subject: '旧', reasons: ['x'] },
        { hash: 'bbbbbbb2222', subject: '新', reasons: ['y'] },
      ],
      baseline
    );
    t('ラチェット: 新規違反は added として落ちる', d2.added.length === 1 && d2.added[0].hash === 'bbbbbbb2222', d2);

    const d3 = diffAgainstBaseline([], baseline);
    t('ラチェット: 直ったのに baseline に残っていれば fixed で落ちる', d3.fixed.length === 1, d3);
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-landed-subjects] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-landed-subjects] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const refIdx = argv.indexOf('--ref');
  const ref = refIdx >= 0 ? argv[refIdx + 1] : 'HEAD';

  let baseline;
  try {
    baseline = readBaseline();
  } catch (e) {
    console.error(`[check-landed-subjects] baseline を読めない: ${e.message}`);
    process.exit(1);
  }

  const result = scan(ref);
  if (result === null) {
    notice(`git log ${ref} を実行できないため着地件名の検査をスキップした（この範囲は検査されていない）`);
    process.exit(0);
  }

  const pre = scanPrecondition(baseline, ref);
  if (pre.skip) {
    // **「0 件だから緑」を作らない。** 浅いクローンでは母集合そのものが取れていない。
    notice(
      `${pre.why}ため着地件名の検査をスキップした。この範囲は検査されていない。` +
        '実効させるには checkout に fetch-depth: 0 を付けること'
    );
    process.exit(0);
  }
  if (pre.broken) {
    // 履歴は完全なのに解決できない＝ baseline のハッシュが誤っている。**skip にしない。**
    console.error(
      `[check-landed-subjects] baseline のハッシュ ${pre.missing.length} 件が履歴に見つからない: ` +
        `${pre.missing.join(', ')}\n` +
        '履歴は浅くない（rev-parse --is-shallow-repository = false）ので、これは打ち間違いか、\n' +
        '別リポジトリの SHA を持ち込んだかである。**放置すると検査全体が黙って無効化される。**\n'
    );
    process.exit(1);
  }

  if (argv.includes('--update')) {
    const next = {
      _note: baseline._note,
      known: result.violations.map((v) => ({
        hash: v.hash.slice(0, 7),
        subject: v.subject,
        reason: v.reasons.join(' / '),
      })),
    };
    fs.writeFileSync(BASELINE_PATH, JSON.stringify(next, null, 2) + '\n');
    console.log(`[check-landed-subjects] baseline を実測で置き直した（${next.known.length} 件）。`);
    return;
  }

  const { added, fixed } = diffAgainstBaseline(result.violations, baseline);
  if (added.length === 0 && fixed.length === 0) {
    console.log(
      `[check-landed-subjects] OK: 着地件名 ${result.landed.length} 件。baseline 外の規約違反はありません。`
    );
    process.exit(0);
  }

  if (added.length) {
    console.error(`[check-landed-subjects] baseline 外の着地件名の違反 ${added.length} 件:`);
    for (const v of added) {
      console.error(`    ${v.hash.slice(0, 7)} ${v.subject}`);
      for (const r of v.reasons) console.error(`        - ${r}`);
    }
    console.error(
      '\n**恒久履歴は force push で直せない。** 規約が定める事後手段は ' +
        '`scripts/changelog-overrides.json` の `action: "remap"` による CHANGELOG 生成時の補正だけである' +
        '（.claude/rules/traceability.md「CHANGELOG 生成時の誤記補正・除外規定」）。\n' +
        '補正したうえで、本 baseline へ当該コミットを追記すること。\n'
    );
  }
  if (fixed.length) {
    console.error(
      `[check-landed-subjects] baseline に在るが現在は違反していない項目 ${fixed.length} 件: ${fixed.join(', ')}`
    );
    console.error('baseline は縮む向きにのみ動かす。該当項目を削ること。\n');
  }
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  LANDED_RE,
  collectLanded,
  reasonsFor,
  readBaseline,
  diffAgainstBaseline,
  scanPrecondition,
  scan,
  selfTest,
  BASELINE_PATH,
};
