#!/usr/bin/env node
'use strict';
/*
 * check-reading-budget.js
 * 必読規約の総量予算（NFR / #755 / IADR-0200。裁定 planning#364）を検査する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。ai-stock-trading の同名検査器（AST/IADR-0204）を土台にした。
 *
 * 運用標準（計画リポジトリ `project-planning` の `docs/ai-implementation-workflow-guide.md` §8・
 * `CLAUDE.md`「実装作業の進め方」）は
 * 「**毎セッション必読の規約は総量 50KB の予算内に収める**」と定める。
 * 本スクリプトはその**母集合を定義し**、実測して予算と比べる。
 *
 * ■ 🔴 なぜ「エージェントごと」に判定するのか（本検査器の設計の核心）
 *
 *   **「毎セッション必読」は、読む主体によって中身が違う。**
 *   Claude Code は `CLAUDE.md` と `.claude/rules/*.md` を自動で読み込むが `AGENTS.md` は読まない。
 *   逆に `AGENTS.md` を読むツール（Codex / Cursor / Aider）は `.claude/rules/` を読まない。
 *   **1 つのセッションが両方を背負うことはない。**
 *
 *   制約は**セッション 1 本が背負う量**に掛かる。したがって
 *   **集合ごとに予算と比べる。合算しない** —— 合算は**誰も背負わない量**を作る。
 *
 *   **実測: 合算すると 2 回とも誤った**（planning#364。ai-stock-trading 側の実測）。`AGENTS.md` を足した
 *   値を「90.2% / 91.5%」と報告し、**「90% を超えたら着手する」という着手条件を満たしたことにしていた**。
 *   正しくは 82.7% であり、条件は満たされていない。
 *   **母集合を取り違えると、数値が狂うだけでなく「いつ動くか」の判断が狂う。**
 *
 * ■ 母集合の根拠（定義と根拠を同じ場所に置く。裁定 planning#364）
 *
 *   定義を別ファイルへ置くと、**定義と実装がずれても誰も気づかない**。よって根拠を各集合のコメントとして持たせる。
 *   - `.claude/rules/` は**列挙せず走査する**。同ディレクトリの `*.md` は自動適用されるため、
 *     ファイルを 1 つ置いただけで予算が動く（列挙は足したファイルを母集合から落とす）。
 *   - **submodule（`src/ai-stock-trading/`）配下の `CLAUDE.md` は入れない。**
 *     そのディレクトリで作業するときだけ読まれる。計画リポジトリは submodule ではないので
 *     （ADR-0048 決定 2 / IADR-0228）そもそも走査対象に現れない。
 *
 * ■ 予算値（**正本は計画リポ運用ガイド §8**）
 *
 *   **51,200 バイト（50KB）。** 出典: 計画リポジトリ `project-planning` の
 *   `docs/ai-implementation-workflow-guide.md` §8
 *   「毎セッション必読の規約文書に総量予算を設ける（50KB 目安。効率が出ている AST の実測 36KB に余裕を見た値）」
 *   ＋ 同節の追記（2026-08-15・planning#364）「予算値 51,200 の算定根拠は Claude 側の実測である」。
 *   **本スクリプトが値を持つのは複製である。** 正本が動いたらここも動かす（出典の無い複製は認めない）。
 *   `AGENTS.md` 系については**実測が無いため同じ値を暫定的に流用する** —— **超過を fail の根拠にせず、
 *   観測に留める**（`failOnOver: false`）。実測が揃った時点で、その集合に見合う値を別に定める。
 *
 * ■ 閾値
 *
 *   超過（100% 超）で **fail**、接近（既定 90% 以上）で **warn**（warn は失敗にしない）。
 *   **超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。**
 *   欠落（母集合に在るはずのファイルが無い）は **missing** として出す。**黙って 0 として扱わない** ——
 *   落とすと「集合が縮んだ」ことに気づけず、予算に収まったように見える。
 *
 * ■ 何を見ないか（明示する）
 *   - **内容の妥当性**（規範が正しいか）。見るのはバイト数だけである。
 *   - **手動で読まれる文書**（`docs/README.md`・別紙 `docs/how-to/*-annex.md` 等）。
 *     母集合は「自動で読み込まれる集合」に限る。
 *
 * 使い方:
 *   node scripts/check-reading-budget.js [--self-test]
 *
 * 環境変数:
 *   READING_BUDGET_BYTES  予算（既定 51200。**正本は上記のとおり計画リポ**）
 *   READING_BUDGET_WARN   warn を出す割合（既定 0.9）
 */
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..');
// 出典: 計画リポジトリ project-planning の docs/ai-implementation-workflow-guide.md §8
// （50KB = 51,200。裁定 planning#364）。複製である。
const BUDGET_BYTES = Number(process.env.READING_BUDGET_BYTES || 51200);
const WARN_RATIO = Number(process.env.READING_BUDGET_WARN || 0.9);
// ★ #730 / [[IADR-0190]] が確保した「恒久的な余白」の下限。**上限だけでは足りない** ——
//   上限内でも「余白が薄すぎて次の規範を足せない」状態は起こる。
//   ここに置くのは、**下限を読む場所が 2 つ以上あるため**である（scripts.repo.test.js の
//   #730 と #790/#793）。リテラルを各所に持つと、片方だけ追随を忘れて静かにずれる
//   —— 予算値（BUDGET_BYTES）を本ファイルへ集めたのと同じ理由である（#755 / [[IADR-0200]]）。
const MARGIN_FLOOR_BYTES = Number(process.env.READING_BUDGET_FLOOR || 1000);

/**
 * エージェントごとの自動読み込み集合。
 *
 * `files` は固定パス、`globDirs` は「そのディレクトリ直下の *.md すべて」を指す。
 * `failOnOver` が false の集合は**観測のみ**（超過しても fail にしない。理由はヘッダの「予算値」）。
 */
const AGENT_SETS = [
  {
    name: 'Claude Code',
    // 根拠: CLAUDE.md「Claude はこのファイルを毎セッション読み込む」
    //       .claude/rules/traceability.md「同ディレクトリの *.md は自動適用される」（companion 機構）
    files: ['CLAUDE.md'],
    globDirs: ['.claude/rules'],
    failOnOver: true,
  },
  {
    name: 'AGENTS.md 対応（Codex / Cursor / Aider）',
    // 根拠: AGENTS.md 冒頭「Claude 以外の AI エージェント…が読み込む共通指示」
    // 🔴 Claude は読まない。Claude Code の集合へ足してはならない。
    // 予算値は Claude 側の実測を暫定流用しているため、超過は fail にしない（観測に留める）。
    files: ['AGENTS.md'],
    globDirs: [],
    failOnOver: false,
  },
  {
    name: 'GitHub Copilot',
    // 根拠: CLAUDE.md「Copilot 固有の運用は .github/copilot-instructions.md」
    // 同上（暫定流用・観測のみ）。
    files: ['.github/copilot-instructions.md'],
    globDirs: [],
    failOnOver: false,
  },
];

/** 集合を実ファイルへ展開する。存在しないものは黙って落とさず、呼び出し側へ返す。 */
function resolveSet(set, repo = REPO) {
  const found = [];
  const missing = [];
  for (const rel of set.files) {
    if (fs.existsSync(path.join(repo, rel))) found.push(rel);
    else missing.push(rel);
  }
  for (const dir of set.globDirs) {
    const abs = path.join(repo, dir);
    if (!fs.existsSync(abs)) {
      missing.push(`${dir}/*.md`);
      continue;
    }
    for (const name of fs.readdirSync(abs).sort()) {
      if (name.endsWith('.md')) found.push(path.posix.join(dir, name));
    }
  }
  return { found, missing };
}

/** 集合の実測（バイト・内訳）。 */
function measure(set, repo = REPO) {
  const { found, missing } = resolveSet(set, repo);
  const entries = found.map((rel) => ({ file: rel, bytes: fs.statSync(path.join(repo, rel)).size }));
  const total = entries.reduce((s, e) => s + e.bytes, 0);
  return { name: set.name, entries, total, missing, failOnOver: set.failOnOver !== false };
}

/** 判定。**超過は over、接近は warn。** */
function verdict(total, budget = BUDGET_BYTES, warnRatio = WARN_RATIO) {
  if (total > budget) return 'over';
  if (total >= budget * warnRatio) return 'warn';
  return 'ok';
}

function pct(total, budget = BUDGET_BYTES) {
  return Math.round((total / budget) * 1000) / 10;
}

function selfTest() {
  const cases = [];
  const t = (name, pass) => cases.push({ name, pass });
  const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'rbudget-'));

  // --- 判定の境界 ---
  t('予算ちょうどは over ではない', verdict(51200, 51200, 0.9) === 'warn');
  t('予算を 1 バイト超えたら over', verdict(51201, 51200, 0.9) === 'over');
  t('90% ちょうどは warn', verdict(46080, 51200, 0.9) === 'warn');
  t('90% 未満は ok', verdict(46079, 51200, 0.9) === 'ok');
  // 🔴 「接近で warn」を持たない実装は、超えてから気づくことになる。境界を固定する。

  // --- 予算値の複製が正本と同じ値であること（出典の無い複製を認めない） ---
  t('予算の既定は 51,200（計画リポ運用ガイド §8 の複製）', BUDGET_BYTES === 51200 || process.env.READING_BUDGET_BYTES !== undefined);
  // 🔴 下限を緩める（0 へ落とす）と、#730 と #790/#793 の**両方が同時に黙る**。値を固定する。
  t('余白の下限の既定は 1,000（#730 / IADR-0190）', MARGIN_FLOOR_BYTES === 1000 || process.env.READING_BUDGET_FLOOR !== undefined);

  // --- 集合の展開 ---
  fs.mkdirSync(path.join(tmp, '.claude/rules'), { recursive: true });
  fs.writeFileSync(path.join(tmp, 'CLAUDE.md'), 'x'.repeat(100));
  fs.writeFileSync(path.join(tmp, '.claude/rules/a.md'), 'y'.repeat(10));
  fs.writeFileSync(path.join(tmp, '.claude/rules/b.repo.md'), 'z'.repeat(20));
  fs.writeFileSync(path.join(tmp, '.claude/rules/not-md.txt'), 'w'.repeat(9999));
  const set = { name: 'x', files: ['CLAUDE.md'], globDirs: ['.claude/rules'] };
  const m = measure(set, tmp);
  t('直下の *.md をすべて拾う（列挙ではなく走査。companion も入る）', m.total === 130);
  t('*.md 以外は拾わない', !m.entries.some((e) => e.file.endsWith('.txt')));
  t('内訳を返す', m.entries.length === 3);

  // 🔴 **存在しないファイルを黙って 0 として扱わない。**
  const miss = measure({ name: 'y', files: ['NOPE.md'], globDirs: ['no/such'] }, tmp);
  t('存在しないファイルは missing として返す', miss.missing.length === 2);
  t('missing は合計に入らない', miss.total === 0);

  // --- 🔴 合算しないこと（本検査器の設計の核心） ---
  t('集合は 3 つに分かれている', AGENT_SETS.length === 3);
  t('AGENTS.md は Claude Code の集合に入っていない', !AGENT_SETS[0].files.includes('AGENTS.md'));
  t(
    'AGENTS.md は専用の集合を持つ',
    AGENT_SETS.some((s) => s.files.includes('AGENTS.md') && s.globDirs.length === 0),
  );
  t('Claude Code の集合だけが fail の根拠になる（AGENTS.md 系は観測のみ）',
    AGENT_SETS[0].failOnOver === true && AGENT_SETS.slice(1).every((s) => s.failOnOver === false));
  t('submodule 配下の CLAUDE.md は入れない',
    !AGENT_SETS.some((s) => s.files.some((f) => /^(planning|src)\//.test(f))));

  fs.rmSync(tmp, { recursive: true, force: true });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-reading-budget] 自己試験 ${failed} 件が失敗した。`);
    return 1;
  }
  console.log(`[check-reading-budget] 自己試験 ${cases.length} 件がすべて通った。`);
  return 0;
}

function main(argv = process.argv) {
  const args = argv.slice(2);
  const known = ['--self-test'];
  const unknown = args.filter((a) => !known.includes(a));
  if (unknown.length) {
    console.error(
      `[check-reading-budget] 未知の引数: ${unknown.join(' ')}（受け付けるのは ${known.join(' / ')}）。` +
        '黙って無視すると、渡し続けているフラグが効いていないことに誰も気付けない。',
    );
    return 1;
  }
  if (args.includes('--self-test')) return selfTest();

  const results = AGENT_SETS.map((s) => measure(s));
  let bad = 0;
  let warned = 0;
  let observedOver = 0;
  for (const r of results) {
    const v = verdict(r.total);
    const mark = v === 'over' ? (r.failOnOver ? '  NG' : 'over') : v === 'warn' ? 'warn' : '  ok';
    console.log(
      `  ${mark}  ${r.name}: ${r.total.toLocaleString()} バイト（予算 ${BUDGET_BYTES.toLocaleString()} の ${pct(r.total)}%）` +
        (r.failOnOver ? '' : '〔観測のみ・予算値は暫定流用〕'),
    );
    for (const e of r.entries) console.log(`          ${e.file}  ${e.bytes.toLocaleString()}`);
    if (r.missing.length) {
      // 集合が縮んだことを黙って通さない（予算に収まったように見えるため）。
      console.log(`          【欠落】${r.missing.join(' / ')}`);
    }
    if (v === 'over') {
      if (r.failOnOver) bad++;
      else observedOver++;
    }
    if (v === 'warn') warned++;
  }

  if (bad) {
    console.error(
      `\n[check-reading-budget] ${bad} 件の集合が予算 ${BUDGET_BYTES.toLocaleString()} バイトを超えた。\n` +
        '  減量するか、予算そのものを計画側へ環流して見直すこと' +
        '（正本: 計画リポジトリ project-planning の docs/ai-implementation-workflow-guide.md §8）。\n' +
        '  🔴 集合をまたいで足し引きしてはならない —— 拘束するのは「セッション 1 本が背負う量」である。',
    );
    return 1;
  }
  if (observedOver) {
    console.log(
      `\n  warn  [check-reading-budget] 観測のみの集合 ${observedOver} 件が暫定予算を超えている。` +
        '実測が揃うまで fail にはしない（planning#364）。',
    );
  }
  if (warned) {
    console.log(
      `\n  warn  [check-reading-budget] ${warned} 件の集合が予算の ${Math.round(WARN_RATIO * 100)}% 以上である。` +
        '超えてから気づくと、減量を迫られる場面で減らせるものが残っていない。',
    );
  } else if (!observedOver) {
    console.log(
      `\n[check-reading-budget] OK: ${results.length} 件の集合すべてが予算 ${BUDGET_BYTES.toLocaleString()} バイト内である。`,
    );
  }
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = {
  main,
  measure,
  resolveSet,
  verdict,
  pct,
  selfTest,
  AGENT_SETS,
  BUDGET_BYTES,
  WARN_RATIO,
  MARGIN_FLOOR_BYTES,
};
