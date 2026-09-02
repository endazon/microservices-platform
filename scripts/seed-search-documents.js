#!/usr/bin/env node
'use strict';
/*
 * seed-search-documents.js
 *
 * FR-02, FR-03, FR-21, UC-01, SC-01 / Issue #992（案 1）:
 *   経路B（dev）へ **索引可能な文書**（本文を持つ＝`MarkdownUri` が立つ文書）を初期投入する。
 *
 * 背景:
 *   IngestionService の `DocumentUpdatedConsumer` は先頭で
 *     `if (ev.MarkdownUri is null) { ...; return; }`
 *   と早期 return する。本文を持たない文書をいくら作っても **parse→chunk→embed→index に一度も
 *   入らない**。統合スタックには文書の初期投入経路が無く（`ABACSEED` が入れるのは ABAC ポリシーだけ）、
 *   「検索が壊れている」と「該当が無い」を CI で区別できなかった（#992 / [[IADR-0255]]）。
 *   本スクリプトはその欠けている経路を埋める。
 *
 * 方式（IADR-0284 決定 1・2。[[IADR-0133]]（ABAC 投入）と同型）:
 *   - 単一情報源は **リポジトリ内の JSON**（`deploy/local/search-seed/documents.json`）。
 *   - 投入は **DocumentService の API 経由**（`POST /documents` の `Body`＝FR-21 の本文直接受け入れ
 *     経路。[[IADR-0264]]）。**直 DB 書き込みも、新しい欄の追加もしない。**
 *   - **冪等**。同じタイトルの文書が既にあれば作らない。
 *   - 認証は Keycloak の client_credentials（client `abac-seeder`。#438）。**資格情報の解決は
 *     `seed-abac-policies.js` の関数を再利用する** —— realm ファイルから引く作法（#933 / #984 の
 *     再発防止）を 2 か所へ写すと、次の realm 変更でまた片方だけ取り残される。
 *
 * 🔴 **使い捨てのスタック専用である。文書を作る（副作用）。** 残しておきたいクラスタに対して
 *    実行しないこと。`k8s-local-up.sh` からは `SEARCHSEED=1` のときだけ呼ばれる（既定オフ）。
 *
 * 実行方法:
 *   1) 経路B が稼働している状態で:
 *        node scripts/seed-search-documents.js
 *      （kubectl port-forward を一時的に自分で張り、終了時に片付ける）
 *   2) 既に到達可能な URL があるなら port-forward を使わない:
 *        SEARCH_SEED_DOCUMENT_URL=http://localhost:5001 SEARCH_SEED_KC_URL=http://keycloak:8080 \
 *          node scripts/seed-search-documents.js
 *   3) 何が投入されるかだけ見る（副作用なし）:
 *        node scripts/seed-search-documents.js --dry-run
 *   4) 検索の合言葉だけを出す（判定スクリプトが読む）:
 *        node scripts/seed-search-documents.js --print-probe-term
 *   5) 全文（キーワード）側でしか答えられないクエリを出す（#1116 の門が読む）:
 *        node scripts/seed-search-documents.js --print-keyword-only-query
 *
 * 主な環境変数:
 *   SEARCH_SEED_FILE（既定 deploy/local/search-seed/documents.json）
 *   SEARCH_SEED_NS（既定 microservices-platform）/ SEARCH_SEED_INFRA_NS（既定 platform-infra）
 *   SEARCH_SEED_REALM / SEARCH_SEED_CLIENT_ID / SEARCH_SEED_CLIENT_SECRET /
 *   SEARCH_SEED_CLIENT_SECRET（既定は ABAC 投入器と同じく realm ファイルから引く。値をここへ写さない）
 *
 * 終了コード: 0=投入済み（no-op を含む） / 1=失敗 / 2=前提未整備（k8s へ到達できない等）
 */

const fs = require('fs');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

// 🔴 資格情報の解決は ABAC 投入器と **同じ実装** を使う（値も作法も 2 か所に持たない）。
// `seed-abac-policies.js` は `require.main` ガードを持つので、require しても投入は走らない。
const abacSeed = require('./seed-abac-policies.js');

const env = (k, d) => process.env[k] || d;
const SEED_FILE = env(
  'SEARCH_SEED_FILE',
  path.join(__dirname, '..', 'deploy', 'local', 'search-seed', 'documents.json'),
);
const NS = env('SEARCH_SEED_NS', 'microservices-platform');
const INFRA_NS = env('SEARCH_SEED_INFRA_NS', 'platform-infra');
const REALM = env('SEARCH_SEED_REALM', 'platform');
const CLIENT_ID = env('SEARCH_SEED_CLIENT_ID', abacSeed.CLIENT_ID);

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);

// 🔴 client_credentials で名乗る（#438 / IADR-0294）。人のパスワードグラントは使わない ——
// MFA を必須にすると `CONFIGURE_TOTP` 未消化の利用者は password grant を拒まれる。
// 投入器は機械であり、機械に第二要素は無い。
const CLIENT_SECRET = (() => {
  if (process.env.SEARCH_SEED_CLIENT_SECRET) return process.env.SEARCH_SEED_CLIENT_SECRET;
  const fromRealm = abacSeed.clientSecretFromRealm(CLIENT_ID);
  if (fromRealm) return fromRealm;
  // 黙って既定値へ落ちない。落ちた事実を出す（無音の失敗が #933 の本体だった）。
  warn(
    `[seed-search-documents] realm ファイルから client ${CLIENT_ID} の secret を読めませんでした` +
      `（${abacSeed.REALM_FILE}）。SEARCH_SEED_CLIENT_SECRET を指定してください。`,
  );
  return '';
})();

// --- 純粋関数（実機なしで試験できるように切り出す。IADR-0284 決定 2） ---------------

/**
 * シード定義から `POST /documents` の要求本文を作る。
 *
 * 🔴 **タグを載せない。** DocumentService は辞書に無いタグを 400 で拒む（SC-05 / #635）ので、
 *    seed が独自のタグを付けると **ABAC とは無関係の 400** で投入が落ちる。
 * 🔴 **`body` は必ず載せる。** ここが欠けると `MarkdownUri` が立たず、取り込みの早期 return で
 *    捨てられる —— **本スクリプトの存在理由そのものが消える。**
 * @param {{title:string, body:string, contentType?:string, attributes?:object}} doc
 * @returns {{title:string, originalUri:null, contentType:string, attributes:object, body:string}}
 */
function buildCreateRequest(doc) {
  return {
    title: doc.title,
    originalUri: null,
    contentType: doc.contentType || 'text/markdown; charset=utf-8',
    attributes: doc.attributes || {},
    body: doc.body,
  };
}

/**
 * 既存文書と突き合わせて「まだ無いもの」だけを返す（冪等性の核）。
 * 突合はタイトルの完全一致で行う（seed のタイトルは合言葉を含み、衝突しない）。
 * @param {Array<{title:string}>} seed
 * @param {Array<{title?:string, Title?:string}>} existing
 */
function selectMissingDocuments(seed, existing) {
  const have = new Set((existing || []).map((d) => String(d.title ?? d.Title ?? '')));
  return seed.filter((d) => !have.has(String(d.title)));
}

/**
 * 検索の合言葉を取り出す。
 * **宣言が無ければ落とす（既定値へ落ちない）** —— 合言葉が空のまま判定へ渡ると
 * 「空文字で検索して 200 が返った」を成功と読む経路ができてしまう。
 * @param {{probeTerm?:string}} seed
 * @returns {string}
 */
function seedProbeTerm(seed) {
  const term = (seed || {}).probeTerm;
  if (typeof term !== 'string' || term.trim() === '')
    throw new Error('シードに probeTerm がありません（検索の合言葉が決まらない）。');
  return term.trim();
}

/**
 * FR-03, #1116: **全文（キーワード）側でしか答えられないクエリ**を、合言葉から導く。
 *
 * 🔴 合言葉をそのまま引いても、**全文インデックスの有無を区別できない** —— Qdrant v1.18.1 は
 * 索引が無いとき `Match { Text }` を**部分文字列の全走査**へ黙って落とすため、
 * `msp-searchseed-tanpopo` は索引が在っても無くても当たる（実機で実測。[[IADR-0318]] 実測 4）。
 * **`#1113` の門（SEARCH_HITS=1）が #1116 の欠陥を通してしまうのはこのためである。**
 *
 * **同じ語のまま順序だけ替える**と、索引の有無で結果が割れる。
 *   - 索引なし（部分文字列） … `tanpopo searchseed msp` は原文に現れないので **0 件**
 *   - 索引あり（トークン集合） … 3 つのトークンはすべて在るので **当たる**
 *
 * 🔴 **合言葉を 2 つに増やさない。** 別の語をここへ書くと、seed を差し替えたときに片方だけ
 * 取り残される（`documents.json` が単一情報源であることを崩さない）。
 *
 * 区切りが無く 1 語に割れないときは **null を返す** —— 呼び出し側は判定を「導けない」と
 * 明示して落とすこと。**合言葉そのものへ黙って落とさない**（落とすと索引が無くても PASS する）。
 * @param {{probeTerm?:string}} seed
 * @returns {string|null}
 */
function seedKeywordOnlyQuery(seed) {
  const tokens = seedProbeTerm(seed)
    .split(/[^\p{L}\p{N}]+/u)
    .filter((t) => t !== '');
  if (tokens.length < 2) return null;
  return [...tokens].reverse().join(' ');
}

/**
 * FR-03, #1118 / [[IADR-0339]] 決定 4: **日本語の語**で全文検索が当たることを門で見るためのクエリ。
 *
 * seed のタイトル（本文の H1 としてチャンクに入る）の**最初の CJK の連なり**を採る。
 * 合言葉（英数字の識別子）では日本語の系統（`text_ngram`）を通らないので、S4 が緑でも
 * 日本語は 0 件のままになり得る（#1118 がまさにその形）。**語をここへ書かない** —— seed の
 * タイトルが単一情報源であり、値を写すと seed を替えたとき片方だけ取り残される。
 *
 * タイトルに CJK が無ければ **null を返す**（呼び出し側は判定を「導けない」と明示して落とす）。
 * @param {{documents?:Array<{title?:string}>}} seed
 * @returns {string|null}
 */
function seedJapaneseKeywordQuery(seed) {
  const title = String(((seed.documents || [])[0] || {}).title || '');
  const m = title.match(/[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー々]+/u);
  return m ? m[0] : null;
}

/**
 * 合言葉が seed 文書に実際に現れるかを検査する。
 * **現れない合言葉で検索しても当たらない** —— 判定が「検索の故障」と「合言葉の書き間違い」を
 * 区別できなくなるので、投入の前にここで落とす。
 * @param {{probeTerm:string, documents:Array<{title:string, body:string}>}} seed
 * @returns {string[]} 違反（合言葉を含まない文書のタイトル）
 */
function documentsMissingProbeTerm(seed) {
  const term = seedProbeTerm(seed);
  return (seed.documents || [])
    .filter((d) => !String(d.title || '').includes(term) || !String(d.body || '').includes(term))
    .map((d) => String(d.title || '(タイトル無し)'));
}

// --- 一時 port-forward（自分で張り、終了時に必ず片付ける） -------------------------
const forwards = [];
function portForward(ns, svc, localPort, remotePort) {
  const child = spawn('kubectl', ['-n', ns, 'port-forward', `svc/${svc}`, `${localPort}:${remotePort}`], {
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  forwards.push(child);
  return child;
}
function cleanup() {
  for (const c of forwards) {
    try {
      c.kill();
    } catch {
      /* 片付けの失敗で終了コードを変えない */
    }
  }
  forwards.length = 0;
}
process.on('exit', cleanup);
process.on('SIGINT', () => {
  cleanup();
  process.exit(1);
});

async function waitReachable(url, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await fetch(url, { signal: AbortSignal.timeout(2000) });
      return true;
    } catch {
      await new Promise((r) => setTimeout(r, 500));
    }
  }
  return false;
}

// --- HTTP ヘルパ ------------------------------------------------------------------
async function getJson(url, token) {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`GET ${url} が失敗しました（${res.status}）`);
  return res.json();
}
async function postJson(url, token, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`POST ${url} が失敗しました（${res.status}）: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

async function fetchToken(kcUrl) {
  const confidential = abacSeed.isConfidentialInRealm(CLIENT_ID);
  if (confidential && !CLIENT_SECRET) {
    warn(
      `[seed-search-documents] realm は client ${CLIENT_ID} を confidential としていますが、` +
        ' client_secret を解決できませんでした。SEARCH_SEED_CLIENT_SECRET を指定してください。',
    );
  }
  const form = abacSeed.buildTokenForm({ clientId: CLIENT_ID, clientSecret: CLIENT_SECRET });
  const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form,
  });
  if (!res.ok) {
    const kind = confidential === null ? '（realm から種別を判定できず）' : confidential ? '（confidential）' : '（public）';
    throw new Error(
      `Keycloak のトークン取得に失敗しました（${res.status}）。client ${CLIENT_ID}${kind} の`
        + ' serviceAccountsEnabled と secret、および service-account へのロール付与を確認してください。',
    );
  }
  return (await res.json()).access_token;
}

function loadSeed() {
  if (!fs.existsSync(SEED_FILE)) throw new Error(`シードファイルがありません: ${SEED_FILE}`);
  return JSON.parse(fs.readFileSync(SEED_FILE, 'utf8'));
}

async function main(argv) {
  const seed = loadSeed();

  // 判定スクリプト（verify-oidc-edge-flow.sh）が合言葉を読む口。副作用なし。
  if (argv.includes('--print-probe-term')) {
    log(seedProbeTerm(seed));
    return 0;
  }

  // FR-03, #1116: 全文側でしか答えられないクエリを読む口（同上・副作用なし）。
  if (argv.includes('--print-keyword-only-query')) {
    const q = seedKeywordOnlyQuery(seed);
    if (q === null) {
      warn('合言葉が 1 語しかないため、全文側でしか答えられないクエリを導けません。');
      return 1;
    }
    log(q);
    return 0;
  }

  // FR-03, #1118: 日本語の語で全文側を引くクエリを読む口（同上・副作用なし）。
  if (argv.includes('--print-japanese-keyword-query')) {
    const q = seedJapaneseKeywordQuery(seed);
    if (q === null) {
      warn('seed のタイトルに日本語（CJK）が無いため、日本語の語のクエリを導けません。');
      return 1;
    }
    log(q);
    return 0;
  }

  const documents = seed.documents || [];
  const probeTerm = seedProbeTerm(seed);
  log(`シード: 文書 ${documents.length} 件 / 合言葉 ${probeTerm}（${SEED_FILE}）`);

  // 合言葉を含まない文書があれば、投入する前に落とす（後で「当たらない」を検索の故障と誤読しない）。
  const broken = documentsMissingProbeTerm(seed);
  if (broken.length > 0) {
    warn(
      `[seed-search-documents] 合言葉「${probeTerm}」をタイトルと本文の両方に含まない文書があります: ` +
        broken.join(' / '),
    );
    return 1;
  }

  if (argv.includes('--dry-run')) {
    for (const d of documents) {
      log(`  [文書] ${d.title}`);
      log(`      属性 ${JSON.stringify(d.attributes || {})} / 本文 ${String(d.body || '').length} 文字`);
    }
    log('--dry-run のため投入しません。');
    return 0;
  }

  let documentUrl = env('SEARCH_SEED_DOCUMENT_URL', '');
  let kcUrl = env('SEARCH_SEED_KC_URL', '');
  if (!documentUrl || !kcUrl) {
    if (spawnSync('kubectl', ['cluster-info'], { stdio: 'ignore' }).status !== 0) {
      warn('k8s に到達できません（kubectl cluster-info が失敗）。経路B を起動するか、');
      warn('SEARCH_SEED_DOCUMENT_URL / SEARCH_SEED_KC_URL で接続先を直接指定してください。');
      return 2;
    }
    if (!documentUrl) {
      // ABAC 投入器（18090/18091）とポートを重ねない。同時実行でどちらかが黙って失敗する。
      portForward(NS, 'document-service', 18092, 8080);
      documentUrl = 'http://localhost:18092';
    }
    if (!kcUrl) {
      portForward(INFRA_NS, 'keycloak', 18093, 8080);
      kcUrl = 'http://localhost:18093';
    }
    const ok =
      (await waitReachable(`${documentUrl}/documents`)) &&
      (await waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`));
    if (!ok) {
      warn('port-forward 経由で document-service / keycloak へ到達できませんでした。');
      return 2;
    }
  }
  log(`接続先: document=${documentUrl} / keycloak=${kcUrl}`);

  const token = await fetchToken(kcUrl);
  log(`管理トークンを取得しました（client_credentials・client=${CLIENT_ID}）`);

  const existing = await getJson(`${documentUrl}/documents`, token);
  const missing = selectMissingDocuments(documents, existing);
  log(`文書: 既存 ${existing.length} 件 / 追加 ${missing.length} 件`);

  for (const d of missing) {
    const created = await postJson(`${documentUrl}/documents`, token, buildCreateRequest(d));
    const uri = (created || {}).markdownUri ?? (created || {}).MarkdownUri ?? null;
    log(`  + 文書 ${d.title}（markdownUri=${uri ?? '(無し)'}）`);
    // 🔴 `MarkdownUri` が立たなければ取り込みは早期 return で捨てる。**投入できたことにしない。**
    if (!uri) {
      warn(
        '[seed-search-documents] 作成した文書が markdownUri を持ちません。' +
          'DocumentService のオブジェクトストレージ配線（services.document.objectStorage）を確認してください。',
      );
      return 1;
    }
  }

  if (missing.length === 0) log('投入済みのため変更はありません（冪等・no-op）。');
  else log('投入しました。取り込み（parse→chunk→embed→index）が起動します。');
  return 0;
}

module.exports = {
  buildCreateRequest,
  selectMissingDocuments,
  seedProbeTerm,
  seedKeywordOnlyQuery,
  seedJapaneseKeywordQuery,
  documentsMissingProbeTerm,
  SEED_FILE,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => {
      cleanup();
      process.exit(code);
    })
    .catch((e) => {
      warn(`[seed-search-documents] ${e.message}`);
      cleanup();
      process.exit(1);
    });
}
