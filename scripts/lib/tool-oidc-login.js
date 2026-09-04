#!/usr/bin/env node
/*
 * NFR-09 / #1163: ツール側 OIDC ログイン開始の判定ロジック（純粋関数）。
 *
 * `scripts/verify-tool-oidc-logins.sh` は I/O（curl / kubectl）だけを担い、**判定はここに置く**。
 * こうしておくと稼働クラスタ無しでも `scripts/scripts.repo.test.js` が判定を固定できる ——
 * シェル本文を grep するだけの検査は「文字列が在ること」しか見ないので、
 * 判定そのものが壊れても緑のまま通ってしまう（#992 / #1124 で学んだ形）。
 *
 * 🔴 **期待値（issuer の host / redirect_uri）をここへ列挙しない**（#1163 受け入れ基準 2）。
 *    - 認可端点は Keycloak の discovery から渡される（`authorizationEndpoint` 引数）。
 *    - `redirect_uri` の正当性は **Keycloak 自身**が判定する（段 b のログインフォーム有無）。
 *      ここで見るのは「ツール自身の origin を指しているか」という構造だけである。
 *    - 列挙で持つと、realm とツール設定の片方だけが変わったとき**静かに割れる**。
 */
'use strict';

// ---- 母集合（#1163 の 7 クライアント） -------------------------------------------
//
// 走査の出所は作業仕様書 `.ai-context/specs/20260903_issue-1163_tool-oidc-login-verifier.md` §母集合。
// realm JSON の `standardFlowEnabled: true` かつ `redirectUris` 非空は 8 件で、
// そのうち `platform-spa` は ADR-0032（BFF セッション方式）の移行でブラウザのログイン開始を
// 持たなくなった（開始は `bff` の `/bff/auth/login`）。よって 7 件。
//
// `clientId` は**ここでは宣言しない** —— 実行時に認可 URL から読み取って報告する。
// 宣言すると realm 側の client 名を写すことになり、受け入れ基準 2 に反する。
const TOOLS = [
  // key        host          start（ログイン開始の取り方）          probe（配備の有無を見る読み取り専用の口）
  { key: 'bff', host: 'edge', start: { kind: 'redirect', path: '/bff/auth/login' }, probe: '/' },
  { key: 'grafana', host: 'grafana', start: { kind: 'redirect', path: '/login/generic_oauth' }, probe: '/login' },
  { key: 'argocd', host: 'argocd', start: { kind: 'redirect', path: '/auth/login' }, probe: '/healthz' },
  { key: 'headlamp', host: 'headlamp', start: { kind: 'redirect', path: '/oidc?cluster=main' }, probe: '/' },
  { key: 'minio', host: 'minio', start: { kind: 'json-get', path: '/api/v1/login', pick: 'minio' }, probe: '/api/v1/login' },
  { key: 'vault', host: 'vault', start: { kind: 'json-post', path: '/v1/auth/oidc/oidc/auth_url', pick: 'vault' }, probe: '/v1/sys/health' },
  { key: 'wiki-js', host: 'wiki', start: { kind: 'wikijs' }, probe: '/login' },
];

// ---- 認可 URL の解析 ---------------------------------------------------------------

/** 認可 URL を分解する。壊れていれば ok:false を返す（例外を投げない）。 */
function parseAuthorizeUrl(location) {
  const raw = String(location || '').trim();
  if (raw === '') {
    return {
      ok: false,
      reason: 'ログイン開始 URL を取り出せない（ツールは応答するが認可 URL を返していない）。'
        + 'OIDC の配線が消えているか、ログイン開始の口が変わっている',
    };
  }
  let u;
  try {
    u = new URL(raw);
  } catch (e) {
    return { ok: false, reason: `Location が URL として読めない: ${raw.slice(0, 120)}` };
  }
  return {
    ok: true,
    endpoint: `${u.origin}${u.pathname}`,
    origin: u.origin,
    clientId: u.searchParams.get('client_id') || '',
    redirectUri: u.searchParams.get('redirect_uri') || '',
    // PAR（Pushed Authorization Request）。BFF（.NET）は `redirect_uri` を URL へ載せず
    // `request_uri` で押し込む。**載っていないことを「壊れている」と読まない。**
    requestUri: u.searchParams.get('request_uri') || '',
  };
}

/**
 * 段 (a) の判定: ログイン開始がエッジ Keycloak の認可端点へ向いているか。
 *
 * @param {object} a
 * @param {string} a.location              ツールが返したログイン開始 URL
 * @param {string} a.authorizationEndpoint discovery から取った認可端点（**期待値の唯一の出所**）
 * @param {string} a.toolOrigin            そのツール自身の origin（redirect_uri の帰属を見る）
 */
function classifyStart({ location, authorizationEndpoint, toolOrigin }) {
  const p = parseAuthorizeUrl(location);
  if (!p.ok) return { status: 'fail', reason: p.reason };

  const expected = String(authorizationEndpoint || '').trim();
  if (expected === '') return { status: 'fail', reason: 'discovery の authorization_endpoint が空' };
  if (p.endpoint !== expected) {
    return {
      status: 'fail',
      reason: `認可端点が discovery と一致しない（実際 ${p.endpoint} / discovery ${expected}）`,
      clientId: p.clientId,
    };
  }
  if (p.clientId === '') {
    return { status: 'fail', reason: '認可 URL に client_id が無い', clientId: '' };
  }
  if (p.redirectUri === '' && p.requestUri === '') {
    return {
      status: 'fail',
      reason: '認可 URL に redirect_uri も request_uri も無い',
      clientId: p.clientId,
    };
  }
  if (p.redirectUri !== '') {
    let r;
    try {
      r = new URL(p.redirectUri);
    } catch (e) {
      return { status: 'fail', reason: `redirect_uri が URL として読めない: ${p.redirectUri}`, clientId: p.clientId };
    }
    // 🔴 realm の登録値と突き合わせない（列挙を持たない）。見るのは**帰属**だけ ——
    //    ツールが自分以外の origin へ戻す redirect を組んでいたら、それは設定事故である。
    //    登録済みかどうかは段 (b) で Keycloak 自身に判定させる。
    if (r.origin !== toolOrigin) {
      return {
        status: 'fail',
        reason: `redirect_uri がツール自身の origin を指していない（${r.origin} ≠ ${toolOrigin}）`,
        clientId: p.clientId,
        redirectUri: p.redirectUri,
      };
    }
  }
  return {
    status: 'ok',
    clientId: p.clientId,
    redirectUri: p.redirectUri,
    requestUri: p.requestUri,
    // PAR は redirect_uri を URL に載せないので、段 (a) では redirect_uri を見られない。
    // **段 (b) が Keycloak に判定させるので検証が抜けるわけではない**（押し込み済みの
    // request_uri をログインフォームまで持っていけたなら、redirect_uri は登録済みである）。
    par: p.redirectUri === '' && p.requestUri !== '',
  };
}

// ---- 段 (b) の判定: Keycloak のログインフォームが返るか ------------------------------

// Keycloak のログインフォームは `id="kc-form-login"` を持つ。テーマ・言語に依らない
// （表示文言で判定すると locale で割れる。実測: 稼働 realm の locale は ja）。
const LOGIN_FORM_MARKER = 'kc-form-login';
// エラー画面は `kc-error` を持ち、`invalid_redirect_uri` のときは HTTP 400 になる。
const ERROR_MARKER = 'kc-error';

/**
 * @param {number|string} httpStatus 認可 URL への GET の状態コード
 * @param {string} body             応答本文
 */
function classifyLoginForm(httpStatus, body) {
  const code = Number(httpStatus);
  const html = String(body || '');
  const hasForm = html.includes(LOGIN_FORM_MARKER);
  const hasError = html.includes(ERROR_MARKER);
  if (code === 200 && hasForm) return { status: 'form' };
  if (hasError) {
    return {
      status: 'error',
      reason: `Keycloak がエラー画面を返した（HTTP ${code}）。`
        + 'redirect_uri が realm の当該 client に未登録か、client が消えている',
    };
  }
  return { status: 'other', reason: `ログインフォームでもエラー画面でもない（HTTP ${code}）` };
}

// ---- ツールごとの「ログイン開始 URL の取り出し」 --------------------------------------

/** MinIO console の `/api/v1/login`（未認証で引ける）から SSO の飛び先を取る。 */
function extractMinioRedirect(text) {
  const o = safeJson(text);
  const rules = (o && o.redirectRules) || [];
  for (const r of rules) {
    if (r && typeof r.redirect === 'string' && r.redirect !== '') return r.redirect;
  }
  return '';
}

/** Vault の `auth/oidc/oidc/auth_url`（未認証で引ける）から飛び先を取る。 */
function extractVaultAuthUrl(text) {
  const o = safeJson(text);
  const url = o && o.data && o.data.auth_url;
  return typeof url === 'string' ? url : '';
}

/**
 * Wiki.js の GraphQL から **oidc ストラテジのキー**を取る。
 * 🔴 キーは seed が既存値を再利用するため環境ごとに違う。**スクリプトへ書けない。**
 */
function extractWikiOidcStrategyKey(text) {
  const o = safeJson(text);
  const list = o && o.data && o.data.authentication && o.data.authentication.activeStrategies;
  for (const s of (Array.isArray(list) ? list : [])) {
    if (s && s.strategy && s.strategy.key === 'oidc' && typeof s.key === 'string' && s.key !== '') {
      return s.key;
    }
  }
  return '';
}

/** discovery 文書から認可端点を取る（**期待値の唯一の出所**）。 */
function extractAuthorizationEndpoint(text) {
  const o = safeJson(text);
  const ep = o && o.authorization_endpoint;
  return typeof ep === 'string' ? ep : '';
}

function safeJson(text) {
  try {
    return JSON.parse(String(text == null ? '' : text));
  } catch (e) {
    return null;
  }
}

/**
 * 陰性対照の URL を組む。**realm に登録されていない redirect_uri** を渡し、
 * Keycloak が拒むこと（＝段 b の登録検査が実際に効いていること）を確かめるために使う。
 * `.invalid` は RFC 2606 の予約 TLD で、解決されることが無い。
 */
const UNREGISTERED_REDIRECT_URI = 'https://msp-verify-unregistered.invalid/callback';
function buildNegativeControlUrl(authorizationEndpoint, clientId) {
  const u = new URL(authorizationEndpoint);
  u.searchParams.set('client_id', clientId);
  u.searchParams.set('redirect_uri', UNREGISTERED_REDIRECT_URI);
  u.searchParams.set('response_type', 'code');
  u.searchParams.set('scope', 'openid');
  u.searchParams.set('state', 'verify-tool-oidc-logins');
  return u.toString();
}

/** 陰性対照の判定。**拒まれなかったら FAIL**（段 b の PASS に意味が無くなるため）。 */
function classifyNegativeControl(httpStatus, body) {
  const r = classifyLoginForm(httpStatus, body);
  if (r.status === 'form') {
    return {
      status: 'fail',
      reason: '未登録の redirect_uri でもログインフォームが返った。'
        + 'Keycloak の redirect_uri 検査が効いておらず、段 (b) の PASS は根拠にならない',
    };
  }
  if (Number(httpStatus) !== 400) {
    return {
      status: 'fail',
      reason: `未登録の redirect_uri が HTTP ${httpStatus} で返った（400 を期待）`,
    };
  }
  return { status: 'ok' };
}

// ---- CLI（シェルから 1 機能ずつ呼ぶ。Windows の `node -e` 罠を避けるためファイルで持つ） ----

// シェルへ返す複合値の区切り。**TAB ではなく US（\u001f）** —— 理由は classify-start の脚注。
const FS = '\u001f';

function readStdin() {
  try {
    return require('fs').readFileSync(0, 'utf8');
  } catch (e) {
    return '';
  }
}

function main(argv) {
  const cmd = argv[0];
  switch (cmd) {
    case 'tools':
      // シェルの反復元。**段数の単一情報源**でもある（TOTAL = 2 × 件数 + 1）。
      process.stdout.write(TOOLS.map((t) => `${t.key}\t${t.host}\t${t.start.kind}\t${t.probe}`).join('\n') + '\n');
      return 0;
    case 'start-path': {
      const t = TOOLS.find((x) => x.key === argv[1]);
      if (!t) { process.stderr.write(`unknown tool: ${argv[1]}\n`); return 1; }
      process.stdout.write(`${t.start.path || ''}\n`);
      return 0;
    }
    case 'authorization-endpoint':
      process.stdout.write(extractAuthorizationEndpoint(readStdin()) + '\n');
      return 0;
    case 'minio-redirect':
      process.stdout.write(extractMinioRedirect(readStdin()) + '\n');
      return 0;
    case 'vault-auth-url':
      process.stdout.write(extractVaultAuthUrl(readStdin()) + '\n');
      return 0;
    case 'wikijs-oidc-key':
      process.stdout.write(extractWikiOidcStrategyKey(readStdin()) + '\n');
      return 0;
    case 'classify-start': {
      const r = classifyStart({
        location: readStdin().trim(),
        authorizationEndpoint: argv[1],
        toolOrigin: argv[2],
      });
      // status FS clientId FS redirectUri FS par FS reason
      //
      // 🔴 **区切りに TAB を使わない。** TAB は IFS の空白類なので、bash の `read` が
      //    連続する区切り（＝空フィールド）を 1 つに畳み、**値が 1 つずつ手前へずれる**。
      //    実測（本 PR の実走 1 回目）: PAR の bff が `redirect_uri=par` と表示され、
      //    vault の FAIL は理由が空になった。US（\u001f）は IFS の空白類ではない。
      process.stdout.write([
        r.status, r.clientId || '', r.redirectUri || '', r.par ? 'par' : '', r.reason || '',
      ].join(FS) + '\n');
      return 0;
    }
    case 'classify-form': {
      const r = classifyLoginForm(argv[1], readStdin());
      process.stdout.write(`${r.status}${FS}${r.reason || ''}\n`);
      return 0;
    }
    case 'negative-control-url':
      process.stdout.write(buildNegativeControlUrl(argv[1], argv[2]) + '\n');
      return 0;
    case 'classify-negative': {
      const r = classifyNegativeControl(argv[1], readStdin());
      process.stdout.write(`${r.status}${FS}${r.reason || ''}\n`);
      return 0;
    }
    default:
      process.stderr.write(`usage: tool-oidc-login.js <tools|start-path|authorization-endpoint|minio-redirect|`
        + `vault-auth-url|wikijs-oidc-key|classify-start|classify-form|negative-control-url|classify-negative>\n`);
      return 1;
  }
}

module.exports = {
  TOOLS,
  FS,
  LOGIN_FORM_MARKER,
  UNREGISTERED_REDIRECT_URI,
  parseAuthorizeUrl,
  classifyStart,
  classifyLoginForm,
  classifyNegativeControl,
  extractMinioRedirect,
  extractVaultAuthUrl,
  extractWikiOidcStrategyKey,
  extractAuthorizationEndpoint,
  buildNegativeControlUrl,
};

if (require.main === module) process.exit(main(process.argv.slice(2)));
