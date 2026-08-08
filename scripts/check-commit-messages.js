#!/usr/bin/env node
'use strict';
/*
 * check-commit-messages.js
 * コミットメッセージ規約（`種別(起点ID): 要約`）の機械チェック。
 * 外部依存ゼロ（Node 標準モジュールのみ）。CI（PR 単位）での再発防止を目的とする。
 *
 * 方針:
 *   - 既存履歴は書き換えない。検査対象は「PR で追加されるコミット」= base..HEAD の範囲のみ。
 *   - dependabot 等の自動コミット・マージコミット・自動生成コミットは除外する。
 *   - 規約違反があれば非ゼロ終了し、CI を失敗させる。
 *
 * 検査範囲の決定（優先順）:
 *   1) 引数 `--range <base>..<head>`
 *   2) 環境変数 `COMMIT_RANGE`
 *   3) PR 環境: `origin/$GITHUB_BASE_REF..HEAD`（GitHub Actions pull_request）
 *   4) フォールバック: `origin/develop..HEAD`（develop 既定運用）→ 取得不可なら `HEAD~20..HEAD`
 *
 * 使い方:
 *   node scripts/check-commit-messages.js [--range base..head] [--verbose]
 */
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');
// 【固有デルタ】本リポジトリにしか存在しない検査器（IADR-0115 の固有デルタ種 3）。
// NFR / #507 / IADR-0140: 他リポジトリ issue 番号の修飾（短縮形への統一・列挙形の修飾漏れ）を
// **コミット件名 / 本文 / PR タイトル**でも検査する。`.github/workflows/` は編集できないため、
// 既にワークフローから呼ばれている本スクリプトへ相乗りするのが CI へ載せる唯一の経路である。
// PR #561 は件名・本文・PR タイトルの 3 面すべてで列挙形の修飾漏れを犯し、書式検査を素通りした。
const { findViolations: findCrossRepoRefViolations } = require('./check-cross-repo-refs.js');

// 規約導入前の既存コミットの恒久適用除外リスト（force push 禁止のため件名を書き換えられない）。
const ALLOWLIST_PATH = path.join(__dirname, 'commit-allowlist.json');

// gen-changelog.js の TYPE_ORDER と一致させること。
const VALID_TYPES = ['feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'style', 'chore'];

// 起点 ID（スコープ）の省略を許す種別。ツールチェーン・雑多な housekeeping は計画 ID に
// 紐づかないことがあるため（traceability.md「雑多な変更は理由を明記する」）。それ以外の
// 内容変更（feat/fix/perf/refactor/docs/test）は起点 ID を必須とする（再発防止）。
const TYPES_ALLOW_NO_SCOPE = ['chore', 'style', 'build', 'ci'];

// 起点 ID の書式（.claude/rules/traceability.md と一致）。
//   FR-xx / NFR / UC-xx / SC-xx / ADR-xxxx / IADR-xxxx / P0..P3（フェーズ骨格）
const ID_PATTERN = /^(FR-\d+|NFR(?:-\w+)?|UC-\d+|SC-\d+|ADR-\d{3,4}|IADR-\d{3,4}|P[0-3])$/;

// 除外する自動コミットの著者（メール/名前に部分一致）。
//   dependabot 等の自動コミットは規約対象外。
const BOT_AUTHORS = [
  'dependabot[bot]',
  'dependabot',
  'github-actions[bot]',
  'github-actions',
  'renovate[bot]',
  'renovate',
  'web-flow', // GitHub UI からのマージコミット署名
];

const US = '\x1f'; // Unit Separator
const RS = '\x1e'; // Record Separator

function parseArgs(argv) {
  const a = { range: null, verbose: false, title: null, author: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--range') a.range = argv[++i];
    else if (argv[i].startsWith('--range=')) a.range = argv[i].slice('--range='.length);
    else if (argv[i] === '--title') a.title = argv[++i];
    else if (argv[i].startsWith('--title=')) a.title = argv[i].slice('--title='.length);
    else if (argv[i] === '--author') a.author = argv[++i];
    else if (argv[i].startsWith('--author=')) a.author = argv[i].slice('--author='.length);
    else if (argv[i] === '--verbose' || argv[i] === '-v') a.verbose = true;
  }
  return a;
}

function tryGit(args) {
  try {
    return execSync(`git ${args}`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch (e) {
    return null;
  }
}

function revExists(ref) {
  return tryGit(`rev-parse --verify --quiet ${ref}`) !== null;
}

/** 検査範囲を決定する。決められない場合は null（= 全コミットは検査しない）を返す。 */
function resolveRange(explicit) {
  if (explicit) return explicit;
  if (process.env.COMMIT_RANGE) return process.env.COMMIT_RANGE;

  // GitHub Actions の pull_request コンテキスト
  const baseRef = process.env.GITHUB_BASE_REF;
  if (baseRef) {
    if (revExists(`origin/${baseRef}`)) return `origin/${baseRef}..HEAD`;
    if (revExists(baseRef)) return `${baseRef}..HEAD`;
  }

  // develop 既定運用のフォールバック
  if (revExists('origin/develop')) return 'origin/develop..HEAD';
  if (revExists('develop')) return 'develop..HEAD';

  // 最終フォールバック（浅いクローン等）
  if (revExists('HEAD~20')) return 'HEAD~20..HEAD';
  return 'HEAD';
}

/** 範囲のコミットを {hash, subject, author, email, body} で返す（マージコミットは除外）。 */
function collectCommits(range) {
  // body（%b）は最後に置く。複数行を含むが、レコード境界は RS なのでフィールド分割は壊れない。
  // #507: 列挙形の修飾漏れは**本文**にも出る（PR #561 の実例）ため件名だけでは足りない。
  const fmt = `%H${US}%s${US}%an${US}%ae${US}%b`;
  const raw = tryGit(`log ${range} --no-merges --pretty=format:${fmt}${RS}`);
  if (raw === null) {
    process.stderr.write(`検査範囲を git log できなかった: ${range}\n`);
    return null;
  }
  if (!raw.trim()) return [];
  return raw
    .split(RS)
    .map((r) => r.replace(/^\n/, '').trim())
    .filter(Boolean)
    .map((line) => {
      const [hash, subject = '', author = '', email = '', body = ''] = line.split(US);
      return { hash, subject, author, email, body };
    });
}

function isBot(c) {
  const hay = `${c.author} ${c.email}`.toLowerCase();
  return BOT_AUTHORS.some((b) => hay.includes(b.toLowerCase()));
}

// #524: PR の作成者ログイン名が「規約対象外の自動 PR」かを判定する（BOT_AUTHORS が単一情報源）。
// ワークフロー側の `if: github.event.pull_request.user.type != 'Bot'` は **App が代行した PR まで
// 除外してしまう**（`claude[bot]` は user.type == 'Bot'）ため、判定をここへ寄せて名前で除外する。
// GitHub App が人の代わりに書いた PR は**検査対象に残す**——スカッシュ後件名は develop に恒久的に
// 残り、force push 禁止のため事後修正できないため（pr-title.yml が「最後の砦」と自称する所以）。
// 照合は **完全一致**（大小文字は無視）である。`isBot`（コミット著者）が部分一致なのは
// 突合先が "名前 <メール>" という連結文字列だからであって、こちらの突合先は**ログイン名そのもの**。
// 部分一致にすると `the-renovate-guy` のような人間のログインまで「bot」と見なして
// 最後の砦を無検査で素通りさせる（PR #527 のレビュー指摘）。除外は狭く取る。
function isBotAuthorName(login) {
  const name = String(login == null ? '' : login).trim().toLowerCase();
  if (!name) return false;
  return BOT_AUTHORS.some((b) => name === b.toLowerCase());
}

/** 短縮/完全 SHA を前方一致で照合する（changelog-overrides.json と同方針）。 */
function hashMatches(a, b) {
  if (!a || !b) return false;
  const x = String(a).toLowerCase();
  const y = String(b).toLowerCase();
  return x.startsWith(y) || y.startsWith(x);
}

/**
 * 恒久適用除外リストを読み込む。未配置・不正 JSON でも CI をブロックしない（fail-open）。
 * 返り値は {hash, reason} の配列。
 */
function loadAllowlist(file = ALLOWLIST_PATH) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch (e) {
    if (e && e.code !== 'ENOENT') {
      process.stderr.write(`commit-allowlist.json を読めなかった（無視）: ${e.message}\n`);
    }
    return [];
  }
  try {
    const json = JSON.parse(raw);
    const list = Array.isArray(json.allow) ? json.allow : [];
    return list.filter((e) => e && typeof e.hash === 'string' && e.hash.trim());
  } catch (e) {
    process.stderr.write(`commit-allowlist.json が不正な JSON（無視）: ${e.message}\n`);
    return [];
  }
}

/** commit hash が allowlist に含まれれば該当エントリを、無ければ null を返す。 */
function findAllowlisted(hash, allowlist) {
  return allowlist.find((e) => hashMatches(hash, e.hash)) || null;
}

function isSkippable(subject) {
  // 自動生成コミット・リバートは規約対象外。
  if (/\[skip ci\]/i.test(subject)) return true;
  if (/^Revert\s+"/.test(subject)) return true;
  // GitHub の PR マージ／スカッシュ既定件名（末尾の (#123)）は許容。件名本体で判定する。
  return false;
}

/**
 * 指定ディレクトリのファイル名から実在する ADR/IADR 番号の集合を返す。ディレクトリを
 * 読めない環境（チェックアウト無しの単独実行・planning submodule 未 populate 等）では
 * null を返し、実在性検査をスキップする（fail-open。check-doc-links.js と同じ扱い）。
 *
 * 背景: 並行実装では ADR 番号の採番衝突が起こり、後発が改番を強いられる。改番はファイル名・
 * 本文・索引・仕様書に及ぶ一方、**PR タイトル（= スカッシュ後のコミット件名）だけが人手の
 * 追随に依存する**ため、実体と別内容の ADR を名乗る件名が統合ブランチへ混入しやすい。
 * 書式チェック（ID_PATTERN）だけではこれを検知できない。
 */
function loadExistingAdrIds(prefix, dir) {
  try {
    const ids = new Set();
    const re = new RegExp(`^${prefix}-(\\d{3,4})[._-]`);
    for (const f of fs.readdirSync(dir)) {
      const m = f.match(re);
      if (m) ids.add(`${prefix}-${m[1]}`);
    }
    return ids;
  } catch (e) {
    return null;
  }
}

/** 実装 ADR（本リポ `docs/adr/`）の実在番号集合。読めなければ null。 */
function loadExistingIadrIds(dir = path.join(__dirname, '..', 'docs', 'adr')) {
  return loadExistingAdrIds('IADR', dir);
}

// 【置換点】本リポジトリが主に実装する計画プロジェクト名（`planning/projects/<name>/`）。
// 裸（無修飾）の `ADR-xxxx` はこの名前空間を指す（.claude/rules/traceability.md の規約）。
// 環境変数 PLAN_PROJECT で上書きできる（テスト・複数構成の検証用）。
const PLAN_PROJECT = process.env.PLAN_PROJECT || 'microservices-platform';

/**
 * 計画 ADR（planning submodule の `projects/<name>/07_adr/`）の実在番号集合。
 * submodule 未 populate なら null（skip）。
 *
 * **自プロジェクトの名前空間に限定する**こと。計画 ID はプロジェクトごとに独立採番のため
 * 番号帯が丸ごと重複する。全プロジェクトの和集合を実在集合にすると、他プロジェクトにしか
 * 存在しない ID まで「実在」として受理され、本検査の目的（改番時に PR タイトルの追随が
 * 漏れて実体と別内容の ADR を名乗る事故の検出）が働かなくなる。
 * 自プロジェクトを解決できない構成では、従来どおり全走査へ退避する（fail-open）。
 */
function loadExistingPlanAdrIds(
  projectsDir = path.join(__dirname, '..', 'planning', 'projects'),
  project = PLAN_PROJECT
) {
  let entries;
  try {
    entries = fs.readdirSync(projectsDir);
  } catch (e) {
    return null;
  }
  // 自プロジェクトの名前空間だけを実在集合とする（規約どおりの厳密な検査）。
  const own = loadExistingAdrIds('ADR', path.join(projectsDir, project, '07_adr'));
  if (own && own.size > 0) return own;

  // 自プロジェクト名を解決できない（PLAN_PROJECT 未設定・単一プロジェクト構成等）場合は
  // 全走査へ退避する。検査が甘くなるが、CI をローカル環境差で落とさない。
  //
  // ただし**複数プロジェクトが見えている構成では、退避した時点で本検査は実質無効**になる
  // （他プロジェクトにしか無い ADR 番号まで「実在」として受理する）。配布既定の PLAN_PROJECT は
  // プレースホルダであり、設定を忘れると黙ってこの状態に落ちる。架空の ID（ADR-9999 等）は
  // 依然として検出されるため、利用者からは検査が効いているように見えてしまう。
  // 「ジョブは成功するのに実は効いていない」状態を作らないよう、退避したことを警告で可視化する。
  // 終了コードは変えない（既存リポジトリの CI を新たに落とさない）。
  if (entries.length > 1) {
    warn(
      `PLAN_PROJECT="${project}" に対応する ${project}/07_adr/ が見つからないため、\n` +
        `計画 ADR の実在性検査を全プロジェクト走査へ退避した（他プロジェクトの ADR 番号も\n` +
        `「実在」として受理される）。scripts/check-commit-messages.js の PLAN_PROJECT を\n` +
        `自プロジェクト名へ設定すること（impl-handoff-kit/HOWTO.md Part B-5）。`,
      { stream: process.stderr, prefix: 'warning: ' }
    );
  }

  const ids = new Set();
  let found = false;
  for (const name of entries) {
    const got = loadExistingAdrIds('ADR', path.join(projectsDir, name, '07_adr'));
    if (got) {
      found = true;
      for (const id of got) ids.add(id);
    }
  }
  return found ? ids : null;
}

/**
 * 計画レンジ（`FR-xx` / `UC-xx` / `SC-xx`）の実在集合を返す（NFR / #579）。
 *
 * **なぜ必要か（#579 の実測）**: 実在性検査は `IADR` と `ADR` にしか実装されておらず、
 * `feat(SC-99)` / `feat(FR-77)` / `feat(UC-88)` はいずれも **exit 0 で受理**されていた。
 * これらはスカッシュ後件名として develop の恒久履歴へ載り、force push 禁止で事後修正できない。
 *
 * **レンジの正は `.claude/rules/traceability.md`「起点 ID の種別」節**であり、
 * そのパーサは既に `check-test-traceability.js`（#472）に在る。**同じ事実を 2 本のパーサで
 * 持たない** —— 片方だけ直したとき、どちらが正か決められなくなる。
 *
 * **fail の向きを 2 つに分ける**（「見つからないから素通り」を一律には採らない）:
 *   - **モジュールが無い**（キット派生リポで `check-test-traceability.js` を持たない構成）
 *     → `null` を返して**当該検査をスキップ**する（呼び出し側が notice で可視化する）。
 *   - **モジュールは在るが節をパースできない** → **例外を投げる**。本リポジトリでは
 *     `.claude/rules/traceability.md` は追跡下の必ず読めるファイルであり、読めない／拾えないのは
 *     環境差ではなく**規約側の破壊**（節の改名・書式変更）である。ここを黙って通すと、
 *     レンジの単一情報源が壊れたまま「違反 0 件」で緑になる。
 */
function loadExistingPlanIds() {
  let traceability;
  try {
    traceability = require('./check-test-traceability.js');
  } catch (e) {
    return null; // キット派生リポで当該検査器を持たない構成。スキップする。
  }
  if (typeof traceability.readPlanIds !== 'function') return null;
  // 節が壊れていれば readPlanIds が投げる。**握り潰さない**（上記の 2 つ目の向き）。
  return new Set(traceability.readPlanIds());
}

/**
 * 件名スコープ中の `IADR-xxxx` / `ADR-xxxx` / `FR-xx` / `UC-xx` / `SC-xx` が実在するか検証し、
 * 違反理由の配列を返す。
 * 各集合が null（読めない環境・当該検査器を持たない構成）の場合は該当種別の検査をスキップする。
 * 書式違反の検出は validateSubject が担う（本関数は書式適合を前提に実在のみ見る）。
 */
function validateIdExistence(subject, iadrIds, planAdrIds, planIds) {
  const s = String(subject == null ? '' : subject).replace(/\s*\(#\d+\)\s*$/, '').trim();
  const m = s.match(/^(\w+)(?:\(([^)]*)\))?(!)?:\s+(.+)$/);
  if (!m || m[2] === undefined) return [];
  const reasons = [];
  for (const id of m[2].split(',').map((x) => x.trim()).filter(Boolean)) {
    if (iadrIds && /^IADR-\d{3,4}$/.test(id) && !iadrIds.has(id)) {
      reasons.push(`起点 ID "${id}" が docs/adr/ に実在しない（採番衝突・改番後のタイトル未追随の可能性）`);
    } else if (planAdrIds && /^ADR-\d{3,4}$/.test(id) && !planAdrIds.has(id)) {
      reasons.push(`起点 ID "${id}" が planning の 07_adr/ に実在しない（誤記・廃止の可能性）`);
    } else if (planIds && /^(FR|UC|SC)-\d+$/.test(id) && !planIds.has(normalizePlanId(id))) {
      // #579: ここが無い間、`feat(SC-99)` は exit 0 で恒久履歴へ載れた。
      reasons.push(
        `起点 ID "${id}" が計画レンジに実在しない` +
          `（.claude/rules/traceability.md「起点 ID の種別」節が正。誤記・別プロジェクトの ID の可能性）`
      );
    }
  }
  return reasons;
}

/**
 * 計画レンジ側はゼロ埋め 2 桁（`FR-01`）で持つが、規約は `FR-012` のような表記も書式として許す
 * （`ID_PATTERN` は `FR-\d+`）。**桁数の違いで「実在しない」と誤検出しない**よう、比較の前に
 * 数値へ正規化して突き合わせる。
 */
function normalizePlanId(id) {
  const m = String(id).match(/^(FR|UC|SC)-(\d+)$/);
  if (!m) return id;
  return `${m[1]}-${String(Number(m[2])).padStart(2, '0')}`;
}

/**
 * 他リポジトリ issue 番号の修飾違反（#507 / IADR-0140）を違反理由の配列で返す。
 * `validateSubject`（書式の単一情報源）とは**別関数**に保つ。書式規約と参照表記の規約は
 * 別物であり、allowlist の「規約に準拠した件名を無意味に除外していない」判定が
 * 表記の是非で揺れないようにするためである。
 *
 * コミットメッセージは Markdown ではない（GitHub はバッククォートをコードスパンとして
 * 描画せず、`#NNN` の自動リンクは効く）ため、コードスパン除外を**しない**モードで見る。
 */
// ラベルは kind と 1:1 で対応させる。分岐が足りないと、CI ログを読んで直す人が
// 実際には存在しない「列挙」を探すことになる（#507 の AI レビュー指摘）。
// **これは 2 度漏れた**（#507 で 1 度、型 4 を足した #590 でもう 1 度）。よって
// `scripts.repo.test.js` が `check-cross-repo-refs.js` の `kind:` リテラルを静的に走査し、
// **全 kind がここに在ること**を機械で固定する。3 度目は検査で止まる。
const CROSS_REPO_REF_LABELS = {
  long: '他リポジトリ名の長い表記',
  enum: '列挙形の修飾漏れ（裸の #NNN が本リポの issue へ誤リンクする）',
  spaced: '空白区切りの修飾（裸の #NNN が本リポの issue へ誤リンクする）',
  owner: 'フルパス形式の owner 誤り（存在しない owner への死んだリンクになる）',
  fence: '閉じないコードフェンス（以降の行が検査から漏れる）',
};

function crossRepoRefReasons(text, where) {
  const s = String(text == null ? '' : text);
  if (!s.trim()) return [];
  return findCrossRepoRefViolations(s, { markdown: false }).map((v) => {
    const label = CROSS_REPO_REF_LABELS[v.kind] || `未知の違反種別 ${v.kind}`;
    return `${where}の ${label}: "${v.matched}" → "${v.suggestion}"（.claude/rules/traceability.md）`;
  });
}

/** 件名を検証し、違反理由の配列を返す（空なら合格）。 */
function validateSubject(subject) {
  const reasons = [];
  // 末尾の PR 番号 " (#123)" は除去して判定する。
  const s = subject.replace(/\s*\(#\d+\)\s*$/, '').trim();

  const m = s.match(/^(\w+)(?:\(([^)]*)\))?(!)?:\s+(.+)$/);
  if (!m) {
    reasons.push('形式が `種別(起点ID): 要約` に一致しない');
    return reasons;
  }
  const [, type, scope, , desc] = m;

  const lowerType = type.toLowerCase();
  if (!VALID_TYPES.includes(lowerType)) {
    reasons.push(`未知の種別 "${type}"（許可: ${VALID_TYPES.join(' / ')}）`);
  }
  if (scope === undefined) {
    // スコープ（起点 ID）が無い。内容変更の種別では必須（抜け穴防止）。
    if (!TYPES_ALLOW_NO_SCOPE.includes(lowerType)) {
      reasons.push(
        `起点 ID が無い（${lowerType} は必須）。例: ${lowerType}(FR-08): ...。` +
          `ID が本当に無い雑多な変更は ${TYPES_ALLOW_NO_SCOPE.join(' / ')} 種別を用いる`
      );
    }
  } else {
    const ids = scope.split(',').map((x) => x.trim()).filter(Boolean);
    if (ids.length === 0) {
      reasons.push('スコープ () が空');
    }
    for (const id of ids) {
      if (!ID_PATTERN.test(id)) {
        reasons.push(`起点 ID "${id}" が書式に一致しない（例: FR-08 / UC-03 / NFR / ADR-0001 / IADR-0001 / P0）`);
      }
    }
  }
  if (!desc || !desc.trim()) {
    reasons.push('要約が空');
  }
  return reasons;
}

/**
 * 単一件名（PR タイトル = スカッシュ後件名の由来）を検査する（再発防止）。
 * git を使わず、渡された 1 件名のみを規約に照合する。Revert / [skip ci] はスキップ扱い。
 * 合格・スキップ時 0、違反時 1 を返す。
 */
function checkSingleTitle(title, author) {
  const subject = String(title == null ? '' : title).trim();
  process.stdout.write(`PR タイトル（スカッシュ後件名）チェック: "${subject}"\n`);

  // #524: 除外は **作成者の名前**で行う（`user.type == 'Bot'` ではない）。判定は BOT_AUTHORS の
  // 単一情報源を使い、ワークフロー側で規約を二重実装しない。
  if (isBotAuthorName(author)) {
    process.stdout.write(`  skip(bot)    作成者 ${author} は規約対象外（BOT_AUTHORS）\n`);
    return 0;
  }

  if (!subject) {
    // タイトル未取得（イベント外実行等）。CI をブロックしない（fail-open）。
    process.stderr.write('PR タイトルが空のため検査をスキップする。\n');
    return 0;
  }
  if (isSkippable(subject)) {
    process.stdout.write('  skip(auto)   Revert / [skip ci] は規約対象外\n');
    return 0;
  }

  const reasons = validateSubject(subject)
    .concat(
      validateIdExistence(
        subject,
        loadExistingIadrIds(),
        loadExistingPlanAdrIds(),
        loadExistingPlanIds()
      )
    )
    // #507: PR タイトルはスカッシュ後件名として develop に恒久的に残る。裸の #NNN を含む
    // 列挙が入ると事後修正できない（force push 禁止）ため、ここで止める。
    .concat(crossRepoRefReasons(subject, 'PR タイトル'));
  if (reasons.length) {
    process.stderr.write('\n✗ PR タイトルが規約違反:\n');
    process.stderr.write(`  ${subject}\n`);
    for (const r of reasons) process.stderr.write(`      - ${r}\n`);
    process.stderr.write(
      '\nスカッシュマージ既定件名は「PR タイトル + (#番号)」。PR タイトルを規約 ' +
        '`種別(起点ID): 要約` に合わせること（詳細は .claude/rules/traceability.md）。\n'
    );
    return 1;
  }
  process.stdout.write('✓ PR タイトルが規約に適合\n');
  return 0;
}

function main() {
  const args = parseArgs(process.argv.slice(2));

  // 単一件名モード（PR タイトル検査）。git リポジトリ内外を問わず動作する。
  const title = args.title != null ? args.title : process.env.PR_TITLE;
  if (title != null) {
    // 作成者は PR_AUTHOR（ワークフローが github.event.pull_request.user.login を渡す）。
    const author = args.author != null ? args.author : process.env.PR_AUTHOR;
    process.exit(checkSingleTitle(title, author));
  }

  if (tryGit('rev-parse --is-inside-work-tree') !== 'true') {
    process.stderr.write('git リポジトリではないため検査をスキップする。\n');
    process.exit(0);
  }

  const range = resolveRange(args.range);
  const commits = collectCommits(range);
  if (commits === null) {
    // 範囲解決に失敗（浅いクローン等）。CI をブロックしないため警告終了。
    process.stderr.write('検査範囲を特定できなかったため、コミット規約チェックをスキップする。\n');
    process.exit(0);
  }

  const allowlist = loadAllowlist();
  const iadrIds = loadExistingIadrIds();
  const planAdrIds = loadExistingPlanAdrIds();
  const planIds = loadExistingPlanIds();
  // 検査を skip したことは notice で可視化する（issue #139）。素の stderr 行は緑ジョブの
  // ログに埋もれて読まれず、「検査していない範囲があること」が CI の UI から読み取れない。
  // 終了コードは変えない（fail-open。ローカル環境差で CI を落とさない）。
  // 注: notice はここ（実行時の呼び出し側）でのみ出す。loadExisting* の内部に置くと、
  // 未 populate を模したテストのフィクスチャが本物のアノテーションを漏らす（#140 と同型）。
  if (!iadrIds) {
    notice('docs/adr/ を読めないため IADR 実在性チェックをスキップした（この範囲は検査されていない）');
  }
  if (!planAdrIds) {
    notice(
      'planning submodule が未 populate のため計画 ADR 実在性チェックをスキップした' +
        '（この範囲は検査されていない。実効しているのは IADR 検査のみである）。' +
        'PR 段階で検査するには checkout に submodules とトークンを付けること'
    );
  }
  if (!planIds) {
    // #579: モジュールを持たない構成（キット派生リポ）でのみここへ来る。節が壊れている場合は
    // loadExistingPlanIds が投げるので、ここは「持っていない」の意味しかない。
    notice(
      'check-test-traceability.js を持たない構成のため FR / UC / SC の実在性チェックをスキップした' +
        '（この範囲は検査されていない）'
    );
  }

  process.stdout.write(`コミット規約チェック: 範囲 ${range}（${commits.length} 件）\n`);

  const violations = [];
  let skipped = 0;
  for (const c of commits) {
    const short = c.hash.slice(0, 8);
    if (isBot(c)) {
      if (args.verbose) process.stdout.write(`  skip(bot)    ${short} ${c.subject}\n`);
      skipped++;
      continue;
    }
    if (isSkippable(c.subject)) {
      if (args.verbose) process.stdout.write(`  skip(auto)   ${short} ${c.subject}\n`);
      skipped++;
      continue;
    }
    // 規約導入前の既存コミットは恒久適用除外（監査のため常に表示する）。
    const allowed = findAllowlisted(c.hash, allowlist);
    if (allowed) {
      process.stdout.write(`  skip(allowlist) ${short} ${c.subject}\n`);
      if (allowed.reason) process.stdout.write(`      ↳ ${allowed.reason}\n`);
      skipped++;
      continue;
    }
    const reasons = validateSubject(c.subject)
      // #579 / #612 レビュー 🔴: **ここに planIds を渡し忘れていた。**
      // `checkSingleTitle`（--title）へは渡していたので `--title` の変異試験は通り、
      // 「検査が効いている」と誤って結論した。`ci.yml` の commit-messages ジョブが実行するのは
      // **こちら（レンジモード）**であり、そこでは FR/UC/SC 実在性が無効のままだった。
      // **同じ型（呼び出し口を 1 つだけ配線する）はこのリポジトリで 3 度目である。**
      // 下の scripts.repo.test.js が実バイナリでレンジモードを通し、再発を止める。
      .concat(validateIdExistence(c.subject, iadrIds, planAdrIds, planIds))
      // #507: 他リポジトリ issue 番号の修飾は件名だけでなく**本文**にも規約が及ぶ
      // （`.claude/rules/traceability.md`「適用箇所: … コミット件名 / footer」）。
      .concat(crossRepoRefReasons(c.subject, '件名'))
      .concat(crossRepoRefReasons(c.body, '本文'));
    if (reasons.length) {
      violations.push({ short, subject: c.subject, reasons });
    } else if (args.verbose) {
      process.stdout.write(`  ok           ${short} ${c.subject}\n`);
    }
  }

  process.stdout.write(`検査対象 ${commits.length - skipped} 件 / 除外 ${skipped} 件\n`);

  if (violations.length) {
    process.stderr.write(`\n✗ 規約違反 ${violations.length} 件:\n`);
    for (const v of violations) {
      process.stderr.write(`  ${v.short}  ${v.subject}\n`);
      for (const r of v.reasons) process.stderr.write(`      - ${r}\n`);
    }
    process.stderr.write('\n規約: `種別(起点ID): 要約`（詳細は .claude/rules/traceability.md）\n');
    process.exit(1);
  }

  process.stdout.write('✓ すべてのコミットが規約に適合\n');
  process.exit(0);
}

if (require.main === module) {
  main();
}

// テスト用途に一部関数を公開する（本体実行時の副作用は上記ガードで抑止）。
module.exports = {
  validateSubject,
  validateIdExistence,
  loadExistingPlanIds,
  normalizePlanId,
  crossRepoRefReasons,
  CROSS_REPO_REF_LABELS,
  loadExistingIadrIds,
  loadExistingPlanAdrIds,
  checkSingleTitle,
  isBot,
  isBotAuthorName,
  BOT_AUTHORS,
  isSkippable,
  hashMatches,
  loadAllowlist,
  findAllowlisted,
  VALID_TYPES,
  TYPES_ALLOW_NO_SCOPE,
  ID_PATTERN,
};
