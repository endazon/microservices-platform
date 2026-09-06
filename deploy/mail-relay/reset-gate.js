#!/usr/bin/env node
'use strict';
/*
 * SC-15, FR-05, NFR-09, ADR-0026 / ADR-0045 / ADR-0078 決定 4, IADR-0404 (#1245 PR-C):
 * **近接 MTA へ投函できないとき、パスワードリセットの申請を機械で閉じる門。**
 *
 *   Deployment のループとして常駐する（deploy/mail-relay/reset-gate.yaml）。node:22-alpine・
 *   **外部依存ゼロ**（Node 標準の net / fetch / fs のみ）。合成監視のプローブ
 *   （deploy/local/synthetic-monitor/probe.js）と realm の後追い（reconcile-realm.js）と同じ作法である。
 *
 * ## なぜ要るか —— 投函できないと「実在する利用者だけ 500」になる
 *
 * Keycloak の送出は**同期**であり、送出に失敗すると認証器が 500 のエラーページを組み立てる。
 * 非実在の利用者名は送出そのものをしないので 200 で返る。**この差だけで利用者名を列挙できる**
 * （SC-15 の存在秘匿の破れ。#1143 で実測）。近接 MTA（キュー付き Postfix）を挟んだことで
 * **上流の停止**は応答に出なくなったが、**近接 MTA 自身が居ない / SYN が落ちる / 投函を拒む**の
 * 3 つ（IADR-0404 の窓 W1 / W1' / W2）は残っている。
 *
 * 申請を閉じれば実在・非実在の双方に**同じ 400 と同じ本文**が返る（実測済み）。本門はそれを
 * **人手（runbook §0）ではなく機械で**行う —— ADR-0078 決定 4 が求めたものである。
 *
 * ## なぜ「能動プローブ」なのか（Pod の Ready でも監査イベントでもない）
 *
 * - **Pod が Ready でも投函は拒まれ得る**（452 キュー満杯・554 差出人拒否・宛先の DNS 検証）。
 *   #1307 は「relay は生きていて投函だけを拒む」状態を develop の CI で実測している。
 * - **監査イベント（SEND_RESET_PASSWORD の error）を待つのは反応的である。** 最初の 1 件は
 *   既に 500 を返しており、その 1 件で利用者名が 1 つ漏れる。
 *
 * したがって本門は **Keycloak が通るのと同じ SMTP 取引**を、利用者の要求より先に自分で打つ。
 *
 * ## 🔴 プローブは RCPT で終える（DATA を送らない）
 *
 * 近接 MTA は `RELAYHOST` へ**宛先によらず全部**中継する（`smtpd_relay_restrictions=permit`）。
 * `DATA` まで送るとプローブのメールが上流（dev では捕捉用 MTA）へ流れ込み、
 * `scripts/check-password-reset-mail.js` の「ちょうど 1 通」（T-17）を壊す。
 * **RCPT で終えても検知能力は落ちない**（むしろ #1307 の実測に照らすと上がる）:
 *
 *   - 接続拒否 / SYN 落ち  → connect の失敗・タイムアウト（W1 / W1'）
 *   - キュー満杯・容量不足 → `452 4.3.1` は **MAIL FROM** で返る（W2）
 *   - 差出人の拒否        → `check_sender_access` は `smtpd_recipient_restrictions` の中＝**RCPT**（IADR-0404 V12）
 *   - 宛先 DNS 検証の再混入 → `reject_unknown_recipient_domain` は **RCPT**（#1307 の再発検知）
 *   - 宛先構文の拒否      → `reject_non_fqdn_recipient` は **RCPT**（W2 の残る入口）
 *
 * 🔴 上は上流イメージのソースと Postfix の仕様からの**導出**であり、稼働クラスタで打っていない
 *    （#1245 PR-D で測る）。**「動くはず」を実測として書かない。**
 *
 * ## 判定は非対称である（閉じるのは速く、開けるのは慎重に）
 *
 * **1 回の失敗で閉じ、連続 N 回の成功で宣言値へ戻す。** 門は「開ける主体」ではなく
 * 「**宣言どおりに戻す**主体」であり、次の 2 つでは決して開けない:
 *   - **宣言（realm JSON）が `resetPasswordAllowed: false`** —— 意図して閉じてある
 *   - **稼働の属性 `reset-gate.state` が `closed` でない** —— 門ではなく**人が手で閉じた**
 *
 * ## 後追い Job との競合（🔴 ここを外すと門ごと無効になる）
 *
 * realm の後追い（`deploy/local/keycloak-setup/reconcile-realm.js`）は宣言との差分を PUT で当てる。
 * 門が閉じた直後に Job が開き直すと、**両方が「直した」と記録して静かに競合する**。
 * IADR-0404 決定 5 はこれを避けるため `resetPasswordAllowed` を**条件つき門所有**にした ——
 * Job が差分から除くのは「宣言 true・稼働 false・`attributes["reset-gate.state"] === "closed"`」の
 * 1 組だけである。**したがって本門は状態を必ず同じ PUT で属性へ書く。**
 * 属性名の綴りが 1 文字でもズレると Job が開き直す。`scripts/reset-gate.test.js` が
 * **両モジュールの定数を突き合わせて**固定している。
 *
 * ## 権限（IADR-0329 の位置からの、面積を限った後退）
 *
 * 専用の機密クライアント `reset-gate` の service account に `realm-management` の
 * **`view-realm` ＋ `manage-realm` だけ**を与える（利用者が 2026-09-05 に承諾）。
 * `PUT /admin/realms/{realm}` は `manage-realm` を要求し、**`resetPasswordAllowed` 1 項目にだけ効く
 * 細粒度権限は Keycloak 24 に無い**ためこれが下限である。危険（同じ権限で `smtpServer` を外へ向ける・
 * `bruteForceProtected` を切ることもできる）を薄めないために:
 *   1. 本コードは PUT 本文で **`resetPasswordAllowed` と `attributes["reset-gate.*"]` の 4 つしか差し替えない**
 *   2. **宣言の門**（`scripts/check-realm-constraints.js`）が「`manage-realm` を持つ SA は 1 つだけ」を固定する
 *   3. secret はリポジトリに置かない（Secret `reset-gate-oidc`）
 *
 * ## 値の既定を持たない
 *
 * プローブ周期・再開の連続成功回数・タイムアウトは**マニフェストが与える**。ADR-0078 決定 1 は
 * 所要時間の閾値を「実測してから」と定めており、**実装が数字を決めない**（probe.js と同じ作法）。
 * 未設定なら**起動しない**。
 *
 * 使い方:
 *   node reset-gate.js              # ループを回す（配備時）
 *   node reset-gate.js --self-test  # 純関数の自己試験（外部 I/O は一切しない）
 */

const fs = require('fs');
const net = require('net');
const path = require('path');

// ---------------------------------------------------------------- 契約（reconcile-realm.js と共有）

/**
 * 門が状態を書き込む realm 属性の綴り。**後追い Job（reconcile-realm.js）の
 * `GATE_STATE_ATTRIBUTE` と一字一句同じでなければならない**（違うと Job が開き直す）。
 * 🔴 realm 宣言（realm JSON）へは書かない —— 書くと `attributes` が宣言所有になり、
 *    Job が `closed` を `open` へ戻す。この属性は**実行時にだけ存在する**。
 */
const GATE_STATE_ATTRIBUTE = 'reset-gate.state';
const GATE_REASON_ATTRIBUTE = 'reset-gate.reason';
const GATE_SINCE_ATTRIBUTE = 'reset-gate.since';
const GATE_STATE_CLOSED = 'closed';
const GATE_STATE_OPEN = 'open';

/** 門が PUT 本文で差し替えてよいキー（realm トップレベル ＋ 属性）。**これ以外は触らない。** */
const GATE_WRITABLE_REALM_KEYS = new Set(['resetPasswordAllowed']);
const GATE_WRITABLE_ATTRIBUTES = new Set([
  GATE_STATE_ATTRIBUTE, GATE_REASON_ATTRIBUTE, GATE_SINCE_ATTRIBUTE,
]);

/**
 * realm の PUT 本文へ載せないキー（別端点で当てるコレクション）。
 * reconcile-realm.js の `REALM_COLLECTION_KEYS` と**同じ役割**である。ここへ載せると
 * Keycloak が丸ごと作り直そうとする／黙って落とすため、read-modify-write の外に置く。
 */
const REALM_COLLECTION_KEYS = new Set([
  'users', 'clients', 'clientScopes', 'roles', 'groups', 'components', 'requiredActions',
  'authenticationFlows', 'authenticatorConfig', 'identityProviders', 'identityProviderMappers',
  'scopeMappings', 'clientScopeMappings', 'defaultDefaultClientScopes', 'defaultOptionalClientScopes',
  'protocolMappers', 'applications', 'oauthClients', 'federatedUsers', 'userFederationProviders',
  'userFederationMappers', 'clientPolicies', 'clientProfiles', 'organizations', 'defaultRole',
  'defaultRoles', 'defaultGroups', 'localizationTexts',
]);

// ---------------------------------------------------------------- 判定（純粋関数）

const asStr = (v) => (v === null || v === undefined ? '' : String(v));

/**
 * 次に打つ手を決める。**純関数**（時計もネットワークも見ない）。
 *
 * 非対称である —— **失敗 1 回で閉じ、連続成功で開ける**。閉じるのを遅らせた分がそのまま
 * 「実在する利用者だけ 500 が返る窓」になるからであり、逆に開けるのを急ぐと
 * 復旧の揺らぎで開閉を繰り返す（admin event が溢れ、利用者から見た挙動も安定しない）。
 *
 * @param {object} s
 * @param {boolean} s.probeOk      直近のプローブが成功したか
 * @param {string}  s.probeReason  失敗の理由（成功時は空文字でよい）
 * @param {boolean} s.declaredAllowed 宣言（realm JSON）の resetPasswordAllowed
 * @param {boolean} s.liveAllowed     稼働 realm の resetPasswordAllowed
 * @param {string}  s.gateState       稼働 realm の attributes["reset-gate.state"]（無ければ空文字）
 * @param {number}  s.consecutiveSuccesses 連続成功回数（今回を含む）
 * @param {number}  s.reopenAfter    開け直すのに要る連続成功回数
 * @returns {{action:'close'|'reopen'|'none', reason:string}}
 */
function decide(s) {
  const gateState = asStr(s.gateState);

  if (!s.probeOk) {
    // 🔴 1 回で閉じる。「2 回連続したら」にすると、その分だけ窓が伸びる。
    if (s.liveAllowed) {
      return { action: 'close', reason: `probe failed: ${asStr(s.probeReason) || 'unknown'}` };
    }
    // 既に閉じている。**再 PUT しない** —— 毎周期 admin event を増やしても情報は増えない。
    return { action: 'none', reason: 'already closed' };
  }

  if (s.liveAllowed) return { action: 'none', reason: 'open and healthy' };

  // ここから下は「プローブは通るのに稼働が閉じている」＝開け直す候補である。
  if (!s.declaredAllowed) {
    // 🔴 宣言が false。**門は開ける主体ではない。** 宣言どおりに戻すだけである。
    return { action: 'none', reason: 'declared closed (gate never opens what the declaration closes)' };
  }
  if (gateState !== GATE_STATE_CLOSED) {
    // 🔴 門が閉じたのではない（人が手で閉じた・属性が消えた）。**他人の意思を上書きしない。**
    //    後追い Job 側も同じ条件でこれを drift として扱う（IADR-0404 決定 5）。
    return { action: 'none', reason: 'closed by someone else (no gate marker)' };
  }
  if (!(s.consecutiveSuccesses >= s.reopenAfter)) {
    return {
      action: 'none',
      reason: `waiting for consecutive successes (${s.consecutiveSuccesses}/${s.reopenAfter})`,
    };
  }
  return { action: 'reopen', reason: `relay reachable for ${s.consecutiveSuccesses} consecutive probes` };
}

/**
 * PUT する realm 表現を組み立てる。**純関数**。
 *
 * 🔴 **read-modify-write であり、差し替えるのは 4 つだけ**である
 * （`resetPasswordAllowed` と `attributes` の `reset-gate.{state,reason,since}`）。
 * `manage-realm` は realm 設定を丸ごと書ける権限なので、**本文に入るものを機械で狭めておく**
 * ——「うっかり別の設定を持っていく」経路を作らないためである（`scripts/reset-gate.test.js` が固定）。
 *
 * @param {object} liveRealm GET /admin/realms/{realm} の応答
 * @param {{allowed:boolean, reason:string, at:string}} change
 * @returns {object} PUT 本文
 */
function buildRealmUpdate(liveRealm, change) {
  const live = liveRealm && typeof liveRealm === 'object' ? liveRealm : {};
  const body = {};
  for (const [k, v] of Object.entries(live)) {
    if (REALM_COLLECTION_KEYS.has(k)) continue;
    body[k] = v;
  }
  body.resetPasswordAllowed = change.allowed === true;
  const attrs = live.attributes && typeof live.attributes === 'object' && !Array.isArray(live.attributes)
    ? { ...live.attributes }
    : {};
  attrs[GATE_STATE_ATTRIBUTE] = change.allowed === true ? GATE_STATE_OPEN : GATE_STATE_CLOSED;
  attrs[GATE_REASON_ATTRIBUTE] = asStr(change.reason);
  attrs[GATE_SINCE_ATTRIBUTE] = asStr(change.at);
  body.attributes = attrs;
  return body;
}

/**
 * 宣言（ConfigMap でマウントした realm JSON 群）から対象 realm の `resetPasswordAllowed` を読む。
 * **純関数**（ファイルの内容は呼び出し側が読んで渡す）。
 *
 * 🔴 読めない・見つからないときは `null` を返し、**呼び出し側は開け直しをしない**
 *   （「宣言が分からないので開ける」は fail-open である）。
 *
 * @param {{name:string, text:string}[]} files
 * @param {string} realmName
 * @returns {boolean|null}
 */
function declaredResetPasswordAllowed(files, realmName) {
  for (const f of files || []) {
    let doc;
    try {
      doc = JSON.parse(f.text);
    } catch {
      continue; // 宣言でないファイルが混ざっていても止まらない（ConfigMap は複数 realm を持ち得る）
    }
    if (doc && doc.realm === realmName) return doc.resetPasswordAllowed === true;
  }
  return null;
}

/**
 * SMTP の応答（複数行あり得る）から、完結した 1 応答を切り出す。**純関数**。
 *
 * 複数行応答は `250-...` が続き `250 ...`（4 文字目が空白）で終わる。
 * まだ完結していなければ `null` を返す。
 *
 * @param {string} buffer 受信済みの文字列
 * @returns {{code:number, text:string, rest:string}|null}
 */
function takeSmtpReply(buffer) {
  // 🔴 ［2026-09-07 / #1245 PR-C レビュー］**行の中身で位置を引かない。**
  // 以前は `buffer.indexOf(lines[i])` で終端を求めていたが、継続行（`250-...`）が
  // 最終行と同じ文字列を部分列として含むと**手前の位置を返す**。実際の Postfix 応答では
  // 起きにくいが、位置は**行の長さの累積**で決まるので、そちらで求めるほうが頑健である。
  const lines = buffer.split(/\r?\n/);
  let offset = 0; // buffer 内での lines[i] の開始位置
  for (let i = 0; i < lines.length; i += 1) {
    const m = /^(\d{3})(?:[ \t]|$)/.exec(lines[i]);
    if (m) {
      return {
        code: Number(m[1]),
        text: lines.slice(0, i + 1).join('\n').trim(),
        rest: buffer.slice(offset + lines[i].length).replace(/^\r?\n/, ''),
      };
    }
    // 区切りは `\r\n` か `\n` のどちらでもよい。実際に buffer に在ったほうを数える。
    offset += lines[i].length + (buffer.startsWith('\r\n', offset + lines[i].length) ? 2 : 1);
  }
  return null;
}

/** SMTP の応答コードが「その段として成功」か。2xx（RSET/QUIT/MAIL/RCPT）と 220（バナー）を許す。 */
function smtpAccepted(step, code) {
  if (step === 'greeting') return code === 220;
  return code >= 200 && code < 300;
}

// ---------------------------------------------------------------- プローブ（外部 I/O）

/**
 * 近接 MTA へ SMTP 取引を打つ。**DATA は送らない**（頭部の注記を参照）。
 * @returns {Promise<{ok:true}|{ok:false, reason:string}>}
 */
function probeRelay({ host, port, mailFrom, rcptTo, timeoutMs }) {
  return new Promise((resolve) => {
    // 「EHLO → MAIL FROM → RCPT TO → RSET → QUIT」。RSET は取引を捨てることを相手へ明示する
    // （キューにも上流にも何も残さない）。
    const steps = [
      { name: 'greeting', send: null },
      { name: 'ehlo', send: 'EHLO reset-gate' },
      { name: 'mail-from', send: `MAIL FROM:<${mailFrom}>` },
      { name: 'rcpt-to', send: `RCPT TO:<${rcptTo}>` },
      { name: 'rset', send: 'RSET' },
      { name: 'quit', send: 'QUIT' },
    ];
    let index = 0;
    let buffer = '';
    let settled = false;

    const socket = net.connect({ host, port });
    // 🔴 Keycloak と同じ待ち時間で待つ（DefaultEmailSenderProvider の 10 000 ms 固定）。
    //    ここを短くすると「Keycloak は待てるのに門だけ諦める」偽陽性になる。
    socket.setTimeout(timeoutMs);

    const finish = (result) => {
      if (settled) return;
      settled = true;
      socket.removeAllListeners();
      socket.destroy();
      resolve(result);
    };

    socket.on('error', (err) => finish({ ok: false, reason: `${err.code || 'error'}: ${err.message}` }));
    socket.on('timeout', () => finish({ ok: false, reason: `timeout after ${timeoutMs}ms` }));
    socket.on('close', () => finish({ ok: false, reason: 'connection closed before QUIT' }));

    const pump = () => {
      for (;;) {
        const reply = takeSmtpReply(buffer);
        if (!reply) return;
        buffer = reply.rest;
        const step = steps[index];
        if (!smtpAccepted(step.name, reply.code)) {
          // 🔴 4xx も 5xx も等しく失敗にする。452（キュー満杯）は一時的だが、
          //    その間に届く申請は実在利用者だけ 500 になる ＝ 秘匿としては恒久の失敗と同じである。
          finish({ ok: false, reason: `${step.name} rejected with ${reply.code}: ${reply.text.split('\n')[0]}` });
          return;
        }
        index += 1;
        if (index >= steps.length) { finish({ ok: true }); return; }
        socket.write(`${steps[index].send}\r\n`);
      }
    };

    socket.on('data', (chunk) => { buffer += chunk.toString('utf8'); pump(); });
  });
}

// ---------------------------------------------------------------- Keycloak Admin REST（外部 I/O）

async function fetchAccessToken({ kcUrl, realm, clientId, clientSecret }) {
  const res = await fetch(`${kcUrl}/realms/${realm}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'client_credentials', client_id: clientId, client_secret: clientSecret,
    }),
  });
  if (!res.ok) throw new Error(`token endpoint returned ${res.status}`);
  const json = await res.json();
  if (!json.access_token) throw new Error('token endpoint returned no access_token');
  return json.access_token;
}

async function getRealm({ kcUrl, realm, token }) {
  const res = await fetch(`${kcUrl}/admin/realms/${realm}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  if (!res.ok) throw new Error(`GET realm returned ${res.status}`);
  return res.json();
}

async function putRealm({ kcUrl, realm, token, body }) {
  const res = await fetch(`${kcUrl}/admin/realms/${realm}`, {
    method: 'PUT',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT realm returned ${res.status}`);
}

// ---------------------------------------------------------------- 自己試験（純関数のみ）

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  const base = {
    probeOk: true, probeReason: '', declaredAllowed: true, liveAllowed: true,
    gateState: GATE_STATE_OPEN, consecutiveSuccesses: 9, reopenAfter: 3,
  };

  ok('健全なら何もしない（陰性対照）', () => {
    assert.strictEqual(decide(base).action, 'none');
  });

  ok('🔴 失敗 1 回で閉じる（2 回目を待たない）', () => {
    const d = decide({ ...base, probeOk: false, probeReason: 'ECONNREFUSED' });
    assert.strictEqual(d.action, 'close');
    assert.ok(d.reason.includes('ECONNREFUSED'), '理由を運べていない');
  });

  ok('既に閉じているときは再 PUT しない（admin event を毎周期増やさない）', () => {
    assert.strictEqual(
      decide({ ...base, probeOk: false, liveAllowed: false, gateState: GATE_STATE_CLOSED }).action, 'none');
  });

  ok('連続成功が足りない間は開けない', () => {
    const d = decide({
      ...base, liveAllowed: false, gateState: GATE_STATE_CLOSED, consecutiveSuccesses: 2, reopenAfter: 3,
    });
    assert.strictEqual(d.action, 'none');
    assert.ok(/2\/3/.test(d.reason), '進捗を出していない');
  });

  ok('連続成功が満ちたら宣言値へ戻す', () => {
    assert.strictEqual(decide({
      ...base, liveAllowed: false, gateState: GATE_STATE_CLOSED, consecutiveSuccesses: 3, reopenAfter: 3,
    }).action, 'reopen');
  });

  ok('🔴 宣言が false なら決して開けない（門は開ける主体ではない）', () => {
    assert.strictEqual(decide({
      ...base, declaredAllowed: false, liveAllowed: false,
      gateState: GATE_STATE_CLOSED, consecutiveSuccesses: 99, reopenAfter: 3,
    }).action, 'none');
  });

  ok('🔴 門の標識が無い（人が手で閉じた）なら開けない', () => {
    for (const gateState of ['', GATE_STATE_OPEN, 'CLOSED']) {
      assert.strictEqual(decide({
        ...base, liveAllowed: false, gateState, consecutiveSuccesses: 99, reopenAfter: 3,
      }).action, 'none', `gateState=${JSON.stringify(gateState)} で開けている`);
    }
  });

  // ---- PUT 本文の面積（manage-realm を持つ以上、ここが実質的な権限の天井である）----
  const liveRealm = {
    id: 'abc', realm: 'platform', resetPasswordAllowed: true, bruteForceProtected: true,
    smtpServer: { host: 'mail-relay', port: '587' }, attributes: { other: 'keep-me' },
    users: [{ username: 'x' }], clients: [{ clientId: 'y' }],
  };

  ok('🔴 PUT 本文は 4 つ（resetPasswordAllowed ＋ reset-gate.* 3 つ）しか変えない', () => {
    const body = buildRealmUpdate(liveRealm, { allowed: false, reason: 'probe failed', at: 'T' });
    for (const k of Object.keys(body)) {
      if (k === 'resetPasswordAllowed' || k === 'attributes') continue;
      assert.deepStrictEqual(body[k], liveRealm[k], `${k} を書き換えている`);
    }
    const changed = Object.keys(body.attributes)
      .filter((k) => body.attributes[k] !== liveRealm.attributes[k]);
    assert.deepStrictEqual(changed.sort(), [...GATE_WRITABLE_ATTRIBUTES].sort(), '属性の面積が違う');
    assert.strictEqual(body.attributes.other, 'keep-me', '他人の属性を落としている');
    assert.strictEqual(body.resetPasswordAllowed, false);
    assert.strictEqual(body.attributes[GATE_STATE_ATTRIBUTE], GATE_STATE_CLOSED);
    assert.strictEqual(GATE_WRITABLE_REALM_KEYS.size + GATE_WRITABLE_ATTRIBUTES.size, 4);
  });

  ok('PUT 本文にコレクションを載せない（別端点で当てるもの）', () => {
    const body = buildRealmUpdate(liveRealm, { allowed: true, reason: 'ok', at: 'T' });
    assert.ok(!('users' in body) && !('clients' in body), 'コレクションを載せている');
    assert.strictEqual(body.attributes[GATE_STATE_ATTRIBUTE], GATE_STATE_OPEN, '開けたのに closed のまま');
  });

  ok('attributes が無い realm でも壊れない', () => {
    const body = buildRealmUpdate({ realm: 'platform' }, { allowed: false, reason: 'r', at: 'T' });
    assert.strictEqual(body.attributes[GATE_STATE_ATTRIBUTE], GATE_STATE_CLOSED);
  });

  // ---- 宣言の読み取り ----
  ok('宣言から対象 realm の resetPasswordAllowed を読む（複数 realm から選ぶ）', () => {
    const files = [
      { name: 'a.json', text: JSON.stringify({ realm: 'other', resetPasswordAllowed: false }) },
      { name: 'b.json', text: JSON.stringify({ realm: 'platform', resetPasswordAllowed: true }) },
    ];
    assert.strictEqual(declaredResetPasswordAllowed(files, 'platform'), true);
    assert.strictEqual(declaredResetPasswordAllowed(files, 'other'), false);
  });

  ok('🔴 宣言を読めないときは null（開ける方へ倒さない）', () => {
    assert.strictEqual(declaredResetPasswordAllowed([{ name: 'x', text: 'not json' }], 'platform'), null);
    assert.strictEqual(declaredResetPasswordAllowed([], 'platform'), null);
  });

  // ---- SMTP の応答の切り出し ----
  ok('複数行応答は最終行まで 1 応答として読む', () => {
    const r = takeSmtpReply('250-mail-relay\r\n250-PIPELINING\r\n250 8BITMIME\r\n');
    assert.strictEqual(r.code, 250);
    assert.ok(r.text.includes('8BITMIME'));
  });

  ok('未完結なら null（途中で判定しない）', () => {
    assert.strictEqual(takeSmtpReply('250-mail-relay\r\n250-PIPELIN'), null);
  });

  // 🔴 ［#1245 PR-C レビュー］継続行が最終行を**部分列として含む**とき、
  // 行の中身で位置を引く実装（`buffer.indexOf(lines[i])`）は手前の位置を返し、
  // `rest` に応答の残骸が混じって次の段の読み取りがずれる。
  ok('🔴 継続行が最終行を部分列として含んでも、次の応答の切り出しがずれない', () => {
    // `250-250 OK` の中に、最終行 `250 OK` がそのまま含まれている。
    const r = takeSmtpReply('250-250 OK\r\n250 OK\r\n220 next\r\n');
    assert.strictEqual(r.code, 250);
    assert.strictEqual(r.rest, '220 next\r\n', `rest がずれている: ${JSON.stringify(r.rest)}`);
    // 陽性対照: 続きを読むと次の応答がそのまま取れる。
    assert.strictEqual(takeSmtpReply(r.rest).code, 220);
  });

  ok('区切りが LF だけでも rest がずれない（CRLF を前提にしない）', () => {
    const r = takeSmtpReply('250 OK\n220 next\n');
    assert.strictEqual(r.code, 250);
    assert.strictEqual(r.rest, '220 next\n');
  });

  ok('🔴 4xx も 5xx も成功にしない（452 キュー満杯・554 差出人拒否・556 宛先拒否）', () => {
    for (const code of [421, 450, 452, 550, 554, 556]) {
      assert.strictEqual(smtpAccepted('mail-from', code), false, `${code} を通している`);
      assert.strictEqual(smtpAccepted('rcpt-to', code), false, `${code} を通している`);
    }
    assert.strictEqual(smtpAccepted('greeting', 220), true);
    assert.strictEqual(smtpAccepted('greeting', 250), false, 'バナーで 250 を許している');
    assert.strictEqual(smtpAccepted('rcpt-to', 250), true);
  });

  console.log(`[reset-gate] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- 配線（main）

function required(name) {
  const value = process.env[name];
  if (!value) {
    console.error(`[reset-gate] 必須の環境変数 ${name} が未設定である。起動しない。`);
    process.exit(1);
  }
  return value;
}

function requiredPositiveNumber(name) {
  const n = Number(required(name));
  if (!Number.isFinite(n) || n <= 0) {
    console.error(`[reset-gate] ${name} は正の数でなければならない。起動しない。`);
    process.exit(1);
  }
  return n;
}

function readDeclaredRealms(dir) {
  try {
    return fs.readdirSync(dir)
      .filter((n) => n.endsWith('.json'))
      .map((n) => ({ name: n, text: fs.readFileSync(path.join(dir, n), 'utf8') }));
  } catch (e) {
    console.error(`[reset-gate] 宣言（${dir}）を読めない: ${e.message}`);
    return [];
  }
}

async function main() {
  if (process.argv.slice(2).includes('--self-test')) { selfTest(); return; }

  const cfg = {
    kcUrl: required('KC_URL'),
    realm: required('KC_REALM'),
    clientId: required('GATE_CLIENT_ID'),
    clientSecret: required('GATE_CLIENT_SECRET'),
    realmDir: required('REALM_DIR'),
    relayHost: required('RELAY_HOST'),
    relayPort: requiredPositiveNumber('RELAY_PORT'),
    mailFrom: required('PROBE_MAIL_FROM'),
    rcptTo: required('PROBE_RCPT_TO'),
    // 🔴 既定値を置かない（ADR-0078 決定 1 は所要時間の閾値を「実測してから」と定めている）。
    timeoutMs: requiredPositiveNumber('PROBE_TIMEOUT_MS'),
    intervalSeconds: requiredPositiveNumber('PROBE_INTERVAL_SECONDS'),
    reopenAfter: requiredPositiveNumber('REOPEN_AFTER_SUCCESSES'),
  };

  console.log(`[reset-gate] 起動: relay=${cfg.relayHost}:${cfg.relayPort} realm=${cfg.realm}`
    + ` interval=${cfg.intervalSeconds}s timeout=${cfg.timeoutMs}ms reopenAfter=${cfg.reopenAfter}`
    + ' / 失敗 1 回で閉じ、連続成功で宣言値へ戻す（宣言が false なら開けない）');

  let consecutiveSuccesses = 0;

  const tick = async () => {
    const probe = await probeRelay({
      host: cfg.relayHost, port: cfg.relayPort,
      mailFrom: cfg.mailFrom, rcptTo: cfg.rcptTo, timeoutMs: cfg.timeoutMs,
    });
    consecutiveSuccesses = probe.ok ? consecutiveSuccesses + 1 : 0;

    const declared = declaredResetPasswordAllowed(readDeclaredRealms(cfg.realmDir), cfg.realm);
    if (declared === null) {
      // 宣言を読めない。**閉じる方向の判断は続けられる**（fail-closed 側）が、開け直しはしない。
      console.error('[reset-gate] 宣言の realm を読めない。開け直しは行わない（fail-closed）。');
    }

    const token = await fetchAccessToken(cfg);
    const live = await getRealm({ ...cfg, token });
    const attrs = live && typeof live.attributes === 'object' && live.attributes ? live.attributes : {};

    const d = decide({
      probeOk: probe.ok,
      probeReason: probe.ok ? '' : probe.reason,
      declaredAllowed: declared === true,
      liveAllowed: live.resetPasswordAllowed === true,
      gateState: asStr(attrs[GATE_STATE_ATTRIBUTE]),
      consecutiveSuccesses,
      reopenAfter: cfg.reopenAfter,
    });

    if (d.action === 'none') return;

    const allowed = d.action === 'reopen';
    await putRealm({
      ...cfg,
      token,
      body: buildRealmUpdate(live, { allowed, reason: d.reason, at: new Date().toISOString() }),
    });
    // 🔴 値も本文も出さない（操作の種類と理由だけ）。realm には秘匿値が入り得る。
    console.log(`[reset-gate] ${d.action}: resetPasswordAllowed=${allowed} 理由=${d.reason}`);
  };

  for (;;) {
    try {
      // eslint-disable-next-line no-await-in-loop
      await tick();
    } catch (err) {
      // 🔴 **失敗しても回り続ける。** 門が止まると窓が開いたままになる（不在は観測側で見せる）。
      console.error(`[reset-gate] tick failed: ${err.message}`);
    }
    // eslint-disable-next-line no-await-in-loop
    await new Promise((resolve) => { setTimeout(resolve, cfg.intervalSeconds * 1000); });
  }
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`[reset-gate] 実行時エラー: ${e && e.stack ? e.stack : e}`);
    process.exit(1);
  });
}

module.exports = {
  decide,
  buildRealmUpdate,
  declaredResetPasswordAllowed,
  takeSmtpReply,
  smtpAccepted,
  GATE_STATE_ATTRIBUTE,
  GATE_REASON_ATTRIBUTE,
  GATE_SINCE_ATTRIBUTE,
  GATE_STATE_CLOSED,
  GATE_STATE_OPEN,
  GATE_WRITABLE_REALM_KEYS,
  GATE_WRITABLE_ATTRIBUTES,
  REALM_COLLECTION_KEYS,
};
