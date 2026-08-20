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
 * 見るもの:
 *   1) 件名の書式 `種別(起点ID): 要約`
 *   2) 起点 ID の実在性 —— `IADR`（本リポ `.ai-context/adr/`）/ `ADR`（planning の `07_adr/`）/
 *      **`FR` / `UC` / `SC`**（拡張点 `check-test-traceability.js` を持つ構成でのみ）
 *   3) **他リポジトリ issue / PR 番号の修飾を 3 つの面で**——**件名・本文（`%b`）・PR タイトル**。
 *      裸の `#NNN` は 3 面とも**本リポジトリの issue へ自動リンクする**ため、誤リンクという
 *      実害が出る。判定器は `check-cross-repo-refs.js` から借りる（規約の単一情報源は向こう側）。
 *      **本文を見るのは、列挙形の修飾漏れが本文にも出るためである**（件名だけでは足りない）。
 *   4) **PR タイトル末尾の `(#NNN)` が PR 自身の番号か**（`PR_NUMBER` / `--pr-number` を渡した
 *      ときのみ。#799）。渡さないときは従来どおり形状だけを見る。**コミット件名モードでは
 *      絶対に一致を要求しない**（スカッシュ後の履歴コミットが全滅するため）。
 *
 * **読めない範囲は skip し、skip したことを notice で出す**（黙って 0 件検査へ落ちない）。
 *
 * 検査範囲の決定（優先順）:
 *   1) 引数 `--range <base>..<head>`
 *   2) 環境変数 `COMMIT_RANGE`
 *   3) PR 環境: `origin/$GITHUB_BASE_REF..HEAD`（GitHub Actions pull_request）
 *   4) フォールバック: `origin/develop..HEAD`（develop 既定運用）→ 取得不可なら `HEAD~20..HEAD`
 *
 * 使い方:
 *   node scripts/check-commit-messages.js [--range base..head] [--verbose]
 *   node scripts/check-commit-messages.js --title "<PR タイトル>" [--author <login>] [--pr-number <N>]
 */
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');
// 他リポジトリ issue / PR 番号の修飾検査を借りる（規約の単一情報源は向こう側）。
// **2 本セットで配布する。**
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
  const a = { range: null, verbose: false, title: null, author: null, prNumber: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--range') a.range = argv[++i];
    else if (argv[i].startsWith('--range=')) a.range = argv[i].slice('--range='.length);
    else if (argv[i] === '--title') a.title = argv[++i];
    else if (argv[i].startsWith('--title=')) a.title = argv[i].slice('--title='.length);
    else if (argv[i] === '--author') a.author = argv[++i];
    else if (argv[i].startsWith('--author=')) a.author = argv[i].slice('--author='.length);
    else if (argv[i] === '--pr-number') a.prNumber = argv[++i];
    else if (argv[i].startsWith('--pr-number=')) a.prNumber = argv[i].slice('--pr-number='.length);
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
  // body（`%b`）は**最後に置く**。複数行を含むが、レコード境界は RS なのでフィールド分割は
  // 壊れない。本文を取るのは、**列挙形の修飾漏れが本文にも出る**ためである（件名だけでは
  // 足りないことが実測で判明した。裸の `#NNN` は本文でも本リポジトリの issue へ自動リンクする）。
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

/**
 * PR 作成者のログイン名が除外対象かを判定する（PR タイトル検査用）。
 *
 * コミット著者向けの isBot() が「著者名＋メール」への**部分一致**なのに対し、
 * こちらは**ログイン名の完全一致**である。PR 作成者は単一のログイン名として
 * 得られ、部分一致にすると `dependabot` を含む無関係なログイン名（例:
 * `not-dependabot-really`）まで除外され得るためである。
 *
 * とくに **GitHub App が作成した PR（`claude[bot]` 等）は除外しない**。
 * ワークフロー側で `user.type != 'Bot'` により弾くと、AI に実装を委ねる運用
 * （本キットが前提とする主要な経路）でだけ PR タイトル検査が skip され、
 * 「最後の砦」が外れる（issue planning#202）。除外の判定は本関数へ一本化する。
 */
function isBotLogin(login) {
  const name = String(login == null ? '' : login).trim().toLowerCase();
  if (!name) return false;
  return BOT_AUTHORS.some((b) => b.toLowerCase() === name);
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

/** 実装 ADR（本リポ `.ai-context/adr/`）の実在番号集合。読めなければ null。 */
function loadExistingIadrIds(dir = path.join(__dirname, '..', '.ai-context', 'adr')) {
  return loadExistingAdrIds('IADR', dir);
}

// 【置換点】本リポジトリが主に実装する計画プロジェクト名（`planning/projects/<name>/`）。
// 裸（無修飾）の `ADR-xxxx` はこの名前空間を指す（.claude/rules/traceability.md の規約）。
// 環境変数 PLAN_PROJECT で上書きできる（テスト・複数構成の検証用）。
// ★ 固有デルタ（分類 B 種 5・#790）: 置換点を本リポの計画プロジェクト名で埋めている。
//   **既定を空／プレースホルダのままにして CI 側で環境変数を渡す形は採らない** —— 未設定だと
//   他プロジェクトにしか無い ADR まで「実在」として受理され、検査が静かに緩む（#756 と同じ判断）。
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
 * 計画レンジ（`FR-xx` / `UC-xx` / `SC-xx`）の実在集合を返す。持たない構成では null。
 *
 * **なぜ要るか（実測）**: 実在性検査が `IADR` と `ADR` にしか無かった間、
 * `feat(SC-99)` / `feat(FR-77)` / `feat(UC-88)` はいずれも **exit 0 で受理**されていた。
 * これらはスカッシュ後件名として統合ブランチの恒久履歴へ載り、force push 禁止で事後修正できない。
 *
 * **レンジの正はリポジトリ固有である**（配布先ごとに計画プロジェクトが違う）。よってキットは
 * **パーサを持たず、拡張点として `./check-test-traceability.js` の `readPlanIds()` を探す**。
 * 同じ事実を 2 本のパーサで持たないためである —— 片方だけ直したとき、どちらが正か決められなくなる。
 *
 * **fail の向きを 2 つに分ける**（「見つからないから素通り」を一律には採らない）:
 *   - **モジュールが無い**（キット既定。当該検査器を持たない構成）
 *     → `null` を返して**当該検査をスキップ**する（呼び出し側が notice で可視化する）。
 *   - **モジュールは在るが節をパースできない** → **例外を投げる**。追跡下の必ず読めるファイルを
 *     読めない／拾えないのは環境差ではなく**規約側の破壊**（節の改名・書式変更）である。
 *     ここを黙って通すと、レンジの単一情報源が壊れたまま「違反 0 件」で緑になる。
 */
function loadExistingPlanIds() {
  let traceability;
  try {
    traceability = require('./check-test-traceability.js');
  } catch (e) {
    // **「持っていない」だけを skip にする。** 種別を見ずに握ると、当該モジュールの構文エラーまで
    // 「持たない構成」として素通りし、上の 2 つ目の向き（節が壊れていれば fail）が崩れる。
    if (e && e.code === 'MODULE_NOT_FOUND') return null;
    throw e;
  }
  if (typeof traceability.readPlanIds !== 'function') return null;
  // 節が壊れていれば readPlanIds が投げる。**握り潰さない。**
  return new Set(traceability.readPlanIds());
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
      reasons.push(`起点 ID "${id}" が .ai-context/adr/ に実在しない（採番衝突・改番後のタイトル未追随の可能性）`);
    } else if (planAdrIds && /^ADR-\d{3,4}$/.test(id) && !planAdrIds.has(id)) {
      reasons.push(`起点 ID "${id}" が planning の 07_adr/ に実在しない（誤記・廃止の可能性）`);
    } else if (planIds && /^(FR|UC|SC)-\d+$/.test(id) && !planIds.has(normalizePlanId(id))) {
      // ここが無い間、`feat(SC-99)` は exit 0 で恒久履歴へ載れた。
      reasons.push(
        `起点 ID "${id}" が計画レンジに実在しない（誤記・別プロジェクトの ID の可能性）`
      );
    }
  }
  return reasons;
}

/**
 * 他リポジトリ issue / PR 番号の修飾違反を違反理由の配列で返す。
 * `validateSubject`（書式の単一情報源）とは**別関数**に保つ。書式規約と参照表記の規約は
 * 別物であり、allowlist の「規約に準拠した件名を無意味に除外していない」判定が
 * 表記の是非で揺れないようにするためである。
 *
 * コミットメッセージは Markdown ではない（GitHub はバッククォートをコードスパンとして
 * 描画せず、`#NNN` の自動リンクは効く）ため、コードスパン除外を**しない**モードで見る。
 */
// ラベルは kind と 1:1 で対応させる。分岐が足りないと、CI ログを読んで直す人が
// 実際には存在しない違反種別を探すことになる。**これは実測で 2 度漏れた**（検査の型を
// 足すたびに漏れる）。**型を足したら必ずここへも足すこと。**
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
// 件名に現れてはならない HTML エンティティ。
//
// **GitHub は PR タイトルの素の `<` `>` `&` `"` を作成時点でエスケープして保存する。**
// スカッシュ後の件名は PR タイトルがそのまま載るため、`git -C <submodule> grep` と書くと
// 恒久履歴へ `git -C &lt;submodule&gt; grep` が焼き付く。**force push 禁止のため直せない**
// （実測: planning#415。生成物は changelog-overrides.json の remap で是正したが履歴は残る）。
//
// **エスケープ済みの文字列を検査するのが要点である。** 書いた側は素の山括弧を書いており、
// 素の `<` を検査しても素通りする —— **エスケープは GitHub 側で起きる**。
// `pr-title.yml` は `pull_request.title`（＝エスケープ済み）を渡すため、マージ前に止まる。
const HTML_ENTITY_PATTERN = /&(?:lt|gt|amp|quot|#\d+|#x[0-9a-fA-F]+);/;

function validateSubject(subject) {
  const reasons = [];
  // 末尾の PR 番号 " (#123)" は除去して判定する。
  const s = subject.replace(/\s*\(#\d+\)\s*$/, '').trim();

  // 形式検査より先に見る。エンティティは形式に適合したまま恒久履歴へ載るためである。
  const entity = s.match(HTML_ENTITY_PATTERN);
  if (entity) {
    reasons.push(
      `HTML エンティティ "${entity[0]}" を含む（GitHub が PR タイトルの < > & " を` +
        'エスケープして保存し、スカッシュ後の件名へ焼き付く。素の山括弧を使わず、' +
        'バッククォートで囲むか山括弧を用いない書き方にする）'
    );
  }

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
 * PR タイトル末尾の ` (#NNN)` が **PR 自身の番号**であることを検証する（#799 / [[IADR-0207]]）。
 *
 * ★ 本リポ発の環流であり、**計画 pin `767a9d48` でキットが完全実装した**（#836 で突合済み）。
 *   したがって固有デルタではない —— この docstring と下の実測値だけが本リポの記録である。
 *
 * なぜ要るか: 規約は末尾の `(#123)` を「**スカッシュマージ既定件名として**許容」と書いており、
 * **GitHub がその PR 自身の番号を自動付加する**挙動を前提にしている。ところが検査は形状しか
 * 見ていなかったため、**起点 issue の番号をタイトルへ書いた PR** が素通りしていた。
 * 実測（2026-08-16・全 PR 443 件）: **末尾に番号を持つ 66 件のうち、自番号と一致するものは 0 件**。
 * GitHub の UI からマージすると自動付加が重なり **`… (#796) (#798)`** と二重になる
 * （`develop` に既に **58 件**着地しており、force push 禁止で事後修正できない）。
 *
 * **番号一致を課すのは PR 番号が読めたときだけ**である。
 *   - `prNumber` が null / undefined → **形状のみ**（従来どおり）。コミット件名モードには PR 番号が
 *     無く、ここで一致を要求するとスカッシュ後の履歴コミット（`… (#794)`）が全滅する。
 *   - 末尾に `(#NNN)` が無い → **合格**。末尾の番号は任意である（自番号を書く動機は本来無い）。
 */
function validateTitlePrNumber(subject, prNumber) {
  if (prNumber == null) return [];
  const m = String(subject == null ? '' : subject).match(/\(#(\d+)\)\s*$/);
  if (!m) return [];
  if (Number(m[1]) === Number(prNumber)) return [];
  return [
    `末尾の "(#${m[1]})" が PR 自身の番号（#${prNumber}）と一致しない。` +
      '末尾の (#NNN) を外すか、PR 自身の番号にすること。' +
      '起点 issue は本文の `Closes #NNN` で示す' +
      '（GitHub はスカッシュ時に PR 番号を自動付加するため、通常はタイトルへ番号を書かない。' +
      '書いたままマージすると "… (#796) (#798)" と二重に付く）',
  ];
}

/**
 * `PR_NUMBER` / `--pr-number` の生値を正の整数へ正規化する。
 * 未設定・空文字は `null`（＝番号一致検査をしない）。**数値として読めない値は `NaN` を返す** ——
 * 呼び出し側が「設定されているのに読めない」を notice で可視化するためである（黙って検査を消さない）。
 */
function normalizePrNumber(raw) {
  if (raw == null) return null;
  const s = String(raw).trim();
  if (!s) return null;
  if (!/^\d+$/.test(s)) return NaN;
  const n = Number(s);
  return n > 0 ? n : NaN;
}

/**
 * 単一件名（PR タイトル = スカッシュ後件名の由来）を検査する（再発防止）。
 * git を使わず、渡された 1 件名のみを規約に照合する。Revert / [skip ci] はスキップ扱い。
 * 合格・スキップ時 0、違反時 1 を返す。
 *
 * `prNumber` は PR 自身の番号（省略時は末尾番号の一致を検査しない。#799）。
 */
function checkSingleTitle(title, author, prNumber) {
  const subject = String(title == null ? '' : title).trim();
  process.stdout.write(`PR タイトル（スカッシュ後件名）チェック: "${subject}"\n`);

  if (!subject) {
    // タイトル未取得（イベント外実行等）。CI をブロックしない（fail-open）。
    process.stderr.write('PR タイトルが空のため検査をスキップする。\n');
    return 0;
  }
  if (isBotLogin(author)) {
    // dependabot 等の自動 PR は規約対象外（BOT_AUTHORS へ完全一致した場合のみ）。
    process.stdout.write(`  skip(bot)    作成者 ${String(author).trim()} は規約対象外\n`);
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
    // PR タイトルはスカッシュ後に**コミット件名として恒久履歴へ載る**面である。
    // 裸の `#NNN` は本リポジトリの issue へ自動リンクするため、ここで止める。
    .concat(crossRepoRefReasons(subject, 'PR タイトル'))
    // 末尾の `(#NNN)` が PR 自身の番号かどうか（#799）。prNumber 未指定なら形状のみ。
    .concat(validateTitlePrNumber(subject, prNumber));
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
  const author = args.author != null ? args.author : process.env.PR_AUTHOR;
  if (title != null) {
    // PR 自身の番号（#799）。**コミット件名モードへは渡さない。**
    // notice は呼び出し側でのみ出す（checkSingleTitle の中に置くと単体テストが
    // 本物の CI アノテーションを漏らす。loadExisting* と同じ扱い）。
    const rawPrNumber = args.prNumber != null ? args.prNumber : process.env.PR_NUMBER;
    let prNumber = normalizePrNumber(rawPrNumber);
    if (Number.isNaN(prNumber)) {
      notice(
        `PR_NUMBER="${rawPrNumber}" を正の整数として読めないため、PR タイトル末尾 (#NNN) の` +
          '番号一致チェックをスキップした（形状のみ検査している）。' +
          'pr-title.yml が github.event.pull_request.number を渡しているか確認すること'
      );
      prNumber = null;
    }
    process.exit(checkSingleTitle(title, author, prNumber));
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
  // 検査を skip したことは notice で可視化する（issue planning#139）。素の stderr 行は緑ジョブの
  // ログに埋もれて読まれず、「検査していない範囲があること」が CI の UI から読み取れない。
  // 終了コードは変えない（fail-open。ローカル環境差で CI を落とさない）。
  // 注: notice はここ（実行時の呼び出し側）でのみ出す。loadExisting* の内部に置くと、
  // 未 populate を模したテストのフィクスチャが本物のアノテーションを漏らす（#140 と同型）。
  if (!iadrIds) {
    notice('.ai-context/adr/ を読めないため IADR 実在性チェックをスキップした（この範囲は検査されていない）');
  }
  if (!planAdrIds) {
    notice(
      'planning submodule が未 populate のため計画 ADR 実在性チェックをスキップした' +
        '（この範囲は検査されていない。実効しているのは IADR 検査のみである）。' +
        'PR 段階で検査するには checkout に submodules とトークンを付けること'
    );
  }
  if (!planIds) {
    // モジュールを持たない構成（キット既定）でのみここへ来る。節が壊れている場合は
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
      .concat(validateIdExistence(c.subject, iadrIds, planAdrIds, planIds))
      // **件名と本文の両方を見る。** 列挙形の修飾漏れは本文にも出る（実測）。
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
  loadExistingIadrIds,
  loadExistingPlanAdrIds,
  loadExistingPlanIds,
  normalizePlanId,
  crossRepoRefReasons,
  CROSS_REPO_REF_LABELS,
  validateTitlePrNumber,
  normalizePrNumber,
  checkSingleTitle,
  isBotLogin,
  isBot,
  isSkippable,
  hashMatches,
  loadAllowlist,
  findAllowlisted,
  VALID_TYPES,
  TYPES_ALLOW_NO_SCOPE,
  ID_PATTERN,
};
