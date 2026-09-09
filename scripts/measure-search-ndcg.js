#!/usr/bin/env node
'use strict';
/*
 * measure-search-ndcg.js
 *
 * FR-02 / FR-03, issue #336: 検索の関連性を **nDCG@10** で実測する。
 *
 * 背景:
 *   計画 ADR-0016（埋め込みは Voyage voyage-3.5 を既定・高機密はセルフホスト併用）と ADR-0017
 *   （セルフホストは Ruri v3）は、モデルの確定を **「PoC（検索精度 nDCG@10・スループット）」** に
 *   委ねている。issue #336 はその実測を稼働環境依存として分離したが、**測る道具そのものが
 *   リポジトリに 1 つも無かった**（棚卸し 2026-08-16 / 09-03 / 09-05 が 3 度続けて指摘）。
 *   `perf/k6/` はレイテンシとスループットだけを測り、**返却結果の順位も ID も見ていない**。
 *
 *   本スクリプトがその道具である。**測定そのものは稼働環境が要る**が、
 *   **集計は純関数なのでクラスタ無しで書き切れて CI で緑にできる**（`scripts.repo.test.js`）。
 *
 * 性質（`measure-abac-combinations.js` と同じ型に乗せる）:
 *   - **読み取り専用**。検索 API へ POST するだけで、何も作らず何も消さない。
 *   - 乱数・現在時刻に依存しない（同一入力なら同一出力＝再現可能。dump に時刻を書かない）。
 *   - 外部依存ゼロ（Node 標準ライブラリのみ）。集計は純関数。
 *   - **収集と集計を分ける。** `--dump` で生の順位を保存し、`--input` で集計だけを追試できる
 *     （測定機会を失った後も、他人が数字を検算できる）。
 *
 * 実行方法:
 *   1) 収集 ＋ 集計（稼働環境が要る）:
 *        NDCG_BASE_URL=https://edge.example NDCG_TOKEN=<jwt> \
 *          node scripts/measure-search-ndcg.js --qrels perf/ndcg/qrels.json --dump run-voyage.json
 *   2) 集計だけ（保存済みの順位から。**環境非依存＝レビューの追試はこちら**）:
 *        node scripts/measure-search-ndcg.js --input run-voyage.json
 *   3) A/B の比較（2 回の収集を並べる。qrels が同一であることは digest で検査する）:
 *        node scripts/measure-search-ndcg.js --input run-voyage.json --input run-ruri.json
 *
 * 主な環境変数:
 *   NDCG_BASE_URL   … 検索 API のベース URL（既定 http://localhost:5000）
 *   NDCG_SEARCH_PATH… 検索の経路（既定 /bff/search。RetrievalService を直に叩くなら /search）
 *   NDCG_TOKEN      … Bearer アクセストークン（**推奨**。MFA 必須化によりパスワードグラントは通らない）
 *   NDCG_KC_TOKEN_URL / NDCG_KC_CLIENT_ID / NDCG_KC_USERNAME / NDCG_KC_PASSWORD
 *                   … 計測専用クライアントを用意した場合のパスワードグラント（perf/k6 と同じ考え方）
 *   NDCG_MODES      … 測るモード（既定 keyword,semantic,hybrid）
 *   NDCG_LABEL      … この収集の名札（例 voyage-3.5 / ruri-v3）。A/B の識別に使う
 *   NDCG_K          … 打ち切り順位（既定 10）。qrels の `k` より優先する
 *
 * 出力: 既定は人が読める要約。--json で機械可読、--dump <path> で収集した生データを保存する。
 */

const fs = require('fs');
const crypto = require('crypto');

// 検索モード（`Knowledge.Contracts/Dtos/SearchDto.cs` の SearchModes と同じ 3 値）。
// 🔴 **ここを 2 値にしない** —— hybrid を測れないと「埋め込みの寄与」を切り分けられない。
const SEARCH_MODES = ['keyword', 'semantic', 'hybrid'];

// 既定の打ち切り順位。ADR-0017 が求める指標は nDCG@10 である。
const DEFAULT_K = 10;

// 関連度の値域（graded relevance）。qrels の雛形と検証で使う。
const MAX_RELEVANCE = 3;

// ---------------------------------------------------------------------------
// 集計（純関数。副作用・I/O を持たない）
// ---------------------------------------------------------------------------

// 利得は**線形**（gain = 関連度）、割引は log2(順位+1)。Järvelin & Kekäläinen の原形である。
//
// 🔴 **指数利得（2^rel - 1）は採らない。** どちらも「graded relevance ＋ log2 割引」だが値が違うので、
// **混ぜて比較すると意味が無い**。ここで 1 つに決め、比較は常に同じ式で行う。
function dcgAtK(gains, k = DEFAULT_K) {
  let sum = 0;
  const limit = Math.min(gains.length, k);
  for (let i = 0; i < limit; i += 1) {
    const gain = Number(gains[i] || 0);
    if (gain <= 0) continue;
    // 順位 i は 0 始まり。順位 1 の割引は log2(2) = 1（＝割引なし）。
    sum += gain / Math.log2(i + 2);
  }
  return sum;
}

// 理想順位の DCG。qrels の関連度を降順に並べたものを k で打ち切る。
function idcgAtK(relevanceValues, k = DEFAULT_K) {
  const sorted = [...(relevanceValues || [])].map(Number).filter((v) => v > 0).sort((a, b) => b - a);
  return dcgAtK(sorted, k);
}

// 🔴 **同一文書の重複を落とす（先頭だけを採る）。**
// 検索が返すのは**チャンク**であり、1 つの文書から複数のチャンクが上位に並ぶことがある。
// qrels は**文書単位**なので、落とさないと同じ文書で 2 度加点され、**nDCG が 1.0 を超え得る**。
function dedupeDocuments(documentIds) {
  const seen = new Set();
  const out = [];
  for (const raw of documentIds || []) {
    const id = String(raw);
    if (seen.has(id)) continue;
    seen.add(id);
    out.push(id);
  }
  return out;
}

// 1 クエリの nDCG@k。
//
// 戻り値の `evaluable` が false なら**平均から除外する**（0.0 として混ぜない）——
// 正解が 1 件も無いクエリの「0 点」は検索の失敗ではなく**ラベルの不在**であり、
// 混ぜると正解を付けていないクエリが多いほど数字が下がる（測っているものが変わる）。
function ndcgAtK(rankedDocumentIds, relevance, k = DEFAULT_K) {
  const ranked = dedupeDocuments(rankedDocumentIds).slice(0, k);
  const rel = relevance || {};
  // qrels に無い文書 ID の関連度は 0（＝当たっていない）。
  const gains = ranked.map((id) => Number(rel[id] || 0));
  const idcg = idcgAtK(Object.values(rel), k);
  const dcg = dcgAtK(gains, k);
  return {
    evaluable: idcg > 0,
    ndcg: idcg > 0 ? dcg / idcg : null,
    dcg,
    idcg,
    returned: ranked.length,
    // 上位 k のうち、関連ありとラベルされた件数（当たりの数。順位を見ない粗い指標）。
    hits: gains.filter((g) => g > 0).length,
    judged: Object.values(rel).filter((v) => Number(v) > 0).length,
  };
}

// qrels の妥当性検査。**壊れた qrels を黙って受けない** —— 数字は出るが意味が無くなる。
function validateQrels(qrels) {
  const errors = [];
  if (!qrels || !Array.isArray(qrels.queries) || qrels.queries.length === 0) {
    errors.push('qrels に queries がありません（1 件以上必要）');
    return errors;
  }
  const ids = new Set();
  for (const q of qrels.queries) {
    if (!q || typeof q.id !== 'string' || q.id.trim() === '') {
      errors.push('queries[].id は空でない文字列である必要があります');
      continue;
    }
    if (ids.has(q.id)) errors.push(`queries[].id '${q.id}' が重複しています`);
    ids.add(q.id);
    if (typeof q.text !== 'string' || q.text.trim() === '') {
      errors.push(`query '${q.id}' の text が空です`);
    }
    const rel = q.relevance || {};
    for (const [docId, value] of Object.entries(rel)) {
      if (!Number.isInteger(value) || value < 0 || value > MAX_RELEVANCE) {
        errors.push(`query '${q.id}' の関連度 ${docId}=${value} が 0..${MAX_RELEVANCE} の整数ではありません`);
      }
    }
  }
  return errors;
}

// qrels の指紋。**違う正解ラベルで測った数字を並べて比較させない**ための鍵である。
// 並び順・空白・k の違いで指紋が変わらないよう、id で整列した正規形から取る。
function qrelsDigest(qrels) {
  const canonical = [...((qrels && qrels.queries) || [])]
    .map((q) => ({
      id: q.id,
      text: q.text,
      relevance: Object.fromEntries(
        Object.entries(q.relevance || {})
          .filter(([, v]) => Number(v) > 0)
          .sort(([a], [b]) => a.localeCompare(b))
      ),
    }))
    .sort((a, b) => a.id.localeCompare(b.id));
  return crypto.createHash('sha256').update(JSON.stringify(canonical)).digest('hex').slice(0, 16);
}

// 1 run（＝ある名札・あるモードで採った順位の集合）の集計。
function summarizeRun(qrels, run, k = DEFAULT_K) {
  const perQuery = [];
  const skipped = [];
  const missing = [];
  for (const q of qrels.queries) {
    const ranked = (run.results || {})[q.id];
    if (!Array.isArray(ranked)) {
      // 収集されていないクエリ。**0 点として混ぜない**（測っていないものは測っていないと言う）。
      missing.push(q.id);
      continue;
    }
    const r = ndcgAtK(ranked, q.relevance, k);
    if (!r.evaluable) {
      skipped.push(q.id);
      continue;
    }
    perQuery.push({ id: q.id, text: q.text, ...r });
  }
  const mean =
    perQuery.length > 0 ? perQuery.reduce((s, e) => s + e.ndcg, 0) / perQuery.length : null;
  return {
    label: run.label || '(no label)',
    mode: run.mode,
    k,
    mean,
    evaluated: perQuery.length,
    // 正解ラベルが 1 件も無いクエリ（除外した。件数と ID を必ず報告する）。
    skippedNoRelevant: skipped,
    // 収集されていないクエリ。
    missingResults: missing,
    perQuery,
  };
}

// 複数の dump を 1 つのデータへ束ねる。
// 🔴 **qrels が違うものは束ねない。** 別の正解ラベルで採った数字を並べると、
// **モデルの差に見えるものが実はラベルの差**になる。
function mergeDatasets(datasets) {
  if (!datasets || datasets.length === 0) throw new Error('入力がありません');
  const base = datasets[0];
  const digest = qrelsDigest(base.qrels);
  const runs = [...(base.runs || [])];
  for (const d of datasets.slice(1)) {
    const other = qrelsDigest(d.qrels);
    if (other !== digest) {
      throw new Error(
        `qrels が一致しません（${digest} と ${other}）。` +
          '違う正解ラベルで測った結果は比較できません（モデルの差とラベルの差が混ざる）。'
      );
    }
    runs.push(...(d.runs || []));
  }
  return { qrels: base.qrels, runs };
}

// 収集済みデータ（qrels ＋ runs）から測定結果をまとめる。
function summarize(data, options = {}) {
  const qrels = data.qrels;
  const errors = validateQrels(qrels);
  if (errors.length > 0) throw new Error(`qrels が不正です:\n  - ${errors.join('\n  - ')}`);

  const k = Number(options.k || qrels.k || DEFAULT_K);
  const runs = (data.runs || []).map((run) => summarizeRun(qrels, run, k));

  // 比較表: 先頭の run を基準に差分を出す（A/B の読み方を 1 つに決める）。
  const baseline = runs[0];
  const comparison = runs.map((r) => ({
    label: r.label,
    mode: r.mode,
    mean: r.mean,
    delta:
      baseline && baseline.mean !== null && r.mean !== null && r !== baseline
        ? r.mean - baseline.mean
        : null,
  }));

  return {
    k,
    qrels: {
      digest: qrelsDigest(qrels),
      queries: qrels.queries.length,
      // 🔴 **正解ラベルの付いていないクエリ数を必ず出す。** 雛形のまま測ると全件がこれになり、
      // 「測れた」と読み違える余地を残さない。
      unlabeled: qrels.queries.filter(
        (q) => Object.values(q.relevance || {}).filter((v) => Number(v) > 0).length === 0
      ).length,
    },
    runs,
    comparison,
  };
}

// ---------------------------------------------------------------------------
// 出力
// ---------------------------------------------------------------------------

const fmt = (v) => (v === null || v === undefined ? '—' : v.toFixed(4));

function renderText(r) {
  const L = [];
  const hr = '----------------------------------------------------------------------';
  L.push(`検索の関連性 nDCG@${r.k} の実測（issue #336 / FR-02・FR-03・ADR-0016・ADR-0017）`);
  L.push(hr);
  L.push(`qrels                 : ${r.qrels.queries} クエリ（指紋 ${r.qrels.digest}）`);
  if (r.qrels.unlabeled > 0) {
    L.push(`  ⚠ 正解ラベルが 1 件も無いクエリ: ${r.qrels.unlabeled} 件（平均から除外される）`);
  }
  L.push('');
  for (const run of r.runs) {
    L.push(`■ ${run.label} / mode=${run.mode}`);
    L.push(`  nDCG@${run.k} 平均      : ${fmt(run.mean)}（評価 ${run.evaluated} クエリ）`);
    if (run.skippedNoRelevant.length) {
      L.push(`  除外（正解ラベル無し）: ${run.skippedNoRelevant.join(', ')}`);
    }
    if (run.missingResults.length) {
      L.push(`  ⚠ 収集されていないクエリ: ${run.missingResults.join(', ')}`);
    }
    for (const q of run.perQuery) {
      L.push(
        `    ${fmt(q.ndcg)}  ${q.text}` +
          `（上位 ${q.returned} 件中 当たり ${q.hits} / 正解 ${q.judged}）`
      );
    }
    L.push('');
  }
  L.push(hr);
  L.push('比較（先頭の run が基準）');
  for (const c of r.comparison) {
    const delta = c.delta === null ? '' : `  差 ${c.delta >= 0 ? '+' : ''}${c.delta.toFixed(4)}`;
    L.push(`  ${fmt(c.mean)}  ${c.label} / ${c.mode}${delta}`);
  }
  L.push(hr);
  return L.join('\n');
}

// ---------------------------------------------------------------------------
// 収集（I/O）
// ---------------------------------------------------------------------------

const env = (k, d) => process.env[k] || d;

// トークン取得。TOKEN があればそれを、無ければパスワードグラント。
//
// 🔴 **MFA 必須化（#438 / IADR-0294）により、realm の対話利用者はパスワードグラントで取れない。**
// `perf/k6/lib/config.js` と同じ事情である。**NDCG_TOKEN を与えて使うこと。**
async function obtainToken() {
  if (process.env.NDCG_TOKEN) return process.env.NDCG_TOKEN;

  const tokenUrl = process.env.NDCG_KC_TOKEN_URL;
  if (!tokenUrl) {
    throw new Error(
      '認証情報がありません。NDCG_TOKEN に取得済みのアクセストークンを与えてください' +
        '（MFA 必須化により realm の対話利用者はパスワードグラントで取得できない。#438）。'
    );
  }
  const res = await fetch(tokenUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'password',
      client_id: env('NDCG_KC_CLIENT_ID', 'platform-spa'),
      username: env('NDCG_KC_USERNAME', 'poc-user'),
      password: env('NDCG_KC_PASSWORD', ''),
    }),
  });
  if (!res.ok) {
    throw new Error(
      `Keycloak トークン取得に失敗しました（${res.status}）。MFA を課さない計測専用クライアントを` +
        '用意するか、NDCG_TOKEN に取得済みのアクセストークンを与えてください。'
    );
  }
  return (await res.json()).access_token;
}

// 1 クエリ × 1 モードの上位 k 件（文書 ID の列）を取る。
async function search(baseUrl, path, token, query, mode, k) {
  const res = await fetch(`${baseUrl.replace(/\/$/, '')}${path}`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, topK: k, mode }),
  });
  if (!res.ok) {
    // 🔴 **失敗を空の結果へ縮退させない。** 空は「該当なし」と区別できず、
    // 落ちている系統を nDCG 0 として記録すると、故障が「精度が低い」に化ける。
    throw new Error(`検索に失敗しました（${mode} / ${res.status}）: ${query}`);
  }
  const body = await res.json();
  return (body.results || []).map((x) => x.documentId ?? x.DocumentId).filter(Boolean);
}

async function collect(qrels, options) {
  const baseUrl = env('NDCG_BASE_URL', 'http://localhost:5000');
  const path = env('NDCG_SEARCH_PATH', '/bff/search');
  const label = env('NDCG_LABEL', 'run');
  const modes = env('NDCG_MODES', SEARCH_MODES.join(','))
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
  const unknown = modes.filter((m) => !SEARCH_MODES.includes(m));
  if (unknown.length) {
    throw new Error(`未知の検索モードです: ${unknown.join(', ')}（有効: ${SEARCH_MODES.join(', ')}）`);
  }

  const token = await obtainToken();
  const runs = [];
  for (const mode of modes) {
    const results = {};
    for (const q of qrels.queries) {
      results[q.id] = await search(baseUrl, path, token, q.text, mode, options.k);
    }
    runs.push({ label, mode, results });
  }
  return { qrels, runs };
}

// ---------------------------------------------------------------------------

function argValues(argv, flag) {
  const out = [];
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === flag && argv[i + 1]) out.push(argv[i + 1]);
  }
  return out;
}

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));

async function main(argv) {
  const asJson = argv.includes('--json');
  const inputs = argValues(argv, '--input');
  const dumps = argValues(argv, '--dump');
  const qrelsPaths = argValues(argv, '--qrels');
  const kFlag = argValues(argv, '--k')[0];
  const k = Number(kFlag || process.env.NDCG_K || 0) || null;

  let data;
  if (inputs.length > 0) {
    data = mergeDatasets(inputs.map(readJson));
  } else {
    const qrelsPath = qrelsPaths[0] || 'perf/ndcg/qrels.json';
    const qrels = readJson(qrelsPath);
    const errors = validateQrels(qrels);
    if (errors.length > 0) throw new Error(`qrels が不正です:\n  - ${errors.join('\n  - ')}`);
    data = await collect(qrels, { k: k || qrels.k || DEFAULT_K });
  }

  if (dumps.length > 0) fs.writeFileSync(dumps[0], `${JSON.stringify(data, null, 2)}\n`);

  const result = summarize(data, { k });
  process.stdout.write(asJson ? `${JSON.stringify(result, null, 2)}\n` : `${renderText(result)}\n`);
}

module.exports = {
  SEARCH_MODES,
  DEFAULT_K,
  MAX_RELEVANCE,
  dcgAtK,
  idcgAtK,
  dedupeDocuments,
  ndcgAtK,
  validateQrels,
  qrelsDigest,
  summarizeRun,
  mergeDatasets,
  summarize,
  renderText,
};

if (require.main === module) {
  main(process.argv.slice(2)).catch((e) => {
    process.stderr.write(`[measure-search-ndcg] ${e.message}\n`);
    process.exit(1);
  });
}
