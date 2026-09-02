#!/usr/bin/env node
'use strict';
/*
 * check-password-reset-mail.js
 * SC-15 / FR-22 / ADR-0026 / ADR-0045 決定 7・8・9（#1144 / #1143）:
 * **パスワードリセットの送出が開発環境で成立し、メール本文がリンクと有効期限だけであり、
 * かつ実在／非実在の利用者名で応答が分かれないこと**を、捕捉用 MTA の API と稼働 realm を読んで確かめる。
 *
 * ## なぜ要るか —— 「手動（実環境）」が消えない原因は捕捉先が無いことだった
 *
 * `docs/tests/SC-15_password-reset.md` の T-10 / T-16 は長らく「**手動（実環境）**」で止まっていた。
 * 理由は仕様が難しいからではなく、**メールの届く先がどこにも無かった**からである（#1144）。
 * 捕捉用 MTA（`deploy/local/infra/mailpit.yaml`）が dev 既定で居るようになったので、
 * **申請 → 送出 → 受信 → 本文**までを機械が通しで測れる。
 *
 * ## 測るもの（すべて fail-closed）
 *
 * - **T-17 送出が成立する**: 実在する利用者名で申請すると、捕捉用 MTA に **ちょうど 1 通**届く。
 *   🔴 **0 件を緑にしない。** 「届かなかった」は「送らなくてよかった」ではない。
 * - **T-16 本文はリンクと有効期限のみ**（ADR-0045 決定 7）: 本文が
 *   **リセットリンク（`action-token`）を持ち**、**有効期限（realm の
 *   `actionTokenGeneratedByUserLifespan` から導いた分数）を述べ**、
 *   **他の URL を 1 つも含まない**（＝リンクと期限以外の導線を足していない）。添付は 0 件。
 * - **宛先**: 対象利用者の登録アドレスであること（別人へ出ていない）。
 * - **T-10 存在秘匿**（#1143）: 実在／非実在の利用者名で**ステータスと本文が区別できない**
 *   （フローの識別子と、申請者自身が送った利用者名の再表示だけを伏せて比較する）。
 * - **T-20 稼働状態の組**（#1143）: **応答を測る前に**「申請の開閉 × 送出先」を見る。
 *   🔴 **開いているのに送出先が使えない組は、それ自体が漏洩である** —— 実在する利用者名のときだけ
 *   送出に失敗して 500 が返り、実在しない利用者名は 200 を返す。**その差で利用者名を列挙できる。**
 *   Keycloak の設定でこの 500 は消せない（テーマはステータスを変えられず、`reset-credential-email` は
 *   `configurable: false` / REQUIRED 固定。#1143 で実測）ので、**開くなら送出先を与える／
 *   与えられないなら閉じる**の二択になる。閉じた状態は両者に同じ 400 を返す（本文もバイト一致・実測）。
 *   🔴 **宣言が正しくてもこの組にはなる** —— Keycloak コンテナが再起動すると `kcadm` で入れた
 *   実行時の `smtpServer` は消える（H2 はコンテナ層）。だから**稼働状態を**見る。
 *
 * ## 値を書き写さない（列挙を持たない）
 *
 * realm 名・対象利用者・クライアント・エッジ URL・有効期限・捕捉用 MTA の宛先は**すべて走査して得る**。
 *   - realm / 利用者 / クライアント / 有効期限 → `deploy/keycloak/*-realm.json`
 *   - エッジ URL                              → Keycloak Deployment の `KC_HOSTNAME_URL`
 *   - 捕捉用 MTA の Service 名と HTTP ポート  → `deploy/local/infra/mailpit.yaml`
 * ここへ書き写すと、宣言を変えたときに検査が静かに空回りする（check-stack-ready.js G7 と同じ姿勢）。
 *
 * ## TLS
 *
 * 🔴 **証明書の検証を切らない。** クラスタのローカル CA（`cert-manager/local-edge-root-ca`）を
 * 取り出して検証に使う。`-k` 相当で測ると、**エッジの TLS が壊れていても緑になる**
 * （実際にそれで壊れに気付けなかった経緯がある）。
 *
 * 使い方:
 *   node scripts/check-password-reset-mail.js              # 稼働クラスタに対して測る
 *   node scripts/check-password-reset-mail.js --self-test  # 判定関数（純関数）の自己試験
 */
const fs = require('fs');
const path = require('path');
const https = require('https');
const http = require('http');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');

/** realm 宣言の在り処（realm 名・利用者・クライアント・有効期限の単一情報源）。 */
const REALM_DIR = path.join('deploy', 'keycloak');
/** 捕捉用 MTA の宣言（Service 名と HTTP ポートの単一情報源）。 */
const MAIL_CAPTURE_MANIFEST = path.join('deploy', 'local', 'infra', 'mailpit.yaml');
const MAIL_CAPTURE_NS = 'platform-infra';
/** Keycloak の Deployment（エッジ URL の単一情報源 `KC_HOSTNAME_URL` を持つ）。 */
const KEYCLOAK_NS = 'platform-infra';
const KEYCLOAK_DEPLOY = 'keycloak';
/** ローカル CA の在り処（エッジ TLS の検証に使う）。 */
const EDGE_CA_NS = 'cert-manager';
const EDGE_CA_SECRET = 'local-edge-root-ca';
/** 送出を待つ上限（SMTP は非同期である）。 */
const DELIVERY_TIMEOUT_MS = 30_000;
const DELIVERY_POLL_MS = 1_000;

// ---------------------------------------------------------------- 収集（外部依存）

function kubectl(args, opts = {}) {
  return spawnSync('kubectl', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, ...opts });
}

function hasTool(bin) {
  return spawnSync(process.platform === 'win32' ? 'where' : 'which', [bin], { encoding: 'utf8' }).status === 0;
}

/** 捕捉用 MTA の Service 名と HTTP ポートを宣言から読む（読めなければ null）。 */
function mailCaptureTarget(repoRoot = REPO_ROOT) {
  try {
    const text = fs.readFileSync(path.join(repoRoot, MAIL_CAPTURE_MANIFEST), 'utf8');
    const svc = text.split(/^---\s*$/m).find((d) => /^kind:\s*Service\s*$/m.test(d));
    if (!svc) return null;
    const name = /^\s{2}name:\s*(\S+)\s*$/m.exec(svc);
    const httpPort = /\{\s*name:\s*http,\s*port:\s*(\d+)/.exec(svc);
    return name && httpPort ? { deploy: name[1], httpPort: httpPort[1] } : null;
  } catch {
    return null;
  }
}

/** 捕捉用 MTA の API を**コンテナ内 loopback**で読む（エッジにも port-forward にも依存しない）。 */
function mailApi(target, apiPath) {
  const r = kubectl(['-n', MAIL_CAPTURE_NS, 'exec', '-i', `deploy/${target.deploy}`, '--',
    'wget', '-q', '-O-', '-T', '15', `http://127.0.0.1:${target.httpPort}${apiPath}`]);
  if (r.status !== 0) return { ok: false, error: (r.stderr || '').trim() || `mailpit API が読めない: ${apiPath}` };
  try {
    return { ok: true, value: JSON.parse(r.stdout) };
  } catch (e) {
    return { ok: false, error: `mailpit API の応答が JSON ではない (${apiPath}): ${e.message}` };
  }
}

/** realm 宣言を 1 つ読む（`deploy/keycloak/*-realm.json` がちょうど 1 件であることを要求する）。 */
function loadRealm(repoRoot = REPO_ROOT) {
  const dir = path.join(repoRoot, REALM_DIR);
  if (!fs.existsSync(dir)) return { ok: false, error: `${REALM_DIR} が無い` };
  const files = fs.readdirSync(dir).filter((n) => n.endsWith('-realm.json'));
  if (files.length !== 1) {
    return { ok: false, error: `${REALM_DIR} の *-realm.json が ${files.length} 件（1 件であることを前提にしている）` };
  }
  try {
    return { ok: true, value: JSON.parse(fs.readFileSync(path.join(dir, files[0]), 'utf8')), file: files[0] };
  } catch (e) {
    return { ok: false, error: `realm JSON を読めない: ${e.message}` };
  }
}

/** Keycloak のエッジ URL（issuer の単一情報源）。 */
function keycloakBaseUrl() {
  const r = kubectl(['get', 'deploy', KEYCLOAK_DEPLOY, '-n', KEYCLOAK_NS, '-o',
    'jsonpath={.spec.template.spec.containers[0].env[?(@.name=="KC_HOSTNAME_URL")].value}']);
  const url = String(r.stdout || '').trim();
  return r.status === 0 && url ? { ok: true, value: url.replace(/\/+$/, '') } : { ok: false, error: 'KC_HOSTNAME_URL を読めない' };
}

/** エッジ TLS を検証するためのローカル CA（**検証を切らない**）。 */
function edgeCa() {
  const r = kubectl(['-n', EDGE_CA_NS, 'get', 'secret', EDGE_CA_SECRET, '-o', 'jsonpath={.data.ca\\.crt}']);
  const b64 = String(r.stdout || '').trim();
  if (r.status !== 0 || !b64) return { ok: false, error: `${EDGE_CA_NS}/${EDGE_CA_SECRET} の ca.crt を読めない` };
  return { ok: true, value: Buffer.from(b64, 'base64').toString('utf8') };
}

/**
 * SC-15 の存在秘匿（#1143）: **稼働 realm の**「申請の開閉」と「送出先」を読む。
 *
 * 🔴 **宣言（realm.json）を見るだけでは足りない。** 稼働クラスタで実測したところ、
 * **Keycloak コンテナが再起動すると `kcadm` で入れた実行時の `smtpServer` は消える**
 * （H2 はコンテナ層にある）。宣言が正しくても、**稼働状態は脆弱な組へ戻り得る**。
 *
 * 資格情報は **Pod の env のまま**使い（`$KEYCLOAK_ADMIN_PASSWORD`）、値は返さないし出力もしない。
 * `--fields` は使わない —— **設定済みの realm に対しても `smtpServer: { }` を返す**（#1144 で実測）。
 *
 * @returns {{ok:true, resetPasswordAllowed:boolean, host:string, from:string}|{ok:false, error:string}}
 */
function runtimeResetConfig(realmName) {
  // realm 名は宣言から走査して得た値。シェルへ素で渡さないよう、英数と一部記号だけを許す。
  if (!/^[A-Za-z0-9._-]+$/.test(String(realmName || ''))) {
    return { ok: false, error: `realm 名が想定の字種でない: ${JSON.stringify(realmName)}` };
  }
  const script =
    '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master'
    + ' --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1'
    + ` && /opt/keycloak/bin/kcadm.sh get realms/${realmName}`;
  const r = kubectl(
    ['-n', KEYCLOAK_NS, 'exec', '-i', `deploy/${KEYCLOAK_DEPLOY}`, '-c', KEYCLOAK_DEPLOY, '--',
      'sh', '-c', script],
  );
  if (r.status !== 0) return { ok: false, error: `稼働 realm を読めなかった: ${(r.stderr || '').trim()}` };
  let realm;
  try {
    // kcadm はログイン行を混ぜないが、念のため最初の `{` から読む。
    const body = String(r.stdout || '');
    realm = JSON.parse(body.slice(body.indexOf('{')));
  } catch (e) {
    return { ok: false, error: `稼働 realm の応答を JSON として読めなかった: ${e.message}` };
  }
  const smtp = realm.smtpServer && typeof realm.smtpServer === 'object' ? realm.smtpServer : {};
  return {
    ok: true,
    resetPasswordAllowed: realm.resetPasswordAllowed === true,
    host: String(smtp.host || ''),
    from: String(smtp.from || ''),
  };
}

// ---------------------------------------------------------------- 判定（純粋関数）

/**
 * SC-15 の存在秘匿（#1143）: **稼働状態の**「申請の開閉 × 送出先」の組を判定する。**純関数**。
 *
 * 応答を測る**前に**この門を通す。理由は 2 つある。
 *   1. **原因を名指しできる。** T-10 が 500/200 で落ちたとき、「どの状態に居るのか」が分からないと
 *      「実装が壊れた」と「realm が脆弱な組へ戻った」を取り違える。
 *   2. **窓を短くできる。** この組は**再起動のたびに戻り得る**（実測）ので、検出は早いほどよい。
 *
 * @param {{resetPasswordAllowed:boolean, host:string, from:string}} cfg
 * @returns {string[]}
 */
function evaluateRuntimeConcealment(cfg) {
  if (!cfg.resetPasswordAllowed) return []; // 閉じている＝両者に同じ 400（実測済み）
  const missing = [cfg.host === '' ? 'host' : null, cfg.from === '' ? 'from' : null].filter(Boolean);
  if (missing.length === 0) return [];
  return [
    `[T-20] 稼働 realm が**脆弱な組**に居る: 申請は開いている（resetPasswordAllowed=true）のに`
    + ` 送出先が使えない（smtpServer.${missing.join(' / smtpServer.')} が空）。`
    + ' この組では**実在する利用者名のときだけ 500** が返り、実在しない利用者名は 200 を返す ——'
    + ' **その差だけで利用者名を列挙できる**（SC-15 の存在秘匿の破れ / #1143）。'
    + ' 🔴 **宣言が正しくてもこの状態にはなる** —— Keycloak コンテナが再起動すると kcadm で入れた'
    + ' 実行時の smtpServer は消える（H2 はコンテナ層）。realm 宣言を再インポートするか、'
    + ' 送出先を与えられないなら resetPasswordAllowed を false にして**両者に同じ応答**を返すこと'
    + '（手順は docs/operations/keycloak-smtp-relay-setup-runbook.md）。',
  ];
}

/**
 * 対象の利用者を realm 宣言から選ぶ。**サービスアカウントと無効な利用者は除く**。
 * @returns {{username:string, email:string}|null}
 */
function pickTargetUser(realm) {
  const users = Array.isArray(realm && realm.users) ? realm.users : [];
  const u = users.find((x) => x
    && x.enabled !== false
    && !x.serviceAccountClientId
    && !/^service-account-/.test(String(x.username || ''))
    && typeof x.email === 'string' && x.email !== '');
  return u ? { username: u.username, email: u.email } : null;
}

/** 認可要求に使う public client を realm 宣言から選ぶ（PKCE は常に付ける）。 */
function pickPublicClient(realm) {
  const clients = Array.isArray(realm && realm.clients) ? realm.clients : [];
  const c = clients.find((x) => x && x.publicClient === true && x.standardFlowEnabled !== false
    && Array.isArray(x.redirectUris) && x.redirectUris.length > 0);
  if (!c) return null;
  // `https://localhost/*` のようなワイルドカードから具体の URI を作る。
  const redirectUri = String(c.redirectUris[0]).replace(/\*+$/, '');
  return { clientId: c.clientId, redirectUri };
}

/** リセットリンクの有効期限（分）。realm から導く（値を書き写さない）。 */
function linkLifetimeMinutes(realm) {
  const sec = realm && realm.actionTokenGeneratedByUserLifespan;
  return Number.isInteger(sec) && sec > 0 ? Math.round(sec / 60) : null;
}

/**
 * 応答本文を**利用者名の有無で変わらないはずの形**へ正規化する（T-10 の比較器）。
 *
 * 落とすもの: ①フローの識別子（`session_code` / `execution` / `tab_id` / `state` / `code` / nonce・UUID）
 * ②Keycloak が画面へ埋める不透明トークン ③**申請者が自分で入力した利用者名の再表示**。
 * 🔴 ③を落とすのは手心ではない —— 入力欄へ返るのは**申請者自身が送った文字列**であり、
 * サーバが知っている事実ではない。ここを残すと比較は必ず不一致になり、**検査が常に赤で無意味**になる。
 * ①②③以外の差（文言・状態・導線）が残れば、それは**登録の有無を漏らしている**。
 *
 * @param {string} html 応答本文
 * @param {string} submitted 申請した利用者名（再表示を伏せるため）
 */
function normalizeConcealmentBody(html, submitted) {
  let s = String(html || '');
  if (submitted) s = s.split(submitted).join('<submitted-username>');
  return s
    .replace(/(session_code|execution|tab_id|client_data|code|nonce|state)=[^&"'\s]*/g, '$1=<opaque>')
    .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '<uuid>')
    .replace(/"[A-Za-z0-9_-]{11}"/g, '"<opaque>"');
}

/**
 * T-10（存在秘匿）: 実在／非実在の申請の応答が**区別できない**こと。**純関数**。
 *
 * 本 PR（#1144）が固定するのは**送出経路が生きているとき**だけである。
 * **送出経路が死んでいるときの同値性は #1143 の射程**であり、同じ比較器をそちらが使う。
 *
 * @param {{existingStatus:number, absentStatus:number, existingBody:string, absentBody:string,
 *          absentDelivered:number}} input
 * @returns {string[]}
 */
function evaluateConcealment(input) {
  const failures = [];
  if (input.existingStatus !== input.absentStatus) {
    failures.push(
      `[T-10] 応答ステータスが登録の有無で分かれている（実在 ${input.existingStatus} / 非実在 ${input.absentStatus}）。`
      + ' この差だけで利用者名を 1 リクエストずつ列挙できる（SC-15 の存在秘匿の破れ）。',
    );
  }
  if (input.existingBody !== input.absentBody) {
    failures.push(
      '[T-10] 応答本文が登録の有無で分かれている（フローの識別子と入力の再表示を伏せたうえで比較した）。'
      + ' 画面に出る文言・状態・導線は、アドレスが登録されているかを教えてはならない。',
    );
  }
  // 非実在の利用者に対してメールを出していたら、それ自体が漏洩である（別人の受信箱に届く）。
  if (input.absentDelivered !== 0) {
    failures.push(`[T-10] 非実在の利用者名の申請で ${input.absentDelivered} 通が送出された（期待 0 通）。`);
  }
  return failures;
}

/** 本文に現れる URL をすべて拾う（末尾の句読点は落とす）。 */
function extractUrls(text) {
  const found = String(text || '').match(/https?:\/\/[^\s<>"'）)]+/g) || [];
  return found.map((u) => u.replace(/[.,、。]+$/, ''));
}

/**
 * 受信したメールが ADR-0045 決定 7 / SC-15 の要件を満たすかを判定する。**純関数**。
 *
 * @param {{requestStatus:number, delivered:number, recipients:string[], expectedEmail:string,
 *          subject:string, text:string, attachments:number,
 *          lifetimeMinutes:(number|null), issuerBase:string}} input
 * @returns {string[]} 失敗メッセージ
 */
function evaluateResetMail(input) {
  const failures = [];

  // T-17: 申請そのものが成立していること。**0 件走査は fail-closed**。
  if (input.requestStatus !== 200) {
    failures.push(
      `[T-17] リセット申請の応答が ${input.requestStatus} である（期待 200）。`
      + ' 送出経路が死んでいるか、申請フォームの解釈が違っている。',
    );
  }
  if (input.delivered !== 1) {
    failures.push(
      `[T-17] 捕捉用 MTA に届いたメールが ${input.delivered} 通である（期待 1 通）。`
      + ' 0 通は「送らなくてよかった」ではなく「送出が成立していない」である（ADR-0045 決定 9 の検証台が働いていない）。',
    );
    return failures; // 届いていないなら本文は測れない（同じことを 2 回言わない）
  }

  // 宛先: 別人へ出ていないこと。
  if (!input.recipients.includes(input.expectedEmail)) {
    failures.push(
      `[T-17] 宛先が対象利用者の登録アドレスではない（期待 ${input.expectedEmail} / 実際 ${JSON.stringify(input.recipients)}）。`,
    );
  }

  // T-16 (1): リセットリンクを持つこと。
  const urls = extractUrls(input.text);
  const resetLinks = urls.filter((u) => u.startsWith(input.issuerBase) && u.includes('login-actions/action-token'));
  if (resetLinks.length === 0) {
    failures.push(
      `[T-16] 本文にリセットリンク（${input.issuerBase}/... login-actions/action-token）が無い。`
      + ' メールが届いてもリセットは完了できない。',
    );
  }

  // T-16 (2): 有効期限を述べていること（分数は realm から導く。ここへ書き写さない）。
  if (input.lifetimeMinutes === null) {
    failures.push('[T-16] realm から有効期限（actionTokenGeneratedByUserLifespan）を読めなかった（既定値へ落とさない）。');
  } else if (!new RegExp(`(?<![0-9])${input.lifetimeMinutes}(?![0-9])`).test(String(input.text))) {
    failures.push(
      `[T-16] 本文が有効期限（${input.lifetimeMinutes} 分）を述べていない（ADR-0045 決定 7）。`
      + ' 期限を知らせないリンクは、切れたときに利用者が原因を判断できない。',
    );
  }

  // T-16 (3): リンクと期限**以外の導線**を足していないこと。
  const extra = urls.filter((u) => !resetLinks.includes(u));
  if (extra.length > 0) {
    failures.push(
      `[T-16] 本文にリセットリンク以外の URL が ${extra.length} 件ある: ${extra.join(' , ')}。`
      + ' ADR-0045 決定 7 はリセットメールを「リンクと有効期限のみ」と定めている（メールは ABAC の外側へ出る）。',
    );
  }
  if (input.attachments !== 0) {
    failures.push(`[T-16] 添付が ${input.attachments} 件ある（期待 0 件。決定 7）。`);
  }
  return failures;
}

// ---------------------------------------------------------------- HTTP（cookie を保つ最小クライアント）

/** 極小の cookie jar。Keycloak のフローは `AUTH_SESSION_ID` 等を跨いで保つ必要がある。 */
function createJar() {
  const jar = new Map();
  return {
    apply(headers) {
      if (jar.size === 0) return headers;
      return { ...headers, cookie: [...jar.entries()].map(([k, v]) => `${k}=${v}`).join('; ') };
    },
    absorb(res) {
      for (const line of res.headers['set-cookie'] || []) {
        const m = /^([^=]+)=([^;]*)/.exec(line);
        if (m) jar.set(m[1], m[2]);
      }
    },
  };
}

/**
 * RFC 6761 §6.3 は「`localhost.` および `.localhost` で終わる名前は**ループバックへ解決しなければ
 * ならない**」と定めている。**Windows の getaddrinfo はこれをサブドメインに対して守らない**
 * （`keycloak.localhost` が `ENOTFOUND` になる。curl は解決するので気付きにくい）。
 * hosts ファイルへ書くのは運用者の手作業であり、**書き忘れが「エッジが落ちている」と同じ見え方になる**。
 * ここでは RFC の要求どおりに落とす —— **他の名前は OS の解決に委ねる**（勝手な宛先を作らない）。
 */
function loopbackAwareLookup(hostname, options, callback) {
  const dns = require('dns');
  const cb = typeof options === 'function' ? options : callback;
  const opts = typeof options === 'function' ? {} : options;
  dns.lookup(hostname, opts, (err, ...rest) => {
    if (!err) return cb(null, ...rest);
    if (!/(^|\.)localhost\.?$/i.test(hostname)) return cb(err);
    return opts && opts.all ? cb(null, [{ address: '127.0.0.1', family: 4 }]) : cb(null, '127.0.0.1', 4);
  });
}

function request(url, { method = 'GET', body = null, jar, ca }) {
  const u = new URL(url);
  const mod = u.protocol === 'https:' ? https : http;
  const headers = jar.apply({
    'user-agent': 'check-password-reset-mail',
    accept: 'text/html',
    ...(body === null ? {} : {
      'content-type': 'application/x-www-form-urlencoded',
      'content-length': Buffer.byteLength(body),
    }),
  });
  return new Promise((resolve, reject) => {
    const req = mod.request(u, { method, headers, lookup: loopbackAwareLookup, ...(ca ? { ca } : {}) }, (res) => {
      jar.absorb(res);
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({
        status: res.statusCode,
        location: res.headers.location || null,
        body: Buffer.concat(chunks).toString('utf8'),
      }));
    });
    req.on('error', reject);
    if (body !== null) req.write(body);
    req.end();
  });
}

function decodeEntities(s) {
  return String(s).replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#x2F;/g, '/');
}

/**
 * SC-15 の `reset-credentials` フローを 1 回通す（**利用者が通る経路そのもの**）。
 * 認可要求 → 申請画面 → POST まで。cookie は 1 回のフローごとに新しい jar で保つ。
 * @returns {Promise<{status:number, body:string}|{error:string}>}
 */
async function submitResetRequest({ base, realmName, client, ca, username }) {
  const jar = createJar();
  const authUrl = `${base}/realms/${encodeURIComponent(realmName)}/protocol/openid-connect/auth`
    + `?client_id=${encodeURIComponent(client.clientId)}`
    + `&redirect_uri=${encodeURIComponent(client.redirectUri)}`
    + '&response_type=code&scope=openid&state=reset-mail-check'
    // PKCE を必須にしている realm があるので常に付ける（不要な realm では無視される）。
    + '&code_challenge_method=S256&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM';
  const login = await request(authUrl, { jar, ca });
  if (login.status !== 200) {
    return { error: `ログイン画面が ${login.status} を返した（${login.location || ''}）。申請導線へ到達できない。` };
  }
  const resetHref = /login-actions\/reset-credentials[^"']*/.exec(login.body);
  if (!resetHref) {
    return { error: 'ログイン画面に reset-credentials への導線が無い（resetPasswordAllowed が false か、テーマが導線を落としている）。' };
  }
  const resetUrl = `${base}/realms/${encodeURIComponent(realmName)}/${decodeEntities(resetHref[0])}`;
  const resetPage = await request(resetUrl, { jar, ca });
  const action = /action="([^"]*login-actions\/reset-credentials[^"]*)"/.exec(resetPage.body);
  if (!action) return { error: `申請フォームの action を読めなかった（reset ページ status=${resetPage.status}）。` };
  return request(decodeEntities(action[1]), {
    method: 'POST', jar, ca, body: `username=${encodeURIComponent(username)}`,
  });
}

/**
 * realm に**実在しない**利用者名を作る。宣言と突き合わせて実在しないことを確かめる
 * （偶然の一致で「非実在のつもりが実在していた」測定になるのを防ぐ）。
 */
function makeAbsentUsername(realm) {
  const taken = new Set((realm.users || []).map((u) => String(u.username || '')));
  for (let i = 0; i < 100; i += 1) {
    const candidate = `no-such-user-${Math.random().toString(36).slice(2, 10)}`;
    if (!taken.has(candidate)) return candidate;
  }
  return null;
}

// ---------------------------------------------------------------- 実行

async function run() {
  const failures = [];
  const notices = [];

  if (!hasTool('kubectl')) {
    return { failures: ['[前提] kubectl が無い。稼働クラスタに対して測る検査なので、逃げ道の環境変数は置かない。'], notices };
  }

  const realmRes = loadRealm();
  if (!realmRes.ok) return { failures: [`[前提] ${realmRes.error}`], notices };
  const realm = realmRes.value;
  const realmName = realm.realm;
  const user = pickTargetUser(realm);
  const client = pickPublicClient(realm);
  const lifetimeMinutes = linkLifetimeMinutes(realm);
  if (!user) return { failures: ['[前提] realm 宣言に、メールアドレスを持つ対話利用者が居ない（0 件走査を緑にしない）。'], notices };
  if (!client) return { failures: ['[前提] realm 宣言に public client（standard flow）が無い。申請画面へ到達できない。'], notices };

  const target = mailCaptureTarget();
  if (!target) return { failures: [`[前提] ${MAIL_CAPTURE_MANIFEST} から捕捉用 MTA の Service を読めない。`], notices };

  const baseRes = keycloakBaseUrl();
  if (!baseRes.ok) return { failures: [`[前提] ${baseRes.error}`], notices };
  const base = baseRes.value;

  const caRes = edgeCa();
  if (!caRes.ok) {
    return {
      failures: [`[前提] ${caRes.error} —— **証明書の検証は切らない**（切ると、エッジの TLS が壊れていても緑になる）。`],
      notices,
    };
  }
  const ca = caRes.value;

  notices.push(`[check-password-reset-mail] realm=${realmName} / 対象=${user.username} / client=${client.clientId}`
    + ` / edge=${base} / 捕捉用 MTA=${MAIL_CAPTURE_NS}/${target.deploy}:${target.httpPort}`);

  // 0) SC-15 の存在秘匿（#1143）: **応答を測る前に**稼働状態の組を見る。
  //    落ちたときに「実装が壊れた」と「realm が脆弱な組へ戻った」を取り違えないため。
  const runtimeCfg = runtimeResetConfig(realmName);
  if (!runtimeCfg.ok) {
    return { failures: [`[前提] ${runtimeCfg.error}`], notices };
  }
  notices.push(`[check-password-reset-mail] 稼働 realm: resetPasswordAllowed=${runtimeCfg.resetPasswordAllowed}`
    + ` / smtpServer.host=${runtimeCfg.host || '(空)'} / smtpServer.from の長さ=${runtimeCfg.from.length}`);
  const concealFailures = evaluateRuntimeConcealment(runtimeCfg);
  if (concealFailures.length > 0) {
    // 脆弱な組に居るなら、応答を測るまでもなく漏洩している。**先に落とす**（測って 500 を出させない）。
    return { failures: concealFailures, notices };
  }
  if (!runtimeCfg.resetPasswordAllowed) {
    // 閉じている＝両者に同じ 400（実測済み）。秘匿としては健全だが、**送出の試験はできない**。
    notices.push('[check-password-reset-mail] 申請が閉じている（resetPasswordAllowed=false）ので、'
      + '送出とメール本文の試験（T-16 / T-17）は行わない。**存在秘匿としては健全な状態である**。');
    return { failures, notices, verified: 'closed' };
  }

  // 1) 受信前の件数を控える（増分で測る。DELETE は busybox wget では打てないので消さない）。
  const before = mailApi(target, '/api/v1/info');
  if (!before.ok) return { failures: [`[前提] ${before.error}`], notices };
  const beforeCount = Number(before.value.Messages) || 0;

  // 2) SC-15 の reset-credentials フローを通す（**利用者が通る経路そのもの**）。
  const submitReset = (username) => submitResetRequest({ base, realmName, client, ca, username });
  const submit = await submitReset(user.username);
  if (submit.error) return { failures: [`[前提] ${submit.error}`], notices };

  // 3) 送出を待つ（SMTP は非同期）。**待たずに 0 件を見て緑にしない。**
  let afterCount = beforeCount;
  const deadline = Date.now() + DELIVERY_TIMEOUT_MS;
  /* eslint-disable no-await-in-loop */
  while (Date.now() < deadline) {
    const info = mailApi(target, '/api/v1/info');
    if (!info.ok) return { failures: [`[前提] ${info.error}`], notices };
    afterCount = Number(info.value.Messages) || 0;
    if (afterCount > beforeCount) break;
    await new Promise((r) => setTimeout(r, DELIVERY_POLL_MS));
  }
  /* eslint-enable no-await-in-loop */
  const delivered = afterCount - beforeCount;

  // 4) 届いた 1 通を読む。
  let mail = { subject: '', text: '', recipients: [], attachments: 0 };
  if (delivered === 1) {
    const list = mailApi(target, '/api/v1/messages?limit=1');
    if (!list.ok) return { failures: [`[前提] ${list.error}`], notices };
    const head = (list.value.messages || [])[0];
    if (!head) return { failures: ['[前提] 件数は増えたのに一覧が空である（捕捉用 MTA の応答が矛盾している）。'], notices };
    const full = mailApi(target, `/api/v1/message/${encodeURIComponent(head.ID)}`);
    if (!full.ok) return { failures: [`[前提] ${full.error}`], notices };
    mail = {
      subject: String(full.value.Subject || ''),
      text: String(full.value.Text || ''),
      recipients: (full.value.To || []).map((t) => String(t.Address || '')),
      attachments: Array.isArray(full.value.Attachments) ? full.value.Attachments.length : 0,
    };
    notices.push(`[check-password-reset-mail] 受信: subject=${JSON.stringify(mail.subject)} / 宛先=${mail.recipients.join(',')}`);
  }

  failures.push(...evaluateResetMail({
    requestStatus: submit.status,
    delivered,
    recipients: mail.recipients,
    expectedEmail: user.email,
    subject: mail.subject,
    text: mail.text,
    attachments: mail.attachments,
    lifetimeMinutes,
    issuerBase: base,
  }));

  // 5) T-10（存在秘匿）: **同じ器で**非実在の利用者名を申請し、応答が区別できないことを測る。
  //    🔴 本 PR が固定するのは**送出経路が生きているとき**だけである（#1144 の射程）。
  //    送出が死んでいるときの同値性 —— 実在する利用者だけ 500 になる形 —— は **#1143** が
  //    同じ比較器（normalizeConcealmentBody / evaluateConcealment）を使って足す。
  const absentUsername = makeAbsentUsername(realm);
  if (!absentUsername) {
    failures.push('[T-10] realm に実在しない利用者名を作れなかった（陰性対照を置けないので緑にしない）。');
  } else {
    const beforeAbsent = mailApi(target, '/api/v1/info');
    if (!beforeAbsent.ok) return { failures: [...failures, `[前提] ${beforeAbsent.error}`], notices };
    const absent = await submitReset(absentUsername);
    if (absent.error) {
      failures.push(`[T-10] 非実在の利用者名で申請できなかった: ${absent.error}`);
    } else {
      // 送出は非同期なので、実在側と同じ待ち時間だけ見てから件数を比べる（早すぎる 0 件で緑にしない）。
      await new Promise((r) => setTimeout(r, DELIVERY_POLL_MS * 3));
      const afterAbsent = mailApi(target, '/api/v1/info');
      if (!afterAbsent.ok) return { failures: [...failures, `[前提] ${afterAbsent.error}`], notices };
      failures.push(...evaluateConcealment({
        existingStatus: submit.status,
        absentStatus: absent.status,
        existingBody: normalizeConcealmentBody(submit.body, user.username),
        absentBody: normalizeConcealmentBody(absent.body, absentUsername),
        absentDelivered: (Number(afterAbsent.value.Messages) || 0) - (Number(beforeAbsent.value.Messages) || 0),
      }));
      notices.push(`[check-password-reset-mail] T-10: 実在=${submit.status} / 非実在=${absent.status}`
        + `（非実在の利用者名 ${absentUsername} は realm 宣言と突き合わせて不在を確認済み）`);
    }
  }
  return { failures, notices, verified: 'open' };
}

// ---------------------------------------------------------------- 自己試験

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  const base = 'https://keycloak.localhost';
  const good = {
    requestStatus: 200,
    delivered: 1,
    recipients: ['poc-user@example.com'],
    expectedEmail: 'poc-user@example.com',
    subject: 'パスワードのリセット',
    text: `以下のリンクをクリックしてください。\n\n${base}/realms/platform/login-actions/action-token?key=abc&tab_id=x\n\n`
      + 'このリンクは30 分だけ有効です。\n',
    attachments: 0,
    lifetimeMinutes: 30,
    issuerBase: base,
  };

  ok('健全なリセットメールは通る（陽性対照）', () => {
    assert.deepStrictEqual(evaluateResetMail(good), [], '健全な本文を落としている');
  });

  ok('T-17: 申請の応答が 200 でなければ落ちる', () => {
    assert.ok(evaluateResetMail({ ...good, requestStatus: 500 }).some((f) => f.includes('[T-17]')));
  });

  ok('T-17: 1 通も届かなければ落ちる（0 件を緑にしない）', () => {
    const f = evaluateResetMail({ ...good, delivered: 0 });
    assert.strictEqual(f.length, 1, '0 通を通している');
    assert.ok(f[0].includes('送出が成立していない'), '0 通の意味を書いていない');
  });

  ok('T-17: 2 通以上でも落ちる（重複送出を見逃さない）', () => {
    assert.strictEqual(evaluateResetMail({ ...good, delivered: 2 }).length, 1, '重複送出を通している');
  });

  ok('T-17: 別人の宛先へ出ていたら落ちる', () => {
    assert.ok(evaluateResetMail({ ...good, recipients: ['someone-else@example.com'] })
      .some((f) => f.includes('宛先')), '別人宛を通している');
  });

  ok('T-16: リセットリンクが無ければ落ちる', () => {
    assert.ok(evaluateResetMail({ ...good, text: 'このリンクは30 分だけ有効です。' })
      .some((f) => f.includes('リセットリンク')), 'リンク無しを通している');
  });

  ok('T-16: 有効期限を述べていなければ落ちる（ADR-0045 決定 7）', () => {
    const text = good.text.replace('このリンクは30 分だけ有効です。', 'すぐに開いてください。');
    assert.ok(evaluateResetMail({ ...good, text }).some((f) => f.includes('有効期限')), '期限無しを通している');
  });

  ok('T-16: 有効期限は realm から導く（realm が 15 分なら 30 分の本文は落ちる）', () => {
    assert.ok(evaluateResetMail({ ...good, lifetimeMinutes: 15 }).some((f) => f.includes('15 分')),
      '本文と realm の食い違いを通している');
    assert.ok(evaluateResetMail({ ...good, lifetimeMinutes: null }).some((f) => f.includes('既定値へ落とさない')),
      '読めなかったのに通している');
  });

  ok('T-16: リンク以外の URL を足すと落ちる（本文はリンクと期限のみ）', () => {
    const text = `${good.text}\n詳しくは https://example.com/help を参照してください。`;
    const f = evaluateResetMail({ ...good, text });
    assert.strictEqual(f.length, 1, '余分な導線を通している');
    assert.ok(f[0].includes('https://example.com/help'), 'どの URL が余分かを示していない');
  });

  ok('T-16: 添付があれば落ちる', () => {
    assert.ok(evaluateResetMail({ ...good, attachments: 1 }).some((f) => f.includes('添付')), '添付を通している');
  });

  // ---- T-10（存在秘匿）の比較器 ----
  const concealOk = {
    existingStatus: 200,
    absentStatus: 200,
    existingBody: normalizeConcealmentBody('<input value="poc-user"><a href="x?session_code=AAA">go</a>', 'poc-user'),
    absentBody: normalizeConcealmentBody('<input value="ghost-42"><a href="x?session_code=BBB">go</a>', 'ghost-42'),
    absentDelivered: 0,
  };

  ok('T-10: 識別子と入力の再表示だけが違う応答は「区別できない」と判定する（陰性対照）', () => {
    assert.deepStrictEqual(evaluateConcealment(concealOk), [], '正当な同値を落としている');
  });

  ok('T-10: ステータスが分かれると落ちる（500 / 200 の差＝利用者名の判定器）', () => {
    const f = evaluateConcealment({ ...concealOk, existingStatus: 500 });
    assert.ok(f.some((x) => x.includes('列挙')), 'ステータス差の意味を書いていない');
  });

  ok('T-10: 文言が分かれると落ちる（本文の差でも列挙できる）', () => {
    const absentBody = normalizeConcealmentBody('<input value="ghost-42">そのアドレスは登録されていません', 'ghost-42');
    assert.ok(evaluateConcealment({ ...concealOk, absentBody }).some((x) => x.includes('応答本文')),
      '本文差を通している');
  });

  ok('T-10: 非実在の利用者へ送出したら落ちる', () => {
    assert.ok(evaluateConcealment({ ...concealOk, absentDelivered: 1 }).some((x) => x.includes('非実在')),
      '別人への送出を通している');
  });

  ok('T-10: 正規化は入力の再表示だけを伏せる（本当の差は残す）', () => {
    const a = normalizeConcealmentBody('value="alice" ok', 'alice');
    const b = normalizeConcealmentBody('value="bob" ok', 'bob');
    assert.strictEqual(a, b, '入力の再表示を伏せられていない（検査が常に赤になる）');
    const c = normalizeConcealmentBody('value="bob" 登録がありません', 'bob');
    assert.notStrictEqual(a, c, '文言の差まで伏せている（漏洩を見逃す）');
  });

  // ---- T-20（#1143）: 稼働状態の「申請の開閉 × 送出先」の組 ----
  ok('T-20: 開いていて送出先も使えるなら通る（陰性対照 / 状態 A）', () => {
    assert.deepStrictEqual(
      evaluateRuntimeConcealment({ resetPasswordAllowed: true, host: 'mailpit', from: 'noreply@x' }),
      [], '健全な組を落としている');
  });

  ok('T-20: 開いたまま送出先が空なら落ちる（状態 B＝実在だけ 500 になる組）', () => {
    const f = evaluateRuntimeConcealment({ resetPasswordAllowed: true, host: '', from: '' });
    assert.strictEqual(f.length, 1, '脆弱な組を通している');
    assert.ok(f[0].includes('列挙'), '漏洩であることを書いていない');
    assert.ok(f[0].includes('再起動'), '**宣言が正しくてもこの状態になる**理由を書いていない');
  });

  ok('T-20: from だけ空・host だけ空のどちらでも落ちる（宛先だけでは送出は成立しない）', () => {
    assert.strictEqual(evaluateRuntimeConcealment({ resetPasswordAllowed: true, host: 'mailpit', from: '' }).length, 1);
    assert.strictEqual(evaluateRuntimeConcealment({ resetPasswordAllowed: true, host: '', from: 'noreply@x' }).length, 1);
  });

  ok('T-20: 閉じていれば送出先が空でも落ちない（状態 D＝両者に同じ 400。実測済み）', () => {
    assert.deepStrictEqual(
      evaluateRuntimeConcealment({ resetPasswordAllowed: false, host: '', from: '' }), [],
      '閉じた状態を漏洩として落としている');
  });

  ok('T-10: 非実在の利用者名は realm 宣言と突き合わせて作る', () => {
    const r = loadRealm();
    assert.ok(r.ok, '実データの realm を読めない');
    const name = makeAbsentUsername(r.value);
    assert.ok(name, '非実在の利用者名を作れない');
    assert.ok(!(r.value.users || []).some((u) => u.username === name), '作った名前が実在している');
  });

  ok('宣言から対象を選べる（実データ・ラチェット）', () => {
    const r = loadRealm();
    assert.ok(r.ok, `realm 宣言を読めない: ${r.error || ''}`);
    assert.ok(pickTargetUser(r.value), '対象利用者を選べない');
    assert.ok(pickPublicClient(r.value), 'public client を選べない');
    assert.ok(linkLifetimeMinutes(r.value) > 0, '有効期限を realm から導けない');
    assert.ok(mailCaptureTarget(), `捕捉用 MTA の宣言を読めない（${MAIL_CAPTURE_MANIFEST}）`);
  });

  console.log(`[check-password-reset-mail] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

async function main() {
  const argv = process.argv.slice(2);
  const unknown = argv.filter((a) => a !== '--self-test');
  if (unknown.length > 0) {
    console.error(`[check-password-reset-mail] 未知の引数: ${unknown.join(' ')}`);
    process.exit(2);
  }
  if (argv.includes('--self-test')) { selfTest(); return; }

  const r = await run();
  for (const notice of r.notices) console.log(notice);
  if (r.failures.length > 0) {
    console.error(`[check-password-reset-mail] ${r.failures.length} 件の失敗:`);
    for (const f of r.failures) console.error(`\n  - ${f}`);
    process.exit(1);
  }
  // 🔴 **測っていないことを「OK」と言わない。** 申請が閉じている状態では送出そのものを試していない。
  console.log(r.verified === 'closed'
    ? '[check-password-reset-mail] OK: 申請が閉じており、実在／非実在で応答が分かれない（存在秘匿は成立）。'
      + ' **送出とメール本文（T-16 / T-17）は測っていない。**'
    : '[check-password-reset-mail] OK: 申請 → 送出 → 捕捉用 MTA での受信 → 本文（リンクと有効期限のみ）'
      + '、および実在／非実在の応答同値性（T-10）が成立している。');
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`[check-password-reset-mail] 実行時エラー: ${e && e.stack ? e.stack : e}`);
    process.exit(1);
  });
}

module.exports = {
  pickTargetUser,
  pickPublicClient,
  linkLifetimeMinutes,
  extractUrls,
  evaluateResetMail,
  normalizeConcealmentBody,
  evaluateConcealment,
  evaluateRuntimeConcealment,
  makeAbsentUsername,
  submitResetRequest,
  mailCaptureTarget,
  loadRealm,
  MAIL_CAPTURE_MANIFEST,
};
