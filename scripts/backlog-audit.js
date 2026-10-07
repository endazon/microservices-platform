#!/usr/bin/env node
'use strict';
/*
 * backlog-audit.js — 定期棚卸し（週次）。滞留を**列挙するだけで状態は書き換えない**。
 * NFR / issue #1347。外部依存ゼロ（Node 標準モジュールのみ。GitHub API は fetch）。
 *
 * なぜ要るか:
 *   CLAUDE.md「乖離の検知は issue 運用と定期棚卸しに委ねる」、IADR-0364「定期棚卸しで確かめる」、
 *   IADR-0235「定期棚卸しの点検項目に入れれば緩和できる」—— 3 箇所が定期棚卸しへ検知を委ねていたが、
 *   **該当するワークフローは存在しなかった**（#1347。横断監査 A-4）。統制を定めた記述に現在の実現手段が
 *   無い状態であり、本スクリプトと `.github/workflows/backlog-audit.yml` がその実現手段である。
 *
 * 見るもの（各節は「指摘なし」を明示的に出す。0 件でも節は消えない）:
 *   1. `.ai-context/adr/` で `status: Proposed` のまま止まっている実装 ADR
 *   2. `.ai-context/specs/` で `done` / `completed` / `superseded` 以外のまま `updated:` が古い作業仕様書
 *   3. `docs/` で `draft` / `in-progress` / `pending` のまま `updated:` が古い文書
 *   4. `scripts/test-traceability-allowlist.json` に残る「写像を後回しにした」テスト（件数）
 *   5. GitHub: `blocked*` ラベルの open issue のうち、**再検証期限が無いか過ぎたもの**（**blocked 判定には再検証の
 *      期限を付け、棚卸しごとに再検証する**。CLAUDE.md）。期限は本文かコメントの `再検証期限: YYYY-MM-DD`
 *      （最後に書かれたものが効く）。経過日数は**最後のコメント**から数える —— `updated_at` はラベル操作で戻り、
 *      参照されても戻らないため使わない（#1773 / IADR-0506）。全件を台帳として報告に並べる
 *   6. GitHub: `ci-failure` ラベルの open issue（後段で落ちたまま放置されているもの）
 *   7. GitHub: 更新が古い open PR
 *   8. 計画 ID レンジ宣言の鮮度（#1775 / IADR-0508）。前段の `check-planning-adr-range.js --with-nfr --upsert-issue`
 *      が書いた JSON を `--plan-range <path>` で受け、FR / UC / SC / ADR / NFR のずれ・確かめられなかった種別・
 *      専用 issue の番号を並べる。🔴 **計画側に届かなかった（`unverified` / 一部の種別だけ未確認）は「指摘なし」と
 *      書かず、未確認として指摘に数える**（黙って緑にしない）。本スクリプト自身は計画リポジトリを読まない
 *
 * 🔴 「success だが無産出」を作り込まない設計（#1347 受け入れ基準 2。AST 側で実測された事故）:
 *   - 報告は**必ず**生成する（指摘 0 件でも「指摘なし」の節を持つ Markdown）。
 *   - `--post` では、同じ open issue（マーカー `<!-- backlog-audit -->`・ラベル `backlog-audit`）の本文を
 *     今回の報告で置き換え、要約コメントを 1 件足す。**足したコメントを読み戻して run 固有のマーカーが
 *     在ることを確かめ、無ければ exit 1** にする。
 *   - `--post` なのに GITHUB_TOKEN / GITHUB_REPOSITORY が無ければ **exit 1**（黙って skip しない）。
 *   - GitHub 面の取得に失敗したら、その節に失敗を書いたうえで **exit 1**（部分報告を成功に見せない）。
 *
 * 使い方:
 *   node scripts/backlog-audit.js                       # 報告を stdout へ（GitHub 面は token があれば取る）
 *   node scripts/backlog-audit.js --out report.md       # ファイルへも書く
 *   node scripts/backlog-audit.js --post                # GitHub の棚卸し issue へ反映（CI 用）
 *   node scripts/backlog-audit.js --stale-days 14       # 「古い」の閾値（既定 14 日）
 *   node scripts/backlog-audit.js --plan-range r.json   # 計画 ID レンジの突合結果（前段の JSON）を節 8 に載せる
 *   node scripts/backlog-audit.js --self-test
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const MARKER = '<!-- backlog-audit -->';
const LABEL = 'backlog-audit';
const BLOCKED_LABELS = ['blocked', 'blocked:env', 'blocked:human', 'blocked:decision'];
const DONE_SPEC_STATUSES = new Set(['done', 'completed', 'superseded']);
const STALE_DOC_STATUSES = new Set(['draft', 'in-progress', 'pending']);
const DEFAULT_STALE_DAYS = 14;

function toPosix(p) { return String(p).replace(/\\/g, '/'); }

/** frontmatter（`---` で囲む先頭ブロック）から `status:` と `updated:` を抜く（純関数）。 */
function parseFrontmatter(text) {
  const m = String(text).match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!m) return {};
  const out = {};
  for (const line of m[1].split(/\r?\n/)) {
    const kv = line.match(/^([A-Za-z_]+):\s*(.*)$/);
    if (kv) out[kv[1]] = kv[2].trim();
  }
  return out;
}

function daysBetween(a, b) { return Math.floor((b - a) / 86400000); }

/**
 * 本文とコメント（時系列順）から最後の `再検証期限` の日付を抜く（純関数。無ければ null）。
 * `再検証期限: 2026-10-19` / `再検証期限：…` / テンプレート欄（`### 再検証期限…` の次行）のいずれも読む。
 */
function parseRevalidationDeadline(texts) {
  let last = null;
  for (const t of texts) {
    // テンプレートのヒント（HTML コメント）を残したまま下に日付を書いても読めるよう、コメントを除いてから探す（#1773）。
    const text = String(t || '').replace(/<!--[\s\S]*?-->/g, '');
    for (const m of text.matchAll(/再検証期限[^\n\d]*\s*(\d{4}-\d{2}-\d{2})/g)) last = m[1];
  }
  // 暦に無い日付（2026-13-45 など）は期限として扱わない —— NaN は「過ぎた」判定を素通りし、黙って期限ありになる。
  return last && !Number.isNaN(Date.parse(last)) ? last : null;
}

/** `updated:` が閾値より古いか（純関数。日付が読めなければ「古い」と扱う —— 読めないのも滞留の一種）。 */
function isStale(updated, now, staleDays) {
  const t = Date.parse(String(updated || ''));
  if (Number.isNaN(t)) return true;
  return daysBetween(t, now) > staleDays;
}

function walkMd(dir, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const name of fs.readdirSync(dir).sort()) {
    const p = path.join(dir, name);
    if (name === 'node_modules' || name === '.git' || name === 'templates') continue;
    if (fs.statSync(p).isDirectory()) walkMd(p, acc);
    else if (/\.md$/.test(name)) acc.push(p);
  }
  return acc;
}

/** 面 1〜4: リポジトリ内の滞留を集める。 */
function collectRepoFindings({ root = REPO_ROOT, now = Date.now(), staleDays = DEFAULT_STALE_DAYS } = {}) {
  const rel = (p) => toPosix(path.relative(root, p));
  const proposedAdrs = [];
  for (const f of walkMd(path.join(root, '.ai-context', 'adr'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    if (/^proposed$/i.test(fm.status || '')) proposedAdrs.push({ file: rel(f), updated: fm.updated || '（不明）' });
  }
  const staleSpecs = [];
  for (const f of walkMd(path.join(root, '.ai-context', 'specs'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    const st = (fm.status || '').toLowerCase();
    if (!DONE_SPEC_STATUSES.has(st) && isStale(fm.updated, now, staleDays)) staleSpecs.push({ file: rel(f), status: fm.status || '（無し）', updated: fm.updated || '（不明）' });
  }
  const staleDocs = [];
  for (const f of walkMd(path.join(root, 'docs'))) {
    const fm = parseFrontmatter(fs.readFileSync(f, 'utf8'));
    const st = (fm.status || '').toLowerCase();
    if (STALE_DOC_STATUSES.has(st) && isStale(fm.updated, now, staleDays)) staleDocs.push({ file: rel(f), status: fm.status, updated: fm.updated || '（不明）' });
  }
  let allowlist = null;
  try {
    const j = JSON.parse(fs.readFileSync(path.join(root, 'scripts', 'test-traceability-allowlist.json'), 'utf8'));
    const entries = Array.isArray(j) ? j : (Array.isArray(j.entries) ? j.entries : Object.keys(j).filter((k) => !k.startsWith('$')));
    allowlist = entries.length;
  } catch { allowlist = null; }
  return { proposedAdrs, staleSpecs, staleDocs, allowlist };
}

async function ghJson(url, token) {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' } });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${url}`);
  return res.json();
}

async function ghPaginate(base, token) {
  const out = [];
  for (let page = 1; page <= 20; page++) {
    const sep = base.includes('?') ? '&' : '?';
    const items = await ghJson(`${base}${sep}per_page=100&page=${page}`, token);
    out.push(...items);
    if (items.length < 100) break;
  }
  return out;
}

/** 面 5〜7: GitHub の滞留を集める（token が無ければ null。呼び出し側が「取れなかった」と書く）。 */
async function collectGithubFindings({ repo, token, now = Date.now(), staleDays = DEFAULT_STALE_DAYS, api = ghPaginate } = {}) {
  if (!repo || !token) return null;
  const base = `https://api.github.com/repos/${repo}`;
  const issues = (await api(`${base}/issues?state=open`, token)).filter((i) => !i.pull_request);
  const prs = await api(`${base}/pulls?state=open`, token);
  const labelsOf = (i) => (i.labels || []).map((l) => (typeof l === 'string' ? l : l.name));
  const blocked = [];
  for (const i of issues.filter((x) => labelsOf(x).some((l) => BLOCKED_LABELS.includes(l)))) {
    // #1773: 日数の基準は `updated_at` ではなく最後のコメント（ラベル操作で戻らない）。
    const comments = await api(`${base}/issues/${i.number}/comments`, token);
    const lastAt = comments.length ? comments[comments.length - 1].created_at : i.created_at;
    const deadline = parseRevalidationDeadline([i.body, ...comments.map((c) => c.body)]);
    blocked.push({
      number: i.number, title: i.title, labels: labelsOf(i).filter((l) => BLOCKED_LABELS.includes(l)),
      deadline, overdue: deadline ? Date.parse(deadline) <= now : false, ageDays: daysBetween(Date.parse(lastAt), now),
    });
  }
  const staleBlocked = blocked.filter((b) => !b.deadline || b.overdue);
  const ciFailures = issues.filter((i) => labelsOf(i).includes('ci-failure')).map((i) => ({ number: i.number, title: i.title, ageDays: daysBetween(Date.parse(i.updated_at), now) }));
  const stalePrs = prs.filter((p) => daysBetween(Date.parse(p.updated_at), now) > staleDays).map((p) => ({ number: p.number, title: p.title, ageDays: daysBetween(Date.parse(p.updated_at), now) }));
  return { blocked, staleBlocked, ciFailures, stalePrs, openIssues: issues.length, openPrs: prs.length };
}

/**
 * 前段（`check-planning-adr-range.js --out`）の JSON を読む。読めなければ `{ error }`（呼び出し側が未確認と書く）。
 * `file` が無い（`--plan-range` 未指定）なら undefined —— ローカル実行で突合していないことを示す。
 */
function readPlanRange(file) {
  if (!file) return undefined;
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (e) {
    return { error: `突合結果 ${toPosix(file)} を読めない（前段が落ちたか書かなかった）: ${e.message}` };
  }
}

/**
 * 節 8 の指摘を並べる（純関数）。ずれた種別ごとに 1 件、確かめられなかったら 1 件、宣言の不読・起票の失敗も 1 件ずつ。
 * 🔴 未確認は「指摘なし」にしない —— 計画側に届かない週が続くと、ずれも見えないまま緑が続く。
 */
function planRangeFindings(pr) {
  if (pr === undefined) return null;
  if (!pr || pr.error) return [`🔴 未確認: ${pr ? pr.error : '突合結果が空'}`];
  const out = [];
  const issueRef = pr.lagIssue && Number.isInteger(pr.lagIssue.number) ? `（専用 issue #${pr.lagIssue.number}）` : '';
  if (pr.status === 'error') out.push(`🔴 宣言を読めない（本リポジトリの欠陥）: ${pr.reason}`);
  for (const r of pr.ranges || []) {
    if (r.status === 'ok') continue;
    const fmt = (n) => String(n).padStart(r.kind === 'ADR' ? 4 : 2, '0');
    const what = r.status === 'behind' ? '宣言が計画側の実物に遅れている' : '宣言が計画側の実物を超えている';
    out.push(`\`${r.kind}\`: ${what} —— 宣言 \`${r.kind}-${fmt(r.declared[0])}..${fmt(r.declared[1])}\` / 計画側 \`${r.kind}-${fmt(r.planning[0])}..${fmt(r.planning[1])}\`${issueRef}`);
  }
  const unv = Array.isArray(pr.unverifiedKinds) ? pr.unverifiedKinds : [];
  if (pr.status === 'unverified' || (pr.status !== 'error' && unv.length)) {
    out.push(`🔴 未確認（${unv.length ? unv.join(' / ') : '全種別'}）: ${pr.reason}`);
  }
  if (pr.lagIssue && pr.lagIssue.action === 'failed') out.push(`🔴 ずれを専用 issue へ書けなかった: ${pr.lagIssue.reason}`);
  return out;
}

function section(title, items, render, none = '指摘なし') {
  const lines = [`### ${title}（${items ? items.length : 0} 件）`, ''];
  if (!items || items.length === 0) lines.push(`- ${none}`);
  else for (const it of items) lines.push(`- ${render(it)}`);
  lines.push('');
  return lines;
}

function renderBlocked(b) {
  const kind = b.labels.includes('blocked') ? '・🔴 種別なしの `blocked`' : '';
  const due = b.deadline ? `期限 ${b.deadline}${b.overdue ? '（過ぎた）' : ''}` : '🔴 期限なし';
  return `#${b.number} ${b.title}（${b.labels.join(' / ')}${kind}・${due}・最後のコメントから ${b.ageDays} 日）`;
}

/** 報告 Markdown を組み立てる（純関数）。指摘 0 件でも全節を持つ。 */
function renderReport({ repoFindings, gh, ghError, runId, date, staleDays = DEFAULT_STALE_DAYS, planRange }) {
  const planFindings = planRangeFindings(planRange);
  const total = repoFindings.proposedAdrs.length + repoFindings.staleSpecs.length + repoFindings.staleDocs.length
    + (gh ? gh.staleBlocked.length + gh.ciFailures.length + gh.stalePrs.length : 0)
    + (planFindings ? planFindings.length : 0);
  const lines = [
    MARKER,
    `<!-- backlog-audit:run:${runId} -->`,
    `## 定期棚卸し ${date}（指摘 ${total} 件・閾値 ${staleDays} 日）`,
    '',
    '**列挙するだけで状態は書き換えない。** 各節は 0 件でも「指摘なし」を明示する（success なのに無産出、を作らないため）。',
    '',
    ...section('Proposed のまま止まっている実装 ADR', repoFindings.proposedAdrs, (a) => `\`${a.file}\`（updated ${a.updated}）`),
    ...section(`${staleDays} 日以上動いていない未完了の作業仕様書`, repoFindings.staleSpecs, (s) => `\`${s.file}\`（${s.status} / updated ${s.updated}）`),
    ...section(`${staleDays} 日以上動いていない draft / in-progress / pending の文書（docs/）`, repoFindings.staleDocs, (d) => `\`${d.file}\`（${d.status} / updated ${d.updated}）`),
    `### 写像を後回しにしたテスト（\`scripts/test-traceability-allowlist.json\`）`, '',
    repoFindings.allowlist == null ? '- 読めなかった' : (repoFindings.allowlist === 0 ? '- 指摘なし（0 件）' : `- ${repoFindings.allowlist} 件が残っている（テストを書いた PR で削除する運用）`), '',
  ];
  if (gh) {
    lines.push(
      ...section('blocked 系ラベルの open issue のうち再検証期限が無い・過ぎたもの（再検証が要る）', gh.staleBlocked, renderBlocked),
      `（blocked 系ラベルの open issue は全部で ${gh.blocked.length} 件 / open issue ${gh.openIssues} 件。台帳として全件を並べる）`, '',
      ...gh.blocked.map((b) => `- ${renderBlocked(b)}`), ...(gh.blocked.length ? [''] : []),
      ...section('ci-failure ラベルの open issue（後段の失敗が放置されている）', gh.ciFailures, (c) => `#${c.number} ${c.title}（${c.ageDays} 日）`),
      ...section(`${staleDays} 日以上動いていない open PR`, gh.stalePrs, (p) => `#${p.number} ${p.title}（${p.ageDays} 日）`),
      `（open PR は全部で ${gh.openPrs} 件）`, '',
    );
  } else {
    lines.push('### GitHub 面（blocked issue / ci-failure / 古い PR）', '', `- 🔴 取得できなかった: ${ghError || 'GITHUB_TOKEN / GITHUB_REPOSITORY が無い'}`, '');
  }
  if (planFindings) {
    const scanned = planRange && !planRange.error ? `突合 ${planRange.scanned} 種 / ${planRange.expected || planRange.scanned} 種` : '突合 0 種';
    lines.push(...section(`計画 ID レンジ宣言の鮮度（${scanned}。\`.claude/rules/traceability.repo.md\` と計画側の実物）`, planFindings, (f) => f,
      `指摘なし（宣言と計画側の実物が一致。${planRange.reason}）`));
  } else {
    lines.push('### 計画 ID レンジ宣言の鮮度', '', '- 突合していない（`--plan-range` 未指定。CI では backlog-audit.yml の前段が渡す）', '');
  }
  lines.push('---', '正本: `scripts/backlog-audit.js`（`.github/workflows/backlog-audit.yml` が週次で実行）。#1347');
  return lines.join('\n');
}

async function ghWrite(method, url, token, body) {
  const res = await fetch(url, { method, headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${method} ${url}: ${await res.text()}`);
  return res.json();
}

/** 棚卸し issue へ反映し、産出を読み戻して確かめる。 */
async function postReport({ repo, token, report, runId, summaryLine }) {
  const base = `https://api.github.com/repos/${repo}`;
  for (const [name, color, description] of [[LABEL, '0e8a16', '定期棚卸し（backlog-audit.yml が週次で更新）'], ['automation', 'ededed', '自動生成']]) {
    try { await ghWrite('POST', `${base}/labels`, token, { name, color, description }); } catch (e) { if (!/^422/.test(e.message)) throw e; }
  }
  const open = await ghPaginate(`${base}/issues?state=open&labels=${encodeURIComponent(LABEL)}`, token);
  let issue = open.find((i) => !i.pull_request && (i.body || '').includes(MARKER));
  if (issue) {
    await ghWrite('PATCH', `${base}/issues/${issue.number}`, token, { body: report });
  } else {
    issue = await ghWrite('POST', `${base}/issues`, token, { title: '[棚卸し] 定期棚卸し（週次・自動更新）', labels: [LABEL, 'automation'], body: report });
  }
  const comment = await ghWrite('POST', `${base}/issues/${issue.number}/comments`, token, { body: `<!-- backlog-audit:run:${runId} -->\n${summaryLine}` });
  // 🔴 産出の検証: 足したコメントを読み戻し、run 固有のマーカーが在ることを確かめる。
  const back = await ghJson(`${base}/issues/comments/${comment.id}`, token);
  if (!String(back.body || '').includes(`backlog-audit:run:${runId}`)) throw new Error('投稿したコメントを読み戻せなかった（産出が確認できない）');
  const body = await ghJson(`${base}/issues/${issue.number}`, token);
  if (!String(body.body || '').includes(`backlog-audit:run:${runId}`)) throw new Error('issue 本文に今回の run のマーカーが無い（本文の更新が確認できない）');
  return issue.number;
}

function argOf(name, dflt) {
  const i = process.argv.indexOf(name);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}

async function main() {
  if (process.argv.includes('--self-test')) { await selfTest(); return; }
  const post = process.argv.includes('--post');
  const staleDays = Number(argOf('--stale-days', DEFAULT_STALE_DAYS));
  const out = argOf('--out', null);
  const planRange = readPlanRange(argOf('--plan-range', null));
  const repo = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  const runId = process.env.GITHUB_RUN_ID || `local-${Date.now()}`;
  const date = new Date().toISOString().slice(0, 10);
  if (post && (!repo || !token)) {
    console.error('[backlog-audit] --post には GITHUB_REPOSITORY と GITHUB_TOKEN が要る（黙って skip しない）。');
    process.exit(1);
  }
  const repoFindings = collectRepoFindings({ staleDays });
  let gh = null; let ghError = null;
  try { gh = await collectGithubFindings({ repo, token, staleDays }); } catch (e) { ghError = e.message; }
  const report = renderReport({ repoFindings, gh, ghError, runId, date, staleDays, planRange });
  console.log(report);
  if (out) fs.writeFileSync(out, report + '\n');
  if (ghError) {
    console.error(`[backlog-audit] GitHub 面の取得に失敗した: ${ghError}（部分報告を成功にしない）`);
    process.exit(1);
  }
  if (post) {
    const total = (report.match(/指摘 (\d+) 件/) || [])[1];
    const n = await postReport({ repo, token, report, runId, summaryLine: `定期棚卸し ${date}: 指摘 ${total} 件。本文を今回の結果で置き換えた。` });
    console.log(`[backlog-audit] issue #${n} を更新し、産出を読み戻して確認した。`);
  }
}

async function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = async (name, fn) => { await fn(); n++; console.log(`  ok  ${name}`); };
  const now = Date.parse('2026-09-09T00:00:00Z');
  await ok('parseFrontmatter: status / updated を抜く', () => {
    assert.deepStrictEqual(parseFrontmatter('---\ntitle: x\nstatus: Proposed\nupdated: 2026-08-01\n---\n# h'), { title: 'x', status: 'Proposed', updated: '2026-08-01' });
    assert.deepStrictEqual(parseFrontmatter('# no frontmatter'), {});
  });
  await ok('isStale: 閾値を超えたら古い・読めない日付は古い扱い', () => {
    assert.strictEqual(isStale('2026-08-01', now, 14), true);
    assert.strictEqual(isStale('2026-09-01', now, 14), false);
    assert.strictEqual(isStale('（不明）', now, 14), true);
  });
  const repoFindings = { proposedAdrs: [], staleSpecs: [], staleDocs: [], allowlist: 0 };
  await ok('renderReport: 指摘 0 件でも全節が「指摘なし」を明示し、マーカーを 2 種持つ', () => {
    const r = renderReport({ repoFindings, gh: { blocked: [], staleBlocked: [], ciFailures: [], stalePrs: [], openIssues: 3, openPrs: 1 }, runId: 'r1', date: '2026-09-09' });
    assert.ok(r.includes(MARKER)); assert.ok(r.includes('backlog-audit:run:r1'));
    assert.strictEqual((r.match(/^- 指摘なし/gm) || []).length, 7, r);
    assert.ok(r.includes('指摘 0 件'));
  });
  await ok('renderReport: 指摘があれば数え上げ、GitHub 面が取れなければその旨を書く', () => {
    const r = renderReport({ repoFindings: { ...repoFindings, proposedAdrs: [{ file: 'a.md', updated: '2026-01-01' }] }, gh: null, ghError: 'boom', runId: 'r2', date: 'd' });
    assert.ok(r.includes('指摘 1 件')); assert.ok(r.includes('取得できなかった: boom')); assert.ok(r.includes('`a.md`'));
  });
  // #1775 / IADR-0508: 節 8（計画 ID レンジ宣言の鮮度）。
  const rangeRow = (kind, d, p, status) => ({ kind, declared: d, planning: p, status });
  const PR_OK = { status: 'ok', scanned: 5, expected: 5, unverifiedKinds: [], reason: '宣言と実物が一致（5 種を突合）', ranges: [rangeRow('ADR', [1, 129], [1, 129], 'ok')] };
  await ok('renderReport: 計画 ID レンジが一致なら節 8 は「指摘なし」・指摘に数えない', () => {
    const r = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p1', date: 'd', planRange: PR_OK });
    assert.ok(r.includes('### 計画 ID レンジ宣言の鮮度（突合 5 種 / 5 種'), r);
    assert.ok(r.includes('- 指摘なし（宣言と計画側の実物が一致'), r);
    assert.ok(r.includes('指摘 0 件'), r);
  });
  await ok('renderReport: 計画側が ADR / NFR とも先へ進んでいれば、種別ごとに遅れを名指しし専用 issue の番号を添える', () => {
    const pr = { ...PR_OK, status: 'behind', reason: 'ADR … / NFR …', lagIssue: { action: 'updated', number: 1801 }, ranges: [
      rangeRow('FR', [1, 22], [1, 22], 'ok'), rangeRow('ADR', [1, 128], [1, 129], 'behind'), rangeRow('NFR', [1, 28], [1, 29], 'behind')] };
    const r = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p2', date: 'd', planRange: pr });
    assert.ok(r.includes('- `ADR`: 宣言が計画側の実物に遅れている —— 宣言 `ADR-0001..0128` / 計画側 `ADR-0001..0129`（専用 issue #1801）'), r);
    assert.ok(r.includes('- `NFR`: 宣言が計画側の実物に遅れている —— 宣言 `NFR-01..28` / 計画側 `NFR-01..29`（専用 issue #1801）'), r);
    assert.ok(!r.includes('`FR`: 宣言'), '一致した種別まで指摘にしている');
    assert.ok(r.includes('指摘 2 件'), r);
  });
  await ok('🔴 renderReport: 計画側に届かない・一部だけ未確認・結果ファイルが読めない、はいずれも「指摘なし」にせず数える', () => {
    const unv = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p3', date: 'd', planRange: { ...PR_OK, status: 'unverified', scanned: 0, unverifiedKinds: ['FR', 'UC', 'SC', 'ADR', 'NFR'], reason: '計画側を取得できない: 404', ranges: [] } });
    assert.ok(unv.includes('- 🔴 未確認（FR / UC / SC / ADR / NFR）: 計画側を取得できない: 404'), unv);
    assert.ok(unv.includes('突合 0 種 / 5 種'), unv); assert.ok(unv.includes('指摘 1 件'), unv);
    const part = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p4', date: 'd', planRange: { ...PR_OK, scanned: 4, unverifiedKinds: ['NFR'], reason: '一致（ただし NFR を導けない）' } });
    assert.ok(part.includes('- 🔴 未確認（NFR）'), part); assert.ok(part.includes('指摘 1 件'), part);
    const missing = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p5', date: 'd', planRange: readPlanRange('/nonexistent/plan-range.json') });
    assert.ok(missing.includes('- 🔴 未確認: 突合結果 /nonexistent/plan-range.json を読めない'), missing);
    assert.ok(missing.includes('指摘 1 件'), missing);
  });
  await ok('renderReport: 宣言の不読（error）と起票の失敗を指摘に数える', () => {
    const r = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p6', date: 'd', planRange: { status: 'error', scanned: 0, expected: 5, unverifiedKinds: ['FR', 'UC', 'SC', 'ADR', 'NFR'], reason: '宣言を読めない: 節が無い', ranges: [], lagIssue: { action: 'failed', reason: 'token が無い' } } });
    assert.ok(r.includes('- 🔴 宣言を読めない（本リポジトリの欠陥）: 宣言を読めない: 節が無い'), r);
    assert.ok(r.includes('- 🔴 ずれを専用 issue へ書けなかった: token が無い'), r);
    assert.ok(!r.includes('🔴 未確認（'), 'error の週に未確認まで重ねて数えている');
    assert.ok(r.includes('指摘 2 件'), r);
  });
  await ok('renderReport: --plan-range 未指定（ローカル）は「突合していない」と書き、指摘に数えない', () => {
    const r = renderReport({ repoFindings, gh: null, ghError: 'x', runId: 'p7', date: 'd' });
    assert.ok(r.includes('- 突合していない（`--plan-range` 未指定'), r); assert.ok(r.includes('指摘 0 件'), r);
  });
  await ok('parseRevalidationDeadline: 本文・コメントの最後の期限を読む（欄の見出し形も読む）', () => {
    assert.strictEqual(parseRevalidationDeadline(['x\n再検証期限: 2026-10-01', '再検証期限：2026-10-19（次回）']), '2026-10-19');
    assert.strictEqual(parseRevalidationDeadline(['### 再検証期限（blocked のときだけ）\n\n2026-11-02', null]), '2026-11-02');
    assert.strictEqual(parseRevalidationDeadline(['### 再検証期限（blocked のときだけ）\n\n_No response_', '期限は来週']), null);
  });
  await ok('collectGithubFindings: blocked は期限が無い・過ぎたものを staleBlocked に（日数はラベル操作で戻らない）、ci-failure と古い PR を拾う', async () => {
    const comments = {
      1: [{ created_at: '2026-08-01T00:00:00Z', body: '再検証期限: 2026-09-01' }],
      2: [{ created_at: '2026-08-20T00:00:00Z', body: '再検証期限: 2026-09-20' }],
      5: [],
    };
    const api = async (url) => {
      const m = url.match(/\/issues\/(\d+)\/comments$/);
      if (m) return comments[m[1]];
      if (url.includes('/issues?')) return [
        { number: 1, title: 'overdue', labels: [{ name: 'blocked:env' }], updated_at: '2026-09-08T00:00:00Z', created_at: '2026-07-01T00:00:00Z', body: '' },
        // ラベル付与で updated_at が新しくても、日数は最後のコメント（08-20）から数える。期限が先なので挙げない
        { number: 2, title: 'fresh', labels: [{ name: 'blocked' }], updated_at: '2026-09-08T00:00:00Z', created_at: '2026-07-01T00:00:00Z', body: '' },
        { number: 3, title: 'ci', labels: [{ name: 'ci-failure' }], updated_at: '2026-09-08T00:00:00Z' },
        { number: 4, title: 'pr-as-issue', labels: [{ name: 'blocked' }], updated_at: '2026-01-01T00:00:00Z', pull_request: {} },
        { number: 5, title: 'no-deadline', labels: [{ name: 'blocked:human' }], updated_at: '2026-09-08T00:00:00Z', created_at: '2026-09-07T00:00:00Z', body: '期限は未定' },
      ];
      return [{ number: 9, title: 'old pr', updated_at: '2026-08-01T00:00:00Z' }, { number: 10, title: 'new pr', updated_at: '2026-09-08T00:00:00Z' }];
    };
    return collectGithubFindings({ repo: 'o/r', token: 't', now, staleDays: 14, api }).then((g) => {
      assert.deepStrictEqual(g.staleBlocked.map((b) => b.number), [1, 5]);
      assert.strictEqual(g.blocked.length, 3);
      const two = g.blocked.find((b) => b.number === 2);
      assert.strictEqual(two.ageDays, 20); assert.strictEqual(two.deadline, '2026-09-20'); assert.strictEqual(two.overdue, false);
      assert.strictEqual(g.blocked.find((b) => b.number === 5).ageDays, 2, 'コメントが無ければ作成日から数える');
      const r = renderReport({ repoFindings, gh: g, runId: 'r3', date: 'd' });
      assert.ok(r.includes('#5 no-deadline（blocked:human・🔴 期限なし'), r);
      assert.ok(r.includes('#2 fresh（blocked・🔴 種別なしの `blocked`・期限 2026-09-20・最後のコメントから 20 日）'), r);
      assert.ok(r.includes('#1 overdue（blocked:env・期限 2026-09-01（過ぎた）'), r);
      assert.deepStrictEqual(g.ciFailures.map((c) => c.number), [3]);
      assert.deepStrictEqual(g.stalePrs.map((p) => p.number), [9]);
    });
  });
  await ok('collectGithubFindings: token が無ければ null（呼び出し側が「取れなかった」と書く）', async () => {
    return collectGithubFindings({ repo: null, token: null }).then((g) => assert.strictEqual(g, null));
  });
  await ok('collectRepoFindings: 実データを読める（Proposed の IADR を数える）', () => {
    const f = collectRepoFindings();
    assert.ok(Array.isArray(f.proposedAdrs)); assert.ok(Array.isArray(f.staleSpecs));
  });
  console.log(`[backlog-audit] 自己試験 ${n} 件 OK。`);
}

if (require.main === module) main().catch((e) => { console.error(`[backlog-audit] ${e.stack || e}`); process.exit(1); });

module.exports = { parseFrontmatter, isStale, parseRevalidationDeadline, collectRepoFindings, collectGithubFindings, readPlanRange, planRangeFindings, renderReport, MARKER, LABEL, BLOCKED_LABELS };
