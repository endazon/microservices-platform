#!/usr/bin/env node
'use strict';
/*
 * check-doc-updated.js
 * NFR / issue #649: 本文を変えたのに frontmatter の `updated:` が古いままの文書を止める。
 *
 * **同型の事故が 2 回起きたので入れた**（`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。
 * どちらも PR #648 のレビューで出た —— 2 巡目で 9 件、4 巡目で `IADR-0030` が 1 件。原因は同じで、
 * 「本文を編集した」と「frontmatter を更新した」が**別操作**であり、**前者だけでも既存の機械検査
 * （check-doc-links / check-cross-repo-refs / check-plan-id-qualification / check-adr-numbering）は
 * 全て通る**ことにある。人間・AI どちらのレビューでも毎回は拾えない。
 *
 * ── 判定式（**3 案を PR #648 の変更文書 12 件へ当ててから決めた**。作業仕様書 #649 に記録）
 *   案 A `updated:` が base から変わっていない          → 2 件（**誤検知 1**。同日中の再編集は据え置きが正しい）
 *   案 B `updated:` < その文書を最後に変えたコミットの日付 → 10 件（**日付境界を跨ぐと全滅**）
 *   案 C `updated:` < base 以降の最初のコミットの日付     → **1 件で正確。これを採る**
 *
 * **案 A・B を実測せずに入れていたら、誤検知で早々に無効化されていた。** 検査器は「思いついた式」
 * ではなく**手元の実例へ当てて誤検知を数えてから**入れる。
 *
 * ── 本文が変わったかの判定
 * **diff のハンク解析ではなく、frontmatter を除いた本文どうしの比較**で行う。ハンクの行番号と
 * frontmatter の範囲を突き合わせる方式は `git diff` の出力形式に依存し、frontmatter が壊れた
 * ファイルで誤判定する。本文比較なら形式に依存しない。
 *
 * ── 検出する型（**件数はここに書かない**。列挙そのものが単一情報源。[[IADR-0141]]）
 *   stale        : 本文が変わったのに `updated:` が base 以降の最初のコミット日より古い
 *   invalid-date : `updated:` があるが `YYYY-MM-DD` として読めない（据え置きと同じく静かに腐る）
 *
 * ── **この検査器が検出しないこと**（網羅ではない。開示しておく）
 *   - `created:` の誤り。作成日は後から動かないため対象外。
 *   - **`docs/templates/` 配下**。雛形の `updated:` は穴（`<YYYY-MM-DD>`）であり、埋まっていないのが
 *     正しい。本文を編集するたびに落ちると検査器が外されるので、丸ごと対象外にする。
 *   - **`updated:` を持たない文書**（`README.md` 等）。notice として件数だけ出す。
 *     「日付を持て」という規約は別の話であり、本検査器の射程ではない。
 *   - **未来日**。`updated: 2099-01-01` は本式を通る。日付の妥当性そのものは見ない。
 *   - **本文を変えずに `updated:` だけ進めた**場合。害が無いため通す。
 *   - `planning/` 配下（別リポジトリ）と、`docs/` 外の Markdown。
 *   - **shallow clone**（merge-base が引けない）。**黙って通さず notice を出して skip する。**
 *
 * ── #647 との違い
 * #647 は「**文書の主張と実装の一致**」（openapi.yaml の宣言ロール vs RequireAuthorization）、
 * 本件は「**文書を触ったのに更新日が動いていない**」を見る。射程が交わらないので束ねない。
 */

const { execFileSync } = require('node:child_process');
const path = require('node:path');

const DEFAULT_BASE = 'origin/develop';
const TARGET_PREFIX = 'docs/';
// **テンプレートは対象外。** `docs/templates/*.md` の `updated:` は雛形の穴（`<YYYY-MM-DD>`）であり、
// 埋まっていないのが正しい状態である。本文（項目追加・誤字直し）を編集するたびに `invalid-date` で
// 落ちると、**検査器が邪魔者になって外される**（PR #652 レビュー 1 巡目が 17 件で実測した）。
const TEMPLATE_PREFIX = 'docs/templates/';
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

function git(args, cwd) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function gitOrNull(args, cwd) {
  try {
    return git(args, cwd);
  } catch {
    return null;
  }
}

// frontmatter（先頭の `---` … `---`）を取り除いた本文を返す。
// frontmatter が無い / 閉じていない場合は全文を本文として扱う（＝本文比較は成立する）。
function stripFrontmatter(text) {
  if (text === null || text === undefined) return null;
  const normalized = text.replace(/\r\n/g, '\n');
  if (!normalized.startsWith('---\n')) return normalized;
  const end = normalized.indexOf('\n---', 3);
  if (end === -1) return normalized;
  const after = normalized.indexOf('\n', end + 1);
  return after === -1 ? '' : normalized.slice(after + 1);
}

// frontmatter から `updated:` の値を取り出す。無ければ null。
function readUpdated(text) {
  if (text === null || text === undefined) return null;
  const normalized = text.replace(/\r\n/g, '\n');
  if (!normalized.startsWith('---\n')) return null;
  const end = normalized.indexOf('\n---', 3);
  if (end === -1) return null;
  const front = normalized.slice(4, end + 1);
  for (const line of front.split('\n')) {
    const m = /^updated:\s*(.*?)\s*$/.exec(line);
    if (m) return m[1].replace(/^["']|["']$/g, '');
  }
  return null;
}

/**
 * 純関数の判定本体。git に触らないのでそのまま試験できる。
 * files: [{ path, baseText|null, headText|null }]  headText === null は削除。
 * firstCommitDate: 'YYYY-MM-DD'（base 以降の最初のコミットの日付）
 */
function findViolations(files, firstCommitDate) {
  const violations = [];
  const skippedNoUpdated = [];

  for (const file of files) {
    if (file.headText === null || file.headText === undefined) continue; // 削除
    if (file.path.startsWith(TEMPLATE_PREFIX)) continue; // 雛形の穴は埋まっていないのが正しい
    const headBody = stripFrontmatter(file.headText);
    const baseBody = file.baseText === null || file.baseText === undefined
      ? null // 新規追加。本文は「全部が変更」とみなす
      : stripFrontmatter(file.baseText);
    if (baseBody !== null && baseBody === headBody) continue; // 本文は変わっていない

    const updated = readUpdated(file.headText);
    if (updated === null) {
      skippedNoUpdated.push(file.path);
      continue;
    }
    if (!DATE_RE.test(updated)) {
      violations.push({ path: file.path, kind: 'invalid-date', updated });
      continue;
    }
    // 案 C: base 以降の最初のコミット日より古ければ据え置き。
    // **同日は通す** —— 同日中の再編集で据え置きが正しい場合があるため（案 A の誤検知）。
    if (updated < firstCommitDate) {
      violations.push({ path: file.path, kind: 'stale', updated, expected: firstCommitDate });
    }
  }

  return { violations, skippedNoUpdated };
}

function formatReport({ violations, skippedNoUpdated }, firstCommitDate) {
  const lines = [];
  lines.push(`[check-doc-updated] 本文を変えたのに updated: が古い文書 ${violations.length} 件を検出しました:`);
  lines.push('');
  for (const v of violations) {
    if (v.kind === 'stale') {
      lines.push(`  ${v.path}`);
      lines.push(`    updated: ${v.updated}  →  ${v.expected} 以降へ（本 PR の最初のコミットは ${firstCommitDate}）`);
    } else {
      lines.push(`  ${v.path}`);
      lines.push(`    updated: ${v.updated} は YYYY-MM-DD として読めません`);
    }
  }
  lines.push('');
  lines.push('規約（CLAUDE.md / docs/README.md）: 内容を変えた文書は frontmatter の updated: も揃える。');
  lines.push('「本文を編集した」と「frontmatter を更新した」は別操作であり、前者だけでも他の検査は通る。');
  if (skippedNoUpdated.length > 0) {
    lines.push('');
    lines.push(`notice: updated: を持たないため対象外 ${skippedNoUpdated.length} 件（README 等）。`);
  }
  return lines.join('\n');
}

// base 以降の最初のコミットの日付。コミットが無ければ null。
function firstCommitDateSince(mergeBase, cwd) {
  const out = gitOrNull(['log', '--format=%ad', '--date=short', `${mergeBase}..HEAD`], cwd);
  if (out === null) return null;
  const dates = out.split('\n').filter(Boolean);
  return dates.length === 0 ? null : dates[dates.length - 1];
}

function collectFiles(mergeBase, cwd) {
  const out = git(['diff', '--name-only', `${mergeBase}...HEAD`], cwd);
  const paths = out
    .split('\n')
    .map((p) => p.trim())
    .filter((p) => p.startsWith(TARGET_PREFIX) && p.endsWith('.md'));
  return paths.map((p) => ({
    path: p,
    baseText: gitOrNull(['show', `${mergeBase}:${p}`], cwd),
    headText: gitOrNull(['show', `HEAD:${p}`], cwd),
  }));
}

function resolveBaseRef(argv) {
  const idx = argv.indexOf('--base');
  if (idx !== -1 && argv[idx + 1]) return argv[idx + 1];
  if (process.env.GITHUB_BASE_REF) return `origin/${process.env.GITHUB_BASE_REF}`;
  return DEFAULT_BASE;
}

function main(argv = process.argv.slice(2), cwd = path.resolve(__dirname, '..')) {
  const baseRef = resolveBaseRef(argv);
  const mergeBaseOut = gitOrNull(['merge-base', baseRef, 'HEAD'], cwd);
  if (mergeBaseOut === null) {
    // shallow clone・base 未取得。**黙って通さない。**
    console.log(
      `[check-doc-updated] skip: ${baseRef} との merge-base を引けませんでした` +
        '（shallow clone か base 未取得）。checkout の fetch-depth: 0 が要ります。'
    );
    return 0;
  }
  const mergeBase = mergeBaseOut.trim();
  const firstDate = firstCommitDateSince(mergeBase, cwd);
  if (firstDate === null) {
    console.log('[check-doc-updated] OK: base 以降にコミットがありません。');
    return 0;
  }

  const files = collectFiles(mergeBase, cwd);
  const result = findViolations(files, firstDate);
  if (result.violations.length === 0) {
    const note =
      result.skippedNoUpdated.length > 0
        ? `（updated: を持たない ${result.skippedNoUpdated.length} 件は対象外）`
        : '';
    console.log(
      `[check-doc-updated] OK: 変更された docs/ の Markdown ${files.length} 件に updated: の据え置きはありません。${note}`
    );
    return 0;
  }
  console.error(formatReport(result, firstDate));
  return 1;
}

if (require.main === module) process.exit(main());

module.exports = {
  main,
  findViolations,
  formatReport,
  stripFrontmatter,
  readUpdated,
  DATE_RE,
  TEMPLATE_PREFIX,
};
