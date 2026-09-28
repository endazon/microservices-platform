#!/usr/bin/env node
'use strict';
/*
 * plan-backend-test-shards.js
 * NFR, #1686 / IADR-0232（2026-09-28 追記）: ci.yml の `backend-build` の脚（行列）を、ユニット × テストのシャードとして導出する。
 *
 * 背景:
 *   ci-latency（IADR-0232 決定 8）が「逆転」を検知した（`build-and-test` の中央値 189 秒 > `claude-review` の最小 148 秒）。
 *   律速は knowledge の脚の「Restore, build and test」（約 150〜157 秒。うち build 約 55 秒・test 約 90 秒）であり、
 *   利用者裁定は「knowledge のテストの脚をシャーディングして逆転を解消する。閾値は変えない」である。
 *
 * 方式:
 *   - シャードの割り当ては `scripts/backend-test-shards.json` が持つ（ユニット → 試験プロジェクトの配列の配列）。
 *     **ci.yml にはユニット名も試験プロジェクト名も書かない**（discover-units の glob と同じ理由。書くと次に増えたとき静かに外れる）。
 *   - 設定に無いユニットは従来どおり 1 脚で `backend.slnx` 全体を試す（`projects` が空）。
 *   - 設定に在るユニットは、シャードごとに 1 脚を出す。各脚は `backend.slnx` 全体を restore / build し（ビルドの検証は
 *     従来と同じく全プロジェクトに掛かる）、**テストだけ**をシャードの試験プロジェクトに絞る。
 *
 * 🔴 取りこぼし・二重実行を許さない（fail-closed）:
 *   設定に在るユニットについて、`backend.slnx` に載る試験プロジェクト（`Microsoft.NET.Test.Sdk` を PackageReference する
 *   csproj。`dotnet test` が試験として走らせる集合と同じ）の集合と、シャードの和が**完全に一致**し、かつ各試験プロジェクトが
 *   **ちょうど 1 つ**のシャードに載っていなければ exit 1 にする。CI では discover-units がこれを実行するため、
 *   試験プロジェクトを足して設定へ載せ忘れると `build-and-test` が赤くなる（黙って走らない試験を作らない）。
 *   設定にあるのに実在しないユニット・シャードが 1 つしかない・空のシャードも同じく exit 1。
 *
 * 使い方:
 *   node scripts/plan-backend-test-shards.js --units '["knowledge","platform"]'   # 行列（JSON 配列）を標準出力へ
 *   node scripts/plan-backend-test-shards.js --check                             # src/*\/backend/backend.slnx の全ユニットで検査のみ
 *
 * 出力（行列の include）の各要素:
 *   { unit, label, key, projects }
 *     label    脚の表示名（設定に無いユニットはユニット名のまま。従来の `backend-build (<unit>)` を保つ）
 *     key      カバレッジ artifact の名前に使う一意な値（`coverage-<key>`）
 *     projects `backend.slnx` からの相対パスを `;` で連結したもの。空なら `backend.slnx` 全体を試す
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。ci.yml の discover-units はランナー既定の node で本スクリプトを呼ぶ
 * （setup-node を足すと律速の経路に数秒載るため。node が無ければ discover-units が落ち、集約が赤くなる）。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const CONFIG_PATH = path.join('scripts', 'backend-test-shards.json');

/** `src/<unit>/backend/backend.slnx` の絶対パス。 */
function slnxPath(repoRoot, unit) {
  return path.join(repoRoot, 'src', unit, 'backend', 'backend.slnx');
}

/** slnx に載る Project の Path（slnx からの相対・`/` 区切り）。 */
function slnxProjects(slnxText) {
  const out = [];
  const re = /<Project\s+[^>]*Path="([^"]+)"/g;
  let m;
  while ((m = re.exec(slnxText)) !== null) out.push(m[1].replace(/\\/g, '/'));
  return out;
}

/** csproj が試験プロジェクトか（`dotnet test` が試験として走らせるのは Microsoft.NET.Test.Sdk を参照するもの）。 */
function isTestProject(csprojText) {
  return /<PackageReference\s+[^>]*Include="Microsoft\.NET\.Test\.Sdk"/.test(csprojText);
}

/** ユニットの試験プロジェクト（`backend.slnx` からの相対パス。昇順）。slnx が読めなければ例外。 */
function testProjectsOf(repoRoot, unit) {
  const slnx = slnxPath(repoRoot, unit);
  const dir = path.dirname(slnx);
  const projects = slnxProjects(fs.readFileSync(slnx, 'utf8'));
  return projects
    .filter((p) => {
      const abs = path.join(dir, p);
      if (!fs.existsSync(abs)) throw new Error(`${path.relative(repoRoot, slnx)} が載せる ${p} が無い`);
      return isTestProject(fs.readFileSync(abs, 'utf8'));
    })
    .sort();
}

/**
 * 行列を導出する。返り値 { legs, errors }。errors が 1 件でもあれば呼び出し側は exit 1 にする。
 * @param {string} repoRoot
 * @param {string[]} units discover-units が導出したユニット
 * @param {{units?: Record<string, {shards: string[][]}>}} config
 */
function planLegs(repoRoot, units, config) {
  const legs = [];
  const errors = [];
  const configured = (config && config.units) || {};
  for (const name of Object.keys(configured)) {
    if (!units.includes(name)) {
      errors.push(`${CONFIG_PATH} にユニット ${name} があるが、src/${name}/backend/backend.slnx が無い（設定が古い）`);
    }
  }
  for (const unit of units) {
    const entry = configured[unit];
    if (!entry) {
      legs.push({ unit, label: unit, key: unit, projects: '' });
      continue;
    }
    const shards = entry.shards;
    if (!Array.isArray(shards) || shards.length < 2) {
      errors.push(`${unit}: shards は 2 つ以上の配列でなければならない（1 つなら設定から外せば従来どおり 1 脚になる）`);
      continue;
    }
    let actual;
    try {
      actual = testProjectsOf(repoRoot, unit);
    } catch (e) {
      errors.push(`${unit}: ${e.message}`);
      continue;
    }
    const seen = new Map();
    shards.forEach((shard, i) => {
      if (!Array.isArray(shard) || shard.length === 0) {
        errors.push(`${unit}: シャード ${i + 1} が空である`);
        return;
      }
      for (const p of shard) {
        if (seen.has(p)) errors.push(`${unit}: ${p} がシャード ${seen.get(p)} と ${i + 1} の両方にある（二重に走る）`);
        else seen.set(p, i + 1);
      }
    });
    for (const p of actual) {
      if (!seen.has(p)) {
        errors.push(`${unit}: 試験プロジェクト ${p} がどのシャードにも無い（走らない）。${CONFIG_PATH} のいずれかのシャードへ足すこと`);
      }
    }
    for (const p of seen.keys()) {
      if (!actual.includes(p)) {
        errors.push(`${unit}: ${p} は backend.slnx に載る試験プロジェクトではない（名前の誤りか、消えた試験プロジェクト）`);
      }
    }
    shards.forEach((shard, i) => {
      if (!Array.isArray(shard) || shard.length === 0) return;
      legs.push({
        unit,
        label: `${unit} ${i + 1}/${shards.length}`,
        key: `${unit}-${i + 1}`,
        projects: shard.join(';'),
      });
    });
  }
  return { legs, errors };
}

function readConfig(repoRoot) {
  return JSON.parse(fs.readFileSync(path.join(repoRoot, CONFIG_PATH), 'utf8'));
}

/** `src/*\/backend/backend.slnx` から導出したユニット（discover-units と同じ規則）。 */
function discoverUnits(repoRoot) {
  const src = path.join(repoRoot, 'src');
  return fs
    .readdirSync(src, { withFileTypes: true })
    .filter((d) => d.isDirectory() && fs.existsSync(slnxPath(repoRoot, d.name)))
    .map((d) => d.name)
    .sort();
}

function main(argv) {
  const i = argv.indexOf('--units');
  let units;
  if (i >= 0) {
    units = JSON.parse(argv[i + 1] || 'null');
    if (!Array.isArray(units) || units.length === 0) {
      process.stderr.write('✗ --units には空でない JSON 配列を渡すこと\n');
      return 1;
    }
  } else if (argv.includes('--check')) {
    units = discoverUnits(REPO_ROOT);
  } else {
    process.stderr.write('使い方: plan-backend-test-shards.js --units <JSON 配列> | --check\n');
    return 2;
  }
  const { legs, errors } = planLegs(REPO_ROOT, units, readConfig(REPO_ROOT));
  if (errors.length) {
    for (const e of errors) process.stderr.write(`::error::${e}\n`);
    return 1;
  }
  if (argv.includes('--check')) {
    process.stdout.write(`✓ ${units.length} ユニット・${legs.length} 脚。シャードの和は各ユニットの試験プロジェクトの集合と一致する。\n`);
    for (const l of legs) process.stdout.write(`  ${l.label}: ${l.projects || '(backend.slnx 全体)'}\n`);
    return 0;
  }
  process.stdout.write(`${JSON.stringify(legs)}\n`);
  return 0;
}

if (require.main === module) process.exit(main(process.argv.slice(2)));

module.exports = { slnxProjects, isTestProject, testProjectsOf, planLegs, discoverUnits, readConfig, CONFIG_PATH };
