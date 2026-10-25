#!/usr/bin/env node
'use strict';
/*
 * FR-05, NFR-09, ADR-0004 / ADR-0026, IADR-0369 (#1088 / #324); SC-22, NFR-18, ADR-0124, IADR-0485 (#1682):
 * realm JSON（宣言）と稼働 realm（Keycloak Admin REST API）の**差分を計画し、当てる**。
 *
 *   Job（deploy/local/keycloak-setup/realm-reconcile-job.yaml）の中で node:22-alpine が実行する。
 *   ホスト側の入口は reconcile-realm.sh。**pod 内で kcadm.sh を exec しない**（別 JVM が Keycloak 本体を
 *   OOMKilled にする。2026-09-02 実測）。
 *
 * ## なぜ要るか —— `--import-realm` は同名 realm が在ると黙って飛ばす
 *
 * Keycloak は `start-dev --import-realm` で立つ。**永続化（PVC）した瞬間から、realm JSON を直しても
 * 稼働 realm は変わらない**（`IGNORE_EXISTING`。エラーも警告も出ない）。かといって `OVERWRITE_EXISTING` や
 * `kc.sh import --override` は realm を丸ごと作り直すので、永続化で守りたい runtime state（TOTP 資格情報・
 * 追加利用者・セッション）を毎回消す。**両立する形は「静的 import（空 PVC のときだけ）＋ 起動後に差分を当てる」**である。
 *
 * ## 境界 —— 宣言が所有する層と、実行時が所有する層（IADR-0369 決定 2）
 *
 * 宣言（realm JSON）が正: realm の非コレクション設定 / requiredActions / realm ロール・client ロール / グループ /
 *   client scopes ＋ protocol mappers / clients（属性・redirect・scope 割当 ＋ mappers）/ **seed 利用者の存在** /
 *   サービスアカウント利用者のロールと属性 / **`smtpServer`**（下記の追記）。
 * 実行時が正（**触らない**）: 既存の人間の利用者の資格情報・属性・ロール・グループ・requiredActions・セッション／
 *   **既存の client の `secret`**（下記の追記）。
 *
 * ［2026-09-28 追記 / #1682 / IADR-0485］**client の `secret` は作成時にだけ宣言が運ぶ**（`CLIENT_CREATE_ONLY_KEYS`）。
 *   ADR-0124 決定 1 は client シークレットを「対になる秘密」（認証基盤と Vault を同時に変えないと成立しない）とし、
 *   **本番の秘密を realm の宣言から外す・初期投入は無いときだけ作る**と定めた。従前は更新のたびに宣言の値（開発用）と
 *   稼働の値を比べて当て直したため、**認証基盤と Vault を対で回しても、次の起動でこの Job が認証基盤側だけを戻し、
 *   片側だけ書いた状態（`invalid_client`）を作っていた。** 今は client が無いとき（`client.create`）と realm が無いとき
 *   （`realm.create`）にだけ宣言の値で作り、既存の client の `secret` は比べず・`PUT` の本文に載せず・読みにも行かない。
 *   宣言の値は開発用の形に限る（`scripts/check-realm-constraints.js` 検査 8）。
 *
 * ［2026-10-09 追記 / #1830 / IADR-0517］**宣言の値は公知の dev の値である。** レルム管理のロールを持つ 3 クライアント
 *   （`identity-admin`・`reset-gate`・`mcp-client-admin`。`DEV_SECRET_GUARDED_CLIENTS`）を、dev ではないクラスタで宣言の値のまま
 *   作らない。Job は kube context を持たないので、ホスト側の `reconcile-realm.sh` が `scripts/lib/dev-client-secret-guard.sh` で
 *   決めた結果を env `DEV_CLIENT_SECRETS_ALLOWED`（`allow` / それ以外は拒否。マニフェストの既定は `deny`）で受ける。
 *   拒否のとき、3 クライアントのどれかを宣言の secret で作る操作（`client.create`・`realm.create`）があれば、**その realm には
 *   何も書かずに**名指しして非 0 で終える（`devSecretCreations`）。
 *   **稼働中の検知**は `--check-dev-secrets`（`RECONCILE_MODE=check-dev-secrets`）。3 クライアントの稼働の secret を
 *   `GET …/client-secret` で読み、宣言の値と一致するものを名指して非 0 で終える。**読むだけで書かない。値は出力しない。**
 *   🔴 稼働の secret を読むのはこのモードの `collectGuardedSecrets` だけである（apply / check の `collectLive` は今も読まない）。
 *
 * ［2026-10-22 追記 / GHSA-rh44-px44-p48q / IADR-0572］**対象を 3 クライアントから、サービスアカウントを持つ機密クライアント全部へ
 *   広げた。** 対象は手で並べず宣言から導出する（`isDevSecretGuardedClient`。`DEV_SECRET_GUARDED_CLIENTS` は撤去した）。
 *   上の 2 段落の「3 クライアント」は、この導出した集合と読み替える。
 *
 * ［2026-10-15 追記 / #1894 / IADR-0546］**既存の人間の seed 利用者にも、アカウントコンソールの自己管理のロール（`account` の
 *   `view-profile`・`manage-account`。`SEED_USER_SELF_SERVICE_ROLES`）だけは足す。** realm import で作られる利用者は realm の既定ロール
 *   （`default-roles-platform`）を受け取らず、SC-16 の account API が 401 / 403 になっていた。実行時に作られる利用者は Keycloak が既定ロールで
 *   同じ 2 つを必ず与えるので、足すのは「どの利用者も持つ基線」までであり、境界（人の利用者は実行時が所有する）の例外はこの 2 つに限る。
 *   **加えるだけで外さない。** 実効（合成を含む）の割当で比べるので、既定ロール経由で持つ利用者には何もしない。
 *
 * ［2026-09-06 追記 / #1245 / IADR-0404］**`smtpServer` を実行時所有から宣言所有へ移した。**
 *   ADR-0078 決定 2 が Keycloak の送出先を**クラスタ内の近接 MTA**（`deploy/mail-relay/`）へ固定し、
 *   秘匿値（実リレーの host/user/password）は relay 側の Secret へ移った。realm に残るのは
 *   クラスタ内の Service 名だけであり、**秘匿値は 1 つも無い**。宣言を正にすると、再起動・再インポート・
 *   本 Job のどれを経ても realm は relay を指す ＝ **状態 B（送出先未設定 ＋ 申請が開いている）が構造で作れなくなる。**
 *   宣言に無いキー（runbook が入れた `user` / `password`）は `merge` が live のまま残す（消さない）。
 *
 * ［2026-09-06 追記 / #1245 / IADR-0404］**`resetPasswordAllowed` は「条件つきで門が所有する」。**
 *   ADR-0078 決定 4 の門（`reset-gate`。#1245 PR-C）が投函不能を検知して realm を閉じたとき、本 Job が
 *   それを drift とみなして開き直すと**門が無効になる**。そこで `GATE_OWNED_REALM_KEYS` を
 *   `RUNTIME_OWNED_REALM_KEYS` とは**別集合**として持ち、除外するのは
 *   「**宣言 true ・稼働 false ・`attributes["reset-gate.state"] === "closed"`**」の 1 組だけにする。
 *   🔴 実行時所有へ入れてはならない —— それだと「**宣言 false なのに稼働 true**」という
 *   **危険な向きの drift**（閉じたはずの申請が開いている）まで見なくなる。
 *
 * ［2026-10-21 追記 / #2024 / IADR-0570］**試験アカウント `e2e-*` の資格情報は宣言にプレースホルダだけを置き、env から差し込む。**
 *   宣言は `users[].credentials[]` の `value`（パスワード）と `secretData`（OTP の生の秘密）に `${E2E_<ROLE>_PASSWORD}` /
 *   `${E2E_<ROLE>_TOTP_SECRET}` を持つ。値は Secret `e2e-accounts`（OpenBao → ESO）を Job が `envFrom`（optional）で読む。
 *   差し込みの口は `resolveE2eCredentials` の 1 つで、取り込み元を作る `--render-import`（k8s-local-up.sh の [3/7]）と共有する。
 *   🔴 **差し込めない利用者は作らない（宣言から外す。drift にも数えない）。** プレースホルダを残したまま作ると、公知の文字列が
 *   パスワードになる。置き換えるのは `E2E_` で始まる名前だけ（宣言で Job の env〔管理者のパスワード等〕を利用者へ写させない）。
 *   作った後の e2e 利用者は他の人の seed 利用者と同じく実行時が所有する（資格情報を比べない・戻さない）。回すときは消して作り直す
 *   （deploy/local/README.md の e2e 節）。
 *
 * ［2026-10-24 追記 / #2208 / IADR-0618］**Keycloak 26 がサービスアカウントの client へ自分で付ける既定スコープ `service_account` は
 *   宣言の外（Keycloak が所有する）。** サービスアカウントを持つ client を PUT するたび（投入器の有効・無効の切替、secret の入れ替え、
 *   本 Job 自身の client.update）に Keycloak が defaultClientScopes へ足し直すので、余剰として外しても次の PUT で戻り、
 *   `--check` だけが落ち続ける。許すのは `KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES` が名指す組（名前・欄・宣言の
 *   `serviceAccountsEnabled: true`）の**稼働側の余剰だけ**で、それ以外の余剰は従来どおり外す（宣言が全集合）。
 *   🔴 宣言にこのスコープを書かない（scripts/check-realm-constraints.js の検査11 が同じ定数で止める）。
 *
 * 集合欄（redirectUris / webOrigins / scope 割当 / enabledEventTypes …）は**宣言が全集合**（置換）。
 *   ［2026-10-25 / #2245 / IADR-0524 の追記］`account-console` の `webOrigins: ["+"]` も、この比べ方で既存の realm へ当たる
 *   （空の稼働へ client.update 1 件。稼働の表現へ宣言を合成して PUT するので、ほかの欄は変えない。当てた後は 0 件）。
 *   新しい分岐は足していない（keycloak-realm-reconcile.test.js が固定する）。
 * 実体（client / role / group / mapper / user）は**加算的**（宣言に無い余剰は消さない）。
 *
 * ## 収束
 *
 * apply は「計画 → 適用 → 再計画」を最大 MAX_PASSES 周し、最後の計画が 0 件でなければ非 0 で終える
 * （当てたつもりで当たっていない状態を緑にしない）。check は計画だけを行い、1 件でも残れば非 0。
 * 標準出力の最終行は `realms=<n> drift=<m> applied=<k>` で、check-stack-ready.js の G9 がこれを読む。
 *
 * 環境変数（Job マニフェストが与える）:
 *   KC_URL              Keycloak（既定 http://keycloak:8080）
 *   KC_ADMIN_USER       master realm の管理者（Secret keycloak-admin/username）
 *   KC_ADMIN_PASSWORD   同パスワード（Secret keycloak-admin/password）。**値は出力しない**
 *   REALM_DIR           realm JSON の置き場（既定 /import ＝ ConfigMap keycloak-realms のマウント先）
 *   RECONCILE_MODE      apply（既定）| check | check-dev-secrets（引数 `--check-dev-secrets` でも同じ）
 *   DEV_CLIENT_SECRETS_ALLOWED  allow のときだけ、無い対象のクライアントを宣言の dev の値で作る（既定は拒否。#1830 / IADR-0572）
 *   E2E_<ROLE>_PASSWORD / E2E_<ROLE>_TOTP_SECRET  試験アカウントの資格情報（Secret e2e-accounts。無ければ e2e 利用者を作らない。#2024）
 *
 * 引数 `--render-import`: 標準入力の realm JSON へ同じ差し込みを当てて標準出力へ書く（Keycloak へは接続しない。#2024）。
 */

const fs = require('fs');
const path = require('path');

const MAX_PASSES = 3;

// realm 表現のうち、トップレベルの PUT で扱わないキー。
//   コレクション（別の端点で個別に当てる）／同一性（id・realm 名）／export 由来のメタ。
const REALM_COLLECTION_KEYS = new Set([
  'users', 'clients', 'clientScopes', 'roles', 'groups', 'components', 'requiredActions',
  'authenticationFlows', 'authenticatorConfig', 'identityProviders', 'identityProviderMappers',
  'scopeMappings', 'clientScopeMappings', 'defaultDefaultClientScopes', 'defaultOptionalClientScopes',
  'protocolMappers', 'applications', 'oauthClients', 'federatedUsers', 'userFederationProviders',
  'userFederationMappers', 'clientPolicies', 'clientProfiles', 'organizations', 'defaultRole',
  'defaultRoles', 'defaultGroups', 'localizationTexts',
]);
const REALM_IDENTITY_KEYS = new Set(['id', 'realm', 'keycloakVersion']);
// 実行時が所有する realm のキー（＝宣言が触らない層）。**ここへ足すときは IADR-0369 の境界表も直す。**
// ［2026-09-06 / #1245 / IADR-0404］`smtpServer` を外した（宣言所有へ移した。頭部の追記を参照）。
// **空集合である。** 集合そのものは残す —— 「realm のトップレベルに実行時所有のキーがあり得る」という
// 境界の表明であり、次に足す人がここを見て IADR-0369 の境界表も直せるようにするためである。
const RUNTIME_OWNED_REALM_KEYS = new Set([]);

// 門（ADR-0078 決定 4 の `reset-gate`。#1245 PR-C）が**条件つきで**所有する realm のキー。
// 🔴 RUNTIME_OWNED_REALM_KEYS とは別集合である。無条件に実行時所有へ入れると
//    「宣言 false なのに稼働 true」（閉じたはずの申請が開いている）という**危険な向きの drift** まで
//    見なくなる。除外するのは `gateHoldsClosed()` が真になる 1 組だけにする。
const GATE_OWNED_REALM_KEYS = new Set(['resetPasswordAllowed']);
// 門が状態を書き込む realm 属性の名前（門と本 Job の唯一の接点）。
// 🔴 **この属性を realm 宣言（realm JSON）へ書いてはならない。** 書くと `attributes` が宣言所有になり、
//    本 Job が `closed` を `open` へ戻してしまう（下の除外は `resetPasswordAllowed` にしか効かない）。
//    属性は**実行時にだけ存在する** —— 門が書き、本 Job は読むだけである。
const GATE_STATE_ATTRIBUTE = 'reset-gate.state';
const GATE_STATE_CLOSED = 'closed';

// client 表現のうち、トップレベルの PUT で扱わないキー（別端点で当てる／同一性／読み取り専用）。
const CLIENT_SKIP_KEYS = new Set([
  'id', 'protocolMappers', 'defaultClientScopes', 'optionalClientScopes', 'authorizationSettings',
  'access', 'registrationAccessToken', 'origin',
]);
// ［2026-09-28 / #1682 / IADR-0485］client 表現のうち、**作成時にだけ**宣言が運ぶキー（ADR-0124 決定 1）。
// 既存の client では比べず、`PUT` の本文にも載せない（Keycloak は本文に `secret` が無ければ現在の値を保つ）。
// 🔴 ここから `secret` を外すと、対の手順で回した client シークレットを次の起動で開発用の値へ戻す。
const CLIENT_CREATE_ONLY_KEYS = new Set(['secret']);
const MAPPER_SKIP_KEYS = new Set(['id']);
// ［2026-10-24 / #2208 / IADR-0618］Keycloak が自分で client へ付けるスコープ（名前 → 欄と、付く条件の宣言側の述語）。
// 稼働側にあっても**余剰と見なさない**（client.scope.remove を計画しない）。欠落の向きは見ない（宣言に書かないので欠落も起きない）。
// Keycloak 26 はサービスアカウントを持つ client の作成・更新（PUT）のたびに既定スコープ `service_account` を足す（2026-10-24 の稼働 PoC で実測）。
// 🔴 ここへ足すと、その名前の余剰を宣言が見なくなる。**名前・欄・条件の 3 つで限定する**（「Keycloak の組み込みは全部許す」にしない）。
//    検査器（scripts/check-realm-constraints.js の検査11）は同じ定数を読み、宣言にこれらを書くことを止める（所有の境界をそろえる）。
const KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES = Object.freeze({
  service_account: Object.freeze({ field: 'defaultClientScopes', when: (client) => !!client && client.serviceAccountsEnabled === true }),
});
// 宣言の client の、その欄の稼働側の余剰 name が Keycloak の自動付与として許されるか。
function isKeycloakAutoAssignedScope(client, field, name) {
  const rule = Object.prototype.hasOwnProperty.call(KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES, name) ? KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES[name] : null;
  return !!rule && rule.field === field && rule.when(client);
}
// ［2026-10-09 / #1830 / IADR-0517］dev ではないクラスタで宣言の（公知の dev の）secret のまま作らない・稼働の値が宣言の値の
// ままかを `--check-dev-secrets` で見るクライアント。
// ［2026-10-22 / GHSA-rh44-px44-p48q / IADR-0572］対象は**手で並べず realm の宣言から導出する** —— サービスアカウントを持つ
// 機密クライアントのうち、宣言が secret を持つもの全部（isDevSecretGuardedClient）。新しいクライアントを宣言に足せば対象に入る。
// 🔴 ホスト側の判定器（scripts/lib/dev-client-secret-guard.sh）も同じ述語を scripts/lib/dev-client-secret-targets.js 経由で使う
//    （単一情報源。Job へはこのファイル 1 本だけがマウントされるので、述語はここに置く）。
const DEV_SECRETS_ALLOWED_ENV = 'DEV_CLIENT_SECRETS_ALLOWED';
// ［2026-10-15 / #1894 / IADR-0546］既存の人間の seed 利用者にも当てる client ロール（境界の唯一の例外）。
// Keycloak が実行時に作る利用者へ既定ロールで必ず与える 2 つ（アカウントコンソールの自己管理）に限る。
// 🔴 ここへ足すと、人の利用者の権限を宣言で上書きする口が広がる。足すなら IADR-0369 の境界表も直す。
const SEED_USER_SELF_SERVICE_ROLES = Object.freeze({ account: Object.freeze(['view-profile', 'manage-account']) });
// 利用者の作成時に POST /users が処理しないもの（作成後にロール割当の端点で当てる）。
const USER_CREATE_SKIP_KEYS = new Set(['realmRoles', 'clientRoles', 'serviceAccountClientId', 'id']);
// ［2026-10-25 / #2026］作成の後に requiredActions を当て直す PUT が載せないもの（PUT はこれらを扱わない／資格情報を二度送らない）。
const USER_UPDATE_SKIP_KEYS = new Set(['credentials', 'groups', 'federatedIdentities']);

// ---------------------------------------------------------------- 比較（純粋関数）

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const asStr = (v) => (v === null || v === undefined ? '' : String(v));

/** 配列の要素がすべてスカラーか。 */
const scalarArray = (a) => Array.isArray(a) && a.every((x) => !isObj(x) && !Array.isArray(x));

/**
 * 宣言（desired）が live に**含まれている**か。宣言に無いキーは見ない（余剰は消さないため）。
 * スカラーは文字列として比べる（Keycloak は "1025" と 1025、true と "true" を往復で揺らす）。
 * スカラー配列は集合として比べる（順序は意味を持たない）。
 */
function contains(desired, live) {
  if (Array.isArray(desired)) {
    if (!Array.isArray(live)) return false;
    if (scalarArray(desired) && scalarArray(live)) {
      const a = [...desired].map(asStr).sort();
      const b = [...live].map(asStr).sort();
      return a.length === b.length && a.every((x, i) => x === b[i]);
    }
    return desired.length === live.length && desired.every((d, i) => contains(d, live[i]));
  }
  if (isObj(desired)) {
    if (!isObj(live)) return false;
    return Object.keys(desired).every((k) => contains(desired[k], live[k]));
  }
  return asStr(desired) === asStr(live);
}

/** live に desired を上書き合成する（オブジェクトは再帰、配列とスカラーは置換）。 */
function merge(live, desired) {
  if (isObj(live) && isObj(desired)) {
    const out = { ...live };
    for (const k of Object.keys(desired)) out[k] = merge(live[k], desired[k]);
    return out;
  }
  return desired === undefined ? live : desired;
}

const pick = (obj, keep) => Object.fromEntries(Object.entries(obj).filter(([k]) => keep(k)));
const byKey = (list, key) => new Map((list || []).map((x) => [x[key], x]));

/**
 * 門が閉じたことによる差分か（＝この差分を drift として扱わないか）を判定する。**純関数**。
 *
 * 真になるのは次の 3 つが**すべて**成り立つときだけである:
 *   1. 門が所有し得るキーである（`GATE_OWNED_REALM_KEYS`）
 *   2. **宣言が true**（開いてよい）で、**稼働が false**（閉じている）
 *   3. 稼働 realm の属性 `reset-gate.state` が `closed`（＝**門が自分で閉じたと記録している**）
 *
 * 3 を要求するのが要点である。属性が無い（人が手で閉じた・realm が壊れた）ときは**従来どおり drift** であり、
 * 宣言へ戻す。逆向き（宣言 false・稼働 true ＝ 閉じたはずの申請が開いている）も**常に drift** である。
 *
 * @param {string} key realm のトップレベルキー
 * @param {*} wantedValue 宣言側の値
 * @param {object|null} liveRealm 稼働 realm の表現
 * @returns {boolean}
 */
function gateHoldsClosed(key, wantedValue, liveRealm) {
  if (!GATE_OWNED_REALM_KEYS.has(key)) return false;
  if (!isObj(liveRealm)) return false;
  if (asStr(wantedValue) !== 'true') return false;
  if (asStr(liveRealm[key]) !== 'false') return false;
  const attrs = liveRealm.attributes;
  if (!isObj(attrs)) return false;
  return asStr(attrs[GATE_STATE_ATTRIBUTE]) === GATE_STATE_CLOSED;
}

// ---------------------------------------------------------------- 計画（純粋関数）

/**
 * 1 つの realm について、宣言 `desired` と稼働 `live` から適用すべき操作の列を計画する。
 *
 * live の形（collectLive が組む。試験は同じ形の fixture を渡す）:
 *   {
 *     realm: <RealmRepresentation>|null,
 *     requiredActions: [...], realmRoles: [...], clientScopes: [...], clients: [...],
 *     clientRoles:   { <clientId>: [...] },          // 宣言が触れる client のみ
 *     roleComposites:{ <roleName>: [...] },          // 宣言が composite を持つロールのみ
 *     groups: [ { ..., subGroups: [...] } ],
 *     users: { <username>: <UserRepresentation>|null },
 *     userClientRoles: { <username>: { <clientId>: [names] } }, // 既存の人の seed 利用者の実効の client ロール（SEED_USER_SELF_SERVICE_ROLES の client だけ）
 *     serviceAccounts: { <clientId>: { user, realmRoles: [names], clientRoles: { <clientId>: [names] } } },
 *   }
 *
 * 返す操作: { op, target, method, path, body, reason }。`op === 'deferred'` は前提（依存先の実体）が
 * まだ無く、次の周で計画し直すもの。check モードでは drift として数える。
 */
function plan(desired, live) {
  const ops = [];
  const realmName = desired.realm;
  const R = `/admin/realms/${encodeURIComponent(realmName)}`;
  const add = (o) => ops.push(o);
  const defer = (target, reason) => add({ op: 'deferred', target, reason });

  if (!live.realm) {
    // realm 自体が無い＝import が一度も走っていない。宣言を丸ごと投入する（import と同じ形）。
    add({ op: 'realm.create', target: realmName, method: 'POST', path: '/admin/realms', body: desired,
      reason: 'realm が稼働側に無い' });
    return ops;
  }

  // 1. realm の非コレクション設定（GET の全体へ宣言を合成して PUT。実行時所有キーは落とす）
  {
    // 門（#1245 PR-C）が閉じている間だけ、そのキーを**宣言側から落とす** ——
    // 落とすと (a) drift として数えられず (b) `merge` が稼働側の値（false）をそのまま body へ運ぶ。
    // 他のキーの差分で PUT が起きても、門が閉じた申請を開き直さない。
    const heldByGate = new Set(
      [...GATE_OWNED_REALM_KEYS].filter((k) => gateHoldsClosed(k, desired[k], live.realm)),
    );
    const wanted = pick(desired, (k) =>
      !REALM_COLLECTION_KEYS.has(k) && !REALM_IDENTITY_KEYS.has(k) && !RUNTIME_OWNED_REALM_KEYS.has(k)
      && !heldByGate.has(k));
    const drifted = Object.keys(wanted).filter((k) => !contains(wanted[k], live.realm[k]));
    if (drifted.length > 0) {
      const body = pick(merge(live.realm, wanted), (k) => !RUNTIME_OWNED_REALM_KEYS.has(k) && !REALM_COLLECTION_KEYS.has(k));
      add({ op: 'realm.update', target: realmName, method: 'PUT', path: R, body,
        reason: `realm 設定の差分: ${drifted.join(', ')}` });
    }
  }

  // 2. requiredActions（alias で突合）
  {
    const liveRa = byKey(live.requiredActions, 'alias');
    for (const ra of desired.requiredActions || []) {
      const cur = liveRa.get(ra.alias);
      if (!cur) {
        add({ op: 'requiredAction.register', target: ra.alias, method: 'POST',
          path: `${R}/authentication/register-required-action`,
          body: { providerId: ra.providerId || ra.alias, name: ra.name || ra.alias },
          reason: 'requiredAction が未登録' });
        continue;
      }
      const wanted = pick(ra, (k) => ['name', 'enabled', 'defaultAction', 'priority', 'config'].includes(k));
      if (!contains(wanted, cur)) {
        add({ op: 'requiredAction.update', target: ra.alias, method: 'PUT',
          path: `${R}/authentication/required-actions/${encodeURIComponent(ra.alias)}`,
          body: merge(cur, wanted), reason: 'requiredAction の差分' });
      }
    }
  }

  // 3. realm ロール（name で突合）
  const liveRoles = byKey(live.realmRoles, 'name');
  for (const role of (desired.roles && desired.roles.realm) || []) {
    const cur = liveRoles.get(role.name);
    const wanted = pick(role, (k) => ['name', 'description', 'attributes'].includes(k));
    if (!cur) {
      add({ op: 'role.create', target: role.name, method: 'POST', path: `${R}/roles`, body: wanted,
        reason: 'realm ロールが無い' });
      continue;
    }
    if (!contains(wanted, cur)) {
      add({ op: 'role.update', target: role.name, method: 'PUT',
        path: `${R}/roles/${encodeURIComponent(role.name)}`, body: merge(cur, wanted), reason: 'realm ロールの差分' });
    }
    if (role.composite && role.composites && Array.isArray(role.composites.realm)) {
      const have = new Set(((live.roleComposites || {})[role.name] || []).map((r) => r.name));
      const missing = role.composites.realm.filter((n) => !have.has(n));
      const reps = missing.map((n) => liveRoles.get(n)).filter(Boolean).map((r) => ({ id: r.id, name: r.name }));
      if (reps.length !== missing.length) defer(`role ${role.name} composites`, '合成先のロールがまだ無い');
      else if (reps.length > 0) {
        add({ op: 'role.composites.add', target: role.name, method: 'POST',
          path: `${R}/roles/${encodeURIComponent(role.name)}/composites`, body: reps, reason: '合成ロールの欠落' });
      }
    }
  }

  // 4. client scopes（name で突合）＋ protocol mappers
  const liveScopes = byKey(live.clientScopes, 'name');
  for (const scope of desired.clientScopes || []) {
    const cur = liveScopes.get(scope.name);
    if (!cur) {
      add({ op: 'clientScope.create', target: scope.name, method: 'POST', path: `${R}/client-scopes`,
        body: pick(scope, (k) => k !== 'id'), reason: 'client scope が無い' });
      continue;
    }
    const wanted = pick(scope, (k) => !['id', 'protocolMappers'].includes(k));
    if (!contains(wanted, cur)) {
      add({ op: 'clientScope.update', target: scope.name, method: 'PUT',
        path: `${R}/client-scopes/${cur.id}`, body: pick(merge(cur, wanted), (k) => k !== 'protocolMappers'),
        reason: 'client scope の差分' });
    }
    planMappers(add, `${R}/client-scopes/${cur.id}`, `scope ${scope.name}`, scope.protocolMappers, cur.protocolMappers);
  }

  // 5. clients（clientId で突合）
  const liveClients = byKey(live.clients, 'clientId');
  for (const client of desired.clients || []) {
    const cur = liveClients.get(client.clientId);
    if (!cur) {
      add({ op: 'client.create', target: client.clientId, method: 'POST', path: `${R}/clients`,
        body: pick(client, (k) => k !== 'id'), reason: 'client が無い' });
      continue;
    }
    // 作成時だけのキー（`secret`）は比べない（ADR-0124 決定 1。稼働の値は対の手順が正）。
    const wanted = pick(client, (k) => !CLIENT_SKIP_KEYS.has(k) && !CLIENT_CREATE_ONLY_KEYS.has(k));
    const drifted = Object.keys(wanted).filter((k) => !contains(wanted[k], cur[k]));
    if (drifted.length > 0) {
      // 本文にも載せない —— GET の表現が `secret` を含んでいても落とす（稼働の値を往復させない）。
      const body = pick(merge(cur, wanted), (k) => !['protocolMappers', 'defaultClientScopes', 'optionalClientScopes', 'access'].includes(k)
        && !CLIENT_CREATE_ONLY_KEYS.has(k));
      add({ op: 'client.update', target: client.clientId, method: 'PUT', path: `${R}/clients/${cur.id}`, body,
        reason: `client の差分: ${drifted.join(', ')}` });
    }
    // scope 割当（宣言が全集合）
    for (const [field, seg] of [['defaultClientScopes', 'default-client-scopes'], ['optionalClientScopes', 'optional-client-scopes']]) {
      if (!Array.isArray(client[field])) continue;
      const want = new Set(client[field]);
      const have = new Set(cur[field] || []);
      for (const name of want) {
        if (have.has(name)) continue;
        const sc = liveScopes.get(name);
        if (!sc) { defer(`client ${client.clientId} ${field} ${name}`, 'scope がまだ無い'); continue; }
        add({ op: 'client.scope.add', target: `${client.clientId}:${name}`, method: 'PUT',
          path: `${R}/clients/${cur.id}/${seg}/${sc.id}`, reason: `${field} の欠落` });
      }
      for (const name of have) {
        if (want.has(name)) continue;
        if (isKeycloakAutoAssignedScope(client, field, name)) continue; // #2208: Keycloak が所有する（PUT のたびに戻る）
        const sc = liveScopes.get(name);
        if (!sc) continue;
        add({ op: 'client.scope.remove', target: `${client.clientId}:${name}`, method: 'DELETE',
          path: `${R}/clients/${cur.id}/${seg}/${sc.id}`, reason: `${field} の余剰（宣言が全集合）` });
      }
    }
    planMappers(add, `${R}/clients/${cur.id}`, `client ${client.clientId}`, client.protocolMappers, cur.protocolMappers);
  }

  // 6. client ロール（宣言 roles.client[clientId][]）
  for (const [clientId, roles] of Object.entries((desired.roles && desired.roles.client) || {})) {
    const cur = liveClients.get(clientId);
    if (!cur) { defer(`client roles of ${clientId}`, 'client がまだ無い'); continue; }
    const have = byKey((live.clientRoles || {})[clientId], 'name');
    for (const role of roles) {
      const wanted = pick(role, (k) => ['name', 'description', 'attributes'].includes(k));
      const c = have.get(role.name);
      if (!c) {
        add({ op: 'clientRole.create', target: `${clientId}:${role.name}`, method: 'POST',
          path: `${R}/clients/${cur.id}/roles`, body: wanted, reason: 'client ロールが無い' });
      } else if (!contains(wanted, c)) {
        add({ op: 'clientRole.update', target: `${clientId}:${role.name}`, method: 'PUT',
          path: `${R}/clients/${cur.id}/roles/${encodeURIComponent(role.name)}`, body: merge(c, wanted),
          reason: 'client ロールの差分' });
      }
    }
  }

  // 7. グループ（name で突合。1 段の subGroups まで）
  const liveGroups = byKey(live.groups, 'name');
  for (const group of desired.groups || []) {
    const cur = liveGroups.get(group.name);
    const wanted = pick(group, (k) => ['name', 'attributes'].includes(k));
    if (!cur) {
      add({ op: 'group.create', target: group.name, method: 'POST', path: `${R}/groups`, body: wanted, reason: 'グループが無い' });
      if ((group.subGroups || []).length > 0) defer(`subGroups of ${group.name}`, '親グループがまだ無い');
      continue;
    }
    if (!contains(wanted, cur)) {
      add({ op: 'group.update', target: group.name, method: 'PUT', path: `${R}/groups/${cur.id}`,
        body: pick(merge(cur, wanted), (k) => k !== 'subGroups'), reason: 'グループの差分' });
    }
    const liveSub = byKey(cur.subGroups, 'name');
    for (const sub of group.subGroups || []) {
      const c = liveSub.get(sub.name);
      const w = pick(sub, (k) => ['name', 'attributes'].includes(k));
      if (!c) {
        add({ op: 'group.child.create', target: `${group.name}/${sub.name}`, method: 'POST',
          path: `${R}/groups/${cur.id}/children`, body: w, reason: 'サブグループが無い' });
      } else if (!contains(w, c)) {
        add({ op: 'group.update', target: `${group.name}/${sub.name}`, method: 'PUT', path: `${R}/groups/${c.id}`,
          body: pick(merge(c, w), (k) => k !== 'subGroups'), reason: 'サブグループの差分' });
      }
    }
  }

  // 8. 利用者
  const roleRef = (name) => { const r = liveRoles.get(name); return r ? { id: r.id, name: r.name } : null; };
  const clientRoleRefs = (clientId, names) => {
    const cur = liveClients.get(clientId);
    if (!cur) return null;
    const have = byKey((live.clientRoles || {})[clientId], 'name');
    const refs = names.map((n) => have.get(n)).filter(Boolean).map((r) => ({ id: r.id, name: r.name }));
    return refs.length === names.length ? { clientUuid: cur.id, refs } : null;
  };
  for (const user of desired.users || []) {
    if (user.serviceAccountClientId) {
      // サービスアカウントは人ではない。宣言側（ロール・属性）を当てる。
      const sa = (live.serviceAccounts || {})[user.serviceAccountClientId];
      if (!sa || !sa.user) { defer(`service account ${user.username}`, 'client（とそのサービスアカウント）がまだ無い'); continue; }
      const U = `${R}/users/${sa.user.id}`;
      const missingRealm = (user.realmRoles || []).filter((n) => !(sa.realmRoles || []).includes(n));
      if (missingRealm.length > 0) {
        const refs = missingRealm.map(roleRef);
        if (refs.some((r) => !r)) defer(`service account ${user.username} realmRoles`, 'ロールがまだ無い');
        else add({ op: 'user.realmRoles.add', target: user.username, method: 'POST', path: `${U}/role-mappings/realm`, body: refs,
          reason: `realm ロールの欠落: ${missingRealm.join(', ')}` });
      }
      for (const [clientId, names] of Object.entries(user.clientRoles || {})) {
        const missing = names.filter((n) => !((sa.clientRoles || {})[clientId] || []).includes(n));
        if (missing.length === 0) continue;
        const cr = clientRoleRefs(clientId, missing);
        if (!cr) { defer(`service account ${user.username} clientRoles ${clientId}`, 'client かロールがまだ無い'); continue; }
        add({ op: 'user.clientRoles.add', target: `${user.username}:${clientId}`, method: 'POST',
          path: `${U}/role-mappings/clients/${cr.clientUuid}`, body: cr.refs, reason: `client ロールの欠落: ${missing.join(', ')}` });
      }
      if (user.attributes && !contains(user.attributes, sa.user.attributes || {})) {
        add({ op: 'user.attributes.update', target: user.username, method: 'PUT', path: U,
          body: merge(sa.user, { attributes: user.attributes }), reason: 'サービスアカウントの属性の差分' });
      }
      continue;
    }
    const cur = (live.users || {})[user.username];
    if (cur) {
      // 既存の人間の利用者は実行時が所有する（境界）。例外は自己管理のロールだけ（#1894 / IADR-0546。加えるだけ）。
      for (const [clientId, allowed] of Object.entries(SEED_USER_SELF_SERVICE_ROLES)) {
        const wanted = ((user.clientRoles || {})[clientId] || []).filter((n) => allowed.includes(n));
        if (wanted.length === 0) continue;
        const have = (((live.userClientRoles || {})[user.username] || {})[clientId]) || [];
        const missing = wanted.filter((n) => !have.includes(n));
        if (missing.length === 0) continue;
        const cr = clientRoleRefs(clientId, missing);
        if (!cr) { defer(`user ${user.username} clientRoles ${clientId}`, 'client かロールがまだ無い'); continue; }
        add({ op: 'user.clientRoles.add', target: `${user.username}:${clientId}`, method: 'POST',
          path: `${R}/users/${cur.id}/role-mappings/clients/${cr.clientUuid}`, body: cr.refs,
          reason: `自己管理のロールの欠落: ${missing.join(', ')}` });
      }
      continue;
    }
    // 作成。POST /users は realmRoles / clientRoles を処理しないので、作成直後の割当を postSteps で運ぶ。
    const postSteps = [];
    let blocked = null;
    if ((user.realmRoles || []).length > 0) {
      const refs = user.realmRoles.map(roleRef);
      if (refs.some((r) => !r)) blocked = 'realm ロールがまだ無い';
      else postSteps.push({ method: 'POST', pathSuffix: '/role-mappings/realm', body: refs });
    }
    for (const [clientId, names] of Object.entries(user.clientRoles || {})) {
      const cr = clientRoleRefs(clientId, names);
      if (!cr) { blocked = `client ロール（${clientId}）がまだ無い`; break; }
      postSteps.push({ method: 'POST', pathSuffix: `/role-mappings/clients/${cr.clientUuid}`, body: cr.refs });
    }
    if (blocked) { defer(`user ${user.username}`, blocked); continue; }
    // ［2026-10-25 / #2026］宣言の requiredActions を作成の後に PUT で当て直す。🔴 POST /users は宣言の requiredActions を
    // **足すだけで外さない**: 作成は realm の defaultAction（本 realm では CONFIGURE_TOTP）を付けた利用者を作り、
    // `UserResource.updateUserFromRep(…, isUpdateExistingUser=false)` は宣言に無い action を消さない（Keycloak 26.7.4 の
    // `removeMissingRequiredActions = isUpdateExistingUser`）。更新（PUT）は消す。realm の取り込み（--import-realm）は
    // 既定の action を付けないので、この差は Job で作った利用者にだけ出る（e2e-live の実行で e2e-* 6 名が OTP の後に
    // 「OTP の登録」へ送られた）。body は作成と同じ宣言から資格情報・グループを除いたもの（PUT はそれらを扱わない）。
    // 既存の利用者（上の `cur`）はここへ来ないので当て直さない。宣言が requiredActions を持たない利用者にも当てない。
    // PUT の失敗は reconcile 全体の失敗にする（applyOps の errors → exitCodeFor。作成だけ済んで黙って緑にしない）。
    if (Array.isArray(user.requiredActions)) {
      postSteps.push({ method: 'PUT', pathSuffix: '', label: 'user.update', reason: 'requiredActions を宣言へ当て直す（作成は既定の action を外さない）',
        body: pick(user, (k) => !USER_CREATE_SKIP_KEYS.has(k) && !USER_UPDATE_SKIP_KEYS.has(k)) });
    }
    add({ op: 'user.create', target: user.username, method: 'POST', path: `${R}/users`,
      body: pick(user, (k) => !USER_CREATE_SKIP_KEYS.has(k)), postSteps, reason: 'seed 利用者が無い' });
  }

  return ops;
}

/**
 * ［GHSA-rh44-px44-p48q / IADR-0572］dev の値の守りの対象か。**純関数**。サービスアカウントを持つ機密クライアント
 * （`serviceAccountsEnabled: true`・`publicClient` でない）で、宣言が `secret` を持つもの。clientId では選ばない（入れ漏れを作らない）。
 * @param {object} c client の表現（宣言・作成の本文）
 */
function isDevSecretGuardedClient(c) {
  return isObj(c) && c.serviceAccountsEnabled === true && c.publicClient !== true && asStr(c.secret) !== '';
}

/** ［GHSA-rh44-px44-p48q / IADR-0572］realm の宣言から導出した対象の clientId（昇順・重複なし）。**純関数**。 */
function devSecretGuardedClients(desired) {
  return [...new Set(((desired && desired.clients) || []).filter(isDevSecretGuardedClient).map((c) => c.clientId))].sort();
}

/**
 * ［#1830 / IADR-0517］計画のうち、対象のクライアントを**宣言の secret（公知の dev の値）で作る**操作の対象を名指す。**純関数**。
 * `client.create` の本文、または `realm.create` の本文の `clients[]` に、対象のクライアントが `secret` つきで在れば挙げる。
 * 宣言の `secret` は検査 8 で開発用の形に限られている（＝リポジトリに公開されている）ので、値は見ずに「在るか」だけで決める。
 * @param {Array<object>} ops plan() の戻り値
 * @returns {string[]} 対象の clientId（昇順・重複なし）
 */
function devSecretCreations(ops) {
  const hits = new Set();
  const guarded = isDevSecretGuardedClient;
  for (const op of ops || []) {
    if (op.op === 'client.create' && guarded(op.body)) hits.add(op.body.clientId);
    if (op.op === 'realm.create' && isObj(op.body)) for (const c of op.body.clients || []) if (guarded(c)) hits.add(c.clientId);
  }
  return [...hits].sort();
}

/**
 * ［#1830 / IADR-0517］稼働の secret が宣言の dev の値のままかを判定する。**純関数**。値は戻り値に含めない。
 * @param {object} desired realm の宣言
 * @param {Record<string, string|null>} liveSecrets clientId → 稼働の secret（client が稼働に無ければ null）
 * @returns {Array<{clientId: string, state: 'dev'|'rotated'|'absent'}>} 宣言が secret を持つ対象のクライアントだけ
 */
function devSecretFindings(desired, liveSecrets) {
  const out = [];
  for (const c of (desired && desired.clients) || []) {
    if (!isDevSecretGuardedClient(c)) continue;
    const live = (liveSecrets || {})[c.clientId];
    let state = 'rotated';
    if (live === null || live === undefined) state = 'absent';
    else if (asStr(live) === asStr(c.secret)) state = 'dev';
    out.push({ clientId: c.clientId, state });
  }
  return out.sort((a, b) => a.clientId.localeCompare(b.clientId));
}

// ---------------------------------------------------------------- 試験アカウントの資格情報（#2024 / IADR-0570）

// 置き換える名前の形。🔴 `E2E_` で始まる名前だけ —— 宣言に `${KC_ADMIN_PASSWORD}` と書けば Job の env を利用者へ写せてしまう。
const E2E_PLACEHOLDER_RE = /\$\{(E2E_[A-Z0-9_]+)\}/g;
const E2E_PLACEHOLDER_MARK = '${E2E_';
// OTP の生の秘密（Keycloak が `secretData.value` に持つ文字列）の形。Keycloak が自分で作る秘密と同じ英数に限る
// （JSON の文字列へそのまま置ける・認証アプリへ見せる base32 はこの UTF-8 バイト列の base32）。
const E2E_TOTP_SECRET_RE = /^[A-Za-z0-9]{20,64}$/;

/**
 * realm の `passwordPolicy` のうち `length(N)` と `regexPattern(…)` だけで値を確かめる。**純関数**。
 * Keycloak はポリシーに反するパスワードで `--import-realm` 全体を失敗させ、`POST /users` を 400 にする（後追いは fail-closed なので
 * up が止まる）。他の項（passwordHistory 等）は作成時に効かないので見ない。
 * @returns {boolean}
 */
function passwordMeetsPolicy(policy, value) {
  for (const term of String(policy || '').split(' and ')) {
    const m = /^\s*([A-Za-z]+)\((.*)\)\s*$/.exec(term);
    if (!m) continue;
    if (m[1] === 'length' && value.length < Number(m[2])) return false;
    if (m[1] === 'regexPattern' && !new RegExp(`^(?:${m[2]})$`).test(value)) return false;
  }
  return true;
}

/** 差し込む値の検査。通らなければ理由（値は含めない）を返す。 */
function e2eValueProblem(kind, value, policy) {
  if (typeof value !== 'string' || value === '') return '無い';
  if (/[\u0000-\u001f\u007f]/.test(value)) return '制御文字を含む';
  if (value.includes('${')) return '${ を含む（Keycloak の取り込みが置き換える）';
  if (kind === 'otp' && !E2E_TOTP_SECRET_RE.test(value)) return '英数 20〜64 文字でない';
  if (kind === 'password' && !passwordMeetsPolicy(policy, value)) return 'realm の passwordPolicy を満たさない';
  return null;
}

/**
 * 宣言の `users[].credentials[]` のプレースホルダ `${E2E_…}` を env の値で置き換える。**純関数**（入力を変えない）。
 *
 * - 置き換える場所は資格情報の `value` と `secretData` だけ。それ以外の場所に `${E2E_` があれば宣言の誤りとして例外。
 * - 利用者ごとに、必要な名前が全部 env に在り検査を通るときだけ置き換えて残す。1 つでも欠ける・落ちる利用者は**外す**
 *   （作らない。外した利用者は plan に現れないので drift にも数えない）。
 * - プレースホルダを持たない利用者・realm はそのまま。
 *
 * @param {object} desired realm の宣言
 * @param {Record<string, string|undefined>} env
 * @returns {{desired: object, resolved: string[], skipped: Array<{username: string, problems: string[]}>}}
 *   problems は「名前: 理由」の列（値は含めない）
 */
function resolveE2eCredentials(desired, env) {
  const out = JSON.parse(JSON.stringify(desired));
  const resolved = [];
  const skipped = [];
  const outside = JSON.parse(JSON.stringify(desired));
  for (const u of outside.users || []) delete u.credentials;
  if (JSON.stringify(outside).includes(E2E_PLACEHOLDER_MARK)) {
    throw new Error(`realm '${desired.realm}' の資格情報の外に ${E2E_PLACEHOLDER_MARK}…} がある（差し込めるのは users[].credentials[] の value / secretData だけ）`);
  }
  const users = [];
  for (const u of out.users || []) {
    const creds = u.credentials || [];
    if (!JSON.stringify(creds).includes('${')) { users.push(u); continue; }
    const problems = [];
    for (const c of creds) {
      const kind = c.type === 'password' ? 'password' : 'otp';
      for (const field of ['value', 'secretData']) {
        if (typeof c[field] !== 'string') continue;
        c[field] = c[field].replace(E2E_PLACEHOLDER_RE, (whole, name) => {
          const problem = e2eValueProblem(kind, env[name], desired.passwordPolicy);
          if (problem) { problems.push(`${name}: ${problem}`); return whole; }
          return env[name];
        });
        // 置き換えない形（`E2E_` 以外の名前）が残れば、その文字列のまま作らない（公知の文字列がパスワードになる）。
        const rest = c[field].replace(E2E_PLACEHOLDER_RE, '');
        if (rest.includes('${')) problems.push(`${field}: E2E_ で始まらないプレースホルダ（置き換えない）`);
      }
      for (const field of Object.keys(c)) {
        if (!['value', 'secretData'].includes(field) && JSON.stringify(c[field]).includes(E2E_PLACEHOLDER_MARK)) {
          throw new Error(`users[${u.username}].credentials の ${field} に ${E2E_PLACEHOLDER_MARK}…} がある（差し込めるのは value / secretData だけ）`);
        }
      }
    }
    if (problems.length > 0) { skipped.push({ username: u.username, problems }); continue; }
    resolved.push(u.username);
    users.push(u);
  }
  if (out.users) out.users = users;
  return { desired: out, resolved, skipped };
}

/** 外した利用者の 1 行（値は含めない）。 */
function describeSkipped(s) {
  return `skipped   user ${s.username} — e2e-accounts から差し込めない: ${s.problems.join(', ')}`;
}

/** protocol mappers を name で突合する（client scope と client で同型）。 */
function planMappers(add, base, label, desiredMappers, liveMappers) {
  const have = byKey(liveMappers, 'name');
  for (const m of desiredMappers || []) {
    const cur = have.get(m.name);
    const wanted = pick(m, (k) => !MAPPER_SKIP_KEYS.has(k));
    if (!cur) {
      add({ op: 'mapper.create', target: `${label} mapper ${m.name}`, method: 'POST',
        path: `${base}/protocol-mappers/models`, body: wanted, reason: 'protocol mapper が無い' });
    } else if (!contains(wanted, cur)) {
      add({ op: 'mapper.update', target: `${label} mapper ${m.name}`, method: 'PUT',
        path: `${base}/protocol-mappers/models/${cur.id}`, body: merge(cur, wanted), reason: 'protocol mapper の差分' });
    }
  }
}

/** 標準出力へ出す 1 行（秘匿値を含めない: body は出さない）。 */
function describe(op) {
  return op.op === 'deferred' ? `deferred  ${op.target} — ${op.reason}` : `${op.op.padEnd(24)} ${op.target} — ${op.reason}`;
}

// ---------------------------------------------------------------- 収集・適用（外部依存）

function makeClient(baseUrl, token) {
  const call = async (method, p, body) => {
    const res = await fetch(baseUrl + p, {
      method,
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, ok: res.ok, json, text };
  };
  const get = async (p) => {
    const r = await call('GET', p);
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`GET ${p} -> ${r.status} ${r.text.slice(0, 200)}`);
    return r.json;
  };
  return { call, get };
}

async function adminToken(baseUrl, user, password) {
  const body = new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: user, password });
  const res = await fetch(`${baseUrl}/realms/master/protocol/openid-connect/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body,
  });
  if (!res.ok) throw new Error(`管理者トークンを取得できない: HTTP ${res.status}（資格情報の値は出さない）`);
  return (await res.json()).access_token;
}

/** 稼働側を、plan が読む形へ集める。宣言が触れるものだけを取りに行く。 */
async function collectLive(kc, desired) {
  const name = desired.realm;
  const R = `/admin/realms/${encodeURIComponent(name)}`;
  const realm = await kc.get(R);
  if (!realm) return { realm: null };
  const [requiredActions, realmRoles, clientScopes, clients] = await Promise.all([
    kc.get(`${R}/authentication/required-actions`),
    kc.get(`${R}/roles?max=1000&briefRepresentation=false`),
    kc.get(`${R}/client-scopes`),
    kc.get(`${R}/clients?max=1000`),
  ]);
  const clientByClientId = byKey(clients, 'clientId');

  // ［2026-09-28 / #1682 / IADR-0485］client の secret は読みに行かない（`GET …/client-secret` を打たない）。
  // 値を取りに行かず、比べず、PUT の本文とログに出さない（`GET /clients` の表現が `secret` を含む版の Keycloak では、
  // 値はこの Job のメモリを通る —— 使わないだけである）。

  // 宣言が触れる client のロール（roles.client の宣言 ＋ 利用者の clientRoles が指す client）
  const roleClientIds = new Set(Object.keys((desired.roles && desired.roles.client) || {}));
  for (const u of desired.users || []) for (const cid of Object.keys(u.clientRoles || {})) roleClientIds.add(cid);
  const clientRoles = {};
  for (const cid of roleClientIds) {
    const cur = clientByClientId.get(cid);
    if (cur) clientRoles[cid] = (await kc.get(`${R}/clients/${cur.id}/roles?max=1000`)) || [];
  }

  const roleComposites = {};
  for (const role of (desired.roles && desired.roles.realm) || []) {
    if (role.composite && role.composites) {
      roleComposites[role.name] = (await kc.get(`${R}/roles/${encodeURIComponent(role.name)}/composites`)) || [];
    }
  }

  const groups = (await kc.get(`${R}/groups?max=1000&briefRepresentation=false`)) || [];
  for (const g of groups) {
    g.subGroups = (await kc.get(`${R}/groups/${g.id}/children?max=1000&briefRepresentation=false`)) || g.subGroups || [];
  }

  const users = {};
  const userClientRoles = {};
  const serviceAccounts = {};
  for (const u of desired.users || []) {
    if (u.serviceAccountClientId) {
      const cur = clientByClientId.get(u.serviceAccountClientId);
      // #1373: 稼働側のクライアントが無い、または **service account が無効**なら SA 利用者は「未存在」。
      // Keycloak は SA 無効クライアントの service-account-user に 400 を返す（404 ではない）ので、ここで
      // 読みに行くと計画に入る前に落ちる。未存在として返せば、plan が client.update（SA 有効化）＋ deferred を
      // 出し、次の周で SA 利用者へロールを当てる（クライアント不在のときと同じ経路）。
      if (!cur || !cur.serviceAccountsEnabled) continue;
      const user = await kc.get(`${R}/clients/${cur.id}/service-account-user`);
      if (!user) continue;
      const realmMappings = (await kc.get(`${R}/users/${user.id}/role-mappings/realm`)) || [];
      const cr = {};
      for (const cid of Object.keys(u.clientRoles || {})) {
        const cc = clientByClientId.get(cid);
        if (cc) cr[cid] = ((await kc.get(`${R}/users/${user.id}/role-mappings/clients/${cc.id}`)) || []).map((r) => r.name);
      }
      serviceAccounts[u.serviceAccountClientId] = { user, realmRoles: realmMappings.map((r) => r.name), clientRoles: cr };
    } else {
      const found = (await kc.get(`${R}/users?username=${encodeURIComponent(u.username)}&exact=true`)) || [];
      users[u.username] = found[0] || null;
      // #1894 / IADR-0546: 自己管理のロールだけ、実効（合成を含む。既定ロール経由も数える）の割当を読む。
      if (!users[u.username]) continue;
      for (const cid of Object.keys(SEED_USER_SELF_SERVICE_ROLES)) {
        const cc = clientByClientId.get(cid);
        if (!cc || !((u.clientRoles || {})[cid] || []).length) continue;
        const eff = (await kc.get(`${R}/users/${users[u.username].id}/role-mappings/clients/${cc.id}/composite`)) || [];
        (userClientRoles[u.username] = userClientRoles[u.username] || {})[cid] = eff.map((r) => r.name);
      }
    }
  }

  return { realm, requiredActions, realmRoles, clientScopes, clients, clientRoles, roleComposites, groups, users, userClientRoles, serviceAccounts };
}

/**
 * ［#1830 / IADR-0517］`--check-dev-secrets` だけが使う。宣言が secret を持つ対象のクライアントの稼働の secret を読む（GET だけ）。
 * realm が無ければ null（測れない）。client が無ければ null の値。値はこの関数の戻り値と比較にしか使わない（出力しない）。
 */
async function collectGuardedSecrets(kc, desired) {
  const R = `/admin/realms/${encodeURIComponent(desired.realm)}`;
  if (!(await kc.get(R))) return null;
  const out = {};
  for (const c of desired.clients || []) {
    if (!isDevSecretGuardedClient(c)) continue;
    const found = (await kc.get(`${R}/clients?clientId=${encodeURIComponent(c.clientId)}`)) || [];
    const cur = found.find((x) => x.clientId === c.clientId);
    if (!cur) { out[c.clientId] = null; continue; }
    const sec = await kc.get(`${R}/clients/${cur.id}/client-secret`);
    if (!sec || typeof sec.value !== 'string') throw new Error(`${c.clientId} の client secret を読めない（値は出さない）`);
    out[c.clientId] = sec.value;
  }
  return out;
}

/**
 * ［#1830 / IADR-0517］`--check-dev-secrets` の本体。dev の値のままのクライアント数を返す。読むだけで書かない。
 * @returns {Promise<{checked: number, dev: number}>}
 */
async function checkDevSecrets(kc, realms, log = console.log) {
  let checked = 0;
  let dev = 0;
  for (const { file, desired } of realms) {
    if (devSecretFindings(desired, {}).length === 0) continue; // 対象のクライアントを宣言していない realm
    log(`==> realm '${desired.realm}' (${file}) mode=check-dev-secrets`);
    const live = await collectGuardedSecrets(kc, desired);
    if (live === null) throw new Error(`realm '${desired.realm}' が稼働側に無い（測れない。0 件を緑にしない）`);
    for (const f of devSecretFindings(desired, live)) {
      checked += 1;
      if (f.state === 'dev') {
        dev += 1;
        log(`    dev-secret  ${f.clientId} — 宣言の（公知の）dev の値のまま。回す: docs/operations/paired-secret-rotation-runbook.md`);
      } else if (f.state === 'absent') {
        log(`    absent      ${f.clientId} — 稼働に無い（作られるときは後追いの守りが掛かる）`);
      } else {
        log(`    ok          ${f.clientId} — dev の値ではない`);
      }
    }
  }
  if (checked === 0) throw new Error('対象のクライアント（サービスアカウントを持つ機密クライアント）を宣言する realm が無い（0 件を緑にしない）');
  log(`realms=${realms.length} checked=${checked} dev-secrets=${dev}`);
  return { checked, dev };
}

async function applyOps(kc, realmName, ops) {
  const R = `/admin/realms/${encodeURIComponent(realmName)}`;
  let applied = 0;
  const errors = [];
  for (const op of ops) {
    if (op.op === 'deferred') continue;
    const r = await kc.call(op.method, op.path, op.body);
    if (!r.ok && r.status !== 409) {
      errors.push(`${op.op} ${op.target}: HTTP ${r.status} ${r.text.slice(0, 300)}`);
      continue;
    }
    applied += 1;
    const stepLines = [];
    if (op.op === 'user.create' && (op.postSteps || []).length > 0) {
      const found = (await kc.get(`${R}/users?username=${encodeURIComponent(op.target)}&exact=true`)) || [];
      const id = found[0] && found[0].id;
      if (!id) { errors.push(`user.create ${op.target}: 作成後に利用者を引けない`); continue; }
      for (const step of op.postSteps) {
        // 409（作成の前から在った利用者）には当て直さない —— 既存の利用者の requiredActions は実行時が所有する。
        if (r.status === 409 && step.label) {
          stepLines.push(`    skipped   ${step.label.padEnd(24)} ${op.target} — 作成の前から在った（409）。既存の利用者は当て直さない`);
          continue;
        }
        const s = await kc.call(step.method, `${R}/users/${id}${step.pathSuffix}`, step.body);
        if (!s.ok) {
          errors.push(`${step.label || 'user.create'} ${op.target} ${step.pathSuffix || '(本体)'}: HTTP ${s.status} ${s.text.slice(0, 200)}`);
        } else if (step.label) {
          stepLines.push(`    applied   ${step.label.padEnd(24)} ${op.target} — ${step.reason}`);
        }
      }
    }
    console.log(`    applied   ${describe(op)}`);
    for (const line of stepLines) console.log(line);
  }
  return { applied, errors };
}

/**
 * 1 つの realm を計画し、apply なら当てて計画し直す（MAX_PASSES まで）。`collect` は試験で差し替える（既定 collectLive）。
 * ［2026-10-25 / #2026］当てられなかった操作の数（applyErrors）を返す —— 作成の後の手順の失敗は再計画の差分に戻らないことがある。
 */
async function reconcileRealm(kc, desired, { mode, devSecretsAllowed, collect = collectLive }) {
  let ops = plan(desired, await collect(kc, desired));
  let applied = 0;
  let guardBlocked = 0;
  let applyErrors = 0;
  if (mode === 'apply') {
    for (let pass = 1; pass <= MAX_PASSES && ops.length > 0; pass += 1) {
      // #1830 / IADR-0517: 対象のクライアントを宣言の dev の値で作る操作があり、許されていなければ、この realm には何も書かない。
      const blocked = devSecretCreations(ops);
      if (blocked.length > 0 && !devSecretsAllowed) {
        console.error(`    ERROR サービスアカウントを持つ機密クライアントを公知の dev の値で作る操作がある: ${blocked.join(', ')}。`
          + ` dev ではない kube context なので、この realm には何も書かない（${DEV_SECRETS_ALLOWED_ENV}=allow でない。#1830）。`
          + ' 先に認証基盤へ dev 以外の secret で作るか、dev のクラスタなら ALLOW_DEV_CLIENT_SECRETS=1 で再実行する');
        guardBlocked += blocked.length;
        break;
      }
      if (blocked.length > 0) console.error(`    WARN サービスアカウントを持つ機密クライアントを宣言の dev の値で作る（許可済み）: ${blocked.join(', ')}`);
      console.log(`    pass ${pass}: ${ops.length} 件`);
      for (const op of ops) if (op.op === 'deferred') console.log(`    ${describe(op)}`);
      const r = await applyOps(kc, desired.realm, ops);
      applied += r.applied;
      for (const e of r.errors) console.error(`    ERROR ${e}`);
      applyErrors += r.errors.length;
      ops = plan(desired, await collect(kc, desired));
    }
  }
  return { ops, applied, guardBlocked, applyErrors };
}

function readRealmFiles(dir) {
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort();
  return files.map((f) => ({ file: f, desired: JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')) }));
}

/**
 * `--render-import`（#2024 / IADR-0570）: 標準入力の realm JSON に試験アカウントの差し込みを当て、Keycloak の取り込み元を標準出力へ書く。
 * 外した利用者は標準エラーへ名前と理由だけを出す（値は出さない）。
 */
function renderImport(text, env, err = (s) => process.stderr.write(`${s}\n`)) {
  const r = resolveE2eCredentials(JSON.parse(text), env);
  if (r.resolved.length > 0) err(`    realm の取り込み元: 試験アカウントを e2e-accounts から差し込む: ${r.resolved.join(', ')}（値は表示しない）`);
  for (const s of r.skipped) err(`    realm の取り込み元: ${describeSkipped(s)}（取り込まない）`);
  return `${JSON.stringify(r.desired, null, 2)}\n`;
}

async function main() {
  if (process.argv.includes('--render-import')) {
    process.stdout.write(renderImport(fs.readFileSync(0, 'utf8'), process.env));
    return;
  }
  const baseUrl = (process.env.KC_URL || 'http://keycloak:8080').replace(/\/+$/, '');
  const mode = process.argv.includes('--check-dev-secrets') ? 'check-dev-secrets' : (process.env.RECONCILE_MODE || 'apply');
  const dir = process.env.REALM_DIR || '/import';
  if (!['apply', 'check', 'check-dev-secrets'].includes(mode)) {
    throw new Error(`RECONCILE_MODE は apply | check | check-dev-secrets（受け取った値: ${mode}）`);
  }
  const devSecretsAllowed = process.env[DEV_SECRETS_ALLOWED_ENV] === 'allow';
  const user = process.env.KC_ADMIN_USER;
  const password = process.env.KC_ADMIN_PASSWORD;
  if (!user || !password) throw new Error('KC_ADMIN_USER / KC_ADMIN_PASSWORD が無い（Secret keycloak-admin の username / password）');

  const realms = readRealmFiles(dir);
  if (realms.length === 0) {
    console.log(`realms=0 drift=0 applied=0`);
    throw new Error(`${dir} に realm JSON が 1 つも無い（走査が壊れている。0 件を緑にしない）`);
  }
  const kc = makeClient(baseUrl, await adminToken(baseUrl, user, password));

  if (mode === 'check-dev-secrets') {
    const { dev } = await checkDevSecrets(kc, realms);
    if (dev > 0) {
      console.error(`ERROR: ${dev} 件のサービスアカウントを持つクライアントの secret が宣言の dev の値のまま（上の dev-secret の行）。dev 以外のクラスタなら直ちに回す`);
      process.exitCode = 1;
    }
    return;
  }

  let drift = 0;
  let guardBlocked = 0;
  let applied = 0;
  let applyErrors = 0;
  for (const { file, desired: declared } of realms) {
    console.log(`==> realm '${declared.realm}' (${file}) mode=${mode}`);
    // #2024 / IADR-0570: 試験アカウントの資格情報を差し込む。差し込めない利用者は作らない（drift にも数えない）。
    const { desired, skipped } = resolveE2eCredentials(declared, process.env);
    for (const s of skipped) console.log(`    ${describeSkipped(s)}`);
    const r = await reconcileRealm(kc, desired, { mode, devSecretsAllowed });
    const ops = r.ops;
    applied += r.applied;
    guardBlocked += r.guardBlocked;
    applyErrors += r.applyErrors;
    for (const op of ops) console.log(`    drift     ${describe(op)}`);
    if (ops.length === 0) console.log('    一致（差分なし）');
    drift += ops.length;
  }
  console.log(`realms=${realms.length} drift=${drift} applied=${applied}`);
  if (applyErrors > 0) console.error(`ERROR: 当てられなかった操作が ${applyErrors} 件（上の ERROR の行）`);
  process.exitCode = exitCodeFor({ drift, guardBlocked, applyErrors }) || process.exitCode;
}

/**
 * ［2026-10-25 / #2026］終了コード。**純関数**。差分が残る・守りで止めた・当てられなかった操作がある、のいずれでも 1。
 * 当てられなかった操作は再計画で差分に戻らないことがある（作成の後の手順。利用者は在るので drift 0 になる）ため、別に数える。
 */
function exitCodeFor({ drift, guardBlocked, applyErrors }) {
  return drift > 0 || guardBlocked > 0 || applyErrors > 0 ? 1 : 0;
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`ERROR: ${e.message}`);
    process.exitCode = 1;
  });
}

module.exports = {
  applyOps, exitCodeFor, reconcileRealm, USER_UPDATE_SKIP_KEYS,
  plan, planMappers, collectLive, contains, merge, describe, gateHoldsClosed,
  devSecretCreations, devSecretFindings, collectGuardedSecrets, checkDevSecrets, isDevSecretGuardedClient, devSecretGuardedClients,
  DEV_SECRETS_ALLOWED_ENV,
  REALM_COLLECTION_KEYS, RUNTIME_OWNED_REALM_KEYS, GATE_OWNED_REALM_KEYS, CLIENT_SKIP_KEYS, CLIENT_CREATE_ONLY_KEYS, MAX_PASSES,
  KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES, isKeycloakAutoAssignedScope,
  GATE_STATE_ATTRIBUTE, GATE_STATE_CLOSED, SEED_USER_SELF_SERVICE_ROLES,
  resolveE2eCredentials, passwordMeetsPolicy, describeSkipped, renderImport, E2E_TOTP_SECRET_RE,
};
