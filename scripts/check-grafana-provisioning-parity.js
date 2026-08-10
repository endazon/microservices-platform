#!/usr/bin/env node
'use strict';
/*
 * check-grafana-provisioning-parity.js — #674 / NFR-19 / ADR-0006 / IADR-0168
 *
 * Grafana の provisioning が **compose（経路 A）と k8s（経路 B）で同内容**であることを検査する。
 *
 * ★ 起点（#674）: #665 で入れた `check-grafana-alerting.js` は **`alerting/` だけ**を突合していた。
 *   **だから `dashboards/` が k8s に丸ごと無いことを誰も見ていなかった** —— しかもその中の
 *   `llm-usage.json` は **#546（IADR-0164）の月次 LLM 費用確認 Runbook が指す行き先**である。
 *   **手順書は行き先を指しているが、経路 B ではそこへ行けなかった。**
 *   **「検査していたのに見つからなかった」のではなく「検査の射程が狭かった」。**
 *
 * ★ `check-grafana-alerting.js` は残す。あちらは **Prometheus の `alerts.yml` と Grafana の
 *   ルールが 1 対 1** という別の軸を見ており、本検査器（経路間パリティ）と重ならない。
 *
 * ★ 比較の方法と、その限界:
 *   - `.json` は `JSON.parse` して深く比較する（鍵の順序・空白に依存しない）
 *   - `.yaml` は **YAML パーサを使わない**（本リポの検査器は Node 標準のみで動かす方針）。
 *     **全行コメント・空行・行末空白を落として比較する。**
 *     **限界**: 鍵の順序を入れ替えただけでも「不一致」と報告する ——
 *     **緩いより厳しい側へ倒している**（本物の乖離を素通りさせない）。
 *
 * 実行: node scripts/check-grafana-provisioning-parity.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const COMPOSE_ROOT = path.join(REPO, 'deploy/grafana/provisioning');
const K8S = path.join(REPO, 'deploy/local/observability/grafana.yaml');

/** compose 側の provisioning ファイルを集める（相対パスとテキスト）。 */
function collectCompose(root) {
  const out = [];
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else out.push({ rel: path.relative(root, p).split(path.sep).join('/'), text: fs.readFileSync(p, 'utf8') });
    }
  };
  walk(root);
  return out;
}

/**
 * k8s マニフェストから ConfigMap の inline を取り出す。
 * `  <key>: |` の次行から、字下げが 4 未満の行が来るまでを本文とし、字下げ 4 を剥がす。
 * **正規表現で本文の中身を読まない**（#664 / #665 で繰り返した「正規表現の側で絞る」型を避ける）。
 */
function extractInlineFiles(text) {
  const lines = text.split('\n');
  const files = new Map();
  for (let i = 0; i < lines.length; i++) {
    const m = /^ {2}([A-Za-z0-9._-]+): \|\s*$/.exec(lines[i]);
    if (!m) continue;
    const body = [];
    let j = i + 1;
    for (; j < lines.length; j++) {
      const l = lines[j];
      if (l.trim() === '') {
        body.push('');
        continue;
      }
      if (!l.startsWith('    ')) break;
      body.push(l.slice(4));
    }
    files.set(m[1], body.join('\n'));
    i = j - 1;
  }
  return files;
}

/** マウントされている provisioning のパス（volumeMounts の mountPath）。 */
function mountedPaths(text) {
  return [...text.matchAll(/^\s*mountPath:\s*(\S+)\s*$/gm)].map((m) => m[1]);
}

/** 全行コメント・空行・行末空白を落とす。 */
function normalizeYaml(text) {
  return text
    .split('\n')
    .map((l) => l.replace(/\s+$/, ''))
    .filter((l) => l.trim() !== '' && !/^\s*#/.test(l))
    .join('\n')
    .trim();
}

/**
 * 鍵を並べ替えて安定化した JSON 文字列。
 * ★ `JSON.stringify(JSON.parse(x))` は**挿入順を保つ**ので鍵順に依存する ——
 *   「鍵順を無視する」と書いた自己試験が落ちて気づいた（**書いた主張のほうが誤っていた**）。
 */
function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value)
      .sort()
      .map((k) => `${JSON.stringify(k)}:${canonicalJson(value[k])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value);
}

function sameContent(rel, a, b) {
  if (rel.endsWith('.json')) {
    try {
      return canonicalJson(JSON.parse(a)) === canonicalJson(JSON.parse(b));
    } catch {
      return false;
    }
  }
  return normalizeYaml(a) === normalizeYaml(b);
}

/**
 * 検査本体（純関数）。
 * `compose` は { rel, text } の配列、`inline` は Map<key, text>、`mounts` はマウント先パスの配列。
 */
function findIssues({ compose, inline, mounts }) {
  const issues = [];
  const mountSet = new Set(mounts);

  for (const { rel, text } of compose) {
    const key = rel.split('/').pop();
    const dir = rel.includes('/') ? rel.slice(0, rel.lastIndexOf('/')) : '';
    if (!inline.has(key)) {
      issues.push(`compose の provisioning/${rel} が k8s の ConfigMap に無い（経路 B に存在しない）`);
      continue;
    }
    if (!sameContent(rel, inline.get(key), text)) {
      issues.push(`provisioning/${rel} が compose と k8s で同内容でない`);
    }
    const want = `/etc/grafana/provisioning/${dir}`;
    if (dir && !mountSet.has(want)) {
      issues.push(`${want} が k8s の volumeMounts に無い（ConfigMap はあるが Grafana から読まれない）`);
    }
  }

  // k8s 側にだけある inline（compose に無い）は、経路 A が無音になる同型の欠陥である。
  const composeKeys = new Set(compose.map((c) => c.rel.split('/').pop()));
  for (const key of inline.keys()) {
    if (!composeKeys.has(key)) {
      issues.push(`k8s の ConfigMap に ${key} があるが compose の provisioning に無い（経路 A に存在しない）`);
    }
  }
  return { issues, composeCount: compose.length, inlineCount: inline.size };
}

function selfTest() {
  const assert = require('assert');
  let passed = 0;
  const t = (name, fn) => {
    fn();
    passed++;
    process.stdout.write(`  ok  ${name}\n`);
  };
  const base = () => ({
    compose: [
      { rel: 'datasources/datasources.yaml', text: 'apiVersion: 1\ndatasources:\n  - name: P\n' },
      { rel: 'dashboards/a.json', text: '{"uid":"a","panels":[]}' },
    ],
    inline: new Map([
      ['datasources.yaml', 'apiVersion: 1\ndatasources:\n  - name: P\n'],
      ['a.json', '{ "panels": [], "uid": "a" }'],
    ]),
    mounts: ['/etc/grafana/provisioning/datasources', '/etc/grafana/provisioning/dashboards'],
  });

  t('揃っていれば違反 0 件（JSON は鍵順・空白を無視する）', () =>
    assert.deepStrictEqual(findIssues(base()).issues, []));

  t('★ k8s に無いファイルを検出する（#674 の欠陥そのもの・変異試験）', () => {
    const b = base();
    b.inline.delete('a.json');
    const r = findIssues(b);
    assert.ok(r.issues.some((x) => x.includes('dashboards/a.json') && x.includes('経路 B に存在しない')), JSON.stringify(r.issues));
  });

  t('★ ConfigMap はあるがマウントされていない形を検出する（死んだ資源・変異試験）', () => {
    const b = base();
    b.mounts = ['/etc/grafana/provisioning/datasources'];
    const r = findIssues(b);
    assert.ok(r.issues.some((x) => x.includes('volumeMounts に無い')), JSON.stringify(r.issues));
  });

  t('内容の乖離を検出する（変異試験）', () => {
    const b = base();
    b.inline.set('datasources.yaml', 'apiVersion: 1\ndatasources:\n  - name: Q\n');
    assert.ok(findIssues(b).issues.some((x) => x.includes('同内容でない')));
  });

  t('YAML のコメントと空行の違いは乖離にしない', () => {
    const b = base();
    b.inline.set('datasources.yaml', '# 注記\napiVersion: 1\n\ndatasources:\n  - name: P\n');
    assert.deepStrictEqual(findIssues(b).issues, []);
  });

  t('★ JSON の中身が違えば検出する（鍵順を無視しても素通りしない）', () => {
    const b = base();
    b.inline.set('a.json', '{"uid":"b","panels":[]}');
    assert.ok(findIssues(b).issues.some((x) => x.includes('同内容でない')));
  });

  t('k8s にだけある inline を検出する（経路 A が無音になる同型）', () => {
    const b = base();
    b.inline.set('extra.yaml', 'x: 1\n');
    assert.ok(findIssues(b).issues.some((x) => x.includes('経路 A に存在しない')));
  });

  t('extractInlineFiles は字下げ 4 を剥がし、次のキーで止まる', () => {
    const got = extractInlineFiles(
      'data:\n  a.yaml: |\n    x: 1\n    y: 2\n  b.json: |\n    {"k":1}\n---\nkind: X\n',
    );
    assert.deepStrictEqual([...got.keys()], ['a.yaml', 'b.json']);
    assert.strictEqual(got.get('a.yaml'), 'x: 1\ny: 2');
    assert.strictEqual(got.get('b.json'), '{"k":1}');
  });

  t('mountedPaths は volumeMounts の mountPath を拾う', () =>
    assert.deepStrictEqual(mountedPaths('  mountPath: /a\n    mountPath: /b\n'), ['/a', '/b']));

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

function main(argv) {
  if (argv.includes('--self-test')) {
    selfTest();
    return 0;
  }

  const compose = collectCompose(COMPOSE_ROOT);
  // 門 A: compose 側を 1 件も拾えない。
  if (compose.length === 0) {
    console.error('[check-grafana-provisioning-parity] compose の provisioning を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  let k8sText;
  try {
    k8sText = fs.readFileSync(K8S, 'utf8');
  } catch {
    console.error('[check-grafana-provisioning-parity] deploy/local/observability/grafana.yaml を読めませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }
  const inline = extractInlineFiles(k8sText);
  // 門 B: k8s 側の inline を 1 件も拾えない（字下げ規則が変わった等）。
  if (inline.size === 0) {
    console.error('[check-grafana-provisioning-parity] k8s の ConfigMap から inline を 1 件も取り出せませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const { issues, composeCount, inlineCount } = findIssues({
    compose,
    inline,
    mounts: mountedPaths(k8sText),
  });

  if (issues.length === 0) {
    console.log(
      `[check-grafana-provisioning-parity] OK: compose ${composeCount} 件 / k8s inline ${inlineCount} 件が` +
        '同内容で、いずれもマウントされています。' +
        '（**Grafana が受理するかは本検査の対象外**。IADR-0165 決定 1）',
    );
    return 0;
  }
  console.error(`[check-grafana-provisioning-parity] 経路間の乖離 ${issues.length} 件:`);
  for (const i of issues) console.error(`  - ${i}`);
  return 1;
}

module.exports = { findIssues, extractInlineFiles, mountedPaths, normalizeYaml, canonicalJson, collectCompose };

if (require.main === module) process.exit(main(process.argv.slice(2)));
