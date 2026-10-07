'use strict';
/*
 * lib/llm-purposes.js
 * **LLM ゲートウェイへ送る用途名（purpose）の全数が、ゲートウェイの `Llm:Routing:PurposeModels` のキーに在るか**を
 * 突き合わせる純関数と、実ツリーの走査（FR-10, FR-11, ADR-0044 決定 1, ADR-0081 フォローアップ 2, IADR-0511 決定 4 / #1785）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。検査の本体は `scripts/scripts.repo.test.js`（#1785 の節）が叩く。
 *
 * ## なぜ要るか（同型の事故）
 *
 * 用途が `PurposeModels` に無いと、ゲートウェイは**例外もログも無く**エンドポイントの `DefaultModel`
 * （`claude-opus-5`・最も高い単価）へ落とし（`LlmRouter.ResolveModel`）、計器は用途を `other` へ丸める
 * （`LlmMetricValues.NormalizePurpose`）。**費用を用途で切り分けられなくなる。**
 * `trade-decision`（IADR-0102）・`trade-decision-screening`（IADR-0340）・`graph-suggestion` /
 * `graph-cluster-summary`（#1785）と、同じ形の欠落が繰り返し起きた。
 *
 * ## 拾う形（呼び出し側の宣言）
 *
 *   ① 名前に `Purpose` を含む `const string`（`PurposeName` / `Purpose` / `RagAnswerPurpose` …）。
 *      名前が `Tag` で終わるもの（計器の属性名。例 `PurposeTag = "ai.purpose"`）は用途名ではないので拾わない。
 *   ② 名前付き引数・初期化子へ直接書いた文字列リテラル（`Purpose: "x"` / `Purpose = "x"`）。
 *
 * 🔴 **拾わない形**: 文字列リテラルを**位置引数で中継**する形（`GenerateAsync(…, "x", ct)`）。引数の分割に入れ子の
 * ラムダ・括弧が絡み、正規表現では脆い。**宣言の形をそろえる側で塞ぐ** —— 用途名は ① の定数で宣言する
 * （`RagOrchestrator` は #1785 で定数へ改めた）。この制約は IADR-0511 決定 4 に書いてある。
 *
 * ## 走査範囲
 *
 * 本リポジトリが所有する backend（`src/platform/backend`・`src/knowledge/backend`）の非試験 `*.cs`。
 * ゲートウェイ自身（用途を受ける側。`default` の補い値を持つ）・試験・`bin` / `obj`・submodule（`src/ai-stock-trading`。
 * 用途は別リポジトリの `LlmPurposes` が持つ）を除く。**0 件走査は呼び出し側で赤にする**（fail-closed）。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');

/** 走査対象（本リポジトリが所有する backend）。 */
const SCAN_ROOTS = ['src/platform/backend', 'src/knowledge/backend'];

/** 用途を受ける側（ゲートウェイ自身）。 */
const GATEWAY_DIR = 'src/platform/backend/Services/LlmGateway';

/** ゲートウェイの構成（`PurposeModels` の唯一の宣言。deploy 側に上書きは無い —— IADR-0511 §母集合 軸 2）。 */
const GATEWAY_APPSETTINGS = `${GATEWAY_DIR}/appsettings.json`;

/** 用途未指定時にゲートウェイが補う値（`LlmMetricValues.DefaultPurpose`）。設定に依らず既知とする。 */
const DEFAULT_PURPOSE = 'default';

const SKIP_SEGMENTS = new Set(['bin', 'obj', 'node_modules']);

/** パスの 1 区間が試験のディレクトリ（`Tests` / `tests` / `*.Tests` / `*.IntegrationTests`）か。 */
function isTestSegment(seg) {
  return /(^|\.)(Integration)?Tests?$/i.test(seg);
}

/** C# の本文 1 つから、呼び出し側が宣言した用途名を拾う（純関数）。返すのは `{ purpose, line, form }` の配列。 */
function extractCallerPurposes(source) {
  const found = [];
  const lineOf = (index) => source.slice(0, index).split('\n').length;

  // ① 名前に Purpose を含む const string。
  const constRe = /\bconst\s+string\s+(\w*Purpose\w*)\s*=\s*"([^"\\]*)"\s*;/g;
  for (let m; (m = constRe.exec(source)); ) {
    if (/Tag$/.test(m[1])) continue;
    found.push({ purpose: m[2], line: lineOf(m.index), form: `const ${m[1]}` });
  }

  // ② 名前付き引数・初期化子への文字列リテラル（① の定数宣言 `string Purpose = "x"` は二重に数えない）。
  const literalRe = /(?<!\bstring\s+)\bPurpose\s*[:=]\s*"([^"\\]*)"/g;
  for (let m; (m = literalRe.exec(source)); ) {
    found.push({ purpose: m[1], line: lineOf(m.index), form: 'Purpose literal' });
  }
  return found;
}

/** ゲートウェイの appsettings.json の本文から `PurposeModels` のキーを読む（純関数）。 */
function readPurposeModelKeys(appsettingsText) {
  const json = JSON.parse(appsettingsText);
  const models = json && json.Llm && json.Llm.Routing && json.Llm.Routing.PurposeModels;
  if (!models || typeof models !== 'object') {
    throw new Error('Llm:Routing:PurposeModels が見つからない（構成の形が変わった。0 件として扱わない）');
  }
  return Object.keys(models);
}

/**
 * 拾った用途名のうち、`PurposeModels` のキーに無いものを返す（純関数）。
 * キーの照合は大小を区別しない（ゲートウェイの辞書は `StringComparer.OrdinalIgnoreCase`）。
 */
function findUnregistered(callerPurposes, purposeModelKeys) {
  const known = new Set(purposeModelKeys.map((k) => k.toLowerCase()));
  known.add(DEFAULT_PURPOSE);
  return callerPurposes.filter((c) => !known.has(String(c.purpose).toLowerCase()));
}

function listCsFiles(absDir, acc = []) {
  if (!fs.existsSync(absDir)) return acc;
  for (const entry of fs.readdirSync(absDir, { withFileTypes: true })) {
    if (SKIP_SEGMENTS.has(entry.name)) continue;
    const abs = path.join(absDir, entry.name);
    const rel = path.relative(REPO_ROOT, abs).split(path.sep).join('/');
    if (entry.isDirectory()) {
      if (isTestSegment(entry.name)) continue;
      if (rel === GATEWAY_DIR) continue;
      listCsFiles(abs, acc);
    } else if (entry.name.endsWith('.cs')) {
      acc.push(rel);
    }
  }
  return acc;
}

/** 実ツリーを走査し、呼び出し側の用途名と `PurposeModels` のキーを返す。 */
function scanTree(root = REPO_ROOT) {
  const callers = [];
  let files = 0;
  for (const scanRoot of SCAN_ROOTS) {
    for (const rel of listCsFiles(path.join(root, scanRoot))) {
      files++;
      const source = fs.readFileSync(path.join(root, rel), 'utf8');
      for (const f of extractCallerPurposes(source)) callers.push({ ...f, file: rel });
    }
  }
  const keys = readPurposeModelKeys(fs.readFileSync(path.join(root, GATEWAY_APPSETTINGS), 'utf8'));
  return { files, callers, keys, unregistered: findUnregistered(callers, keys) };
}

module.exports = {
  SCAN_ROOTS,
  GATEWAY_APPSETTINGS,
  DEFAULT_PURPOSE,
  isTestSegment,
  extractCallerPurposes,
  readPurposeModelKeys,
  findUnregistered,
  scanTree,
};
