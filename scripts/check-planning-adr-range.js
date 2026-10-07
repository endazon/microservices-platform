#!/usr/bin/env node
'use strict';
/*
 * check-planning-adr-range.js
 * NFR / planning#591 Q2 / IADR-0423: 本リポジトリが宣言する計画 ID レンジと、計画リポジトリが
 * 公開する実物の導出結果を突き合わせる。外部依存ゼロ（gh CLI を子プロセスで呼ぶだけ）。
 *
 * 背景:
 *   本リポジトリの計画 ID レンジ宣言（`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節）は
 *   `check-trace-blocks.js` / `check-commit-messages.js` の**一次情報**である。**宣言が計画側の実物より
 *   遅れている間、そのレンジ外の ID を引く PR は CI が落ちて通らない。** 前進の契機は従来
 *   「自分の作業が新しい ADR を引いて `check-trace-blocks` に止められたとき」という**事後検知**だった
 *   （別紙 `docs/how-to/plan-id-range-history-annex.md`）。本スクリプトは先回りして突合する。
 *   `secrets.GITHUB_TOKEN` は本リポジトリしか読めず 404 になるため、cross-repo 読み取り用の
 *   `PLANNING_REPO_TOKEN` を使う（ai-stock-trading の実測。同リポの #717）。
 *
 * 出典の変更（planning#591 Q2・計画 ADR-0093 決定 1・2026-09-09）:
 *   従前は計画リポの `07_adr/` ディレクトリ一覧を取り、**ADR の最大番号だけ**を突き合わせていた。
 *   計画側が `tools/doc-checks/kg-ranges.json` を「実物から導出して公開する成果物」へ格上げした
 *   （手で書かず `gen-plan-ranges.js --write` で揃え、`--check` を CI の必須チェックに置いた）ため、
 *   本スクリプトはその**公開ファイルを 1 回取得し、FR / UC / SC / ADR の 4 種すべて**を突き合わせる。
 *   🔴 **これは計画 ADR-0093 決定 3 が範囲を 4 点に限って認めた例外である**（対象は ID レンジの突合ただ 1 つ／
 *   取得は読み取り専用の HTTP に限る／落とし方は警告に限る／ビルドやテストの前提にしない）。
 *   **他の planning 依存を復活させない**（ADR-0029 決定 2）。
 *
 * fail-open の設計と、その「放置しない」方法:
 *   secret が無い／API が失敗した、のいずれでも **exit 0** で `status: "unverified"` と理由を書く
 *   （宣言が読めないときは `error`・exit 1。下記「宣言の遅れを『作業』にする」）。
 *   🔴 **ただし「ずれが 0 件」と「検査が動いていない」は必ず区別できるようにする**（ADR-0093 決定 3）。
 *   そのために **`scanned`（実際に突き合わせられた種別の数）を必ず併記する。** `scanned: 0` は
 *   「検査が動いていない」であり、`scanned: 4` かつ指摘 0 件が「ずれが無い」である。
 *   **計画側の取得失敗では終了コードを変えない** —— ADR-0093 決定 3 は「落とし方は警告に限り、ビルドやテストの前提にしない」と
 *   定めており、同決定が述べる「fail-open のままにしない」の内容は**走査件数の併記**である。
 *
 * 宣言の遅れを「作業」にする（#1775 / IADR-0508。AST 側の同型は AST#1208）:
 *   本突合は PR CI で毎回走っていたが、ずれても警告（アノテーション）が出るだけで、宣言は手で
 *   引き直されるまで遅れたままだった（ADR-0129 の遅れは第 4 回全体監査 B-1 が拾った。#1769 / #1774）。
 *   NFR の採番宣言はどこからも突き合わされていなかった。そこで週次の棚卸し（backlog-audit.yml）の前段で
 *   `--with-nfr --upsert-issue` を付けて走らせる:
 *   - `--with-nfr`: 計画側の要求一覧（`projects/<name>/02_requirements/*.md`）の**定義表の行頭セル**
 *     `| NFR-NN |` から NFR の最大番号を導き（計画リポの `gen-plan-ranges.js` が参考行に出すのと同じ規則）、
 *     宣言 `` `NFR-01`〜`NFR-NN` `` と突き合わせる。🔴 **PR CI では付けない**（計画 ADR-0093 決定 2 は NFR を
 *     公開レンジ表へ足すかを別裁定とした。週次の報告に限って見る）。取得は読み取り専用の HTTP（`gh api`）に限る。
 *   - `--upsert-issue`: `behind` / `ahead` のとき専用の issue（ラベル `plan-range-lag`・マーカー
 *     `<!-- plan-range-lag -->`）を 1 件だけ起票し、以後の週は本文を上書きする。**自動ではクローズしない**。
 *   - 🔴 **宣言を読めない（節が無い・書式が崩れた）は `unverified` ではなく `error`・exit 1。**
 *     計画側の到達性ではなく本リポジトリの欠陥であり、`readPlanIds()` が例外で落とすのと揃える。
 *   - 起票が要るのに書けない（token 不在・API 失敗）ときも exit 1（検知を黙って捨てない）。
 *   - 計画側に届かない（`unverified`、または一部の種別だけ届かない `unverifiedKinds`）は exit 0 のまま
 *     warning と棚卸し報告の「未確認」で明示する（計画 ADR-0093 決定 3。黙って緑にしない）。
 *
 * 使い方:
 *   node scripts/check-planning-adr-range.js --out <path.json> [--with-nfr] [--upsert-issue] [--rules <path>]
 *   node scripts/check-planning-adr-range.js --self-test
 *
 * gh は `GH_TOKEN` を環境変数から読む。本スクリプトは env `PLANNING_REPO_TOKEN` を子プロセスの
 * `GH_TOKEN` へ写す（既定の `GITHUB_TOKEN` は使わない——それでは 404 になることが実測済み）。
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { emit } = require('./lib/ci-annotate.js');
const tt = require('./check-test-traceability.js');
// 🔴 計画 ADR レンジのパーサを本ファイルへ書き写さない —— check-trace-blocks.js が公開する
//    planAdrRange() を再利用する（同じ事実を 2 本のパーサで持たない。check-commit-messages.js が
//    readPlanIds() を再利用しているのと同じ理由）。ai-stock-trading 版は lib/plan-ranges.js を使うが、
//    本リポにその lib は無い —— キットとの乖離は受容する（計画 ADR-0048 決定 6）。
const { planAdrRange } = require('./check-trace-blocks.js');

const DEFAULT_OWNER = 'endazon';
const DEFAULT_REPO = 'project-planning';
/** 計画側が公開する導出結果（計画 ADR-0093 決定 1）。 */
const DEFAULT_FILE = 'tools/doc-checks/kg-ranges.json';
/** `kg-ranges.json` のトップレベルキー（`projects/<name>/` のディレクトリ名）。 */
const PROJECT_KEY = 'microservices-platform';
/** 突き合わせる種別。🔴 NFR は既定では入れない（計画 ADR-0093 決定 2）。`--with-nfr` のときだけ足す（#1775）。 */
const KINDS = ['FR', 'UC', 'SC', 'ADR'];
const NFR_KINDS = [...KINDS, 'NFR'];
/** NFR の導出元（計画 ADR-0093 決定 2 の参考行と同じ。定義表の行頭セル）。 */
const NFR_SOURCE_DIR = `projects/${PROJECT_KEY}/02_requirements`;

/** 宣言のずれを追う専用 issue（#1775 / IADR-0508）。 */
const LAG_LABEL = 'plan-range-lag';
const LAG_MARKER = '<!-- plan-range-lag -->';
const LAG_TITLE = 'chore(NFR): 計画 ID レンジの宣言が計画側の実物とずれている（週次棚卸しの自動起票）';
const RULES_REL = '.claude/rules/traceability.repo.md';
const ANNEX_REL = 'docs/how-to/plan-id-range-history-annex.md';

function parseArgs(argv) {
  const a = { selfTest: false, out: null, withNfr: false, upsertIssue: false, rules: null };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--with-nfr') a.withNfr = true;
    else if (t === '--upsert-issue') a.upsertIssue = true;
    else if (t === '--out') a.out = argv[++i];
    else if (t.startsWith('--out=')) a.out = t.slice('--out='.length);
    else if (t === '--rules') a.rules = argv[++i];
  }
  return a;
}

/**
 * 本文から**定義表の行頭セル**の NFR 番号を取り出す（純関数）。計画リポの `gen-plan-ranges.js` の
 * `matchCells` と同じ規則 —— 自由なトークン走査は他所の参照まで拾う（計画 ADR-0093 実測 3）。
 */
function matchNfrCells(text) {
  return [...String(text).matchAll(/^\|\s*NFR-(\d{2,4})\s*[,|]/gm)].map((m) => Number(m[1]));
}

/**
 * 計画側の要求一覧から NFR のレンジを導く。`gh api` で一覧を取り、`.md` を raw で読む（読み取り専用）。
 * @returns {[number, number]}
 */
function fetchPlanningNfrRange({
  owner = DEFAULT_OWNER, repo = DEFAULT_REPO, dir = NFR_SOURCE_DIR, token, execFn = execFileSync,
} = {}) {
  if (!token) throw new Error('PLANNING_REPO_TOKEN が渡されていない（secret 不在）');
  const opts = {
    encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, GH_TOKEN: token, GITHUB_TOKEN: token },
  };
  let list;
  try {
    list = JSON.parse(execFn('gh', ['api', `repos/${owner}/${repo}/contents/${dir}`], opts));
  } catch (e) {
    throw new Error(`${dir} の一覧を読めない: ${e.message || e}`);
  }
  const files = (Array.isArray(list) ? list : []).filter((f) => f && f.type === 'file' && /\.md$/.test(f.name));
  if (files.length === 0) throw new Error(`${dir} に要求一覧（.md）が無い`);
  const nums = files.flatMap((f) => matchNfrCells(execFn('gh', [
    'api', `repos/${owner}/${repo}/contents/${f.path}`, '-H', 'Accept: application/vnd.github.raw',
  ], opts)));
  if (nums.length === 0) throw new Error(`${dir} の定義表に NFR の行が 1 件も無い`);
  return [Math.min(...nums), Math.max(...nums)];
}

/** 宣言の NFR 採番（`` `NFR-01`〜`NFR-29` ``）を読む。無ければ例外（fail-loud）。 */
function readDeclaredNfrRange(rulesPath = path.join(__dirname, '..', RULES_REL)) {
  let md;
  try {
    md = fs.readFileSync(rulesPath, 'utf8');
  } catch {
    throw new Error(`${RULES_REL} を読めない（NFR 採番の宣言元）`);
  }
  const section = tt.planRangeSection(md);
  if (section === null) throw new Error(`${RULES_REL} に「${tt.PLAN_RANGE_HEADING}」節が見つからない`);
  const m = /`NFR-(\d+)`〜`NFR-(\d+)`/.exec(section);
  if (!m) throw new Error(`${RULES_REL} の「${tt.PLAN_RANGE_HEADING}」節に NFR の採番（例: \`NFR-01\`〜\`NFR-29\`）が見つからない`);
  return [Number(m[1]), Number(m[2])];
}

/**
 * 計画リポジトリが公開する `kg-ranges.json` を取り、本プロジェクトのレンジ表を返す。
 * `execFn` は差し替え可能（テストで gh を呼ばずに済ませる）。token が無ければ例外。
 * @returns {{[kind: string]: [number, number]}}
 */
function fetchPlanningRanges({
  owner = DEFAULT_OWNER, repo = DEFAULT_REPO, file = DEFAULT_FILE,
  projectKey = PROJECT_KEY, token, execFn = execFileSync,
} = {}) {
  if (!token) throw new Error('PLANNING_REPO_TOKEN が渡されていない（secret 不在。B-3）');
  const out = execFn('gh', [
    'api', `repos/${owner}/${repo}/contents/${file}`,
    '-H', 'Accept: application/vnd.github.raw',
  ], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, GH_TOKEN: token, GITHUB_TOKEN: token },
  });
  let doc;
  try {
    doc = JSON.parse(out);
  } catch (e) {
    throw new Error(`${file} が JSON として読めない: ${e.message || e}`);
  }
  const table = doc && doc[projectKey];
  if (!table || typeof table !== 'object') {
    throw new Error(`${file} に「${projectKey}」のレンジ表が無い（計画側でプロジェクトキーが変わった可能性）`);
  }
  const ranges = {};
  for (const kind of KINDS) {
    const v = table[kind];
    if (Array.isArray(v) && v.length === 2 && Number.isInteger(v[0]) && Number.isInteger(v[1])) {
      ranges[kind] = [v[0], v[1]];
    }
  }
  if (Object.keys(ranges).length === 0) {
    throw new Error(`${file} の「${projectKey}」に ${KINDS.join(' / ')} のレンジが 1 件も無い`);
  }
  return ranges;
}

/**
 * 本リポジトリが宣言しているレンジを読む。
 * FR/UC/SC は `check-test-traceability.js` の `readPlanIds()`、ADR は `lib/plan-ranges.js` が単一情報源。
 * 🔴 パーサを書き写さない —— 本番の抽出関数そのものを呼ぶ。
 * @returns {{[kind: string]: [number, number]}}
 */
function readDeclaredRanges({
  readIdsFn = tt.readPlanIds, readAdrFn = planAdrRange, readNfrFn = readDeclaredNfrRange, rulesPath, withNfr = false,
} = {}) {
  const ranges = {};
  // rulesPath 未指定なら各パーサの既定（本リポジトリの宣言ファイル）を使う。
  const ids = rulesPath ? readIdsFn(rulesPath) : readIdsFn();
  for (const id of ids) {
    const m = /^(FR|UC|SC)-(\d+)$/.exec(String(id));
    if (!m) continue;
    const [, kind, num] = m;
    const n = Number(num);
    const cur = ranges[kind];
    if (!cur) ranges[kind] = [n, n];
    else ranges[kind] = [Math.min(cur[0], n), Math.max(cur[1], n)];
  }
  // planAdrRange() は読めなければ null を返す（例外ではない）。🔴 #1775 / IADR-0508: ここで例外へ変える ——
  //    ADR の宣言が消えた・崩れたのは本リポジトリの欠陥であり、黙って 3 種だけの突合へ縮めない。
  const adr = rulesPath ? readAdrFn(rulesPath) : readAdrFn();
  if (!adr || !Number.isInteger(adr.from) || !Number.isInteger(adr.to)) {
    throw new Error(`${RULES_REL} の「${tt.PLAN_RANGE_HEADING}」節に計画 ADR のレンジ（例: \`ADR-0001..0129\`）が読めない`);
  }
  ranges.ADR = [adr.from, adr.to];
  if (withNfr) ranges.NFR = rulesPath ? readNfrFn(rulesPath) : readNfrFn();
  return ranges;
}

/**
 * 種別ごとに宣言と実物を突き合わせる（純関数）。
 * @returns {{ranges: Array, scanned: number, status: 'ok'|'behind'|'ahead'}}
 */
function compareRanges(declared, planning, kinds = KINDS) {
  const rows = [];
  for (const kind of kinds) {
    const d = declared && declared[kind];
    const p = planning && planning[kind];
    if (!d || !p) continue;
    let status = 'ok';
    if (p[1] > d[1]) status = 'behind';
    else if (p[1] < d[1]) status = 'ahead';
    rows.push({ kind, declared: d, planning: p, status });
  }
  // 🔴 behind を ahead より優先して報告する。前進漏れのほうが実害（レンジ外 ID を引く PR の CI 落ち）を起こす。
  let status = 'ok';
  if (rows.some((r) => r.status === 'behind')) status = 'behind';
  else if (rows.some((r) => r.status === 'ahead')) status = 'ahead';
  return { ranges: rows, scanned: rows.length, status };
}

function pad4(n) {
  return String(n).padStart(4, '0');
}

function fmtFor(kind) {
  return kind === 'ADR' ? pad4 : (n) => String(n).padStart(2, '0');
}

/** 指摘のある種別だけを人が読める 1 行にする。 */
function describe(rows) {
  const bad = rows.filter((r) => r.status !== 'ok');
  if (bad.length === 0) return `宣言と実物が一致（${rows.length} 種を突合）`;
  return bad.map((r) => {
    const fmt = fmtFor(r.kind);
    const diff = Math.abs(r.planning[1] - r.declared[1]);
    const dir = r.status === 'behind' ? `実物 ${fmt(r.planning[1])} に ${diff} 件遅れている` : `実物 ${fmt(r.planning[1])} を ${diff} 件超えている`;
    return `${r.kind}: 宣言 ${fmt(r.declared[0])}..${fmt(r.declared[1])} が${dir}`;
  }).join(' / ');
}

/**
 * 全体の結合点。例外は投げない。計画側の取得失敗は unverified（一部の種別なら unverifiedKinds）、
 * 宣言の読み取り失敗は error を返す（#1775 / IADR-0508。後者は main が exit 1 にする）。
 * 既存の JSON キー（status / declaredMax / planningMax / reason / checkedAt / source）は維持する
 * —— backlog-audit.yml のプロンプトと scripts.repo.test.js が読んでいる契約である。
 */
function resolve({
  token, withNfr = false,
  readDeclaredFn = () => readDeclaredRanges({ withNfr }), fetchFn = fetchPlanningRanges, fetchNfrFn = fetchPlanningNfrRange,
} = {}) {
  const kinds = withNfr ? NFR_KINDS : KINDS;
  let declared = null;
  let planning = null;
  let declaredError = null;
  const reasons = [];
  try {
    declared = readDeclaredFn();
  } catch (e) {
    declaredError = `宣言を読めない: ${e.message || e}`;
    reasons.push(declaredError);
  }
  try {
    planning = fetchFn({ token });
  } catch (e) {
    reasons.push(`計画側を取得できない: ${e.message || e}`);
  }
  if (withNfr) {
    try {
      planning = { ...(planning || {}), NFR: fetchNfrFn({ token }) };
    } catch (e) {
      reasons.push(`計画側の NFR を導けない: ${e.message || e}`);
    }
  }
  const cmp = compareRanges(declared, planning, kinds);
  // 🔴 突き合わせられなかった種別を名指しする（「ずれ 0 件」と「一部を見ていない」を区別する）。
  const unverifiedKinds = kinds.filter((k) => !cmp.ranges.some((r) => r.kind === k));
  const declaredMax = declared && declared.ADR ? declared.ADR[1] : null;
  const planningMax = planning && planning.ADR ? planning.ADR[1] : null;
  const base = {
    declaredMax,
    planningMax,
    scanned: cmp.scanned,
    expected: kinds.length,
    unverifiedKinds,
    ranges: cmp.ranges,
    checkedAt: new Date().toISOString(),
    source: `${DEFAULT_OWNER}/${DEFAULT_REPO}/${DEFAULT_FILE}#${PROJECT_KEY}`,
  };
  // 🔴 宣言が読めないのは本リポジトリの欠陥である。unverified に混ぜない（readPlanIds() と揃える fail-loud）。
  if (declaredError) {
    return { status: 'error', ...base, reason: reasons.join('。') };
  }
  // 🔴 scanned が 0 なら「ずれが無い」ではなく「検査が動いていない」である。
  if (cmp.scanned === 0) {
    return { status: 'unverified', ...base, reason: reasons.join('。') || '突き合わせられた種別が 1 件も無い' };
  }
  const detail = describe(cmp.ranges);
  const reason = reasons.length ? `${detail}（ただし ${reasons.join('。')}）` : detail;
  return { status: cmp.status, ...base, reason };
}

/** 宣言のずれ（behind / ahead）があり、専用 issue で作業にすべきか。 */
function needsLagIssue(result) {
  return Boolean(result) && (result.status === 'behind' || result.status === 'ahead');
}

/** 終了コード。宣言不読（error）だけを 1 にする。計画側の取得失敗は 0（計画 ADR-0093 決定 3）。 */
function exitCodeFor(result) {
  return result && result.status === 'error' ? 1 : 0;
}

/** 種別のレンジを宣言の書式で書く（`ADR-0001..0129` / `NFR-01..29`）。 */
function rangeText(kind, r) {
  const fmt = fmtFor(kind);
  return `${kind}-${fmt(r[0])}..${fmt(r[1])}`;
}

/**
 * 専用 issue の本文（純関数）。🔴 引き直しの転記元は計画 ADR の本文ではなく計画リポの
 * `gen-plan-ranges.js --check` の実測である（ADR の本文の数値は、その ADR 自身が加わった時点で古くなる）。
 */
function renderLagIssue(result, { date, runId } = {}) {
  const label = { ok: '一致', behind: '**遅れ**', ahead: '**先走り**' };
  const rows = (result.ranges || []).map((r) => `| ${r.kind} | \`${rangeText(r.kind, r.declared)}\` | \`${rangeText(r.kind, r.planning)}\` | ${label[r.status] || r.status} |`);
  const unv = (result.unverifiedKinds || []).length ? [`未確認の種別: ${result.unverifiedKinds.join(' / ')}（計画側から取れなかった）`, ''] : [];
  return [
    LAG_MARKER,
    `<!-- plan-range-lag:run:${runId || 'local'} -->`,
    `## 計画 ID レンジの宣言のずれ（${date || result.checkedAt || ''} の週次棚卸しで検知）`,
    '',
    `\`${RULES_REL}\`「起点 ID の種別（固有）」節の宣言が、計画リポジトリの実物（\`${result.source}\`。NFR は要求一覧の定義表）とずれている。`,
    '',
    `**${result.reason}**（突合 ${result.scanned} 種）`,
    '',
    '| 種別 | 宣言 | 計画側の実物 | 判定 |',
    '| --- | --- | --- | --- |',
    ...rows,
    '',
    ...unv,
    '遅れている間は、実在する ID を引くコミット件名・PR タイトル・trace ブロックが「存在しない ID」として拒否される。',
    '',
    '### 引き直しの手順',
    '',
    '1. 計画リポで `node tools/doc-checks/gen-plan-ranges.js --check` を実測する（転記元は実測。計画 ADR の本文の数値ではない。NFR は同出力の参考行）',
    `2. \`${RULES_REL}\` の宣言を実測へ揃え、別紙 \`${ANNEX_REL}\` へ日付と実測を追記する（NFR を動かすなら別紙 §4 の内訳も）`,
    '3. PR 本文に `Closes #<この issue の番号>` を書く',
    '',
    '---',
    '本 issue は `.github/workflows/backlog-audit.yml` の前段（`scripts/check-planning-adr-range.js --with-nfr --upsert-issue`）が、ずれが続く間は毎週本文を上書きする。**自動ではクローズしない**（IADR-0508）。',
  ].join('\n');
}

async function ghRequest(method, url, token, body) {
  const res = await fetch(url, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${method} ${url}`);
  return res.json();
}

/**
 * ずれがあれば専用 issue を upsert する。ずれが無い・確かめられないなら何もしない（自動クローズもしない）。
 * `api(method, url, body)` は差し替え可能（自己試験は実ネットワークを叩かない）。
 * @returns {Promise<{action: 'none'|'created'|'updated', number?: number}>}
 */
async function upsertLagIssue({ result, repo, token, date, runId, api } = {}) {
  if (!needsLagIssue(result)) return { action: 'none' };
  // 🔴 ずれを検知したのに書けないことを黙らせない（呼び出し側が exit 1 にする）。
  if (!repo || !token) throw new Error('宣言のずれを検知したが、起票に要る GITHUB_REPOSITORY / GITHUB_TOKEN が無い');
  const call = api || ((method, url, body) => ghRequest(method, url, token, body));
  const base = `https://api.github.com/repos/${repo}`;
  try {
    await call('POST', `${base}/labels`, { name: LAG_LABEL, color: 'd93f0b', description: '計画 ID レンジ宣言のずれ（backlog-audit.yml が自動起票）' });
  } catch (e) {
    // 既にあれば 422。それ以外は投げ直す。
    if (!/^422/.test(String(e.message))) throw e;
  }
  const body = renderLagIssue(result, { date, runId });
  const open = await call('GET', `${base}/issues?state=open&labels=${encodeURIComponent(LAG_LABEL)}&per_page=100`);
  const found = (open || []).find((i) => !i.pull_request && String(i.body || '').includes(LAG_MARKER));
  if (found) {
    const r = await call('PATCH', `${base}/issues/${found.number}`, { body });
    return { action: 'updated', number: r && r.number ? r.number : found.number };
  }
  const r = await call('POST', `${base}/issues`, { title: LAG_TITLE, labels: [LAG_LABEL], body });
  if (!r || !Number.isInteger(r.number)) throw new Error('issue を作成したが番号を読み戻せない');
  return { action: 'created', number: r.number };
}

async function selfTest() {
  const PLANNING_OK = { FR: [1, 22], UC: [1, 11], SC: [1, 21], ADR: [1, 93] };
  const DECLARED_OK = { FR: [1, 22], UC: [1, 11], SC: [1, 21], ADR: [1, 93] };
  const PLANNING_NFR = [1, 29];
  const DECLARED_NFR = { ...DECLARED_OK, NFR: [1, 29] };
  const cases = [
    {
      name: 'compareRanges: 4 種すべて一致なら ok・scanned=4',
      run: () => compareRanges(DECLARED_OK, PLANNING_OK),
      expect: (r) => r.status === 'ok' && r.scanned === 4,
    },
    {
      // 🔴 陽性対照。ずらしたら落ちることを確かめずに検査器を信用しない。
      name: '陽性対照: ADR だけずらすと behind になり、指摘はその 1 種だけ（宣言が 0088 のままの形）',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 88] }, PLANNING_OK),
      expect: (r) => r.status === 'behind' && r.scanned === 4
        && r.ranges.filter((x) => x.status !== 'ok').length === 1
        && r.ranges.find((x) => x.kind === 'ADR').status === 'behind',
    },
    {
      name: '陽性対照: ADR 以外（SC）のずれも検出する（従前は ADR しか見ていなかった）',
      run: () => compareRanges({ ...DECLARED_OK, SC: [1, 20] }, PLANNING_OK),
      expect: (r) => r.status === 'behind' && r.ranges.find((x) => x.kind === 'SC').status === 'behind',
    },
    {
      name: '宣言が先走っていれば ahead',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 99] }, PLANNING_OK),
      expect: (r) => r.status === 'ahead',
    },
    {
      name: 'behind と ahead が同時なら behind を優先する（実害が大きい側）',
      run: () => compareRanges({ ...DECLARED_OK, ADR: [1, 88], SC: [1, 99] }, PLANNING_OK),
      expect: (r) => r.status === 'behind',
    },
    {
      name: '🔴 scanned=0 は ok ではなく unverified（「ずれが無い」と「動いていない」を区別する）',
      run: () => compareRanges(DECLARED_OK, {}),
      expect: (r) => r.scanned === 0 && r.ranges.length === 0,
    },
    {
      name: 'resolve: 一致なら ok・scanned=4',
      run: () => resolve({ token: 't', readDeclaredFn: () => DECLARED_OK, fetchFn: () => PLANNING_OK }),
      expect: (r) => r.status === 'ok' && r.scanned === 4 && r.declaredMax === 93 && r.planningMax === 93,
    },
    {
      name: 'resolve: secret 不在は unverified・scanned=0・理由に PLANNING_REPO_TOKEN を残す（exit させない）',
      run: () => resolve({ token: '', readDeclaredFn: () => DECLARED_OK }),
      expect: (r) => r.status === 'unverified' && r.scanned === 0
        && /PLANNING_REPO_TOKEN/.test(r.reason) && r.declaredMax === 93,
    },
    {
      name: 'resolve: gh 失敗（404 等）は unverified に理由を残す',
      run: () => resolve({
        token: 't',
        readDeclaredFn: () => DECLARED_OK,
        fetchFn: () => { throw new Error('gh: HTTP 404: Not Found'); },
      }),
      expect: (r) => r.status === 'unverified' && /404/.test(r.reason) && r.scanned === 0,
    },
    {
      // 🔴 #1775 / IADR-0508: 宣言の不読は本リポジトリの欠陥。unverified（計画側に届かない）と混ぜず error・exit 1。
      name: 'resolve: 宣言が読めなければ unverified ではなく error（exit 1。readPlanIds と同じ fail-loud）',
      run: () => {
        const r = resolve({
          token: 't',
          readDeclaredFn: () => { throw new Error('節が無い'); },
          fetchFn: () => PLANNING_OK,
        });
        return { r, code: exitCodeFor(r) };
      },
      expect: ({ r, code }) => r.status === 'error' && /宣言を読めない/.test(r.reason) && r.planningMax === 93 && code === 1,
    },
    {
      name: 'exitCodeFor: ok / behind / ahead / unverified は 0（計画側の取得失敗で落とさない＝計画 ADR-0093 決定 3）',
      run: () => ['ok', 'behind', 'ahead', 'unverified'].map((status) => exitCodeFor({ status })),
      expect: (codes) => codes.every((c) => c === 0),
    },
    {
      name: 'readDeclaredRanges: ADR の宣言が消えた宣言ファイルは例外（従前は null を黙って 3 種の突合へ縮めていた）',
      run: () => {
        const tmp = path.join(require('os').tmpdir(), `plan-range-bad-${process.pid}.md`);
        fs.writeFileSync(tmp, '# x\n\n## 起点 ID の種別（固有）\n\n- レンジは `FR-01..22` / `UC-01..11` / `SC-01..22`（ADR の宣言が消えた）。\n', 'utf8');
        try {
          readDeclaredRanges({ rulesPath: tmp });
          return 'なぜか成功した';
        } catch (e) { return e.message; } finally { fs.unlinkSync(tmp); }
      },
      expect: (m) => /ADR/.test(m) && m !== 'なぜか成功した',
    },
    {
      name: 'readDeclaredNfrRange: NFR の採番が無い宣言ファイルは例外（--with-nfr で黙って 4 種へ縮めない）',
      run: () => {
        const tmp = path.join(require('os').tmpdir(), `plan-range-nonfr-${process.pid}.md`);
        fs.writeFileSync(tmp, '# x\n\n## 起点 ID の種別（固有）\n\n- レンジは `FR-01..22` / `ADR-0001..0129`。\n', 'utf8');
        try {
          readDeclaredNfrRange(tmp);
          return 'なぜか成功した';
        } catch (e) { return e.message; } finally { fs.unlinkSync(tmp); }
      },
      expect: (m) => /NFR/.test(m) && m !== 'なぜか成功した',
    },
    {
      // 🔴 本番の抽出関数そのものを呼ぶ。値は固定しない（次に NFR を足す PR で自己試験まで直す羽目にしない）。
      name: 'readDeclaredRanges({ withNfr }): 実物の宣言から NFR も読める（値は固定しない）',
      run: () => readDeclaredRanges({ withNfr: true }),
      expect: (r) => Array.isArray(r.NFR) && r.NFR[0] === 1 && r.NFR[1] >= r.NFR[0],
    },
    {
      name: 'matchNfrCells: 定義表の行頭セルだけを拾い、本文中の参照や行頭でないセルは拾わない',
      run: () => matchNfrCells('| NFR-01 | 性能 |\n| NFR-29, NFR-30 | x |\n本文の NFR-40 は拾わない\n | NFR-50 | 行頭でない |\n'),
      expect: (a) => JSON.stringify(a) === '[1,29]',
    },
    {
      name: 'fetchPlanningNfrRange: 要求一覧を gh api で取り、.md だけを raw で読んで NFR のレンジを返す',
      run: () => {
        const seen = [];
        const r = fetchPlanningNfrRange({
          token: 'secret-x',
          execFn: (cmd, args, opts) => {
            seen.push(args.join(' '));
            if (cmd !== 'gh' || opts.env.GH_TOKEN !== 'secret-x') throw new Error('gh 以外か token が渡っていない');
            if (!args.includes('Accept: application/vnd.github.raw')) {
              return JSON.stringify([
                { type: 'file', name: '.gitkeep', path: `${NFR_SOURCE_DIR}/.gitkeep` },
                { type: 'file', name: '01_requirements.md', path: `${NFR_SOURCE_DIR}/01_requirements.md` },
              ]);
            }
            return '| NFR-01 | a |\n| NFR-31 | b |\n';
          },
        });
        return { r, seen };
      },
      expect: ({ r, seen }) => r[0] === 1 && r[1] === 31 && seen.length === 2 && !seen.some((x) => x.includes('.gitkeep')),
    },
    {
      name: 'fetchPlanningNfrRange: 定義表に NFR が 1 行も無ければ例外（0 件を「一致」にしない）',
      run: () => {
        try {
          fetchPlanningNfrRange({
            token: 't',
            execFn: (cmd, args) => (args.includes('Accept: application/vnd.github.raw')
              ? '本文だけ\n' : JSON.stringify([{ type: 'file', name: 'a.md', path: 'a.md' }])),
          });
          return 'なぜか成功した';
        } catch (e) { return e.message; }
      },
      expect: (m) => /NFR の行が 1 件も無い/.test(m),
    },
    // 🔴 #1775: 計画側が先へ進んだ形を種別ごとに作り、遅れた種別と件数が出力に名指しされることを確かめる。
    ...NFR_KINDS.map((kind) => ({
      name: `陽性対照（--with-nfr）: 計画側の ${kind} が 2 件先へ進むと behind・出力に「${kind}」と「2 件遅れ」が出る`,
      run: () => resolve({
        token: 't',
        withNfr: true,
        readDeclaredFn: () => DECLARED_NFR,
        fetchFn: () => PLANNING_OK,
        fetchNfrFn: () => PLANNING_NFR,
        ...(kind === 'NFR' ? { fetchNfrFn: () => [1, PLANNING_NFR[1] + 2] } : { fetchFn: () => ({ ...PLANNING_OK, [kind]: [1, PLANNING_OK[kind][1] + 2] }) }),
      }),
      expect: (r) => r.status === 'behind' && r.scanned === 5 && r.unverifiedKinds.length === 0
        && new RegExp(`${kind}: 宣言 .+ に 2 件遅れている`).test(r.reason)
        && r.ranges.filter((x) => x.status === 'behind').map((x) => x.kind).join() === kind,
    })),
    {
      name: 'resolve（--with-nfr）: 5 種一致なら ok・scanned=5・未確認の種別なし',
      run: () => resolve({ token: 't', withNfr: true, readDeclaredFn: () => DECLARED_NFR, fetchFn: () => PLANNING_OK, fetchNfrFn: () => PLANNING_NFR }),
      expect: (r) => r.status === 'ok' && r.scanned === 5 && r.expected === 5 && r.unverifiedKinds.length === 0,
    },
    {
      name: '🔴 resolve（--with-nfr）: NFR だけ取れなければ、他が一致でも unverifiedKinds に NFR を名指しし理由を残す',
      run: () => resolve({
        token: 't', withNfr: true, readDeclaredFn: () => DECLARED_NFR, fetchFn: () => PLANNING_OK,
        fetchNfrFn: () => { throw new Error('HTTP 404'); },
      }),
      expect: (r) => r.status === 'ok' && r.scanned === 4 && r.unverifiedKinds.join() === 'NFR' && /NFR を導けない: HTTP 404/.test(r.reason),
    },
    {
      name: 'resolve（--with-nfr）: 計画側に全く届かなければ unverified・scanned=0・5 種すべて未確認',
      run: () => resolve({ token: '', withNfr: true, readDeclaredFn: () => DECLARED_NFR }),
      expect: (r) => r.status === 'unverified' && r.scanned === 0 && r.unverifiedKinds.length === 5,
    },
    {
      name: 'renderLagIssue: マーカー・ずれた種別の行（NFR は 2 桁）・引き直しの手順を持つ',
      run: () => renderLagIssue(
        resolve({ token: 't', withNfr: true, readDeclaredFn: () => DECLARED_NFR, fetchFn: () => PLANNING_OK, fetchNfrFn: () => [1, 30] }),
        { date: '2026-10-13', runId: 'r9' },
      ),
      expect: (b) => b.startsWith(LAG_MARKER) && b.includes('plan-range-lag:run:r9')
        && b.includes('| NFR | `NFR-01..29` | `NFR-01..30` | **遅れ** |')
        && b.includes('| ADR | `ADR-0001..0093` | `ADR-0001..0093` | 一致 |')
        && b.includes('NFR: 宣言 01..29 が実物 30 に 1 件遅れている')
        && b.includes('gen-plan-ranges.js --check') && b.includes(ANNEX_REL) && b.includes('Closes #'),
    },
    {
      name: 'fetchPlanningRanges: raw JSON からプロジェクトのレンジを取り、GH_TOKEN を子プロセスへ写す',
      run: () => {
        let seenEnv = null;
        let seenArgs = null;
        const r = fetchPlanningRanges({
          token: 'secret-x',
          execFn: (cmd, args, opts) => {
            seenEnv = opts.env;
            seenArgs = args;
            if (cmd !== 'gh' || args[0] !== 'api') throw new Error('gh api 以外を呼んだ');
            return JSON.stringify({
              'microservices-platform': { FR: [1, 22], UC: [1, 11], SC: [1, 21], ADR: [1, 93] },
              'ai-stock-trading': { FR: [1, 21], UC: [1, 7], SC: [1, 3], ADR: [1, 37] },
            });
          },
        });
        return { r, tokenPassed: seenEnv && seenEnv.GH_TOKEN === 'secret-x', args: seenArgs };
      },
      expect: (x) => x.tokenPassed === true && x.r.ADR[1] === 93 && x.r.SC[1] === 21
        && x.args.join(' ').includes(DEFAULT_FILE)
        && x.args.join(' ').includes('application/vnd.github.raw'),
    },
    {
      name: 'fetchPlanningRanges: プロジェクトキーが無ければ例外（黙って 0 件検査へ落ちない）',
      run: () => {
        try {
          fetchPlanningRanges({ token: 't', execFn: () => JSON.stringify({ other: {} }) });
          return 'なぜか成功した';
        } catch (e) { return e.message; }
      },
      expect: (m) => /microservices-platform/.test(m),
    },
    {
      // 🔴 本番の抽出関数そのものを呼ぶ。正規表現を試験側へ書き写すと本番だけ変えても緑のままになる。
      name: 'readDeclaredRanges: 実物の宣言ファイルから 4 種すべてを読める',
      run: () => readDeclaredRanges(),
      expect: (r) => KINDS.every((k) => Array.isArray(r[k]) && r[k][1] >= r[k][0] && r[k][0] === 1),
    },
    {
      // 🔴 **現在の宣言値をリテラルと突き合わせない。** それをやると、次にレンジを前進させる PR で
      //    この自己試験まで同時に直さないと `ahead` で落ちる —— **本検査器が塞ごうとしている
      //    「導出値の書き写し」そのものである**（母集合の規則 10）。ここで固定するのは
      //    「実物の宣言が 4 種そろってパースでき、比較器がそれを処理できる」という配線であり、
      //    **ずれの検出は上の陽性対照（フィクスチャ）が持つ。** 実際のずれは CI の本走が見る。
      name: 'readDeclaredRanges → compareRanges の配線が実物の宣言で通る（値は固定しない）',
      run: () => {
        const declared = readDeclaredRanges();
        return compareRanges(declared, declared);
      },
      expect: (r) => r.status === 'ok' && r.scanned === 4,
    },
  ];
  // upsertLagIssue は非同期（GitHub API）。api を差し替えて呼び出しの列を記録する。
  const BEHIND = resolve({ token: 't', readDeclaredFn: () => DECLARED_OK, fetchFn: () => ({ ...PLANNING_OK, ADR: [1, 95] }) });
  const recorder = (open) => {
    const calls = [];
    const api = async (method, url, body) => {
      calls.push(`${method} ${url.replace(/^https:\/\/api\.github\.com\/repos\/o\/r/, '')}`);
      if (method === 'POST' && url.endsWith('/labels')) throw new Error('422 Unprocessable Entity');
      if (method === 'GET') return open;
      if (method === 'POST') return { number: 77, body: body.body };
      return { number: Number(url.split('/').pop()) };
    };
    return { calls, api };
  };
  const asyncCases = [
    {
      name: 'upsertLagIssue: behind で既存が無ければ専用 issue を作る（ラベル付き）',
      run: async () => {
        const { calls, api } = recorder([{ number: 5, body: '別の issue' }]);
        const r = await upsertLagIssue({ result: BEHIND, repo: 'o/r', token: 't', api });
        return { r, calls };
      },
      expect: ({ r, calls }) => r.action === 'created' && r.number === 77
        && calls.join('|') === `POST /labels|GET /issues?state=open&labels=${LAG_LABEL}&per_page=100|POST /issues`,
    },
    {
      name: 'upsertLagIssue: 既存（マーカー付き）があれば本文を上書きし、新しく作らない',
      run: async () => {
        const { calls, api } = recorder([{ number: 12, body: `${LAG_MARKER}\n古い本文` }]);
        const r = await upsertLagIssue({ result: BEHIND, repo: 'o/r', token: 't', api });
        return { r, calls };
      },
      expect: ({ r, calls }) => r.action === 'updated' && r.number === 12
        && calls[calls.length - 1] === 'PATCH /issues/12' && !calls.includes('POST /issues'),
    },
    {
      name: 'upsertLagIssue: ok / unverified / error では API を一度も呼ばない（自動クローズもしない）',
      run: async () => {
        const out = [];
        for (const status of ['ok', 'unverified', 'error']) {
          const { calls, api } = recorder([]);
          const r = await upsertLagIssue({ result: { ...BEHIND, status }, repo: 'o/r', token: 't', api });
          out.push(`${r.action}:${calls.length}`);
        }
        return out.join(',');
      },
      expect: (s) => s === 'none:0,none:0,none:0',
    },
    {
      name: '🔴 upsertLagIssue: ずれを検知したのに token が無ければ例外（黙って skip しない）',
      run: async () => {
        try {
          await upsertLagIssue({ result: BEHIND, repo: 'o/r', token: '' });
          return 'なぜか成功した';
        } catch (e) { return e.message; }
      },
      expect: (m) => /GITHUB_TOKEN/.test(m),
    },
  ];
  let failed = 0;
  for (const c of [...cases, ...asyncCases]) {
    let got;
    try {
      got = await c.run();
    } catch (e) {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      例外: ${e.message}\n`);
      continue;
    }
    if (c.expect(got)) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify(got)}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length + asyncCases.length} 件すべて合格\n`);
  return 0;
}

async function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(await selfTest());
  if (!args.out) {
    process.stderr.write('usage: check-planning-adr-range.js --out <path.json> [--with-nfr] [--upsert-issue] [--rules <path>] | --self-test\n');
    process.exit(2);
  }
  const result = resolve({
    token: process.env.PLANNING_REPO_TOKEN,
    withNfr: args.withNfr,
    readDeclaredFn: () => readDeclaredRanges({ withNfr: args.withNfr, ...(args.rules ? { rulesPath: args.rules } : {}) }),
  });
  let code = exitCodeFor(result);
  if (args.upsertIssue) {
    // 起票の結果も JSON へ残す（棚卸し報告が専用 issue の番号を引けるように）。
    try {
      result.lagIssue = await upsertLagIssue({
        result,
        repo: process.env.GITHUB_REPOSITORY,
        token: process.env.GITHUB_TOKEN,
        date: new Date().toISOString().slice(0, 10),
        runId: process.env.GITHUB_RUN_ID,
      });
    } catch (e) {
      result.lagIssue = { action: 'failed', reason: String(e.message || e) };
      code = 1;
    }
  }
  fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
  fs.writeFileSync(args.out, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
  // 🔴 走査件数を必ず添える。0 件は「ずれが無い」ではなく「検査が動いていない」である。
  const unv = result.unverifiedKinds && result.unverifiedKinds.length && result.scanned > 0
    ? `（未確認の種別: ${result.unverifiedKinds.join(' / ')}）` : '';
  const line = `計画 ID レンジ鮮度: ${result.status}（突合 ${result.scanned} 種）— ${result.reason}${unv}`;
  if (result.status === 'error') {
    emit('error', `${line}（宣言の欠陥。計画側の到達性とは別に落とす）`, { stream: process.stderr, prefix: '  error  ' });
  } else if (result.status === 'behind' || result.status === 'ahead' || unv) {
    emit('warning', line, { stream: process.stderr, prefix: '  warning  ' });
  } else if (result.status === 'unverified') {
    emit('warning', `${line}（「ずれが無い」ではなく「検査が動いていない」）`, { stream: process.stderr, prefix: '  warning  ' });
  } else {
    process.stdout.write(`✓ ${line}\n`);
  }
  const li = result.lagIssue;
  if (li && li.action === 'failed') {
    emit('error', `宣言のずれを専用 issue へ書けなかった: ${li.reason}`, { stream: process.stderr, prefix: '  error  ' });
  } else if (li) {
    process.stdout.write(li.action === 'none'
      ? `専用 issue: 起票不要（status: ${result.status}）\n`
      : `専用 issue: #${li.number} を${li.action === 'created' ? '作成した' : '更新した'}\n`);
  }
  process.stdout.write(`結果を書いた: ${args.out}\n`);
  process.exit(code);
}

if (require.main === module) {
  main(process.argv).catch((e) => {
    process.stderr.write(`${e.stack || e}\n`);
    process.exit(1);
  });
}

module.exports = {
  fetchPlanningRanges, fetchPlanningNfrRange, readDeclaredRanges, readDeclaredNfrRange, matchNfrCells,
  compareRanges, describe, resolve, selfTest, needsLagIssue, exitCodeFor, rangeText, renderLagIssue, upsertLagIssue,
  DEFAULT_FILE, PROJECT_KEY, KINDS, NFR_KINDS, LAG_LABEL, LAG_MARKER, LAG_TITLE,
};
