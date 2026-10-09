#!/usr/bin/env node
'use strict';
/*
 * check-mcp-client-provisioning.js
 *
 * FR-16, SC-12, 計画 ADR-0123 決定 2・3・フォローアップ 3, IADR-0516 決定 2〜4・4a・6 (#1817 / #1829):
 * **SC-12 の無人の登録・属性の差し替えが、稼働の Keycloak に何を作り・何を作らないかを実測する**（integration-stack の門）。
 *
 * 背景: 段 1（#1786）の書き込み口は、偽の Keycloak（状態を持つ HTTP の受け手）とプロセス内の口でしか試されていない。
 *   - 🔴 **フォローアップ 3**（認可サービスと同じ照会 `users?username=service-account-<client>&exact=true` がサービスアカウントの
 *     利用者を返すこと）は、稼働の Keycloak でしか確かめられない（IADR-0516 決定 6 の残余）。返らなければ書き込み口は
 *     書かずに 502 を返し、無人の登録は 1 件も通らない。
 *   - 管理用の主体 `mcp-client-admin` のロール（manage-clients / manage-users）で、作成・SA の照会・属性の書き込み・補償の削除が
 *     本当に通るかも、稼働の Keycloak でしか分からない（足りなければ 403 → 502）。
 *
 * 測ること（受け入れ基準 4。番号は作業仕様書 20261009_1817 の M1〜M6。M7 は 20261009_1818。M8 は 20261009_1829。M9・M10 は 20261009_1844。M11 は 20261009_1845。M12 は 20261009_1846）:
 *   M1 無人の登録が 201（503 にならない）。Keycloak のクライアントに入口の印（msp.mcp-client.managed-by=mcp-server）があり、
 *      機密・SA つき・人の流れ（標準・暗黙・直接付与）は閉・fullScopeAllowed=false。
 *   M2 `users?username=service-account-<client>&exact=true` がちょうど 1 件で、割り当てた属性が入っている（集合値は多値）。
 *   M3 差し替え（入口の印あり）が 200 で、Keycloak の属性が丸ごと置き換わる。
 *   M4 部分集合の外れ（tags）と doc_scope=private-note は、登録で 400・Keycloak にクライアントも SA 利用者も作られない。
 *      差し替えでも 400 で、Keycloak の属性は変わらない。
 *   M5 プラットフォームのクライアント名（abac-seeder）の登録は 400 で、その SA の属性は変わらない。差し替えは、入口ができる前の
 *      登録簿の行（psql で置く無人の行）に対して 400 で、属性は変わらない（入口の印の確かめ）。
 *   M6 補償: 表示名が登録簿の上限（200 文字）を超え Keycloak の上限（255 文字）に収まる登録は「IdP へ書けて登録簿で落ちる」ので
 *      **500**（502 / 503 は IdP 側の失敗で補償を通らないので赤）になり、Keycloak にクライアントも SA 利用者も残らず、登録簿にも
 *      行が無く、管理イベントに mcp-client-admin の「作成 → 削除」が在る。併せて mcp-client-admin の資格情報で
 *      M1 のクライアントを消せる（補償が使う削除の権限）。
 *   M7 照合（#1818 / IADR-0516 決定 5）: 入口で登録したクライアントのサービスアカウントの属性を master の管理者で直接書き換え、
 *      入口の印つきのクライアントを登録簿を通らずに作ると、McpServer の定期の照合がそれぞれを `kind=attributes_differ` /
 *      `kind=orphan` として名指しする（`kubectl logs` で読む。最大 RECONCILE_WAIT_MS）。
 *      🔴 **ゲージの値は読まない** —— この使い捨てのスタックは観測スタック（Prometheus）を起こさない。読むのは照合のログである。
 *      それでも、照合の読み取りの口（クライアントの一覧が入口の印を含むこと・mcp-client-admin の権限で一覧と照会が通ること）は
 *      稼働の Keycloak でしか確かめられない。照合が失敗し続けていれば名指しは出ないので、この門が赤になる。
 *      ［#1829］入口の印つきのクライアントを master の管理者で直接無効にすると `kind=enabled_differs` として名指しされることも待つ
 *      （一覧の表現が enabled を含むことの実測）。
 *   M8 無効化の写し（#1829 / IADR-0516 決定 4a）: 入口で登録したクライアントで、無効化の前は client_credentials のトークンが出る（陽性対照）。
 *      SC-12 で無効化（200）すると Keycloak のクライアントの enabled が false・トークン発行が拒否され（4xx・access_token なし）・
 *      登録簿の行も無効。再有効化（200）で enabled が true・トークンが再び出て、テンプレートの項目（入口の印・人の流れの閉）が残る。
 *      🔴 無効化の後・再有効化の後のそれぞれで、**トークンを要求する前に**サービスアカウントの利用者が同じ ID で 1 人だけ残り属性が
 *      変わらないことを見る（PR #1832 監査 🔴1: SA の項目を欠いたクライアントの PUT は Keycloak 24 で SA を消し、トークンの要求が空の SA を作り直す）。否定形: 入口ができる前の登録簿の行（M5 が置く abac-seeder）を
 *      無効化しても abac-seeder の enabled は true のまま、再有効化は 400 で enabled は true のまま。
 *   M9 有人の登録（#1844 / 計画 ADR-0134 決定 1・フォローアップ 1〜3 / IADR-0516 の #1844 追記）: 有人の登録が 201 で、Keycloak のクライアントが
 *      公開・認可コードだけ・PKCE S256・リダイレクト URI が入力どおり・Web オリジン空・入口の印・audience の写像（mcp-server）。
 *      ワイルドカードの URI は 400 で何も作らない。認可の要求（`/auth`。ブラウザを使わず状態コードと Location を読む）で、
 *      PKCE なし・`plain` は invalid_request でリダイレクトへ返され（ログイン画面へ進まない）、S256 は進む。登録していない path・host・
 *      https の port・`localhost` は 400（リダイレクトしない）。🔴 **ループバックの port の扱い（FU3）を測る**: ループバックは port を明示して
 *      登録する（port なしの登録は SC-12 が 400 で拒み、Keycloak に何も作らない）。Keycloak 24 の `RedirectUtils` は port なしで登録された
 *      `http://127.0.0.1/cb` に `http://127.0.0.1:<任意>@evil.example/cb` を一致させ、認可コードを evil.example へ送る（CVE-2024-8883）。
 *      port を明示した `http://127.0.0.1:<port>/cb` は登録どおりの port で進み、別の port・`:<port>@evil.example`・`:1@evil.example` は 400。
 *      `http://[::1]:<port>/cb` も port まで完全一致（別の port・`@evil.example` は 400）。例示のアクセストークン（管理 API の evaluate-scopes）の aud に mcp-server・azp・利用者名が在る。
 *      無人のトークンの aud にも mcp-server が在る。無効化・再有効化が enabled へ写り、テンプレートが残る。補償（表示名の長さ）も無人と同じく走る。
 *   M11 secret の一度だけの表示と再発行（#1845 / 計画 ADR-0134 決定 2・フォローアップ 4・6 / IADR-0516 の #1845 追記）: 無人の登録の 201 の
 *      `clientSecret` が Keycloak の現在の secret（master の管理者で読む）と一致し、その値で client_credentials のトークンが出る。一覧に secret は無い。
 *      SC-12 の再発行が 200 で、新しい値は旧い値と違い Keycloak の現在の secret と一致する。**再発行の直後に旧 secret は拒否される**（FU6: client secret
 *      rotation〔preview〕が無効で猶予が無い）。新しい値でトークンが出る。有人の 201 は `clientSecret` を持たない。
 *      FU6 の観測（判定しない）: 再発行の値が Keycloak の管理イベントの詳細（`adminEventsDetailsEnabled=true`）に残るかを数えて出す（値は出さない）。
 *      🔴 secret の値はログ・失敗の文言に出さない（判定器は一致・不一致だけを返す）。
 *   M10 DCR が閉じている（FU3）: 匿名の動的クライアント登録（`clients-registrations/openid-connect`・`default`）と偽の初期アクセストークンは
 *      401 / 403 でクライアントが増えない。初期アクセストークンは 1 つも無い。匿名のポリシーに Trusted Hosts（信頼ホストは空）が在る。
 *   M12 platform の audience（#1846 / IADR-0523 / planning#770）: 全サービスの JWT 検証は aud に `platform-api` があるトークンだけを受け付ける。
 *      稼働の Keycloak で、**実際に発行されるトークンの aud** を測る（evaluate-scopes の例示のトークン・client credentials の実トークン）:
 *      利用者のトークン（`bff`・利用者 SAMPLE_USER）と呼び出し元のサービスアカウント（document-service・AST の kb-reader）・使い捨ての登録者に
 *      platform-api が在り mcp-server は無い。運用ツール（grafana）の利用者のトークン・MCP クライアント（M9 の有人・無人）のトークンに
 *      platform-api は無い。MCP クライアントの実トークンで McpServer の既定のスキームの口（GET /mcp-clients）を呼ぶと 401（ロール不足の 403 ではない）。
 *
 * 主体は 3 つに分ける（測る側と測られる側を同じにしない）:
 *   - 登録者: 実行のたびに master の管理者が作る**使い捨ての機密クライアント**（SA に platform-admin・既定スコープ profile / roles /
 *     platform-api-audience〔#1846。無いと McpServer の管理 API が 401〕）。
 *     🔴 realm の abac-seeder を使わない —— 既定スコープに profile が無く preferred_username が載らないので、登録者の属性を
 *     引けず、部分集合の判定は「検証できません」の 400 になる（規則そのものを測れない）。使い捨ての登録者は名前で引けるので、
 *     McpServer → 認可サービス → Keycloak の名指しの照会（サービスアカウントを返すか）も同時に通る。終わったら消す。
 *   - 照会: master の管理者（Secret platform-infra/keycloak-admin を kubectl で読む。paired-secret-rotation-runbook.md 1-1 と同じ）。
 *   - 削除の権限の実測: mcp-client-admin（Secret microservices-platform/mcp-client-admin-oidc を kubectl で読む）。
 *
 * 🔴 秘密の値は引数にもログにも出さない（kubectl の出力を読み、fetch の本文で送る。NFR-18 / #1793）。
 * 🔴 作ったものは片付ける（Keycloak のクライアント・登録簿の行）。使い捨てのクラスタでも残さない。
 *
 * 実行:
 *   node scripts/check-mcp-client-provisioning.js --self-test   # 純関数の自己試験（稼働クラスタに触れない）
 *   node scripts/check-mcp-client-provisioning.js --live        # 稼働スタックで実測する（port-forward を自分で張る）
 *
 * 主な環境変数: MCP_PROV_NS（既定 microservices-platform）/ MCP_PROV_INFRA_NS（既定 platform-infra）/ MCP_PROV_REALM（既定 platform）/
 *   MCP_PROV_MCP_URL・MCP_PROV_KC_URL（与えれば port-forward を張らない）。
 *
 * 終了コード: 0=すべて期待どおり / 1=期待と違う（門の赤） / 2=前提未整備（k8s へ到達できない等） / 3=明示の指定が無い（#1550）
 */

const { spawn, spawnSync } = require('child_process');
const { requireLiveOptIn } = require('./lib/live-opt-in.js');

const env = (k, d) => process.env[k] || d;
const NS = env('MCP_PROV_NS', 'microservices-platform');
const INFRA_NS = env('MCP_PROV_INFRA_NS', 'platform-infra');
const REALM = env('MCP_PROV_REALM', 'platform');
// M5 で名乗る「入口を通らずに realm に在るプラットフォームのクライアント」。
const PLATFORM_CLIENT = 'abac-seeder';
const REGISTRAR_ROLE = 'platform-admin';
const ADMIN_CLIENT = 'mcp-client-admin';
const ADMIN_SECRET = 'mcp-client-admin-oidc';
const MANAGED_BY_ATTRIBUTE = 'msp.mcp-client.managed-by';
const MANAGED_BY_VALUE = 'mcp-server';
const SA_PREFIX = 'service-account-';
// IADR-0385: 集合値のキー（多値で書く）。UserAttributeEncoding.IsSetValued と同じ 2 つ。
const SET_VALUED = new Set(['tags', 'projects']);
// 登録簿の DisplayName は varchar(200)、Keycloak のクライアントの name は 255 文字まで（M6 の前提）。
const REGISTRY_DISPLAY_NAME_MAX = 200;
const KEYCLOAK_NAME_MAX = 255;
// M7: 照合の既定の周期は 1 分（IdpReconciliationOptions.DefaultInterval）。周期 ＋ 1 回の照合の期限（周期と同じ）＋ 余裕で待つ。
const RECONCILE_WAIT_MS = Number(env('MCP_PROV_RECONCILE_WAIT_MS', '150000'));
const RECONCILE_POLL_MS = 10000;
// M9（#1844）: 有人のクライアントのテンプレート（KeycloakServiceAccountProvisioner.PublicClientTemplate と同じ値）。
const MCP_AUDIENCE = 'mcp-server';
// M12（#1846）: platform の API の共有 audience と、それを載せるクライアントスコープ（deploy/keycloak の realm と同じ値）。
const PLATFORM_AUDIENCE = 'platform-api';
const PLATFORM_AUDIENCE_SCOPE = 'platform-api-audience';
const PKCE_ATTRIBUTE = 'pkce.code.challenge.method';
// M9: 例示のアクセストークンを作る利用者（realm の宣言の人の利用者）。
const SAMPLE_USER = env('MCP_PROV_SAMPLE_USER', 'poc-user');
// M10: 既定の匿名のクライアント登録ポリシー（Keycloak 24 DefaultClientRegistrationPolicies）。
const REGISTRATION_POLICY_TYPE = 'org.keycloak.services.clientregistration.policy.ClientRegistrationPolicy';

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);

// ---------------------------------------------------------------- 純関数（--self-test が試す）

/** サービスアカウントの利用者名（Keycloak は小文字で持つ。ToolUserContext.ServiceAccountUserName と同じ綴り）。 */
function serviceAccountUserName(clientId) {
  return SA_PREFIX + String(clientId).toLowerCase();
}

/** 入力の属性（登録の契約の文字列）を、Keycloak の利用者属性として期待する形（キー → 並べた値の配列）へ写す。 */
function expectedKeycloakAttributes(requested) {
  const out = {};
  for (const [k, v] of Object.entries(requested || {})) {
    out[k] = SET_VALUED.has(k.toLowerCase())
      ? [...new Set(String(v).split(/[\s,]+/).map((x) => x.trim()).filter(Boolean))].sort()
      : [String(v)];
  }
  return out;
}

/** Keycloak の属性を、比べられる形（キー → 並べた値の配列）へ。keys を与えればそのキーだけ。 */
function normalizeAttributes(attributes, keys) {
  const out = {};
  for (const [k, v] of Object.entries(attributes || {})) {
    if (keys && !keys.includes(k)) continue;
    out[k] = [...(Array.isArray(v) ? v : [v])].map(String).sort();
  }
  return out;
}

function sameAttributes(a, b) {
  const ka = Object.keys(a).sort();
  const kb = Object.keys(b).sort();
  return JSON.stringify(ka) === JSON.stringify(kb) && ka.every((k) => JSON.stringify(a[k]) === JSON.stringify(b[k]));
}

/** M1: 作られたクライアントの表現を判定する。違反の一覧を返す。 */
function evaluateCreatedClient(clients, clientId) {
  const errors = [];
  const hits = (clients || []).filter((c) => c.clientId === clientId);
  if (hits.length !== 1) return [`クライアント ${clientId} が Keycloak に ${hits.length} 件（ちょうど 1 件であるべき）`];
  const c = hits[0];
  const marker = (c.attributes || {})[MANAGED_BY_ATTRIBUTE];
  if (marker !== MANAGED_BY_VALUE) errors.push(`入口の印 ${MANAGED_BY_ATTRIBUTE}=${MANAGED_BY_VALUE} が無い（実際: ${JSON.stringify(marker)}）`);
  if (c.publicClient !== false) errors.push(`publicClient が false でない（${JSON.stringify(c.publicClient)}）`);
  if (c.serviceAccountsEnabled !== true) errors.push('serviceAccountsEnabled が true でない');
  for (const k of ['standardFlowEnabled', 'implicitFlowEnabled', 'directAccessGrantsEnabled', 'fullScopeAllowed']) {
    if (c[k] !== false) errors.push(`${k} が false でない（${JSON.stringify(c[k])}。人の流れ・全ロールが開く）`);
  }
  return errors;
}

/** M2: 名指しの照会の結果を判定する（フォローアップ 3）。違反の一覧を返す。 */
function evaluateServiceAccountLookup(users, clientId, requested) {
  const want = serviceAccountUserName(clientId);
  if (!Array.isArray(users) || users.length !== 1) {
    return [`users?username=${want}&exact=true が ${Array.isArray(users) ? users.length : '配列でない'} 件（ちょうど 1 件であるべき。` +
      '0 件なら認可サービスの照会もサービスアカウントを引けず、無人の実行は常に拒否になる）'];
  }
  const errors = [];
  if (users[0].username !== want) errors.push(`利用者名が ${users[0].username}（期待 ${want}）`);
  const expected = expectedKeycloakAttributes(requested);
  const got = normalizeAttributes(users[0].attributes, Object.keys(expected));
  if (!sameAttributes(expected, got)) errors.push(`属性が違う（期待 ${JSON.stringify(expected)}・実際 ${JSON.stringify(got)}）`);
  return errors;
}

/** M4・M6: 何も作られていないこと。 */
function evaluateNothingCreated(clients, users, clientId) {
  const errors = [];
  const c = (clients || []).filter((x) => x.clientId === clientId);
  if (c.length > 0) errors.push(`クライアント ${clientId} が Keycloak に残っている（${c.length} 件）`);
  const u = (users || []).filter((x) => x.username === serviceAccountUserName(clientId));
  if (u.length > 0) errors.push(`利用者 ${serviceAccountUserName(clientId)} が Keycloak に残っている（${u.length} 件）`);
  return errors;
}

/**
 * M6: 補償の経路を通った応答か（PR #1827 監査 🟡2）。
 * 🔴 **5xx なら何でもよい、にしない。** IdP への書き込みそのものが失敗した 502（IdpFirstWrite が ProblemDetails で返す）や
 *    書き込み口の無い 503 でも「何も残らない」は自明に真になり、補償は一度も走っていない。
 *    登録簿への書き込みが例外で落ちたとき IdpFirstWrite は補償してから**元の例外を投げ直す**。McpServer は例外の写し替えを
 *    持たない（UsePlatformMiddleware に例外ハンドラが無い）ので、ホストの既定の **500** になり、IdpFirstWrite の
 *    502 / 503 の題名（「IdP へ書けなかった」「書き込み口が構成されていない」）は本文に現れない。
 */
function evaluateCompensationResponse(status, text) {
  const body = String(text || '');
  if (status !== 500) {
    return [`状態が ${status}（期待 500 ＝ IdP へ書けた後に登録簿で落ちた）。502 / 503 は IdP 側の失敗で、補償の経路を通っていない: ${body.slice(0, 300)}`];
  }
  if (/IdP へ書けなかった|書き込み口が構成されていない/.test(body)) return [`500 だが本文が IdP 側の失敗を告げる: ${body.slice(0, 300)}`];
  return [];
}

/**
 * M6: 管理イベントで「作成 → 削除」が実際に起きたこと。events は Keycloak の admin-events（resourceType=CLIENT）。
 * 作成の表現（詳細の記録が有効）が clientId を含む CREATE を 1 件、その resourcePath への DELETE がそれ以後に在ること。
 * actorUserId を与えれば、両方の主体がその利用者（mcp-client-admin の SA）であること。
 */
function evaluateCompensationEvents(events, clientId, actorUserId) {
  const list = Array.isArray(events) ? events : [];
  const creates = list.filter((e) => e.operationType === 'CREATE' && e.resourceType === 'CLIENT'
    && (() => { try { return JSON.parse(e.representation || '{}').clientId === clientId; } catch { return false; } })());
  if (creates.length !== 1) return [`クライアント ${clientId} の作成の管理イベントが ${creates.length} 件（ちょうど 1 件であるべき。0 件なら IdP へ書いていない＝補償を測れていない）`];
  const created = creates[0];
  const deletes = list.filter((e) => e.operationType === 'DELETE' && e.resourceType === 'CLIENT'
    && e.resourcePath === created.resourcePath && Number(e.time) >= Number(created.time));
  const errors = [];
  if (deletes.length !== 1) errors.push(`${created.resourcePath} の削除の管理イベントが ${deletes.length} 件（補償の削除が起きていない）`);
  if (actorUserId) {
    for (const [label, e] of [['作成', created], ['削除', deletes[0]]]) {
      if (e && (e.authDetails || {}).userId !== actorUserId) errors.push(`${label}の主体が mcp-client-admin の SA でない（${JSON.stringify(e.authDetails)}）`);
    }
  }
  return errors;
}

/** M6 の前提: 登録簿では落ち、Keycloak では書ける長さの表示名。 */
function overlongDisplayName() {
  const name = `SC-12 compensation probe ${'x'.repeat(230)}`.slice(0, 230);
  if (!(name.length > REGISTRY_DISPLAY_NAME_MAX && name.length <= KEYCLOAK_NAME_MAX)) throw new Error('表示名の長さの前提が崩れた');
  return name;
}

/**
 * M7: 照合のログ（McpServer の Warning「client=<id> kind=<kind>」）に、期待した名指しが在るか。違反の一覧を返す。
 * 照合の失敗（「照合できなかった」）が出ていれば、その旨を添える（名指しが無い理由の手掛かり）。
 */
function evaluateReconciliationLog(logText, expected) {
  const text = String(logText || '');
  const errors = [];
  for (const { clientId, kind } of expected) {
    const line = `client=${clientId} kind=${kind}`;
    // 前後の境界: クライアント ID の前方一致（probe-x と probe-x-2）を取り違えない。
    const re = new RegExp(`client=${clientId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} kind=${kind}(?![\\w-])`);
    if (!re.test(text)) errors.push(`照合のログに「${line}」が無い`);
  }
  if (errors.length > 0 && /照合できなかった|照合の口が構成されていない/.test(text)) {
    errors.push('照合が失敗している（McpServer のログに「照合できなかった」または「照合の口が構成されていない」がある）');
  }
  return errors;
}

/** M8: トークンが出たか（200 かつ access_token あり）。 */
function evaluateTokenIssued(res) {
  const r = res || {};
  if (r.status === 200 && r.json && typeof r.json.access_token === 'string' && r.json.access_token) return [];
  return [`トークンが出ない（状態 ${r.status}・error ${JSON.stringify((r.json || {}).error)}）`];
}

/**
 * M8: トークン発行が Keycloak に**拒否された**か。🔴 5xx・到達不能は「拒否」ではない（Keycloak の不調で緑にしない）。
 * 無効なクライアントへの client_credentials は 401 invalid_client（版によって 400 unauthorized_client）である。
 */
function evaluateTokenRefused(res) {
  const r = res || {};
  if (r.json && r.json.access_token) return [`無効化した後もトークンが出た（状態 ${r.status}）`];
  if (r.status !== 400 && r.status !== 401) return [`状態が ${r.status}（期待 400 / 401 ＝ Keycloak がクライアントを拒否した）`];
  return [];
}

/**
 * M8（PR #1832 監査 🔴1）: 無効化・再有効化の後もサービスアカウントの利用者が**同じ ID で** 1 人だけ残り、属性が変わっていないか。
 * 🔴 **トークンを要求する前に**判定する —— Keycloak は SA の利用者が無いクライアントの client_credentials で空の利用者を作り直すので、
 *    トークンの後に見ると「消えて作り直された」を見逃す（同じ ID であることも見るのはそのため）。
 */
function evaluateServiceAccountIntact(users, clientId, requested, expectedUserId) {
  const errors = evaluateServiceAccountLookup(users, clientId, requested);
  if (errors.length > 0) return errors;
  if (expectedUserId && users[0].id !== expectedUserId) {
    return [`サービスアカウントの利用者の ID が変わった（前 ${expectedUserId}・後 ${users[0].id}。消えて作り直された）`];
  }
  return [];
}

/** M8: Keycloak のクライアントがちょうど 1 件で、enabled が期待どおりか。 */
function evaluateClientEnabled(clients, clientId, expected) {
  const hits = (clients || []).filter((c) => c.clientId === clientId);
  if (hits.length !== 1) return [`クライアント ${clientId} が Keycloak に ${hits.length} 件（ちょうど 1 件であるべき）`];
  return hits[0].enabled === expected ? [] : [`enabled が ${JSON.stringify(hits[0].enabled)}（期待 ${expected}）`];
}

/**
 * M9（#1844）: 作られた有人のクライアントの表現を判定する。違反の一覧を返す。
 * 🔴 未指定を「閉」と読まない（読み戻しの意味が消える）。
 */
function evaluatePublicClient(clients, clientId, redirectUris) {
  const hits = (clients || []).filter((c) => c.clientId === clientId);
  if (hits.length !== 1) return [`クライアント ${clientId} が Keycloak に ${hits.length} 件（ちょうど 1 件であるべき）`];
  const c = hits[0];
  const errors = [];
  const attrs = c.attributes || {};
  if (attrs[MANAGED_BY_ATTRIBUTE] !== MANAGED_BY_VALUE) errors.push(`入口の印 ${MANAGED_BY_ATTRIBUTE}=${MANAGED_BY_VALUE} が無い`);
  if (c.publicClient !== true) errors.push(`publicClient が true でない（${JSON.stringify(c.publicClient)}）`);
  if (c.standardFlowEnabled !== true) errors.push('standardFlowEnabled が true でない（認可コードが閉じている）');
  for (const k of ['implicitFlowEnabled', 'directAccessGrantsEnabled', 'serviceAccountsEnabled', 'fullScopeAllowed']) {
    if (c[k] !== false) errors.push(`${k} が false でない（${JSON.stringify(c[k])}）`);
  }
  if (attrs[PKCE_ATTRIBUTE] !== 'S256') errors.push(`${PKCE_ATTRIBUTE} が S256 でない（${JSON.stringify(attrs[PKCE_ATTRIBUTE])}）`);
  const got = [...(c.redirectUris || [])].sort();
  const want = [...redirectUris].sort();
  if (JSON.stringify(got) !== JSON.stringify(want)) errors.push(`redirectUris が入力と違う（期待 ${JSON.stringify(want)}・実際 ${JSON.stringify(got)}）`);
  if (!Array.isArray(c.webOrigins) || c.webOrigins.length !== 0) errors.push(`webOrigins が空でない（${JSON.stringify(c.webOrigins)}）`);
  const audience = (c.protocolMappers || []).some((m) => m.protocolMapper === 'oidc-audience-mapper'
    && (m.config || {})['included.custom.audience'] === MCP_AUDIENCE && (m.config || {})['access.token.claim'] === 'true');
  if (!audience) errors.push(`audience の写像（${MCP_AUDIENCE}・access.token.claim=true）が無い`);
  return errors;
}

/** 無人のクライアントの表現に audience の写像があるか（M9。#1844）。 */
function evaluateAudienceMapper(clients, clientId) {
  const c = (clients || []).find((x) => x.clientId === clientId);
  if (!c) return [`クライアント ${clientId} が無い`];
  const ok = (c.protocolMappers || []).some((m) => m.protocolMapper === 'oidc-audience-mapper'
    && (m.config || {})['included.custom.audience'] === MCP_AUDIENCE);
  return ok ? [] : [`audience の写像（${MCP_AUDIENCE}）が無い`];
}

/**
 * M9: 認可の要求（`/protocol/openid-connect/auth`）の応答を分類する（fetch の redirect: 'manual'）。
 *   login          … 200（ログイン画面へ進んだ＝リダイレクト URI と PKCE の検査を通った）
 *   error-redirect … 30x で Location が要求のリダイレクト URI へ戻り、error を持つ（PKCE の不備など。URI は信頼された）
 *   rejected       … 4xx で Location なし（リダイレクト URI を信頼しなかった。Keycloak はエラーページを返す）
 *   other          … それ以外（5xx・別の場所へのリダイレクト等。どの期待にも合わない）
 */
function classifyAuthResponse(status, location, redirectUri) {
  if (status === 200) return { outcome: 'login' };
  if (status >= 300 && status < 400 && location) {
    let url;
    try { url = new URL(location); } catch { return { outcome: 'other', detail: `Location を読めない: ${location}` }; }
    const target = `${url.origin}${url.pathname}`;
    let want;
    try { const w = new URL(redirectUri); want = `${w.origin}${w.pathname}`; } catch { want = redirectUri; }
    if (target === want && url.searchParams.get('error')) return { outcome: 'error-redirect', error: url.searchParams.get('error') };
    return { outcome: 'other', detail: `${status} → ${location}` };
  }
  if (status >= 400 && status < 500 && !location) return { outcome: 'rejected' };
  return { outcome: 'other', detail: `状態 ${status}・Location ${JSON.stringify(location)}` };
}

/** M9: 分類の結果が期待どおりか（error-redirect は invalid_request であること）。 */
function evaluateAuthOutcome(result, expected) {
  if (!result || result.outcome !== expected) {
    return [`期待 ${expected}・実際 ${result ? result.outcome : '(なし)'}${result && result.detail ? `（${result.detail}）` : ''}`];
  }
  if (expected === 'error-redirect' && result.error !== 'invalid_request') return [`error が invalid_request でない（${result.error}）`];
  return [];
}

/**
 * M9: 登録したループバックの URI から、認可コードの横取り（CVE-2024-8883）の形の redirect_uri を作る。
 * 利用者情報（`127.0.0.1:<port>@`）でブラウザの宛先を evil.example へすり替える形で、登録どおりの port と別の port（1）の 2 つ。
 * 🔴 port の無い URI・ループバックでない URI は作らない（空を返す。呼び出し側が赤にする）。
 */
function loopbackHijackProbes(redirectUri) {
  const m = /^http:\/\/(127\.0\.0\.1|\[::1\]):(\d{1,5})(\/.*)?$/.exec(String(redirectUri || ''));
  if (!m) return [];
  const path = m[3] || '/';
  return [`http://${m[1]}:${m[2]}@evil.example${path}`, `http://${m[1]}:1@evil.example${path}`];
}

/** JWT の本文（署名は検めない。発行元から直接受け取った値の中身を読むだけ）。 */
function decodeJwtPayload(token) {
  const part = String(token || '').split('.')[1];
  if (!part) return null;
  try { return JSON.parse(Buffer.from(part.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8')); } catch { return null; }
}

/** M9: アクセストークンの aud に MCP サーバーが在り、azp がクライアント、（人なら）利用者名が在るか。 */
function evaluateTokenAudience(payload, clientId, { human = false } = {}) {
  if (!payload) return ['トークンの本文を読めない'];
  const errors = [];
  const aud = Array.isArray(payload.aud) ? payload.aud : (payload.aud === undefined ? [] : [payload.aud]);
  if (!aud.includes(MCP_AUDIENCE)) errors.push(`aud に ${MCP_AUDIENCE} が無い（${JSON.stringify(payload.aud)}）`);
  if (payload.azp !== clientId) errors.push(`azp が ${JSON.stringify(payload.azp)}（期待 ${clientId}）`);
  if (human && !payload.preferred_username) errors.push('preferred_username が無い（MCP サーバーが利用者名を読めない）');
  return errors;
}

/**
 * M12（#1846）: トークンの aud に platform-api が在る（expect=true）／無い（expect=false）か。
 * platform-api を持つトークンに mcp-server が同居していれば赤（MCP クライアントの意味の値を platform の呼び出し元へ載せない）。
 */
function evaluatePlatformAudience(payload, expect) {
  if (!payload) return ['トークンの本文を読めない'];
  const aud = Array.isArray(payload.aud) ? payload.aud : (payload.aud === undefined ? [] : [payload.aud]);
  const has = aud.includes(PLATFORM_AUDIENCE);
  if (expect && !has) return [`aud に ${PLATFORM_AUDIENCE} が無い（${JSON.stringify(payload.aud)}）。platform のサービスで 401 になる`];
  if (!expect && has) return [`aud に ${PLATFORM_AUDIENCE} が在る（${JSON.stringify(payload.aud)}）。platform のサービスを通ってしまう`];
  if (expect && aud.includes(MCP_AUDIENCE)) return [`aud に ${MCP_AUDIENCE} が同居している（${JSON.stringify(payload.aud)}）`];
  return [];
}

/**
 * M10: 匿名・偽の資格の DCR が拒否され、クライアントが増えていないか。
 * 🔴 5xx・到達不能は「閉じている」ではない（Keycloak の不調で緑にしない）。
 */
function evaluateDcrRefused(res, before, after) {
  const r = res || {};
  const errors = [];
  if (r.status !== 401 && r.status !== 403) errors.push(`状態が ${r.status}（期待 401 / 403）: ${String(r.text || '').slice(0, 200)}`);
  if (r.json && (r.json.client_id || r.json.clientId)) errors.push(`クライアントが作られた（${r.json.client_id || r.json.clientId}）`);
  if (typeof before !== 'number' || typeof after !== 'number' || after !== before) errors.push(`クライアントの件数が変わった（前 ${before}・後 ${after}）`);
  return errors;
}

/** M10: 匿名のクライアント登録ポリシーに Trusted Hosts（信頼ホストは空）が在るか。 */
function evaluateRegistrationPolicies(components) {
  const list = Array.isArray(components) ? components : [];
  const trusted = list.filter((c) => c.providerId === 'trusted-hosts' && c.subType === 'anonymous');
  if (trusted.length === 0) return ['匿名のポリシーに Trusted Hosts が無い（匿名の DCR を止めるものが無い）'];
  const hosts = trusted.flatMap((c) => ((c.config || {})['trusted-hosts']) || []).filter((h) => String(h).trim() !== '');
  return hosts.length === 0 ? [] : [`Trusted Hosts に信頼ホストがある（${JSON.stringify(hosts)}）。その host からは匿名の DCR が開く`];
}

/** M10: 初期アクセストークンが 1 つも無いか。 */
function evaluateNoInitialAccessTokens(list) {
  if (!Array.isArray(list)) return ['初期アクセストークンの一覧を読めない'];
  return list.length === 0 ? [] : [`初期アクセストークンが ${list.length} 個ある（その数だけ DCR が開く）`];
}

/** M9: PKCE の S256 の検証子と挑戦（RFC 7636）。 */
function pkcePair() {
  const crypto = require('crypto');
  const verifier = crypto.randomBytes(32).toString('base64url');
  const challenge = crypto.createHash('sha256').update(verifier).digest('base64url');
  return { verifier, challenge };
}

/**
 * M11（#1845）: 登録の 201 が client secret を一度だけ含み、それが Keycloak の現在の secret と一致するか。
 * 🔴 値は文言に出さない（一致・不一致・欠落だけを返す）。
 */
function evaluateIssuedSecret(body, keycloakSecret) {
  const secret = (body || {}).clientSecret;
  if (typeof secret !== 'string' || secret.length === 0) return ['登録の応答に clientSecret が無い'];
  if (typeof keycloakSecret !== 'string' || keycloakSecret.length === 0) return ['Keycloak の現在の secret を読めない'];
  return secret === keycloakSecret ? [] : ['応答の clientSecret が Keycloak の現在の secret と違う'];
}

/** M11（#1845）: 応答（有人の 201・一覧）が client secret を持たないか。 */
function evaluateNoSecret(body) {
  const items = Array.isArray(body) ? body : [body || {}];
  return items.some((x) => x && x.clientSecret !== undefined && x.clientSecret !== null)
    ? ['応答に clientSecret が載っている'] : [];
}

/** M11（#1845）: 再発行の値が新しく（旧い値と違い）、Keycloak の現在の secret と一致するか。値は文言に出さない。 */
function evaluateRotatedSecret(previous, reissued, keycloakSecret) {
  if (typeof reissued !== 'string' || reissued.length === 0) return ['再発行の応答に clientSecret が無い'];
  const errors = [];
  if (reissued === previous) errors.push('再発行の値が旧い値と同じ');
  if (reissued !== keycloakSecret) errors.push('再発行の値が Keycloak の現在の secret と違う');
  return errors;
}

/** M11（FU6 の観測）: 管理イベントの表現に値を含むものの数（判定には使わない。値は返さない）。 */
function countEventsContaining(events, value) {
  if (!Array.isArray(events) || typeof value !== 'string' || value.length === 0) return 0;
  return events.filter((e) => typeof (e || {}).representation === 'string' && e.representation.includes(value)).length;
}

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const t = (name, fn) => { fn(); n++; log(`  ok  ${name}`); };

  t('利用者名は小文字の service-account-<client>', () => {
    assert.strictEqual(serviceAccountUserName('Probe-X'), 'service-account-probe-x');
  });
  t('集合値は多値（順序・重複・空白を正規化）、単一値は 1 要素', () => {
    assert.deepStrictEqual(expectedKeycloakAttributes({ projects: 'b, a,a', department: 'engineering' }),
      { projects: ['a', 'b'], department: ['engineering'] });
  });
  const okClient = {
    clientId: 'p', publicClient: false, serviceAccountsEnabled: true, standardFlowEnabled: false,
    implicitFlowEnabled: false, directAccessGrantsEnabled: false, fullScopeAllowed: false,
    attributes: { [MANAGED_BY_ATTRIBUTE]: MANAGED_BY_VALUE },
  };
  t('M1: テンプレートどおりなら違反 0、印・機密・人の流れ・fullScope の崩れはそれぞれ赤', () => {
    assert.deepStrictEqual(evaluateCreatedClient([okClient], 'p'), []);
    assert.ok(evaluateCreatedClient([], 'p')[0].includes('0 件'));
    assert.ok(evaluateCreatedClient([{ ...okClient, attributes: {} }], 'p')[0].includes('入口の印'));
    for (const k of ['publicClient', 'standardFlowEnabled', 'implicitFlowEnabled', 'directAccessGrantsEnabled', 'fullScopeAllowed']) {
      assert.strictEqual(evaluateCreatedClient([{ ...okClient, [k]: true }], 'p').length, 1, k);
    }
    assert.strictEqual(evaluateCreatedClient([{ ...okClient, implicitFlowEnabled: undefined }], 'p').length, 1, '未指定を閉と読まない');
  });
  t('M2: ちょうど 1 件・属性一致で 0、0 件・2 件・属性違いは赤（余分なキーは見ない）', () => {
    const user = { username: 'service-account-p', attributes: { department: ['engineering'], projects: ['b', 'a'], other: ['x'] } };
    const req = { department: 'engineering', projects: 'a,b' };
    assert.deepStrictEqual(evaluateServiceAccountLookup([user], 'p', req), []);
    assert.ok(evaluateServiceAccountLookup([], 'p', req)[0].includes('0 件'));
    assert.ok(evaluateServiceAccountLookup([user, user], 'p', req)[0].includes('2 件'));
    assert.strictEqual(evaluateServiceAccountLookup([{ ...user, attributes: { department: ['sales'] } }], 'p', req).length, 1);
  });
  t('M4・M6: クライアントか SA 利用者のどちらかが残れば赤', () => {
    assert.deepStrictEqual(evaluateNothingCreated([], [], 'p'), []);
    assert.strictEqual(evaluateNothingCreated([{ clientId: 'p' }], [], 'p').length, 1);
    assert.strictEqual(evaluateNothingCreated([], [{ username: 'service-account-p' }], 'p').length, 1);
  });
  t('M5: 属性の比較は順序に依らず、値・キーの違いは不一致', () => {
    assert.ok(sameAttributes(normalizeAttributes({ a: ['2', '1'] }), normalizeAttributes({ a: ['1', '2'] })));
    assert.ok(!sameAttributes(normalizeAttributes({ a: ['1'] }), normalizeAttributes({ a: ['1'], b: ['x'] })));
  });
  t('M6: 補償の応答は 500 だけ。502（IdP へ書けなかった）・503・400・201 は赤（陰性対照）', () => {
    assert.deepStrictEqual(evaluateCompensationResponse(500, ''), []);
    for (const st of [502, 503, 504, 400, 201]) assert.strictEqual(evaluateCompensationResponse(st, '').length, 1, String(st));
    assert.strictEqual(evaluateCompensationResponse(500, '{"title":"IdP へ書けなかった（登録簿にも書いていない）"}').length, 1);
  });
  t('M6: 管理イベントは作成 1 件 ＋ 同じ資源の削除が後に 1 件。作成が無い（IdP へ書いていない）・削除が無い・主体違いは赤', () => {
    const cr = { operationType: 'CREATE', resourceType: 'CLIENT', resourcePath: 'clients/u1', time: 10,
      representation: JSON.stringify({ clientId: 'p' }), authDetails: { userId: 'sa' } };
    const del = { operationType: 'DELETE', resourceType: 'CLIENT', resourcePath: 'clients/u1', time: 11, authDetails: { userId: 'sa' } };
    assert.deepStrictEqual(evaluateCompensationEvents([cr, del], 'p', 'sa'), []);
    assert.ok(evaluateCompensationEvents([], 'p', 'sa')[0].includes('0 件'));
    assert.strictEqual(evaluateCompensationEvents([cr], 'p', 'sa').length, 1);
    assert.strictEqual(evaluateCompensationEvents([cr, { ...del, time: 9 }], 'p', 'sa').length, 1, '作成より前の削除を数えない');
    assert.strictEqual(evaluateCompensationEvents([cr, { ...del, resourcePath: 'clients/u2' }], 'p', 'sa').length, 1);
    assert.strictEqual(evaluateCompensationEvents([cr, { ...del, authDetails: { userId: 'other' } }], 'p', 'sa').length, 1);
  });
  t('M7: 照合のログに名指しが在れば 0、無い・種類違い・前方一致だけは赤。失敗のログは理由として添える', () => {
    const log = 'warn: X[0]\n      登録簿と IdP の食い違いを検知した: client=p-drift kind=attributes_differ。照合は直さない\n'
      + 'warn: X[0]\n      登録簿と IdP の食い違いを検知した: client=p-orphan kind=orphan。照合は直さない\n';
    const want = [{ clientId: 'p-drift', kind: 'attributes_differ' }, { clientId: 'p-orphan', kind: 'orphan' }];
    assert.deepStrictEqual(evaluateReconciliationLog(log, want), []);
    assert.strictEqual(evaluateReconciliationLog('', want).length, 2);
    assert.strictEqual(evaluateReconciliationLog(log.replace('kind=orphan', 'kind=client_missing'), want).length, 1);
    assert.strictEqual(evaluateReconciliationLog('client=p-drift-2 kind=attributes_differ', [want[0]]).length, 1, '別のクライアントの名指しを数えない');
    assert.strictEqual(evaluateReconciliationLog('client=p-drift kind=attributes_differ-x', [want[0]]).length, 1, '種類の前方一致を数えない');
    const failing = evaluateReconciliationLog('fail: X[0]\n      登録簿と IdP を照合できなかった', want);
    assert.strictEqual(failing.length, 3);
    assert.ok(failing[2].includes('照合が失敗している'));
  });
  t('M8: トークンは 200 かつ access_token で出たと読む。4xx・空の access_token・5xx は出ていない', () => {
    assert.deepStrictEqual(evaluateTokenIssued({ status: 200, json: { access_token: 'x' } }), []);
    assert.strictEqual(evaluateTokenIssued({ status: 200, json: {} }).length, 1);
    assert.strictEqual(evaluateTokenIssued({ status: 401, json: { error: 'invalid_client' } }).length, 1);
    assert.strictEqual(evaluateTokenIssued(undefined).length, 1);
  });
  t('M8: 拒否は 400 / 401 で access_token なしだけ。トークンが出た・5xx・到達不能は赤（Keycloak の不調で緑にしない）', () => {
    assert.deepStrictEqual(evaluateTokenRefused({ status: 401, json: { error: 'invalid_client' } }), []);
    assert.deepStrictEqual(evaluateTokenRefused({ status: 400, json: { error: 'unauthorized_client' } }), []);
    assert.ok(evaluateTokenRefused({ status: 200, json: { access_token: 'x' } })[0].includes('トークンが出た'));
    for (const st of [500, 502, 503, 403, undefined]) assert.strictEqual(evaluateTokenRefused({ status: st, json: null }).length, 1, String(st));
  });
  t('M8: enabled は 1 件の完全一致で比べる。0 件・2 件・未指定・違う値は赤', () => {
    assert.deepStrictEqual(evaluateClientEnabled([{ clientId: 'p', enabled: false }], 'p', false), []);
    assert.strictEqual(evaluateClientEnabled([{ clientId: 'p', enabled: true }], 'p', false).length, 1);
    assert.strictEqual(evaluateClientEnabled([{ clientId: 'p' }], 'p', true).length, 1, '未指定を有効と読まない');
    assert.strictEqual(evaluateClientEnabled([], 'p', true).length, 1);
    assert.strictEqual(evaluateClientEnabled([{ clientId: 'p', enabled: true }, { clientId: 'p', enabled: true }], 'p', true).length, 1);
    assert.strictEqual(evaluateClientEnabled([{ clientId: 'p-2', enabled: false }], 'p', false).length, 1, '前方一致を数えない');
  });
  t('M8: SA は同じ ID で 1 人・属性が同じなら 0。0 件（消えた）・ID 違い（作り直された）・属性違い（空）は赤', () => {
    const sa = { id: 'u1', username: 'service-account-p', attributes: { department: ['engineering'] } };
    const req = { department: 'engineering' };
    assert.deepStrictEqual(evaluateServiceAccountIntact([sa], 'p', req, 'u1'), []);
    assert.ok(evaluateServiceAccountIntact([], 'p', req, 'u1')[0].includes('0 件'));
    assert.ok(evaluateServiceAccountIntact([{ ...sa, id: 'u2' }], 'p', req, 'u1')[0].includes('作り直された'));
    assert.strictEqual(evaluateServiceAccountIntact([{ ...sa, id: 'u2', attributes: {} }], 'p', req, 'u1').length, 1, '空の利用者は属性違い');
  });
  const pub = {
    clientId: 'h', publicClient: true, standardFlowEnabled: true, implicitFlowEnabled: false, directAccessGrantsEnabled: false,
    serviceAccountsEnabled: false, fullScopeAllowed: false, redirectUris: ['https://a/cb', 'http://127.0.0.1:50000/cb'], webOrigins: [],
    attributes: { [MANAGED_BY_ATTRIBUTE]: MANAGED_BY_VALUE, [PKCE_ATTRIBUTE]: 'S256' },
    protocolMappers: [{ protocolMapper: 'oidc-audience-mapper', config: { 'included.custom.audience': MCP_AUDIENCE, 'access.token.claim': 'true' } }],
  };
  t('M9: 有人のテンプレートどおりなら 0、公開・PKCE・流れ・URI・オリジン・audience・印の崩れはそれぞれ赤', () => {
    const uris = ['http://127.0.0.1:50000/cb', 'https://a/cb'];
    assert.deepStrictEqual(evaluatePublicClient([pub], 'h', uris), []);
    assert.ok(evaluatePublicClient([], 'h', uris)[0].includes('0 件'));
    for (const [k, v] of [['publicClient', false], ['standardFlowEnabled', false], ['implicitFlowEnabled', true],
      ['directAccessGrantsEnabled', true], ['serviceAccountsEnabled', true], ['fullScopeAllowed', true],
      ['implicitFlowEnabled', undefined], ['webOrigins', ['+']], ['redirectUris', ['https://a/*']], ['protocolMappers', []]]) {
      assert.strictEqual(evaluatePublicClient([{ ...pub, [k]: v }], 'h', uris).length, 1, `${k}=${JSON.stringify(v)}`);
    }
    assert.strictEqual(evaluatePublicClient([{ ...pub, attributes: { [MANAGED_BY_ATTRIBUTE]: MANAGED_BY_VALUE, [PKCE_ATTRIBUTE]: 'plain' } }], 'h', uris).length, 1);
    assert.strictEqual(evaluatePublicClient([{ ...pub, attributes: { [PKCE_ATTRIBUTE]: 'S256' } }], 'h', uris).length, 1);
    assert.strictEqual(evaluatePublicClient([{ ...pub, protocolMappers: [{ protocolMapper: 'oidc-audience-mapper', config: { 'included.custom.audience': 'account', 'access.token.claim': 'true' } }] }], 'h', uris).length, 1);
    assert.deepStrictEqual(evaluateAudienceMapper([pub], 'h'), []);
    assert.strictEqual(evaluateAudienceMapper([{ ...pub, protocolMappers: [] }], 'h').length, 1);
  });
  t('M9: 認可の要求の分類（200=login・リダイレクト URI へ error つき=error-redirect・4xx で Location なし=rejected・他は other）', () => {
    assert.deepStrictEqual(classifyAuthResponse(200, null, 'https://a/cb'), { outcome: 'login' });
    assert.deepStrictEqual(classifyAuthResponse(302, 'https://a/cb?error=invalid_request&state=s', 'https://a/cb'),
      { outcome: 'error-redirect', error: 'invalid_request' });
    assert.deepStrictEqual(classifyAuthResponse(400, null, 'https://a/cb'), { outcome: 'rejected' });
    assert.strictEqual(classifyAuthResponse(302, 'https://evil/cb?error=x', 'https://a/cb').outcome, 'other', '別の場所へのリダイレクトを拒否と読まない');
    assert.strictEqual(classifyAuthResponse(302, 'https://a/cb?code=x', 'https://a/cb').outcome, 'other', 'コードつきの戻りを拒否と読まない');
    assert.strictEqual(classifyAuthResponse(500, null, 'https://a/cb').outcome, 'other', '5xx を拒否と読まない');
    assert.deepStrictEqual(evaluateAuthOutcome({ outcome: 'error-redirect', error: 'invalid_request' }, 'error-redirect'), []);
    assert.strictEqual(evaluateAuthOutcome({ outcome: 'error-redirect', error: 'access_denied' }, 'error-redirect').length, 1);
    assert.strictEqual(evaluateAuthOutcome({ outcome: 'login' }, 'rejected').length, 1);
  });
  t('M9: 横取りの形（CVE-2024-8883）は登録どおりの port と別の port の 2 つで、宛先は evil.example。port なし・非ループバックは作らない', () => {
    assert.deepStrictEqual(loopbackHijackProbes('http://127.0.0.1:50000/cb'),
      ['http://127.0.0.1:50000@evil.example/cb', 'http://127.0.0.1:1@evil.example/cb']);
    assert.deepStrictEqual(loopbackHijackProbes('http://[::1]:53123/cb'),
      ['http://[::1]:53123@evil.example/cb', 'http://[::1]:1@evil.example/cb']);
    assert.deepStrictEqual(loopbackHijackProbes('http://127.0.0.1:50000'),
      ['http://127.0.0.1:50000@evil.example/', 'http://127.0.0.1:1@evil.example/']);
    // ブラウザ（WHATWG URL）は利用者情報を外して evil.example へ送る —— 試験の形が本当に横取りの形であること。
    for (const u of loopbackHijackProbes('http://127.0.0.1:50000/cb')) assert.strictEqual(new URL(u).host, 'evil.example', u);
    assert.deepStrictEqual(loopbackHijackProbes('http://127.0.0.1/cb'), [], 'port なしは作らない');
    assert.deepStrictEqual(loopbackHijackProbes('https://a/cb'), []);
    assert.deepStrictEqual(loopbackHijackProbes('http://localhost:50000/cb'), []);
    // 横取りを許す Keycloak（ログイン画面へ進む＝200、または evil.example へコードつきで戻す）は rejected の期待で赤になる。
    assert.strictEqual(evaluateAuthOutcome(classifyAuthResponse(200, null, 'http://127.0.0.1:50000@evil.example/cb'), 'rejected').length, 1);
    assert.strictEqual(evaluateAuthOutcome(
      classifyAuthResponse(302, 'http://evil.example/cb?code=x', 'http://127.0.0.1:50000@evil.example/cb'), 'rejected').length, 1);
    assert.deepStrictEqual(evaluateAuthOutcome(classifyAuthResponse(400, null, 'http://127.0.0.1:50000@evil.example/cb'), 'rejected'), []);
  });
  t('M9: トークンの aud・azp・利用者名（aud は文字列でも配列でも読む。無ければ赤）', () => {
    const tok = (p) => `x.${Buffer.from(JSON.stringify(p)).toString('base64url')}.y`;
    assert.deepStrictEqual(evaluateTokenAudience(decodeJwtPayload(tok({ aud: ['mcp-server', 'x'], azp: 'h', preferred_username: 'u' })), 'h', { human: true }), []);
    assert.deepStrictEqual(evaluateTokenAudience(decodeJwtPayload(tok({ aud: 'mcp-server', azp: 's' })), 's'), []);
    assert.strictEqual(evaluateTokenAudience(decodeJwtPayload(tok({ aud: 'account', azp: 'h' })), 'h').length, 1);
    assert.strictEqual(evaluateTokenAudience(decodeJwtPayload(tok({ azp: 'h' })), 'h').length, 1);
    assert.strictEqual(evaluateTokenAudience(decodeJwtPayload(tok({ aud: 'mcp-server', azp: 'h' })), 'h', { human: true }).length, 1);
    assert.strictEqual(evaluateTokenAudience(decodeJwtPayload('garbage'), 'h').length, 1);
  });
  t('M11: 登録の secret は Keycloak の現在値と一致するときだけ緑。欠落・不一致・Keycloak を読めないは赤。文言に値を出さない', () => {
    const v = 'placeholder-value-a';
    assert.deepStrictEqual(evaluateIssuedSecret({ clientSecret: v }, v), []);
    assert.strictEqual(evaluateIssuedSecret({ clientSecret: null }, v).length, 1);
    assert.strictEqual(evaluateIssuedSecret({}, v).length, 1);
    assert.strictEqual(evaluateIssuedSecret({ clientSecret: v }, undefined).length, 1);
    const mismatch = evaluateIssuedSecret({ clientSecret: v }, 'placeholder-value-b');
    assert.strictEqual(mismatch.length, 1);
    assert.ok(!mismatch.join('').includes(v) && !mismatch.join('').includes('placeholder-value-b'), '値を文言に出さない');
  });
  t('M11: 有人の 201・一覧は clientSecret を持たない（null・欠落は緑、値があれば赤）', () => {
    assert.deepStrictEqual(evaluateNoSecret({ clientId: 'h', clientSecret: null }), []);
    assert.deepStrictEqual(evaluateNoSecret([{ clientId: 'a' }, { clientId: 'b' }]), []);
    assert.strictEqual(evaluateNoSecret({ clientSecret: 'placeholder-value-a' }).length, 1);
    assert.strictEqual(evaluateNoSecret([{ clientId: 'a' }, { clientSecret: 'placeholder-value-a' }]).length, 1);
  });
  t('M11: 再発行の値は新しく Keycloak の現在値と一致するときだけ緑。旧い値のまま・不一致・欠落は赤', () => {
    assert.deepStrictEqual(evaluateRotatedSecret('placeholder-old', 'placeholder-new', 'placeholder-new'), []);
    assert.strictEqual(evaluateRotatedSecret('placeholder-old', 'placeholder-old', 'placeholder-old').length, 1);
    assert.strictEqual(evaluateRotatedSecret('placeholder-old', 'placeholder-new', 'placeholder-old').length, 1);
    assert.strictEqual(evaluateRotatedSecret('placeholder-old', undefined, 'placeholder-new').length, 1);
    assert.strictEqual(countEventsContaining([{ representation: '{"value":"placeholder-new"}' }, {}], 'placeholder-new'), 1);
    assert.strictEqual(countEventsContaining(null, 'placeholder-new'), 0);
  });
  t('M10: DCR は 401 / 403 かつ件数が変わらないときだけ閉。201・5xx・件数の増加・作られた client_id は赤', () => {
    assert.deepStrictEqual(evaluateDcrRefused({ status: 403, json: { error: 'insufficient_scope' } }, 10, 10), []);
    assert.deepStrictEqual(evaluateDcrRefused({ status: 401, json: null }, 10, 10), []);
    assert.ok(evaluateDcrRefused({ status: 201, json: { client_id: 'x' } }, 10, 11).length >= 2);
    assert.strictEqual(evaluateDcrRefused({ status: 500, json: null }, 10, 10).length, 1);
    assert.strictEqual(evaluateDcrRefused({ status: 403, json: null }, 10, 11).length, 1);
    assert.strictEqual(evaluateDcrRefused({ status: 403, json: null }, undefined, 10).length, 1, '件数を読めなければ赤');
  });
  t('M10: 匿名の Trusted Hosts（信頼ホストは空）と初期アクセストークン 0 個', () => {
    const th = { providerId: 'trusted-hosts', subType: 'anonymous', config: { 'trusted-hosts': [] } };
    assert.deepStrictEqual(evaluateRegistrationPolicies([th, { providerId: 'max-clients', subType: 'anonymous' }]), []);
    assert.deepStrictEqual(evaluateRegistrationPolicies([{ ...th, config: {} }]), []);
    assert.strictEqual(evaluateRegistrationPolicies([]).length, 1);
    assert.strictEqual(evaluateRegistrationPolicies([{ ...th, subType: 'authenticated' }]).length, 1, '認証つきの側だけでは匿名を止めない');
    assert.strictEqual(evaluateRegistrationPolicies([{ ...th, config: { 'trusted-hosts': ['10.0.0.1'] } }]).length, 1);
    assert.deepStrictEqual(evaluateNoInitialAccessTokens([]), []);
    assert.strictEqual(evaluateNoInitialAccessTokens([{ id: 'x' }]).length, 1);
    assert.strictEqual(evaluateNoInitialAccessTokens(null).length, 1);
  });
  t('M12: platform-api の有無を期待どおりに判定し、mcp-server の同居・aud なし・本文なしを赤にする', () => {
    assert.deepStrictEqual(evaluatePlatformAudience({ aud: ['account', 'platform-api'] }, true), []);
    assert.deepStrictEqual(evaluatePlatformAudience({ aud: 'platform-api' }, true), []);
    assert.strictEqual(evaluatePlatformAudience({}, true).length, 1);
    assert.strictEqual(evaluatePlatformAudience({ aud: ['platform-api', 'mcp-server'] }, true).length, 1);
    assert.deepStrictEqual(evaluatePlatformAudience({ aud: 'mcp-server' }, false), []);
    assert.deepStrictEqual(evaluatePlatformAudience({}, false), []);
    assert.strictEqual(evaluatePlatformAudience({ aud: ['grafana', 'platform-api'] }, false).length, 1);
    assert.strictEqual(evaluatePlatformAudience(null, false).length, 1);
  });
  t('M12 (#1846): 使い捨ての登録者に platform-api-audience を割り当てる（無いと McpServer の管理 API が 401 で門ごと赤）', () => {
    const src = require('fs').readFileSync(__filename, 'utf8');
    assert.match(src, /for \(const name of \['profile', 'roles', PLATFORM_AUDIENCE_SCOPE\]\)/);
  });
  t('M9: PKCE の S256 の挑戦は検証子の SHA-256 の base64url', () => {
    const { verifier, challenge } = pkcePair();
    assert.ok(/^[A-Za-z0-9_-]{43}$/.test(verifier) && /^[A-Za-z0-9_-]{43}$/.test(challenge));
    assert.strictEqual(require('crypto').createHash('sha256').update(verifier).digest('base64url'), challenge);
  });
  t('M7: enabled_differs の名指しも同じ判定器で読む', () => {
    assert.deepStrictEqual(evaluateReconciliationLog('client=p-off kind=enabled_differs。', [{ clientId: 'p-off', kind: 'enabled_differs' }]), []);
    assert.strictEqual(evaluateReconciliationLog('client=p-off kind=attributes_differ', [{ clientId: 'p-off', kind: 'enabled_differs' }]).length, 1);
  });
  t('M6: 補償の表示名は登録簿の上限を超え、Keycloak の上限に収まる', () => {
    const name = overlongDisplayName();
    assert.ok(name.length > REGISTRY_DISPLAY_NAME_MAX && name.length <= KEYCLOAK_NAME_MAX);
  });
  // #1835: 期限切れの 401 は 1 度だけ取り直して送り直す。本当の 401・取り直せないトークン・401 以外は送り直さない。
  const retryCases = async () => {
    const fake = (statuses) => {
      const seen = [];
      return { seen, sendFn: async (b) => { seen.push(b); return { status: statuses[seen.length - 1] }; } };
    };
    const counter = () => { let k = 0; return bearerSource(async () => `t${++k}`); };
    let f = fake([401, 200]);
    assert.strictEqual((await sendWithRefresh(f.sendFn, counter())).status, 200, '期限切れの後に取り直して通らない');
    assert.deepStrictEqual(f.seen, ['t1', 't2'], '取り直したトークンで送り直していない');
    f = fake([401, 401]);
    assert.strictEqual((await sendWithRefresh(f.sendFn, counter())).status, 401, '2 度目の 401 を緑にした');
    assert.strictEqual(f.seen.length, 2, '送り直しは 1 度だけ');
    f = fake([403]);
    assert.strictEqual((await sendWithRefresh(f.sendFn, counter())).status, 403);
    assert.deepStrictEqual(f.seen, ['t1'], '401 以外で送り直した');
    f = fake([401]);
    assert.strictEqual((await sendWithRefresh(f.sendFn, 'fixed')).status, 401, '取り直せないトークンで送り直した');
    assert.deepStrictEqual(f.seen, ['fixed']);
    const src = counter();
    assert.strictEqual(await src(), 't1');
    assert.strictEqual(await src(), 't1', '取り直す前に毎回取りに行った');
    assert.strictEqual(await src.refresh(), 't2');
    assert.strictEqual(await src(), 't2', '取り直した値を使い続けない');
  };
  // #1835 監査 🟡1: 取り直しの部品だけでなく、live の 3 つのトークンが取り直せる形で配線されていることも固定する（外れると同じ 401 の赤が戻る）。
  t('#1835: live の管理者・登録者・mcp-client-admin のトークンは取り直せる形（bearerSource）で持つ', () => {
    const src = require('fs').readFileSync(__filename, 'utf8');
    assert.match(src, /const admin = bearerSource\(/, '管理者のトークンが取り直せる形でない');
    assert.match(src, /const provisioner = bearerSource\(/, 'mcp-client-admin のトークンが取り直せる形でない');
    assert.match(src, /const registrarToken = bearerSource\(/, '登録者のトークンが取り直せる形でない');
    assert.match(src, /return \{ id: created\.id, token: registrarToken \}/, '登録者の取り直せるトークンを返していない');
  });
  return retryCases().then(() => {
    n++;
    log('  ok  #1835: 期限切れの 401 は取り直して 1 度だけ送り直す。2 度目の 401・401 以外・取り直せないトークンはそのまま返す');
    log(`self-test OK: ${n} 件`);
    return 0;
  });
}

// ---------------------------------------------------------------- 稼働クラスタへの I/O

const forwards = [];
function portForward(ns, svc, localPort, remotePort) {
  const child = spawn('kubectl', ['-n', ns, 'port-forward', `svc/${svc}`, `${localPort}:${remotePort}`], { stdio: ['ignore', 'ignore', 'ignore'] });
  forwards.push(child);
}
function cleanupForwards() {
  for (const c of forwards) {
    try { c.kill(); } catch { /* 片付けの失敗で終了コードを変えない */ }
  }
  forwards.length = 0;
}

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

/** Secret のキーを読む（値は stdout から受け、引数にもログにも出さない）。 */
function readSecretKey(ns, name, key) {
  const r = spawnSync('kubectl', ['-n', ns, 'get', 'secret', name, '-o', 'json'], { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 });
  if (r.status !== 0) throw new Error(`Secret ${ns}/${name} を読めない（exit ${r.status}）`);
  const b64 = ((JSON.parse(r.stdout).data) || {})[key];
  if (!b64) throw new Error(`Secret ${ns}/${name} にキー ${key} が無い`);
  return Buffer.from(b64, 'base64').toString('utf8');
}

/** 登録簿（mcp_svc）へ SQL を流す。SQL は標準入力で渡す（値に秘密は無い）。 */
function psql(sql) {
  const r = spawnSync('kubectl', ['-n', INFRA_NS, 'exec', '-i', 'deploy/postgres', '-c', 'postgres', '--',
    'psql', '-U', 'postgres', '-d', 'mcp_svc', '-v', 'ON_ERROR_STOP=1', '-q', '-t', '-A'], { input: sql, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`psql が失敗した（exit ${r.status}）: ${String(r.stderr).trim().slice(0, 300)}`);
  return String(r.stdout).trim();
}

const sqlLiteral = (s) => `'${String(s).replace(/'/g, "''")}'`;

async function token(kcUrl, realm, form) {
  const res = await fetch(`${kcUrl}/realms/${realm}/protocol/openid-connect/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form),
  });
  if (!res.ok) throw new Error(`トークン取得が ${res.status}（realm ${realm}・client ${form.client_id}）`);
  return (await res.json()).access_token;
}

/** トークンの取得を試みる（M8。投げずに状態と本文を返す。secret は本文で送り、ログに出さない）。 */
async function tokenAttempt(kcUrl, realm, form) {
  try {
    const res = await fetch(`${kcUrl}/realms/${realm}/protocol/openid-connect/token`, {
      method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form),
    });
    let json = null;
    try { json = await res.json(); } catch { /* 本文が JSON でない */ }
    return { status: res.status, json };
  } catch (e) {
    return { status: undefined, json: null, error: e.message };
  }
}

async function send(method, url, bearer, body) {
  const res = await fetch(url, {
    method,
    headers: { Authorization: `Bearer ${bearer}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* 本文が JSON でない（5xx 等） */ }
  return { status: res.status, json, text };
}

/**
 * #1835: 取り直せるトークン。実走は M7 の照合待ち（最大 150 秒）を挟んで長く、master の管理者のトークン（既定の寿命 60 秒）や
 * 登録者のトークンが途中で切れる。最初に 1 度だけ取ったトークンを使い回すと、期限切れの 401 を「期待と違う」と取り違えて門が赤になる。
 */
function bearerSource(fetchToken) {
  let current = null;
  const source = async () => (current ??= await fetchToken());
  source.refresh = async () => (current = await fetchToken());
  return source;
}

/**
 * 401 なら 1 度だけ取り直して送り直す（取り直せるトークンのときだけ）。2 度目も 401 なら、その 401 をそのまま返す
 * （本当に拒まれたものを緑にしない）。`sendFn` は差し替えられる（self-test 用）。
 */
async function sendWithRefresh(sendFn, bearer) {
  if (typeof bearer !== 'function') return sendFn(bearer);
  const first = await sendFn(await bearer());
  if (first.status !== 401) return first;
  return sendFn(await bearer.refresh());
}

const call = (method, url, bearer, body) => sendWithRefresh((b) => send(method, url, b, body), bearer);

/**
 * 使い捨ての登録者を作る（master の管理者で）。機密・SA のみ・人の流れは閉・既定スコープ profile / roles・SA に platform-admin。
 * secret は Keycloak が生成した値を管理 API から読む（コードに値を持たない）。戻り値は { id, token }。
 */
async function createRegistrar(kcAdmin, kcUrl, admin, clientId) {
  const mustOk = (r, what, ...want) => { if (!want.includes(r.status)) throw new Error(`${what} が ${r.status}: ${String(r.text).slice(0, 200)}`); return r; };
  mustOk(await call('POST', `${kcAdmin}/clients`, admin, {
    clientId, name: 'SC-12 provisioning probe registrar (temporary)', enabled: true, protocol: 'openid-connect',
    publicClient: false, serviceAccountsEnabled: true, standardFlowEnabled: false, implicitFlowEnabled: false,
    directAccessGrantsEnabled: false, redirectUris: [], webOrigins: [],
  }), '使い捨ての登録者の作成', 201);
  const created = (mustOk(await call('GET', `${kcAdmin}/clients?clientId=${encodeURIComponent(clientId)}`, admin), '登録者の引き直し', 200).json || [])
    .find((c) => c.clientId === clientId);
  if (!created) throw new Error('使い捨ての登録者を引き直せない');
  const scopes = mustOk(await call('GET', `${kcAdmin}/client-scopes`, admin), 'client-scopes の取得', 200).json || [];
  // #1846: platform-api-audience が無いと、登録者のトークンは McpServer の管理 API（既定のスキーム。aud=platform-api を要求）で 401 になる。
  for (const name of ['profile', 'roles', PLATFORM_AUDIENCE_SCOPE]) {
    const scope = scopes.find((x) => x.name === name);
    if (!scope) throw new Error(`realm にスコープ ${name} が無い`);
    mustOk(await call('PUT', `${kcAdmin}/clients/${created.id}/default-client-scopes/${scope.id}`, admin), `スコープ ${name} の割当`, 204);
  }
  const saUser = mustOk(await call('GET', `${kcAdmin}/clients/${created.id}/service-account-user`, admin), '登録者の SA', 200).json;
  const role = mustOk(await call('GET', `${kcAdmin}/roles/${REGISTRAR_ROLE}`, admin), `ロール ${REGISTRAR_ROLE} の取得`, 200).json;
  mustOk(await call('POST', `${kcAdmin}/users/${saUser.id}/role-mappings/realm`, admin, [role]), 'ロールの割当', 204);
  const secret = mustOk(await call('GET', `${kcAdmin}/clients/${created.id}/client-secret`, admin), '登録者の secret の取得', 200).json.value;
  const registrarToken = bearerSource(() => token(kcUrl, REALM, { grant_type: 'client_credentials', client_id: clientId, client_secret: secret }));
  await registrarToken(); // 取れることをここで確かめる（従来どおり、取れなければこの場で落ちる）
  return { id: created.id, token: registrarToken };
}

async function live() {
  let mcpUrl = env('MCP_PROV_MCP_URL', '');
  let kcUrl = env('MCP_PROV_KC_URL', '');
  if (!mcpUrl || !kcUrl) {
    if (spawnSync('kubectl', ['cluster-info'], { stdio: 'ignore' }).status !== 0) {
      warn('k8s に到達できません（kubectl cluster-info が失敗）。MCP_PROV_MCP_URL / MCP_PROV_KC_URL で接続先を直接指定してください。');
      return 2;
    }
    if (!mcpUrl) { portForward(NS, 'mcp-service', 18093, 8080); mcpUrl = 'http://localhost:18093'; }
    if (!kcUrl) { portForward(INFRA_NS, 'keycloak', 18094, 8080); kcUrl = 'http://localhost:18094'; }
    const reachable = (await waitReachable(`${mcpUrl}/health/ready`)) && (await waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`));
    if (!reachable) { warn('port-forward 経由で mcp-service / keycloak へ到達できませんでした。'); return 2; }
  }
  log(`接続先: mcp=${mcpUrl} / keycloak=${kcUrl} / realm=${REALM}`);

  const adminUser = readSecretKey(INFRA_NS, 'keycloak-admin', 'username');
  // #1835: 管理者・管理用の主体のトークンは取り直せる形で持つ（期限切れの 401 で門を赤にしない）。
  const admin = bearerSource(() => token(kcUrl, 'master', {
    grant_type: 'password', client_id: 'admin-cli', username: adminUser, password: readSecretKey(INFRA_NS, 'keycloak-admin', 'password'),
  }));
  await admin();
  // 管理用の主体のトークンが出ること自体が、Secret（供給の連鎖）と realm の宣言の secret の一致の実測である。
  const provisioner = bearerSource(() => token(kcUrl, REALM, {
    grant_type: 'client_credentials', client_id: ADMIN_CLIENT, client_secret: readSecretKey(NS, ADMIN_SECRET, 'client-secret'),
  }));
  await provisioner();
  const kcAdmin = `${kcUrl}/admin/realms/${REALM}`;
  const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1e6).toString(36)}`;
  const id = (suffix) => `probe-mcp-${run}-${suffix}`;
  const registrarId = id('registrar');
  let registrar = null; // 使い捨ての登録者のトークン（下の try の先頭で作る）
  const clientsOf = async (clientId) => {
    const r = await call('GET', `${kcAdmin}/clients?clientId=${encodeURIComponent(clientId)}`, admin);
    if (r.status !== 200) throw new Error(`GET clients?clientId=${clientId} が ${r.status}`);
    return r.json;
  };
  const usersOf = async (clientId) => {
    const r = await call('GET', `${kcAdmin}/users?username=${encodeURIComponent(serviceAccountUserName(clientId))}&exact=true&briefRepresentation=false`, admin);
    if (r.status !== 200) throw new Error(`GET users?username=… が ${r.status}`);
    return r.json;
  };
  const register = (clientId, attributes, displayName = 'SC-12 provisioning probe') =>
    call('POST', `${mcpUrl}/mcp-clients`, registrar, { clientId, displayName, kind: 'service-account', attributes });
  const replace = (clientId, attributes) => call('PUT', `${mcpUrl}/mcp-clients/${encodeURIComponent(clientId)}/attributes`, registrar, { attributes });
  const toggle = (clientId, action) => call('POST', `${mcpUrl}/mcp-clients/${encodeURIComponent(clientId)}/${action}`, registrar);
  const registryRows = async () => {
    const r = await call('GET', `${mcpUrl}/mcp-clients`, registrar);
    if (r.status !== 200) throw new Error(`GET /mcp-clients が ${r.status}`);
    return r.json;
  };

  const created = [];
  const failures = [];
  const step = (label, errors) => {
    if (errors.length === 0) log(`  ✓ ${label}`);
    else { for (const e of errors) failures.push(`${label}: ${e}`); log(`  ✗ ${label}\n      ${errors.join('\n      ')}`); }
  };
  const status = (r, want) => (r.status === want ? [] : [`状態が ${r.status}（期待 ${want}）: ${String(r.text).slice(0, 300)}`]);
  const legacyMarker = `SC-12 provisioning probe legacy row ${run}`;
  let legacyInserted = false; // M5 が入口ができる前の行（abac-seeder）を置いたか（M8 の否定形の前提）

  try {
    created.push(registrarId); // 作りかけで落ちても片付けの対象に入れる（clientId で引いて消す）
    registrar = (await createRegistrar(kcAdmin, kcUrl, admin, registrarId)).token;
    log(`トークン: 登録者=${registrarId}（使い捨て）/ 照会=master の管理者 / 削除の権限の実測=${ADMIN_CLIENT}（いずれも取得できた）`);

    // --- M1・M2 ----------------------------------------------------------------------------------
    const okId = id('ok');
    const okAttrs = { department: 'engineering', projects: 'probe-a,probe-b' };
    const r1 = await register(okId, okAttrs);
    if (r1.status === 201) created.push(okId);
    step('M1 無人の登録が 201（503 にならない）', status(r1, 201));
    step('M1 Keycloak のクライアントに入口の印があり、機密・SA つき・人の流れは閉', evaluateCreatedClient(await clientsOf(okId), okId));
    step('M2 users?username=service-account-<client>&exact=true がちょうど 1 件で属性が入っている（フォローアップ 3）',
      evaluateServiceAccountLookup(await usersOf(okId), okId, okAttrs));
    const row = (await registryRows()).find((c) => c.clientId === okId);
    step('M1 登録簿の行は IdP へ書いた値の写し', row && sameAttributes(normalizeAttributes(row.attributes), normalizeAttributes(okAttrs))
      ? [] : [`登録簿の行が無いか属性が違う: ${JSON.stringify(row)}`]);

    // --- M3 --------------------------------------------------------------------------------------
    const r3 = await replace(okId, { department: 'sales' });
    step('M3 差し替え（入口の印あり）が 200', status(r3, 200));
    const afterReplace = await usersOf(okId);
    step('M3 Keycloak の属性が丸ごと置き換わる', evaluateServiceAccountLookup(afterReplace, okId, { department: 'sales' })
      .concat('projects' in (((afterReplace[0] || {}).attributes) || {}) ? ['projects が残っている（丸ごと置き換えでない）'] : []));

    // --- M4 --------------------------------------------------------------------------------------
    for (const [suffix, attrs, label] of [
      ['subset', { tags: `probe-outside-${run}` }, '部分集合の外れ（tags）'],
      ['private', { doc_scope: 'private-note' }, 'doc_scope=private-note'],
    ]) {
      const cid = id(suffix);
      const r = await register(cid, attrs);
      if (r.status === 201) created.push(cid);
      step(`M4 登録: ${label} は 400`, status(r, 400));
      // 拒否の理由が「外れた値の名指し」であること（登録者の属性を引けなかった 400 と区別する。引けなければ規則は測れていない）。
      const named = suffix === 'subset' ? `probe-outside-${run}` : 'private-note';
      step(`M4 登録: ${label} の拒否理由が外れた値を名指しする`, String(r.text).includes(named) ? [] : [`本文に ${named} が無い: ${String(r.text).slice(0, 300)}`]);
      step(`M4 登録: ${label} で Keycloak に何も作られない`, evaluateNothingCreated(await clientsOf(cid), await usersOf(cid), cid));
      const before = normalizeAttributes(((await usersOf(okId))[0] || {}).attributes);
      const rr = await replace(okId, attrs);
      step(`M4 差し替え: ${label} は 400`, status(rr, 400));
      const after = normalizeAttributes(((await usersOf(okId))[0] || {}).attributes);
      step(`M4 差し替え: ${label} で Keycloak の属性は変わらない`, sameAttributes(before, after) ? [] : [`前 ${JSON.stringify(before)}・後 ${JSON.stringify(after)}`]);
    }

    // --- M5 --------------------------------------------------------------------------------------
    const platformClient = PLATFORM_CLIENT;
    const seederBefore = normalizeAttributes(((await usersOf(platformClient))[0] || {}).attributes);
    if (Object.keys(seederBefore).length === 0) failures.push(`M5 の前提: ${serviceAccountUserName(platformClient)} の属性を読めない（0 件を緑にしない）`);
    const r5 = await register(platformClient, { department: `probe-${run}` });
    step(`M5 登録: プラットフォームのクライアント名（${platformClient}）は 400`, status(r5, 400));
    const seederAfterRegister = normalizeAttributes(((await usersOf(platformClient))[0] || {}).attributes);
    step(`M5 登録: ${serviceAccountUserName(platformClient)} の属性は変わらない`,
      sameAttributes(seederBefore, seederAfterRegister) ? [] : [`前 ${JSON.stringify(seederBefore)}・後 ${JSON.stringify(seederAfterRegister)}`]);
    const stillThere = (await clientsOf(platformClient)).filter((c) => c.clientId === platformClient);
    step(`M5 登録: ${platformClient} のクライアントは消されず、入口の印も付かない`,
      stillThere.length === 1 && !((stillThere[0].attributes || {})[MANAGED_BY_ATTRIBUTE]) ? [] : [`${JSON.stringify(stillThere.map((c) => c.attributes))}`]);

    // 入口ができる前の登録簿の行（無人・abac-seeder）を置き、差し替えが入口の印の無いクライアントへ書かないことを測る。
    const existing = psql(`SELECT count(*) FROM "Clients" WHERE "ClientId" = ${sqlLiteral(platformClient)};`);
    if (existing !== '0') {
      failures.push(`M5 の前提: 登録簿に ${platformClient} の行が既にある（${existing} 件）。差し替えの実測を行わない`);
    } else {
      psql(`INSERT INTO "Clients" ("Id","ClientId","DisplayName","Kind","Enabled","Attributes","EgressTier","RegisteredAt","UpdatedAt")
        VALUES (gen_random_uuid(), ${sqlLiteral(platformClient)}, ${sqlLiteral(legacyMarker)}, 1, true, '{}'::jsonb, 2, now(), now());`);
      legacyInserted = true;
      const r5b = await replace(platformClient, { department: `probe-${run}` });
      step(`M5 差し替え: 入口ができる前の行（${platformClient}）は 400`, status(r5b, 400));
      const seederAfterReplace = normalizeAttributes(((await usersOf(platformClient))[0] || {}).attributes);
      step(`M5 差し替え: ${serviceAccountUserName(platformClient)} の属性は変わらない`,
        sameAttributes(seederBefore, seederAfterReplace) ? [] : [`前 ${JSON.stringify(seederBefore)}・後 ${JSON.stringify(seederAfterReplace)}`]);
    }

    // --- M6 --------------------------------------------------------------------------------------
    const compId = id('comp');
    const r6 = await register(compId, { department: 'engineering' }, overlongDisplayName());
    if (r6.status === 201) created.push(compId);
    step('M6 IdP へ書けて登録簿で落ちる登録は 500（502 / 503 は IdP 側の失敗で補償の経路を通っていない）',
      evaluateCompensationResponse(r6.status, r6.text));
    step('M6 補償で Keycloak にクライアントも SA 利用者も残らない', evaluateNothingCreated(await clientsOf(compId), await usersOf(compId), compId));
    step('M6 登録簿にも行が無い', (await registryRows()).some((c) => c.clientId === compId) ? ['登録簿に行が残っている'] : []);
    // 「作成 → 削除」が実際に起きたことを管理イベントで確かめる（realm は adminEventsEnabled / adminEventsDetailsEnabled）。
    const adminSa = (await usersOf(ADMIN_CLIENT))[0];
    const ev = await call('GET', `${kcAdmin}/admin-events?resourceTypes=CLIENT&max=500`, admin);
    step(`M6 管理イベントで、${ADMIN_CLIENT} がクライアントを作ってから消した（補償が実際に走った）`,
      ev.status === 200 ? evaluateCompensationEvents(ev.json, compId, adminSa && adminSa.id)
        : [`GET admin-events が ${ev.status}`]);

    // 補償が使う削除の権限を、mcp-client-admin の資格情報そのもので測る（M1 のクライアントを消す＝片付けを兼ねる）。
    const target = (await clientsOf(okId)).find((c) => c.clientId === okId);
    if (target) {
      const del = await call('DELETE', `${kcAdmin}/clients/${target.id}`, provisioner);
      step(`M6 ${ADMIN_CLIENT} の資格情報で入口が作ったクライアントを消せる`, status(del, 204));
      step('M6 消した後は Keycloak にクライアントも SA 利用者も無い', evaluateNothingCreated(await clientsOf(okId), await usersOf(okId), okId));
    } else {
      failures.push('M6 の前提: M1 のクライアントが無い（削除の権限を測れない）');
    }

    // --- M7（#1818）--------------------------------------------------------------------------------
    // 入口を通らない IdP の直接の操作（ADR-0123 決定 2 が禁じた操作）を 2 つ作り、照合がそれぞれを名指しするのを待つ。
    const since = new Date(Date.now() - 5000).toISOString();
    const driftId = id('drift');
    const r7 = await register(driftId, { department: 'engineering' });
    if (r7.status === 201) created.push(driftId);
    step('M7 前提: 照合の対象にする無人の登録が 201', status(r7, 201));
    const saUser = (await usersOf(driftId))[0];
    if (saUser) {
      // read-modify-write（PUT は部分更新でない）。属性だけを登録簿と違う値へ書き換える。
      const full = await call('GET', `${kcAdmin}/users/${saUser.id}`, admin);
      const rep = { ...(full.json || {}), attributes: { ...((full.json || {}).attributes || {}), department: ['sales'] } };
      for (const k of ['access', 'disableableCredentialTypes', 'userProfileMetadata']) delete rep[k];
      step('M7 前提: サービスアカウントの属性を master の管理者で直接書き換える', status(await call('PUT', `${kcAdmin}/users/${saUser.id}`, admin, rep), 204));
    } else {
      failures.push('M7 の前提: 照合の対象のサービスアカウントが無い');
    }
    const orphanId = id('orphan');
    created.push(orphanId);
    step('M7 前提: 入口の印つきのクライアントを登録簿を通らずに作る（補償が走らなかった残骸の再現）', status(await call('POST', `${kcAdmin}/clients`, admin, {
      clientId: orphanId, name: 'SC-12 reconciliation probe orphan', enabled: true, protocol: 'openid-connect',
      publicClient: false, serviceAccountsEnabled: true, standardFlowEnabled: false, implicitFlowEnabled: false,
      directAccessGrantsEnabled: false, redirectUris: [], webOrigins: [],
      attributes: { [MANAGED_BY_ATTRIBUTE]: MANAGED_BY_VALUE },
    }), 201));
    // ［#1829］入口で登録したクライアントを master の管理者で直接無効にする（登録簿は有効のまま）→ enabled_differs。
    const offId = id('off');
    const r7c = await register(offId, { department: 'engineering' });
    if (r7c.status === 201) created.push(offId);
    step('M7 前提: 有効・無効の照合の対象にする無人の登録が 201', status(r7c, 201));
    const offClient = (await clientsOf(offId)).find((c) => c.clientId === offId);
    if (offClient) {
      // read-modify-write（検査対象の口と同じ部分本文の形を使わない＝測る側を測られる側から独立させる）。
      const full = (await call('GET', `${kcAdmin}/clients/${offClient.id}`, admin)).json || {};
      step('M7 前提: 入口の印つきのクライアントを master の管理者で直接無効にする',
        status(await call('PUT', `${kcAdmin}/clients/${offClient.id}`, admin, { ...full, enabled: false }), 204));
    } else {
      failures.push('M7 の前提: 有効・無効の照合の対象のクライアントが無い');
    }
    const expected = [{ clientId: driftId, kind: 'attributes_differ' }, { clientId: orphanId, kind: 'orphan' },
      { clientId: offId, kind: 'enabled_differs' }];
    const deadline = Date.now() + RECONCILE_WAIT_MS;
    let reconcileErrors = ['照合のログを読めていない'];
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, RECONCILE_POLL_MS));
      const logs = spawnSync('kubectl', ['-n', NS, 'logs', 'deploy/mcp-service', '--all-containers=true', `--since-time=${since}`],
        { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
      if (logs.status !== 0) { reconcileErrors = [`kubectl logs が失敗した（exit ${logs.status}）`]; continue; }
      reconcileErrors = evaluateReconciliationLog(logs.stdout, expected);
      if (reconcileErrors.length === 0) break;
    }
    step(`M7 照合が、属性の書き換えを attributes_differ・登録簿に無い印つきのクライアントを orphan・直接の無効化を enabled_differs として名指しする（${RECONCILE_WAIT_MS / 1000} 秒以内）`,
      reconcileErrors);

    // --- M8（#1829）--------------------------------------------------------------------------------
    const toggleId = id('toggle');
    const r8 = await register(toggleId, { department: 'engineering' });
    if (r8.status === 201) created.push(toggleId);
    step('M8 前提: 無効化の対象にする無人の登録が 201', status(r8, 201));
    const toggled = (await clientsOf(toggleId)).find((c) => c.clientId === toggleId);
    if (toggled) {
      const sec = await call('GET', `${kcAdmin}/clients/${toggled.id}/client-secret`, admin);
      const secret = ((sec.json || {}).value) || '';
      if (!secret) failures.push(`M8 の前提: ${toggleId} の secret を読めない（状態 ${sec.status}）`);
      const issue = () => tokenAttempt(kcUrl, REALM, { grant_type: 'client_credentials', client_id: toggleId, client_secret: secret });
      const toggleAttrs = { department: 'engineering' };
      const saBefore = (await usersOf(toggleId))[0];
      step('M8 前提: サービスアカウントの利用者が 1 人で属性が入っている', evaluateServiceAccountLookup(await usersOf(toggleId), toggleId, toggleAttrs));
      step('M8 陽性対照: 無効化の前は client_credentials のトークンが出る', evaluateTokenIssued(await issue()));

      step('M8 SC-12 の無効化が 200', status(await toggle(toggleId, 'disable'), 200));
      step('M8 無効化で Keycloak のクライアントの enabled が false', evaluateClientEnabled(await clientsOf(toggleId), toggleId, false));
      // 🔴 トークンを要求する前に見る（要求が SA を作り直して事故を隠す。PR #1832 監査 🔴1）。
      step('M8 無効化の後もサービスアカウントの利用者が同じ ID で残り、属性が変わらない',
        evaluateServiceAccountIntact(await usersOf(toggleId), toggleId, toggleAttrs, saBefore && saBefore.id));
      step('M8 無効化の後は client_credentials のトークン発行が Keycloak に拒否される', evaluateTokenRefused(await issue()));
      const offRow = (await registryRows()).find((c) => c.clientId === toggleId);
      step('M8 登録簿の行も無効', offRow && offRow.enabled === false ? [] : [`登録簿の行: ${JSON.stringify(offRow)}`]);

      step('M8 SC-12 の再有効化が 200', status(await toggle(toggleId, 'enable'), 200));
      step('M8 再有効化で Keycloak のクライアントの enabled が true', evaluateClientEnabled(await clientsOf(toggleId), toggleId, true));
      step('M8 再有効化の後もサービスアカウントの利用者が同じ ID で残り、属性が変わらない（トークンの要求より前に見る）',
        evaluateServiceAccountIntact(await usersOf(toggleId), toggleId, toggleAttrs, saBefore && saBefore.id));
      step('M8 再有効化の後はトークンが再び出る', evaluateTokenIssued(await issue()));
      step('M8 再有効化の後もテンプレートの項目（入口の印・機密・人の流れの閉）が残る（enabled と SA・authorization の現在値だけを書いた）',
        evaluateCreatedClient(await clientsOf(toggleId), toggleId));
    } else {
      failures.push('M8 の前提: 無効化の対象のクライアントが無い');
    }
    // 否定形: 入口ができる前の登録簿の行（M5 が置いた abac-seeder）。プラットフォームのクライアントを無効化の経路から変えない。
    if (legacyInserted) {
      step(`M8 否定形の前提: ${PLATFORM_CLIENT} は Keycloak で有効`, evaluateClientEnabled(await clientsOf(PLATFORM_CLIENT), PLATFORM_CLIENT, true));
      step(`M8 否定形: ${PLATFORM_CLIENT} の行の無効化は 200（登録簿だけ）`, status(await toggle(PLATFORM_CLIENT, 'disable'), 200));
      step(`M8 否定形: 無効化しても ${PLATFORM_CLIENT} の enabled は true のまま`,
        evaluateClientEnabled(await clientsOf(PLATFORM_CLIENT), PLATFORM_CLIENT, true));
      step(`M8 否定形: ${PLATFORM_CLIENT} の行の再有効化は 400（入口を通らない主体へ接続を開かない）`, status(await toggle(PLATFORM_CLIENT, 'enable'), 400));
      step(`M8 否定形: 再有効化の後も ${PLATFORM_CLIENT} の enabled は true のまま`,
        evaluateClientEnabled(await clientsOf(PLATFORM_CLIENT), PLATFORM_CLIENT, true));
    } else {
      failures.push(`M8 の否定形の前提: M5 が入口ができる前の行（${PLATFORM_CLIENT}）を置けていない`);
    }

    // --- M9（#1844 / 計画 ADR-0134 決定 1・フォローアップ 1〜3）--------------------------------------
    const humanId = id('human');
    const httpsRedirect = `https://${humanId}.example.test/cb`;
    // 🔴 ループバックは port を明示して登録する（CVE-2024-8883。port なしは SC-12 が 400 で拒む）。
    const v4Port = 50000;
    const v4Redirect = `http://127.0.0.1:${v4Port}/cb`;
    const v6Port = 53123;
    const v6Redirect = `http://[::1]:${v6Port}/cb`;
    const humanRedirects = [httpsRedirect, v4Redirect, v6Redirect];
    const registerHuman = (clientId, redirectUris, displayName = 'SC-12 provisioning probe (attended)') =>
      call('POST', `${mcpUrl}/mcp-clients`, registrar, { clientId, displayName, kind: 'interactive', redirectUris });
    const r9 = await registerHuman(humanId, humanRedirects);
    if (r9.status === 201) created.push(humanId);
    step('M9 有人の登録が 201（503 にならない）', status(r9, 201));
    step('M9 Keycloak のクライアントは公開・認可コードだけ・PKCE S256・URI は入力どおり・Web オリジン空・入口の印・audience の写像',
      evaluatePublicClient(await clientsOf(humanId), humanId, humanRedirects));
    const humanRow = (await registryRows()).find((c) => c.clientId === humanId);
    step('M9 登録簿の行は有人', humanRow && humanRow.kind === 'interactive' ? [] : [`登録簿の行: ${JSON.stringify(humanRow)}`]);

    const wildId = id('human-wild');
    const r9w = await registerHuman(wildId, [`https://${wildId}.example.test/*`]);
    if (r9w.status === 201) created.push(wildId);
    step('M9 ワイルドカードのリダイレクト URI は 400', status(r9w, 400));
    step('M9 ワイルドカードの登録で Keycloak に何も作られない', evaluateNothingCreated(await clientsOf(wildId), [], wildId));

    // 🔴 CVE-2024-8883: port なしのループバック（127.0.0.1・[::1]）は SC-12 が 400 で拒み、Keycloak に何も作らない。
    for (const [suffix, uri] of [['v4', 'http://127.0.0.1/cb'], ['v6', 'http://[::1]/cb']]) {
      const portlessId = id(`human-noport-${suffix}`);
      const r9p = await registerHuman(portlessId, [uri]);
      if (r9p.status === 201) created.push(portlessId);
      step(`M9 port なしのループバック（${uri}）は 400`, status(r9p, 400));
      step(`M9 port なしのループバックの登録で Keycloak に何も作られない（${uri}）`, evaluateNothingCreated(await clientsOf(portlessId), [], portlessId));
    }

    // 認可の要求（ブラウザを使わない）。リダイレクト URI の検査が PKCE の検査より先に走るので、URI の試験は正しい S256 を添えて送る。
    const { challenge } = pkcePair();
    const authorize = async (redirectUri, pkce) => {
      const q = new URLSearchParams({ client_id: humanId, response_type: 'code', scope: 'openid', redirect_uri: redirectUri, state: 'probe' });
      if (pkce === 'S256') { q.set('code_challenge', challenge); q.set('code_challenge_method', 'S256'); }
      if (pkce === 'plain') { q.set('code_challenge', challenge); q.set('code_challenge_method', 'plain'); }
      try {
        const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/auth?${q}`, { redirect: 'manual' });
        await res.arrayBuffer();
        return classifyAuthResponse(res.status, res.headers.get('location'), redirectUri);
      } catch (e) {
        return { outcome: 'other', detail: e.message };
      }
    };
    step('M9 陽性対照: 登録した https の URI ＋ S256 はログイン画面へ進む', evaluateAuthOutcome(await authorize(httpsRedirect, 'S256'), 'login'));
    step('M9 PKCE なしは invalid_request でリダイレクトへ返される（ログイン画面へ進まない）',
      evaluateAuthOutcome(await authorize(httpsRedirect, 'none'), 'error-redirect'));
    step('M9 PKCE の plain は invalid_request でリダイレクトへ返される',
      evaluateAuthOutcome(await authorize(httpsRedirect, 'plain'), 'error-redirect'));
    step('M9 登録していない path は 400（リダイレクトしない）', evaluateAuthOutcome(await authorize(`${httpsRedirect}2`, 'S256'), 'rejected'));
    step('M9 登録していない host は 400', evaluateAuthOutcome(await authorize(`https://evil-${humanId}.example.test/cb`, 'S256'), 'rejected'));
    step('M9 https の port 違いは 400', evaluateAuthOutcome(await authorize(`https://${humanId}.example.test:8443/cb`, 'S256'), 'rejected'));
    step('M9 localhost（登録していない綴り）は 400', evaluateAuthOutcome(await authorize('http://localhost:49152/cb', 'S256'), 'rejected'));
    // 🔴 FU3: ループバックの port の扱い（Keycloak 24 RedirectUtils の読みを稼働で確かめる）。
    // 🔴 port を明示した登録は完全一致（CVE-2024-8883 の横取りの形も、別の port も 400）。
    step(`M9 FU3: ${v4Redirect} は登録どおりの port で進む`, evaluateAuthOutcome(await authorize(v4Redirect, 'S256'), 'login'));
    step('M9 FU3: 127.0.0.1 も port まで完全一致（別の port 49152 は 400）',
      evaluateAuthOutcome(await authorize('http://127.0.0.1:49152/cb', 'S256'), 'rejected'));
    step('M9 FU3: 127.0.0.1 でも path 違いは 400', evaluateAuthOutcome(await authorize(`http://127.0.0.1:${v4Port}/other`, 'S256'), 'rejected'));
    const hijacks = [...loopbackHijackProbes(v4Redirect), ...loopbackHijackProbes(v6Redirect)];
    step('M9 CVE-2024-8883: 横取りの形を 4 つ作れた（127.0.0.1・[::1] × 登録どおりの port・別の port）',
      hijacks.length === 4 ? [] : [`横取りの形が ${hijacks.length} 個（期待 4）`]);
    for (const hijack of hijacks) {
      step(`M9 CVE-2024-8883: ${hijack}（利用者情報で宛先を evil.example へすり替える形）は 400`,
        evaluateAuthOutcome(await authorize(hijack, 'S256'), 'rejected'));
    }
    step(`M9 FU3: http://[::1]:${v6Port}/cb は登録どおりの port で進む`, evaluateAuthOutcome(await authorize(v6Redirect, 'S256'), 'login'));
    step('M9 FU3: [::1] は port まで完全一致（別の port は 400。port を落とす扱いは 127.0.0.1 / localhost だけ）',
      evaluateAuthOutcome(await authorize(`http://[::1]:${v6Port + 1}/cb`, 'S256'), 'rejected'));

    // audience: 管理 API の evaluate-scopes で、人の利用者の例示のアクセストークンを作って読む（ブラウザのログインを使わない）。
    const humanClient = (await clientsOf(humanId)).find((c) => c.clientId === humanId);
    const sample = (await call('GET', `${kcAdmin}/users?username=${encodeURIComponent(SAMPLE_USER)}&exact=true`, admin)).json || [];
    if (humanClient && sample[0]) {
      const ex = await call('GET', `${kcAdmin}/clients/${humanClient.id}/evaluate-scopes/generate-example-access-token`
        + `?scope=openid&userId=${encodeURIComponent(sample[0].id)}`, admin);
      step('M9 有人の例示のアクセストークンの aud に mcp-server・azp がクライアント・利用者名が在る',
        ex.status === 200 ? evaluateTokenAudience(ex.json, humanId, { human: true }) : [`例示のトークンが ${ex.status}`]);
    } else {
      failures.push(`M9 の前提: 有人のクライアントか利用者 ${SAMPLE_USER} が無い（audience を測れない）`);
    }

    // 無人のトークンの aud（`/mcp` が audience を検証するので、無人にも写像が要る）。
    const saAudId = id('aud-sa');
    const r9s = await register(saAudId, { department: 'engineering' });
    if (r9s.status === 201) created.push(saAudId);
    step('M9 前提: audience を測る無人の登録が 201', status(r9s, 201));
    step('M9 無人のクライアントにも audience の写像がある', evaluateAudienceMapper(await clientsOf(saAudId), saAudId));
    const saAud = (await clientsOf(saAudId)).find((c) => c.clientId === saAudId);
    if (saAud) {
      const sec = ((await call('GET', `${kcAdmin}/clients/${saAud.id}/client-secret`, admin)).json || {}).value || '';
      const issued = await tokenAttempt(kcUrl, REALM, { grant_type: 'client_credentials', client_id: saAudId, client_secret: sec });
      step('M9 無人のトークンの aud に mcp-server・azp がクライアント',
        evaluateTokenIssued(issued).concat(issued.json && issued.json.access_token
          ? evaluateTokenAudience(decodeJwtPayload(issued.json.access_token), saAudId) : []));
    }

    // 無効化・再有効化（IADR-0516 決定 4a を有人へ広げた）。テンプレートは変えない。
    step('M9 有人の無効化が 200', status(await toggle(humanId, 'disable'), 200));
    step('M9 有人の無効化で Keycloak の enabled が false', evaluateClientEnabled(await clientsOf(humanId), humanId, false));
    step('M9 有人の再有効化が 200', status(await toggle(humanId, 'enable'), 200));
    step('M9 有人の再有効化で enabled が true', evaluateClientEnabled(await clientsOf(humanId), humanId, true));
    step('M9 再有効化の後もテンプレートが残る', evaluatePublicClient(await clientsOf(humanId), humanId, humanRedirects));

    // 補償（無人の M6 と同じ形。IdP へ書けて登録簿で落ちる → 500・何も残らない・作成 → 削除の管理イベント）。
    const humanCompId = id('human-comp');
    const r9c = await registerHuman(humanCompId, [`https://${humanCompId}.example.test/cb`], overlongDisplayName());
    if (r9c.status === 201) created.push(humanCompId);
    step('M9 有人も IdP へ書けて登録簿で落ちる登録は 500', evaluateCompensationResponse(r9c.status, r9c.text));
    step('M9 有人の補償で Keycloak にクライアントが残らない', evaluateNothingCreated(await clientsOf(humanCompId), [], humanCompId));
    const ev9 = await call('GET', `${kcAdmin}/admin-events?resourceTypes=CLIENT&max=500`, admin);
    step(`M9 管理イベントで、${ADMIN_CLIENT} が有人のクライアントを作ってから消した`,
      ev9.status === 200 ? evaluateCompensationEvents(ev9.json, humanCompId, ((await usersOf(ADMIN_CLIENT))[0] || {}).id)
        : [`GET admin-events が ${ev9.status}`]);

    // --- M10（#1844 FU3: DCR が閉じている）----------------------------------------------------------
    const countClients = async () => {
      const r = await call('GET', `${kcAdmin}/clients?first=0&max=10000`, admin);
      return r.status === 200 && Array.isArray(r.json) ? r.json.length : undefined;
    };
    const dcr = async (path, bearer) => {
      try {
        const res = await fetch(`${kcUrl}/realms/${REALM}/clients-registrations/${path}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}) },
          body: JSON.stringify(path === 'openid-connect'
            ? { client_name: `probe-dcr-${run}`, redirect_uris: [`https://dcr-${run}.example.test/cb`] }
            : { clientId: id('dcr'), redirectUris: [`https://dcr-${run}.example.test/cb`] }),
        });
        const text = await res.text();
        let json = null;
        try { json = text ? JSON.parse(text) : null; } catch { /* 本文が JSON でない */ }
        return { status: res.status, json, text };
      } catch (e) {
        return { status: undefined, json: null, text: e.message };
      }
    };
    for (const [label, path, bearer] of [
      ['匿名の OIDC DCR（clients-registrations/openid-connect）', 'openid-connect', null],
      ['匿名の Keycloak 形式の DCR（clients-registrations/default）', 'default', null],
      ['偽の初期アクセストークンの OIDC DCR', 'openid-connect', 'not-a-real-initial-access-token'],
    ]) {
      const before = await countClients();
      const res = await dcr(path, bearer);
      if (res.json && res.json.client_id) created.push(res.json.client_id);
      if (res.json && res.json.clientId) created.push(res.json.clientId);
      step(`M10 FU3: ${label} は 401 / 403 でクライアントが増えない`, evaluateDcrRefused(res, before, await countClients()));
    }
    const iat = await call('GET', `${kcAdmin}/clients-initial-access`, admin);
    step('M10 FU3: 初期アクセストークンが 1 つも無い', iat.status === 200 ? evaluateNoInitialAccessTokens(iat.json) : [`GET clients-initial-access が ${iat.status}`]);
    const policies = await call('GET', `${kcAdmin}/components?type=${encodeURIComponent(REGISTRATION_POLICY_TYPE)}`, admin);
    step('M10 FU3: 匿名のクライアント登録ポリシーに Trusted Hosts（信頼ホストは空）が在る（realm の取り込みが既定のポリシーを足した）',
      policies.status === 200 ? evaluateRegistrationPolicies(policies.json) : [`GET components が ${policies.status}`]);

    // --- M11（#1845 / 計画 ADR-0134 決定 2・FU4・FU6）。🔴 secret の値はログ・文言に出さない ----------------------
    const secretId = id('secret');
    const keycloakSecretOf = async (cid) => {
      const c = (await clientsOf(cid)).find((x) => x.clientId === cid);
      if (!c) return undefined;
      const r = await call('GET', `${kcAdmin}/clients/${c.id}/client-secret`, admin);
      return r.status === 200 ? (r.json || {}).value : undefined;
    };
    const tokenWith = (secret) => tokenAttempt(kcUrl, REALM, { grant_type: 'client_credentials', client_id: secretId, client_secret: secret || '' });
    const r11 = await register(secretId, { department: 'engineering' });
    if (r11.status === 201) created.push(secretId);
    step('M11 前提: 無人の登録が 201', r11.status === 201 ? [] : [`状態が ${r11.status}（期待 201）`]);
    const issuedSecret = (r11.json || {}).clientSecret;
    step('M11 FU4: 登録の 201 の clientSecret が Keycloak の現在の secret と一致する', evaluateIssuedSecret(r11.json, await keycloakSecretOf(secretId)));
    step('M11 応答の secret で client_credentials のトークンが出る', evaluateTokenIssued(await tokenWith(issuedSecret)));
    step('M11 一覧（登録簿）に clientSecret が無い', evaluateNoSecret(await registryRows()));
    const r11b = await call('POST', `${mcpUrl}/mcp-clients/${encodeURIComponent(secretId)}/reissue-secret`, registrar);
    step('M11 FU4: SC-12 の再発行が 200', r11b.status === 200 ? [] : [`状態が ${r11b.status}（期待 200）`]);
    const reissuedSecret = (r11b.json || {}).clientSecret;
    step('M11 FU4: 再発行の値は新しく、Keycloak の現在の secret と一致する', evaluateRotatedSecret(issuedSecret, reissuedSecret, await keycloakSecretOf(secretId)));
    step('M11 FU6: 再発行の直後に旧 secret は拒否される（client secret rotation が無効で猶予が無い）', evaluateTokenRefused(await tokenWith(issuedSecret)));
    step('M11 再発行した secret でトークンが出る', evaluateTokenIssued(await tokenWith(reissuedSecret)));
    const humanSecretId = id('human-nosecret');
    const r11h = await registerHuman(humanSecretId, [`https://${humanSecretId}.example.test/cb`]);
    if (r11h.status === 201) created.push(humanSecretId);
    step('M11 有人の登録の 201 は clientSecret を持たない', r11h.status === 201 ? evaluateNoSecret(r11h.json) : [`状態が ${r11h.status}（期待 201）`]);
    // FU6 の観測（判定しない）: Keycloak 24 の regenerate は値つきの表現を管理イベントへ渡す。realm は詳細を保存する。数だけを出す。
    const ev11 = await call('GET', `${kcAdmin}/admin-events?operationTypes=ACTION&max=500`, admin);
    log(`  ℹ M11 FU6 観測（判定しない）: 再発行の値を表現に含む管理イベント ${ev11.status === 200 ? countEventsContaining(ev11.json, reissuedSecret) : `（GET admin-events が ${ev11.status}）`} 件`);

    // --- M12（#1846 / IADR-0523: platform の audience）---------------------------------------------
    // 例示のアクセストークン（evaluate-scopes）は、そのクライアントの既定スコープで実際に発行されるトークンの本文と同じ写像を通る。
    const example = async (clientId, userId) => {
      const c = (await clientsOf(clientId)).find((x) => x.clientId === clientId);
      if (!c) return { errors: [`クライアント ${clientId} が realm に無い`] };
      const ex = await call('GET', `${kcAdmin}/clients/${c.id}/evaluate-scopes/generate-example-access-token`
        + `?scope=openid&userId=${encodeURIComponent(userId)}`, admin);
      return ex.status === 200 ? { payload: ex.json } : { errors: [`例示のトークンが ${ex.status}`] };
    };
    const saUserId = async (clientId) => ((await usersOf(clientId))[0] || {}).id;
    const measure = async (label, clientId, userId, expect) => {
      if (!userId) { step(label, [`測る利用者（${clientId}）が引けない`]); return; }
      const r = await example(clientId, userId);
      const payload = r.payload;
      step(label, r.errors || evaluatePlatformAudience(payload, expect));
      if (payload) log(`      実測 aud(${clientId}) = ${JSON.stringify(payload.aud)}`);
    };
    const sampleUserId = (sample[0] || {}).id;
    await measure('M12 利用者のトークン（bff・BFF が後段へ中継する）の aud に platform-api が在り mcp-server は無い', 'bff', sampleUserId, true);
    await measure('M12 呼び出し元のサービスアカウント（document-service）の aud に platform-api が在る', 'document-service', await saUserId('document-service'), true);
    await measure('M12 AST の platform realm のクライアント（ai-stock-trading-kb-reader。既定スコープが profile だけだった）の aud に platform-api が在る',
      'ai-stock-trading-kb-reader', await saUserId('ai-stock-trading-kb-reader'), true);
    await measure('M12 否定形: 運用ツール（grafana）の利用者のトークンの aud に platform-api は無い', 'grafana', sampleUserId, false);
    await measure('M12 否定形: 有人の MCP クライアントのトークンの aud に platform-api は無い', humanId, sampleUserId, false);
    step('M12 使い捨ての登録者の実トークンの aud に platform-api が在る', evaluatePlatformAudience(decodeJwtPayload(await registrar()), true));
    if (saAud) {
      const sec = ((await call('GET', `${kcAdmin}/clients/${saAud.id}/client-secret`, admin)).json || {}).value || '';
      const issued = await tokenAttempt(kcUrl, REALM, { grant_type: 'client_credentials', client_id: saAudId, client_secret: sec });
      const mcpToken = issued.json && issued.json.access_token;
      step('M12 否定形: 無人の MCP クライアントの実トークンの aud に platform-api は無い',
        evaluateTokenIssued(issued).concat(mcpToken ? evaluatePlatformAudience(decodeJwtPayload(mcpToken), false) : []));
      if (mcpToken) {
        // 🔴 401 であること（ロール不足の 403 ではない）＝ audience の検証で止まった。
        step('M12 否定形: MCP クライアントの実トークンで McpServer の既定のスキームの口（GET /mcp-clients）は 401',
          status(await send('GET', `${mcpUrl}/mcp-clients`, mcpToken), 401));
      }
    } else {
      failures.push('M12 の前提: M9 の無人の MCP クライアントが無い（MCP クライアントの実トークンを測れない）');
    }
  } finally {
    // 片付け（失敗しても門の判定は上の結果で決める）。使い捨ての登録者も消す（SA 利用者ごと消える）。
    for (const cid of created) {
      try {
        for (const c of (await clientsOf(cid)).filter((x) => x.clientId === cid)) await call('DELETE', `${kcAdmin}/clients/${c.id}`, admin);
      } catch (e) { warn(`  (片付け) クライアント ${cid} を消せなかった: ${e.message}`); }
    }
    try {
      psql(`DELETE FROM "Clients" WHERE "ClientId" LIKE ${sqlLiteral(`probe-mcp-${run}-%`)} OR "DisplayName" = ${sqlLiteral(legacyMarker)};`);
    } catch (e) { warn(`  (片付け) 登録簿の行を消せなかった: ${e.message}`); }
  }

  if (failures.length > 0) {
    warn(`\n✗ SC-12 の IdP への書き込みの実測: ${failures.length} 件が期待と違う`);
    for (const f of failures) warn(`  - ${f}`);
    return 1;
  }
  log('\n✓ SC-12 の IdP への書き込みの実測: すべて期待どおり（M1〜M12）');
  return 0;
}

module.exports = {
  evaluatePublicClient, evaluateAudienceMapper, evaluatePlatformAudience, classifyAuthResponse, evaluateAuthOutcome, loopbackHijackProbes, decodeJwtPayload, evaluateTokenAudience,
  evaluateDcrRefused, evaluateRegistrationPolicies, evaluateNoInitialAccessTokens,
  evaluateIssuedSecret, evaluateNoSecret, evaluateRotatedSecret, countEventsContaining,
  evaluateCompensationResponse, evaluateCompensationEvents,
  serviceAccountUserName, expectedKeycloakAttributes, normalizeAttributes, sameAttributes,
  evaluateCreatedClient, evaluateServiceAccountLookup, evaluateNothingCreated, overlongDisplayName,
  evaluateReconciliationLog, evaluateTokenIssued, evaluateTokenRefused, evaluateClientEnabled, evaluateServiceAccountIntact,
  bearerSource, sendWithRefresh,
};

if (require.main === module) {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest().then((code) => process.exit(code), (e) => { warn(e.stack || e.message); process.exit(1); });
    return;
  }
  // NFR, #1550: ここから先は稼働クラスタへ当たる。明示の指定が無ければ何もせずに終わる（副作用の前）。
  requireLiveOptIn('check-mcp-client-provisioning', argv, { offline: '--self-test' });
  live()
    .then((code) => { cleanupForwards(); process.exit(code); })
    .catch((e) => { warn(`[check-mcp-client-provisioning] ${e.message}`); cleanupForwards(); process.exit(1); });
}
