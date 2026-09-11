#!/usr/bin/env node
'use strict';
/*
 * check-login-existence-disclosure.js
 * SC-13 / FR-05 / ADR-0026 / ADR-0078 決定 1（#1245 PR-0）:
 * **ログイン経路の応答が、利用者名が実在するかどうかで区別できないこと**を、
 * 実在する利用者名と実在しない利用者名で**対にして**測る。
 *
 * ## なぜ要るか —— 「Keycloak 既定挙動だから大丈夫」は導出であって実測ではない
 *
 * 計画 ADR-0078 決定 1 は、存在秘匿の統制を「**応答の区別不能性**（ステータス・本文・所要時間）」と定め、
 * **射程に認証系の経路（ログインとパスワードリセット申請）を含める**と明文化した。
 * 同 §残るもの は「**ログインのステータスコードは実測していない。決定 1 の射程には入るが、
 * リセット申請と同じ形の漏洩があるかは確かめていない**」と書いている。
 *
 * 🔴 **リセット申請側も「Keycloak 既定」のままで漏れていた** —— 送出先が使えない状態では
 * 実在する利用者名だけ 500、実在しないものは 200 を返し、**その差で利用者名を列挙できた**（#1143 の実測）。
 * ログイン側だけが導出で安全だと言える理由は無い。**だから測る。**
 *
 * ## 測るもの
 *
 * 対にするのは「**失敗するログイン**」である。成功するログインは実在側にしか起こり得ないので
 * 対にならない（成功した資格情報 POST は 302 ＋ 空本文であることを `verify-oidc-edge-flow.sh` が実測済み）。
 *
 * | 側 | 利用者名 | 資格情報 | 期待 |
 * | --- | --- | --- | --- |
 * | 実在（**陽性対照**） | realm 宣言に在る対話利用者 | **誤ったもの** | 200 ＋ ログイン画面の再描画 |
 * | 非実在（**陰性対照**） | realm 宣言と突き合わせて不在を確認した名前 | **同じ誤ったもの** | 同上 |
 *
 * - **判定する面（不一致は fail）**: HTTP ステータス／応答本文（正規化後）／リダイレクト先（`Location`）。
 * - 🔴 **判定しない面**: **所要時間。出すだけである。**
 *   ADR-0078 決定 1 は「**本 ADR は閾値を定めない**」と明記している。実装側で数字を置けば、
 *   それが確定値として固まる（IADR-0421 決定 6 が暫定値へ「暫定」と書き続けたのと同じ姿勢）。
 *
 * ## 🔴 測り方の事故を、測る前に塞ぐ
 *
 * #1245 の実測（2026-09-10）は、**21 文字の非実在名と 5 文字の実在名を比べて「本文が 16 バイト違う」**
 * という結果を得た。**16 は名前の長さの差そのもの**であり、存在の漏れではなく測り方の誤りだった。
 * 申請した利用者名は応答へそのまま反映されるので、**長さの違う名前を比べれば本文長は必ず違う。**
 * 本スクリプトは**バイト長の一致を前提条件として先に落とす**（測ってから気付く形にしない）。
 *
 * ## 🔴 brute-force の計数を使い切らない
 *
 * realm は `bruteForceProtected` / `failureFactor` を宣言している。失敗するログインを繰り返すと
 * **実在側の利用者だけが一時ロックされる** —— ①測定が途中から別の状態を測ることになり、
 * ②その利用者でログインする後段の門を巻き込む。
 * したがって**実在側へ与える失敗は `failureFactor` 未満**に抑える（**値は realm から導き、書き写さない**）。
 * 連続する失敗の間隔も空ける（`quickLoginCheckMilliSeconds` の「速すぎる失敗」判定を踏まないため）。
 *
 * 🔴 **ロックアウトそのものは測らない。** 「実在する利用者だけがロックされる」は
 * **もう 1 つの存在判定器**だが、意図的にロックを作ると使い捨てスタックの後段と再実行の前提が変わる。
 * 稼働クラスタでの意図的な状態作りは **#1245 PR-D の形**である（本スクリプトは導出を出力に書くだけ）。
 *
 * ## 値を書き写さない
 *
 * realm 名・対象利用者・標準フローのクライアント（#1413: public でなくてよい）・エッジ URL・ローカル CA は**すべて走査して得る**
 * （`check-password-reset-mail.js` と同じ作法）。持っている定数は**上流の既定値**と
 * **プロトコル上の固定文字列**だけである。
 *
 * ## 借りているもの（複製しない）
 *
 * 本文の正規化規則・cookie の扱い・HTTP 器・エッジ URL / ローカル CA の取得は
 * `check-password-reset-mail.js` から **require して借りる**。同じ統制を隣の経路で測るのに
 * 比較器が 2 つあると、片方だけが直る（**正規化規則の正本は 1 つ**）。
 *
 * 使い方:
 *   node scripts/check-login-existence-disclosure.js              # 稼働クラスタに対して測る
 *   node scripts/check-login-existence-disclosure.js --self-test  # 判定関数（純関数）の自己試験
 */
const {
  loadRealm,
  pickTargetUser,
  pickBrowserFlowClient,
  hasTool,
  keycloakBaseUrl,
  edgeCa,
  createJar,
  request,
  decodeEntities,
  normalizeConcealmentBody,
} = require('./check-password-reset-mail');

const TAG = '[check-login-existence-disclosure]';

/**
 * 両側に使う**誤った資格情報**。
 * 🔴 **秘密ではない。** realm のどの利用者にも設定されていない固定文字列であり、
 * **両側で同じものを使う**ことが要点である（片側だけ別の文字列にすると、長さと内容の差が本文比較へ混入する）。
 */
const WRONG_CREDENTIAL = 'invalid-credential-for-msp-1245-pr0';

/**
 * Keycloak の `quickLoginCheckMilliSeconds` の**上流既定値**（ミリ秒）。
 * realm がこの項目を宣言していないときだけ使う。
 * 出典: Keycloak の realm 表現の既定（`RealmRepresentation` / Admin Console「Quick login check milliseconds」）。
 * 🔴 **宣言が在るならそちらが勝つ**（値を書き写さない作法）。
 */
const KEYCLOAK_DEFAULT_QUICK_LOGIN_CHECK_MS = 1000;

/** 「速すぎる失敗」判定の境界を確実に外すための上乗せ（ミリ秒）。 */
const QUICK_LOGIN_MARGIN_MS = 250;

/**
 * `bruteForceProtected` が false のときの標本数。
 * 🔴 **この場合 realm から導ける上限は無い**（ロックが無いので計数の制約が無い）。
 * よってこれは**測定の都合で決めた数**であり、realm の宣言由来ではない。
 */
const UNPROTECTED_SAMPLE_COUNT = 5;

/** 非実在名を作るときの文字（ASCII のみ＝**文字数とバイト長が一致する**）。 */
const ABSENT_NAME_ALPHABET = 'abcdefghijklmnopqrstuvwxyz0123456789';

// ---------------------------------------------------------------- 純関数（判定・導出）

/**
 * 1 回の実行で**実在側へ与えてよい失敗の回数**を realm から導く。
 *
 * - `bruteForceProtected` が true で `failureFactor` が 2 以上 → **`failureFactor - 1`**（ロックの手前で止まる）
 * - `bruteForceProtected` が false → `UNPROTECTED_SAMPLE_COUNT`（導ける上限が無い）
 * - それ以外（宣言が読めない・`failureFactor` が 1 以下） → **1（保守側へ倒す）**
 *
 * @param {object} realm realm 宣言
 * @returns {number} 1 以上の整数
 */
function loginProbeBudget(realm) {
  const protectedOn = realm && realm.bruteForceProtected === true;
  if (!protectedOn) return UNPROTECTED_SAMPLE_COUNT;
  const factor = realm.failureFactor;
  if (Number.isInteger(factor) && factor >= 2) return factor - 1;
  return 1;
}

/**
 * 同じ利用者に対する連続した失敗の**最小間隔**（ミリ秒）を realm から導く。
 * Keycloak は `quickLoginCheckMilliSeconds` 以内に続く失敗を「速すぎる」と見て一時ロックを掛ける。
 *
 * @param {object} realm realm 宣言
 * @returns {number}
 */
function probeSpacingMs(realm) {
  const declared = realm && realm.quickLoginCheckMilliSeconds;
  const base = Number.isInteger(declared) && declared > 0 ? declared : KEYCLOAK_DEFAULT_QUICK_LOGIN_CHECK_MS;
  return base + QUICK_LOGIN_MARGIN_MS;
}

/**
 * realm に**実在しない**利用者名を、**指定したバイト長ちょうど**で作る。
 *
 * 🔴 **長さを指定できることが要点である**（#1245 の測り方の失敗を塞ぐ）。
 * 既存の `makeAbsentUsername` は固定接頭辞つきで長さを選べない。
 *
 * @param {object} realm realm 宣言
 * @param {number} length 作りたいバイト長
 * @param {() => number} [rng] 乱数（試験で差し替える）
 * @returns {string|null} 作れなければ null（**呼び出し側は緑にしない**）
 */
function makeAbsentUsernameOfLength(realm, length, rng = Math.random) {
  if (!Number.isInteger(length) || length < 1) return null;
  const taken = new Set(((realm && realm.users) || []).map((u) => String((u && u.username) || '')));
  for (let attempt = 0; attempt < 200; attempt += 1) {
    let candidate = '';
    for (let i = 0; i < length; i += 1) {
      candidate += ABSENT_NAME_ALPHABET[Math.floor(rng() * ABSENT_NAME_ALPHABET.length) % ABSENT_NAME_ALPHABET.length];
    }
    if (!taken.has(candidate)) return candidate;
  }
  return null;
}

/** `Location` を、利用者名の有無で変わらないはずの形へ正規化する（本文と同じ器を使う）。 */
function normalizeLoginLocation(location, submitted) {
  if (location === null || location === undefined) return null;
  return normalizeConcealmentBody(String(location), submitted);
}

/**
 * 所要時間の要約。**判定には使わない**（ADR-0078 決定 1 は閾値を定めていない）。
 * @param {number[]} samples
 * @returns {{n:number, min:number, median:number, max:number}|null}
 */
function summarizeTimings(samples) {
  const xs = (samples || []).filter((x) => typeof x === 'number' && Number.isFinite(x)).slice().sort((a, b) => a - b);
  if (xs.length === 0) return null;
  const mid = Math.floor(xs.length / 2);
  const median = xs.length % 2 === 1 ? xs[mid] : (xs[mid - 1] + xs[mid]) / 2;
  return { n: xs.length, min: xs[0], median, max: xs[xs.length - 1] };
}

/**
 * 対の前提を確かめる（**測る前に**落とす）。
 *
 * @param {{existingUsername:string, absentUsername:string, realmUsernames:string[]}} input
 * @returns {string[]}
 */
function evaluateProbePairing(input) {
  const failures = [];
  const existing = String(input.existingUsername || '');
  const absent = String(input.absentUsername || '');
  if (existing === '' || absent === '') {
    failures.push(`${TAG} [前提] 対の利用者名を用意できなかった（陽性対照または陰性対照が欠けている）。`
      + ' 片側だけで測ると「区別できない」は成立しない。');
    return failures;
  }
  const existingBytes = Buffer.byteLength(existing);
  const absentBytes = Buffer.byteLength(absent);
  if (existingBytes !== absentBytes) {
    failures.push(`${TAG} [前提] 対の利用者名のバイト長が違う（実在 ${existingBytes} / 非実在 ${absentBytes}）。`
      + ' 申請した利用者名は応答へそのまま反映されるので、**長さが違えば本文長は必ず違う** ——'
      + ' それは存在の漏れではなく測り方の誤りである（#1245 の実測で 1 度踏んでいる）。');
  }
  if ((input.realmUsernames || []).includes(absent)) {
    failures.push(`${TAG} [前提] 陰性対照に選んだ利用者名 ${absent} が realm 宣言に実在する。`
      + ' 「非実在のつもりが実在していた」測定になる。');
  }
  return failures;
}

/**
 * 🔴 **本体の判定**: ログイン経路の応答が、利用者名の実在で区別できないこと。**純関数**。
 *
 * 判定するのは**ステータス・リダイレクト先・本文**の 3 面である（所要時間は判定しない）。
 * 本文と `Location` は**正規化済みの値**を受け取る（フローの識別子と入力の再表示を伏せたもの）。
 *
 * @param {{existingError:?string, absentError:?string,
 *          existingStatuses:number[], absentStatuses:number[],
 *          existingLocation:?string, absentLocation:?string,
 *          existingBody:string, absentBody:string}} input
 * @returns {string[]}
 */
function evaluateLoginConcealment(input) {
  const failures = [];

  // 0) **測れていないものを「一致した」と言わない。** 両側エラーで黙って緑になる形を塞ぐ。
  if (input.existingError) {
    failures.push(`${TAG} [前提] 実在する利用者名でログイン経路を通せなかった: ${input.existingError}`);
  }
  if (input.absentError) {
    failures.push(`${TAG} [前提] 非実在の利用者名でログイン経路を通せなかった: ${input.absentError}`);
  }
  if (failures.length > 0) return failures;

  const existingStatuses = input.existingStatuses || [];
  const absentStatuses = input.absentStatuses || [];
  if (existingStatuses.length === 0 || absentStatuses.length === 0) {
    failures.push(`${TAG} [前提] 標本が 0 件の側がある（実在 ${existingStatuses.length} 件 /`
      + ` 非実在 ${absentStatuses.length} 件）。0 件走査を緑にしない。`);
    return failures;
  }

  // 1) **同じ側の中でステータスが揺れたら、途中で状態が変わっている**（一時ロック等）。
  //    平均して均さない —— 別の状態を測った結果を 1 つの結論に混ぜてはならない。
  for (const [label, statuses] of [['実在', existingStatuses], ['非実在', absentStatuses]]) {
    const distinct = [...new Set(statuses)];
    if (distinct.length > 1) {
      failures.push(`${TAG} [T-09] ${label}側のステータスが測定中に変わった（${distinct.join(' / ')}）。`
        + ' 途中で状態が変わっている（brute-force の一時ロック等）ので、この実行の結論は採れない。');
    }
  }

  // 2) ステータス（ADR-0078 決定 1 が明文化した面。**PR-0 の主題**）
  if (existingStatuses[0] !== absentStatuses[0]) {
    failures.push(`${TAG} [T-09] ログインの応答ステータスが利用者名の実在で分かれている`
      + `（実在 ${existingStatuses[0]} / 非実在 ${absentStatuses[0]}）。`
      + ' この差だけで利用者名を 1 リクエストずつ列挙できる（SC-13 の存在秘匿の破れ）。');
  }

  // 3) リダイレクト先
  if ((input.existingLocation || null) !== (input.absentLocation || null)) {
    failures.push(`${TAG} [T-10] ログインのリダイレクト先が利用者名の実在で分かれている`
      + `（実在 ${input.existingLocation || '(無し)'} / 非実在 ${input.absentLocation || '(無し)'}）。`
      + ' 遷移先そのものが、その利用者名が登録されているかを教えている。');
  }

  // 4) 本文（フローの識別子と入力の再表示を伏せたうえで比較する）
  if (input.existingBody !== input.absentBody) {
    failures.push(`${TAG} [T-11] ログインの応答本文が利用者名の実在で分かれている`
      + '（フローの識別子と入力の再表示を伏せたうえで比較した）。'
      + ' 画面に出る文言・状態・導線は、その利用者名が登録されているかを教えてはならない。');
  }
  return failures;
}

// ---------------------------------------------------------------- 収集（稼働クラスタ）

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * ログインを 1 回**失敗させる**（利用者が通る経路そのもの）。
 * 認可要求 → ログイン画面 → 資格情報 POST まで。cookie は 1 回ごとに新しい jar で保つ。
 *
 * @returns {Promise<{status:number, location:?string, body:string, elapsedMs:number}|{error:string}>}
 */
async function attemptLogin({ base, realmName, client, ca, username, credential }) {
  const jar = createJar();
  const authUrl = `${base}/realms/${encodeURIComponent(realmName)}/protocol/openid-connect/auth`
    + `?client_id=${encodeURIComponent(client.clientId)}`
    + `&redirect_uri=${encodeURIComponent(client.redirectUri)}`
    + '&response_type=code&scope=openid&state=login-existence-check'
    // PKCE を必須にしている realm があるので常に付ける（不要な realm では無視される）。
    + '&code_challenge_method=S256&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM';
  const page = await request(authUrl, { jar, ca });
  if (page.status !== 200) {
    return { error: `ログイン画面が ${page.status} を返した（${page.location || ''}）。認証導線へ到達できない。` };
  }
  const action = /action="([^"]*login-actions\/authenticate[^"]*)"/.exec(page.body);
  if (!action) {
    return { error: 'ログイン画面に authenticate の action が無い（テーマがフォームを落としているか、画面が別物である）。' };
  }
  const form = `username=${encodeURIComponent(username)}`
    + `&password=${encodeURIComponent(credential)}`
    + '&credentialId=';
  const startedAt = process.hrtime.bigint();
  const res = await request(decodeEntities(action[1]), { method: 'POST', jar, ca, body: form });
  const elapsedMs = Number(process.hrtime.bigint() - startedAt) / 1e6;
  return { status: res.status, location: res.location, body: res.body, elapsedMs };
}

async function run() {
  const notices = [];

  if (!hasTool('kubectl')) {
    return { failures: [`${TAG} [前提] kubectl が無い。稼働クラスタに対して測る検査なので、逃げ道の環境変数は置かない。`], notices };
  }

  const realmRes = loadRealm();
  if (!realmRes.ok) return { failures: [`${TAG} [前提] ${realmRes.error}`], notices };
  const realm = realmRes.value;
  const realmName = realm.realm;

  const user = pickTargetUser(realm);
  if (!user) return { failures: [`${TAG} [前提] realm 宣言に対話利用者が居ない（0 件走査を緑にしない）。`], notices };
  const client = pickBrowserFlowClient(realm);
  if (!client) return { failures: [`${TAG} [前提] realm 宣言に標準フローのクライアント（redirectUri あり・bearer-only でない）が無い。ログイン画面へ到達できない。`], notices };

  const baseRes = keycloakBaseUrl();
  if (!baseRes.ok) return { failures: [`${TAG} [前提] ${baseRes.error}`], notices };
  const base = baseRes.value;

  const caRes = edgeCa();
  if (!caRes.ok) {
    return {
      failures: [`${TAG} [前提] ${caRes.error} —— **証明書の検証は切らない**（切ると、エッジの TLS が壊れていても緑になる）。`],
      notices,
    };
  }
  const ca = caRes.value;

  const realmUsernames = (realm.users || []).map((u) => String((u && u.username) || ''));
  const absentUsername = makeAbsentUsernameOfLength(realm, Buffer.byteLength(user.username));
  if (!absentUsername) {
    return {
      failures: [`${TAG} [前提] realm に実在しない、同じバイト長の利用者名を作れなかった（陰性対照を置けないので緑にしない）。`],
      notices,
    };
  }

  const pairing = evaluateProbePairing({
    existingUsername: user.username,
    absentUsername,
    realmUsernames,
  });
  if (pairing.length > 0) return { failures: pairing, notices };

  const budget = loginProbeBudget(realm);
  const spacing = probeSpacingMs(realm);
  notices.push(`${TAG} realm=${realmName} / 実在=${user.username} / 非実在=${absentUsername}`
    + `（同じ ${Buffer.byteLength(user.username)} バイト・realm 宣言と突き合わせて不在を確認済み）`
    + ` / client=${client.clientId} / edge=${base}`);
  notices.push(`${TAG} 標本数=${budget}（realm の bruteForceProtected=${realm.bruteForceProtected === true}`
    + ` / failureFactor=${realm.failureFactor} から導いた。**ロックの手前で止める**）`
    + ` / 失敗の間隔=${spacing}ms`);

  const existing = { statuses: [], timings: [], location: null, body: '', error: null };
  const absent = { statuses: [], timings: [], location: null, body: '', error: null };

  /* eslint-disable no-await-in-loop */
  for (let i = 0; i < budget; i += 1) {
    // 🔴 **交互に測る。** 片側をまとめて測ると、実行機の負荷変動が片方だけに乗る。
    for (const [side, username] of [[existing, user.username], [absent, absentUsername]]) {
      if (side.error) continue;
      const res = await attemptLogin({ base, realmName, client, ca, username, credential: WRONG_CREDENTIAL });
      if (res.error) { side.error = res.error; continue; }
      side.statuses.push(res.status);
      side.timings.push(res.elapsedMs);
      if (side.statuses.length === 1) {
        side.location = normalizeLoginLocation(res.location, username);
        side.body = normalizeConcealmentBody(res.body, username);
      }
    }
    // 同じ利用者への連続した失敗が「速すぎる」と判定されないよう間隔を空ける。
    if (i < budget - 1) await sleep(spacing);
  }
  /* eslint-enable no-await-in-loop */

  const failures = evaluateLoginConcealment({
    existingError: existing.error,
    absentError: absent.error,
    existingStatuses: existing.statuses,
    absentStatuses: absent.statuses,
    existingLocation: existing.location,
    absentLocation: absent.location,
    existingBody: existing.body,
    absentBody: absent.body,
  });

  if (existing.statuses.length > 0 && absent.statuses.length > 0) {
    notices.push(`${TAG} [T-09] 実在=${existing.statuses[0]} / 非実在=${absent.statuses[0]}`
      + ` / 正規化後の本文長 実在=${Buffer.byteLength(existing.body)} 非実在=${Buffer.byteLength(absent.body)}`
      + ` / Location 実在=${existing.location || '(無し)'} 非実在=${absent.location || '(無し)'}`);
  }

  // 🔴 所要時間は**出すだけ**である（ADR-0078 決定 1 は閾値を定めていない）。
  const te = summarizeTimings(existing.timings);
  const ta = summarizeTimings(absent.timings);
  if (te && ta) {
    const ratio = ta.median > 0 ? (te.median / ta.median) : null;
    notices.push(`${TAG} [観測] 所要時間（**判定しない**）:`
      + ` 実在 n=${te.n} min=${te.min.toFixed(1)} 中央=${te.median.toFixed(1)} max=${te.max.toFixed(1)} ms /`
      + ` 非実在 n=${ta.n} min=${ta.min.toFixed(1)} 中央=${ta.median.toFixed(1)} max=${ta.max.toFixed(1)} ms`
      + `${ratio === null ? '' : ` / 中央値の比（実在÷非実在）=${ratio.toFixed(2)}`}`);
    notices.push(`${TAG} [観測] 🔴 **この標本数では所要時間から結論を出さない。**`
      + ' 標本を増やすには実在側の失敗を重ねることになり、brute-force の一時ロックに触れる。'
      + ' 反復と閾値の確定は稼働クラスタでの実測（#1245 PR-D）と計画側の裁定に委ねる。');
  }

  notices.push(`${TAG} [導出・未実測] realm は bruteForceProtected=${realm.bruteForceProtected === true} /`
    + ` failureFactor=${realm.failureFactor} / permanentLockout=${realm.permanentLockout === true} を宣言している。`
    + ' **失敗を重ねると実在側の利用者だけが一時ロックされる** ——'
    + ' これはもう 1 つの存在判定器だが、**本検査は意図的にロックを作らない**（後段の門と再実行の前提を壊すため）。'
    + ' 実測は #1245 PR-D の射程である。');

  return { failures, notices, sampled: { existing: existing.statuses.length, absent: absent.statuses.length } };
}

// ---------------------------------------------------------------- 自己試験

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  const PAGE = (username, sessionCode) => '<html><body><form id="kc-form-login" action="/realms/platform/login-actions/'
    + `authenticate?session_code=${sessionCode}&execution=abc&tab_id=xyz">`
    + `<input name="username" value="${username}"/>`
    + '<span class="kc-feedback-text">社員ID またはパスワードが正しくありません</span></form></body></html>';

  const pair = (opts = {}) => ({
    existingError: null,
    absentError: null,
    existingStatuses: opts.existingStatuses || [200],
    absentStatuses: opts.absentStatuses || [200],
    existingLocation: null,
    absentLocation: null,
    existingBody: normalizeConcealmentBody(PAGE('admin', 'sc-1'), 'admin'),
    absentBody: normalizeConcealmentBody(PAGE('ekuze', 'sc-2'), 'ekuze'),
    ...opts,
  });

  // --- S-01 陽性対照: 何も違わなければ失敗 0 件 -------------------------------------
  ok('S-01 陽性: ステータス・本文・Location が同じ → 失敗 0 件', () => {
    assert.deepStrictEqual(evaluateLoginConcealment(pair()), []);
  });

  // --- S-02〜S-04 陰性対照: 3 つの面がそれぞれ検出されること ------------------------
  ok('S-02 陰性: ステータスが分かれる → 検出する', () => {
    const f = evaluateLoginConcealment(pair({ existingStatuses: [500], absentStatuses: [200] }));
    assert.ok(f.some((x) => x.includes('[T-09]') && x.includes('ステータスが利用者名の実在で分かれている')), f.join('\n'));
  });
  ok('S-03 陰性: 正規化後の本文が分かれる → 検出する', () => {
    const f = evaluateLoginConcealment(pair({
      absentBody: normalizeConcealmentBody(
        PAGE('ekuze', 'sc-2').replace('正しくありません', 'そのような利用者は登録されていません'), 'ekuze',
      ),
    }));
    assert.ok(f.some((x) => x.includes('[T-11]')), f.join('\n'));
  });
  ok('S-04 陰性: Location が分かれる → 検出する', () => {
    const f = evaluateLoginConcealment(pair({
      existingLocation: '/realms/platform/login-actions/required-action',
      absentLocation: null,
    }));
    assert.ok(f.some((x) => x.includes('[T-10]')), f.join('\n'));
  });

  // --- S-05・S-06 陽性対照: 偽陽性を出さないこと ------------------------------------
  ok('S-05 陽性: フローの識別子だけが違う → 検出しない', () => {
    const a = normalizeConcealmentBody(PAGE('admin', 'AAAAAAAA'), 'admin');
    const b = normalizeConcealmentBody(PAGE('admin', 'ZZZZZZZZ'), 'admin');
    assert.strictEqual(a, b);
    assert.deepStrictEqual(evaluateLoginConcealment(pair({ existingBody: a, absentBody: b })), []);
  });
  ok('S-06 陽性: 入力した利用者名の再表示だけが違う → 検出しない', () => {
    // 同じ長さの名前なら、再表示を伏せたあとの本文は一致する。
    assert.deepStrictEqual(evaluateLoginConcealment(pair()), []);
  });

  // --- S-07 対の前提（測る前に落とす） ----------------------------------------------
  ok('S-07 陰性: 利用者名のバイト長が違う → 測る前に落ちる', () => {
    const f = evaluateProbePairing({
      existingUsername: 'admin',
      absentUsername: 'no-such-user-abcd1234',
      realmUsernames: ['admin'],
    });
    assert.ok(f.some((x) => x.includes('バイト長が違う')), f.join('\n'));
  });
  ok('S-07b 陽性: 同じバイト長・realm に不在 → 前提の失敗 0 件', () => {
    assert.deepStrictEqual(evaluateProbePairing({
      existingUsername: 'admin',
      absentUsername: 'ekuze',
      realmUsernames: ['admin', 'poc-user'],
    }), []);
  });
  ok('S-07c 陰性: 陰性対照が realm に実在する → 検出する', () => {
    const f = evaluateProbePairing({
      existingUsername: 'admin',
      absentUsername: 'admin',
      realmUsernames: ['admin'],
    });
    assert.ok(f.some((x) => x.includes('実在する')), f.join('\n'));
  });
  ok('S-07d 陰性: 片側の利用者名が空 → 検出する', () => {
    const f = evaluateProbePairing({ existingUsername: 'admin', absentUsername: '', realmUsernames: [] });
    assert.ok(f.some((x) => x.includes('対の利用者名を用意できなかった')), f.join('\n'));
  });

  // --- S-08 測れていないものを緑にしない --------------------------------------------
  ok('S-08 陰性: 片側がエラーで測れていない → 緑にしない', () => {
    const f = evaluateLoginConcealment(pair({ absentError: 'ログイン画面が 503 を返した' }));
    assert.ok(f.length > 0 && f[0].includes('[前提]'), f.join('\n'));
  });
  ok('S-08b 陰性: 両側がエラー → 「一致した」と言わない', () => {
    const f = evaluateLoginConcealment(pair({ existingError: 'x', absentError: 'y' }));
    assert.strictEqual(f.length, 2);
  });
  ok('S-08c: 到達できなかった側があるとき、原因は 1 つだけ名指しする（後続の判定へ雪崩れない）', () => {
    // 実行時は「到達できなかった側」の標本が 0 件になる。到達不能を報告したうえで
    // さらに「標本 0 件」「本文が違う」まで並べると、**原因の切り分けを誤らせる**。
    const f = evaluateLoginConcealment(pair({ absentError: 'ログイン画面が 503 を返した', absentStatuses: [], absentBody: '' }));
    assert.strictEqual(f.length, 1, f.join('\n'));
    assert.ok(f[0].includes('[前提]') && !f[0].includes('標本が 0 件'), f[0]);
  });

  // --- S-09 非実在名の生成 ------------------------------------------------------------
  ok('S-09 陽性: 指定したバイト長ちょうどの非実在名を作る', () => {
    const realm = { users: [{ username: 'admin' }, { username: 'poc-user' }] };
    for (const len of [1, 5, 8, 21]) {
      const nameGenerated = makeAbsentUsernameOfLength(realm, len);
      assert.strictEqual(Buffer.byteLength(nameGenerated), len, `len=${len}`);
      assert.ok(!['admin', 'poc-user'].includes(nameGenerated));
    }
  });
  ok('S-09b 陰性: 作れない長さ（0 / 負 / 非整数）では null を返す', () => {
    const realm = { users: [] };
    assert.strictEqual(makeAbsentUsernameOfLength(realm, 0), null);
    assert.strictEqual(makeAbsentUsernameOfLength(realm, -1), null);
    assert.strictEqual(makeAbsentUsernameOfLength(realm, 1.5), null);
  });
  ok('S-09c 陰性: 候補が全部実在するなら null（衝突を黙って返さない）', () => {
    // 長さ 1・アルファベット全 36 文字を realm が占めている状況を作る。
    const realm = { users: ABSENT_NAME_ALPHABET.split('').map((c) => ({ username: c })) };
    assert.strictEqual(makeAbsentUsernameOfLength(realm, 1), null);
  });

  // --- S-10 標本数の導出 --------------------------------------------------------------
  ok('S-10 陽性: bruteForceProtected かつ failureFactor=5 → 4（ロックの手前）', () => {
    assert.strictEqual(loginProbeBudget({ bruteForceProtected: true, failureFactor: 5 }), 4);
  });
  ok('S-10b: 保護が無効 → 導ける上限が無いので測定都合の値', () => {
    assert.strictEqual(loginProbeBudget({ bruteForceProtected: false, failureFactor: 5 }), UNPROTECTED_SAMPLE_COUNT);
  });
  ok('S-10c 陰性: failureFactor が読めない／1 以下 → 保守側（1 回）へ倒す', () => {
    assert.strictEqual(loginProbeBudget({ bruteForceProtected: true }), 1);
    assert.strictEqual(loginProbeBudget({ bruteForceProtected: true, failureFactor: 1 }), 1);
    assert.strictEqual(loginProbeBudget({ bruteForceProtected: true, failureFactor: 'many' }), 1);
  });
  ok('S-10d: 失敗の間隔は宣言があればそれ、無ければ上流既定 ＋ 余裕', () => {
    assert.strictEqual(probeSpacingMs({ quickLoginCheckMilliSeconds: 3000 }), 3000 + QUICK_LOGIN_MARGIN_MS);
    assert.strictEqual(probeSpacingMs({}), KEYCLOAK_DEFAULT_QUICK_LOGIN_CHECK_MS + QUICK_LOGIN_MARGIN_MS);
  });

  // --- S-11 所要時間の要約（判定はしないが、出す値は正しくあること） ------------------
  ok('S-11 陽性: 中央値（奇数個・偶数個）と最小・最大', () => {
    assert.deepStrictEqual(summarizeTimings([30, 10, 20]), { n: 3, min: 10, median: 20, max: 30 });
    assert.deepStrictEqual(summarizeTimings([40, 10, 20, 30]), { n: 4, min: 10, median: 25, max: 40 });
  });
  ok('S-11b 陰性: 空・非数だけなら null（0 を出さない）', () => {
    assert.strictEqual(summarizeTimings([]), null);
    assert.strictEqual(summarizeTimings([NaN, undefined, 'x']), null);
  });

  // --- S-12 標本 0 件・測定中の状態変化 -----------------------------------------------
  ok('S-12 陰性: 片側の標本が 0 件 → 失敗（0 件走査を緑にしない）', () => {
    const f = evaluateLoginConcealment(pair({ absentStatuses: [] }));
    assert.ok(f.some((x) => x.includes('標本が 0 件')), f.join('\n'));
  });
  ok('S-12b 陰性: 同じ側でステータスが揺れた → 結論を採らない', () => {
    const f = evaluateLoginConcealment(pair({ existingStatuses: [200, 200, 401] }));
    assert.ok(f.some((x) => x.includes('測定中に変わった')), f.join('\n'));
  });
  ok('S-12c 陽性: 同じ側で揺れていなければ鳴らない', () => {
    assert.deepStrictEqual(evaluateLoginConcealment(pair({ existingStatuses: [200, 200], absentStatuses: [200, 200] })), []);
  });

  console.log(`${TAG} self-test: ${n} 件すべて成功`);
}

// ---------------------------------------------------------------- main

async function main() {
  const argv = process.argv.slice(2);
  const unknown = argv.filter((a) => a !== '--self-test');
  if (unknown.length > 0) {
    console.error(`${TAG} 未知の引数: ${unknown.join(' ')}`);
    process.exit(2);
  }
  if (argv.includes('--self-test')) { selfTest(); return; }

  const r = await run();
  for (const notice of r.notices) console.log(notice);
  if (r.failures.length > 0) {
    console.error(`${TAG} ${r.failures.length} 件の失敗:`);
    for (const f of r.failures) console.error(`\n  - ${f}`);
    process.exit(1);
  }
  console.log(`${TAG} OK: ログイン経路の応答（ステータス・リダイレクト先・本文）が`
    + `利用者名の実在で区別できない（実在 ${r.sampled.existing} 標本 / 非実在 ${r.sampled.absent} 標本）。`
    + ' **所要時間は測って出しただけで、判定していない。**');
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`${TAG} 実行時エラー: ${e && e.stack ? e.stack : e}`);
    process.exit(1);
  });
}

module.exports = {
  loginProbeBudget,
  probeSpacingMs,
  makeAbsentUsernameOfLength,
  normalizeLoginLocation,
  summarizeTimings,
  evaluateProbePairing,
  evaluateLoginConcealment,
  attemptLogin,
  ABSENT_NAME_ALPHABET,
  UNPROTECTED_SAMPLE_COUNT,
  KEYCLOAK_DEFAULT_QUICK_LOGIN_CHECK_MS,
  QUICK_LOGIN_MARGIN_MS,
};
