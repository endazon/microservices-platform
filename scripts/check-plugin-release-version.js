#!/usr/bin/env node
/**
 * FR-20 / ADR-0037 決定 1 / IADR-0338 フォローアップ 3 / IADR-0375, #1213:
 * Obsidian プラグインのリリース資産を出す前に、**版が 3 箇所で一致している**ことを確かめる。
 *
 * 版は 3 つの場所に現れる:
 *   1. `src/obsidian-plugin/package.json` の `version` —— pnpm workspace メンバとしての版
 *   2. `src/obsidian-plugin/manifest.json` の `version` —— **Obsidian が読む版**（利用者に見える唯一の版）
 *   3. リリースタグ `obsidian-plugin-v<version>` —— どのコミットを配ったかの記録
 *
 * 🔴 **食い違うと、直らないまま利用者へ届く。** Obsidian は `manifest.json` の版しか見ないので、
 * タグだけ上げても利用者の画面では版が変わらず、「入れ替えたのに古い版のまま」に見える
 * （逆に `manifest.json` だけ上げると、どのコミットの成果物かがタグから辿れない）。
 * どちらも**静かに壊れる**（ビルドもテストも通る）ので、機械で止める。
 *
 * 検出しないもの（本検査は網羅ではない）:
 *   - 版を**上げ忘れた**こと。3 つが揃って古いのは正しい状態（配布のたびに上げる規律は人が持つ）。
 *   - 成果物の中身。egress は `check-static-egress.js`、ビルドの成否は esbuild が見る。
 *
 * 使い方:
 *   node scripts/check-plugin-release-version.js                 # package.json と manifest.json の突合
 *   node scripts/check-plugin-release-version.js --tag <ref>     # ＋ タグとの突合（リリース時）
 *   node scripts/check-plugin-release-version.js --self-test     # 検査器自身の試験
 *
 * `<ref>` は `refs/tags/obsidian-plugin-v0.2.0` でも `obsidian-plugin-v0.2.0` でもよい
 * （GitHub Actions の `github.ref` をそのまま渡せる）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const PLUGIN_DIR = path.join(REPO_ROOT, 'src', 'obsidian-plugin');

/**
 * リリースタグの前置。**モノレポなので裸の `v0.2.0` は使わない** ——
 * このリポジトリの他の成果物（サービスイメージ・SPA）が同じ名前空間のタグを使えなくなる。
 * この文字列はワークフロー（`.github/workflows/obsidian-plugin-release.yml`）の
 * `on.push.tags` と同じでなければならない（`scripts.repo.test.js` が突合する）。
 */
const TAG_PREFIX = 'obsidian-plugin-v';

/** 版の形。プレリリース識別子（`0.3.0-rc.1`）は許すが、ビルドメタデータ（`+`）は許さない。 */
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

/** `refs/tags/x` → `x`。前置が無ければそのまま返す。 */
function stripRefPrefix(ref) {
  return String(ref).replace(/^refs\/tags\//, '');
}

/**
 * 純関数の判定。**ここが検査の全体**であり、I/O は呼び出し側が行う。
 * @returns {string[]} 違反の説明（空なら合格）
 */
function evaluate({ pkgVersion, manifestVersion, tag }) {
  const problems = [];

  for (const [label, value] of [
    ['package.json の version', pkgVersion],
    ['manifest.json の version', manifestVersion],
  ]) {
    if (typeof value !== 'string' || value.length === 0) {
      problems.push(`${label} が無い（読めない値: ${JSON.stringify(value)}）`);
    } else if (!SEMVER.test(value)) {
      problems.push(`${label} が semver の形ではない: ${JSON.stringify(value)}`);
    }
  }
  if (problems.length > 0) return problems; // 形が壊れているなら突合しても意味が無い

  if (pkgVersion !== manifestVersion) {
    problems.push(
      `package.json（${pkgVersion}）と manifest.json（${manifestVersion}）の version が違う。` +
        'Obsidian は manifest.json しか見ないので、片方だけ上げると利用者側で版が変わらない',
    );
  }

  if (tag !== undefined && tag !== null && String(tag).length > 0) {
    const name = stripRefPrefix(tag);
    if (!name.startsWith(TAG_PREFIX)) {
      problems.push(
        `タグ ${JSON.stringify(name)} が前置 ${JSON.stringify(TAG_PREFIX)} で始まっていない。` +
          'モノレポなので裸の版タグは使わない',
      );
    } else {
      const tagVersion = name.slice(TAG_PREFIX.length);
      if (tagVersion !== manifestVersion) {
        problems.push(
          `タグの版（${tagVersion}）と manifest.json の version（${manifestVersion}）が違う。` +
            'タグを打ち直すか、manifest.json と package.json を上げてからタグを打つ',
        );
      }
    }
  }

  return problems;
}

/** JSON を読む。読めない・壊れているは fail-closed（例外を投げて呼び出し側が exit 1）。 */
function readJson(file) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch (e) {
    throw new Error(`${file} を読めない: ${e.message}`);
  }
  try {
    return JSON.parse(raw);
  } catch (e) {
    throw new Error(`${file} が JSON として壊れている: ${e.message}`);
  }
}

function run(argv) {
  const tagIndex = argv.indexOf('--tag');
  const tag = tagIndex >= 0 ? argv[tagIndex + 1] : undefined;
  if (tagIndex >= 0 && (tag === undefined || tag.startsWith('--'))) {
    console.error('[check-plugin-release-version] --tag には値が要る（例: --tag refs/tags/obsidian-plugin-v0.2.0）');
    process.exit(1);
  }

  let pkg;
  let manifest;
  try {
    pkg = readJson(path.join(PLUGIN_DIR, 'package.json'));
    manifest = readJson(path.join(PLUGIN_DIR, 'manifest.json'));
  } catch (e) {
    console.error(`[check-plugin-release-version] ${e.message}`);
    process.exit(1);
  }

  const problems = evaluate({ pkgVersion: pkg.version, manifestVersion: manifest.version, tag });
  if (problems.length > 0) {
    console.error('[check-plugin-release-version] 版が揃っていません:\n');
    for (const p of problems) console.error(`  ✗ ${p}`);
    console.error(
      `\n配布の版は manifest.json が正である（Obsidian が読む唯一の版）。タグは ${TAG_PREFIX}<version> で打つ。`,
    );
    process.exit(1);
  }

  console.log(
    `[check-plugin-release-version] OK: version=${manifest.version}` +
      (tag ? `（タグ ${stripRefPrefix(tag)} と一致）` : '（package.json と manifest.json が一致）'),
  );
}

function selfTest() {
  const cases = [
    ['合格: 2 つが一致（タグなし）', { pkgVersion: '0.2.0', manifestVersion: '0.2.0' }, 0],
    ['合格: タグも一致', { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: 'obsidian-plugin-v0.2.0' }, 0],
    [
      '合格: refs/tags/ 前置つき',
      { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: 'refs/tags/obsidian-plugin-v0.2.0' },
      0,
    ],
    ['合格: プレリリース', { pkgVersion: '0.3.0-rc.1', manifestVersion: '0.3.0-rc.1', tag: 'obsidian-plugin-v0.3.0-rc.1' }, 0],
    ['違反: package と manifest が食い違う', { pkgVersion: '0.3.0', manifestVersion: '0.2.0' }, 1],
    [
      '違反: タグの版がずれる',
      { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: 'obsidian-plugin-v0.3.0' },
      1,
    ],
    ['違反: 裸の版タグ', { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: 'v0.2.0' }, 1],
    ['違反: 前置が v 無し', { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: 'obsidian-plugin-0.2.0' }, 1],
    ['違反: version が無い', { pkgVersion: undefined, manifestVersion: '0.2.0' }, 1],
    ['違反: semver でない', { pkgVersion: '0.2', manifestVersion: '0.2' }, 2],
    ['違反: ビルドメタデータつき', { pkgVersion: '0.2.0+1', manifestVersion: '0.2.0+1' }, 2],
    ['合格: 空文字のタグは「タグ指定なし」と読む', { pkgVersion: '0.2.0', manifestVersion: '0.2.0', tag: '' }, 0],
  ];
  let failed = 0;
  for (const [name, input, expected] of cases) {
    const got = evaluate(input).length;
    if (got !== expected) {
      console.error(`  ✗ ${name}（期待 ${expected} 件 / 実際 ${got} 件）`);
      failed++;
    } else console.log(`  ok  ${name}`);
  }
  if (failed > 0) process.exit(1);
  console.log(`✓ self-test: ${cases.length} 件すべて通過`);
}

module.exports = { TAG_PREFIX, SEMVER, evaluate, stripRefPrefix };

if (require.main === module) {
  if (process.argv.includes('--self-test')) selfTest();
  else run(process.argv.slice(2));
}
