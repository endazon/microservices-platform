#!/usr/bin/env node
'use strict';
/*
 * check-realm-constraints.js
 * Keycloak realm export（deploy/keycloak/*-realm.json）の文字列フィールド長が、import 先 RDB の
 * カラム上限（varchar(255)）を超えていないかを機械検査する（Issue #18 再発防止）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-unit-dependencies.js / check-image-mapping.js と同型。
 *
 * 背景: #307 で追加した client `ai-stock-trading-kb-writer` の description が 364 文字あり、Keycloak の
 * CLIENT.DESCRIPTION（varchar(255)）上限を超過。realm import が SQL エラー(SQLSTATE 22001)で失敗し
 * Keycloak pod がクラッシュした。export は JSON なので長さは静的検査でき、import 前に止められる。
 *
 * 検査対象（いずれも Keycloak の JPA エンティティで varchar(255) のカラムに対応する自由記述/名称）:
 *   - clients[].clientId / name / description
 *   - clients[].protocolMappers[].name
 *   - clientScopes[].name / description
 *   - clientScopes[].protocolMappers[].name
 *   - roles.realm[].name / description
 *   - roles.client[*][].name / description
 *   - groups（再帰）.name
 *   - realm / displayName / displayNameHtml
 * 長さは「文字数（コードポイント）」で数える（Postgres の varchar(N) は文字数上限。マルチバイトでも
 * 1 文字 = 1）。網羅的なスキーマ検証ではなく、オーバーフローしやすい自由記述/名称に絞った軽い lint
 * （PR #317 レビュー指摘）。対象外の varchar 系フィールド（attributes 値・authenticationFlows.alias 等）で
 * 同種の import 失敗が起きた場合は、この collectFields に対象を足して範囲を広げる。
 *
 *
 * 検査2: 経路ごとに必須の redirect URI / web origin の欠落（Issue #385 再発防止）。
 * 背景: `wiki-js` client の登録 URL は経路ごとに別物（edge 集約 50000 / k8s port-forward 3300 /
 * compose(dev) host 公開 3001 / in-cluster 3000）。#385 では 3001 を「port-forward 用」と取り違えた結果、
 * 非 edge の port-forward 経路（3300）が realm 未登録のまま docs だけが案内し、OIDC が
 * invalid_redirect_uri で完了しなかった。長さと違い URL 欠落は静的に列挙できるため import 前に止める。
 * 対象 client が realm に存在しない場合は検査しない（realm 分割・将来の client 削除で誤検出しない）。
 *
 * 使い方:
 *   node scripts/check-realm-constraints.js            # deploy/keycloak/*-realm.json を検査。違反で exit 1。
 *   node scripts/check-realm-constraints.js <path...>  # 明示したファイルのみ検査。
 *   node scripts/check-realm-constraints.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const REALM_DIR = 'deploy/keycloak';
// Keycloak の該当カラムはいずれも varchar(255)。閾値は 1 箇所に集約する。
const MAX_LEN = 255;

// 経路ごとに必須の redirect URI / web origin（Issue #385 再発防止）。落とすとその経路の OIDC が
// invalid_redirect_uri で完了しなくなるため、宣言的に列挙して CI で欠落を検出する。
// 経路の対応は IADR-0095 の追記（2026-07-26・#385）の表を単一情報源とする。
const REQUIRED_CLIENT_URLS = {
  'wiki-js': {
    redirectUris: [
      'http://wiki.localhost:50000/*', // edge 集約（IADR-0091・LOCALEDGE=1）
      'http://localhost:3300/*',       // k8s の port-forward（非 edge・svc/wiki-js 3300:3000）
      'http://localhost:3001/*',       // compose(dev) の host 公開（IADR-0032・ports 3001:3000）
      'http://wiki-js:3000/*',         // in-cluster
    ],
    webOrigins: [
      'http://wiki.localhost:50000',
      'http://localhost:3300',
      'http://localhost:3001',
    ],
  },
};

/*
 * 検査3: ADR-0026 が定める認証ポリシーの realm 実現値（Issue #578 / IADR-0197）。
 * 背景: SC-14（OTP／多要素認証）・SC-15（パスワードリセット）は Keycloak テーマと realm 設定で実現する。
 * ADR-0026 は具体値（12 文字以上・3 種以上・直近 5 世代／TOTP 6 桁・前後 1 ステップ／5 回失敗で 15 分ロック／
 * リセットリンク 30 分／デバイス記憶 30 日）を確定しているが、**realm.json は 8 項目すべて未設定**であった（#578 の実測）。
 * 値の一致は静的に検査できるため、確定要件からの逸脱を import 前に止める。
 * 対象は本プロジェクトの realm（`platform`）に限る —— 別プロジェクトの realm（AST 等）は ADR-0026 の射程外。
 */
// 「英大文字／小文字／数字／記号のうち 3 種以上」。Keycloak の組み込みポリシー
// （upperCase / lowerCase / digits / specialChars）はいずれも AND であり、「4 種のうち 3 種」という
// **選言を表現できない**。4 通りの組み合わせを先読みの選言として書く（IADR-0197 決定 3）。
// この定数が単一情報源であり、realm.json 側はこの文字列と一致していなければ違反とする。
const PASSWORD_CLASS_REGEX =
  '^(?:(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])|(?=.*[a-z])(?=.*[A-Z])(?=.*[^A-Za-z0-9])'
  + '|(?=.*[a-z])(?=.*[0-9])(?=.*[^A-Za-z0-9])|(?=.*[A-Z])(?=.*[0-9])(?=.*[^A-Za-z0-9])).*$';

const AUTH_POLICY_REALM = 'platform';

// realm 直下のスカラー値の期待。値は ADR-0026 の確定値をそのまま置く（単位はコメントで示す）。
const AUTH_POLICY_SCALARS = {
  displayName: '汎用プラットフォーム',
  passwordPolicy: `length(12) and passwordHistory(5) and regexPattern(${PASSWORD_CLASS_REGEX})`,
  otpPolicyType: 'totp',
  otpPolicyAlgorithm: 'HmacSHA1', // RFC 6238 既定。認証アプリの互換性が最も広い
  otpPolicyDigits: 6,
  otpPolicyPeriod: 30,        // 秒
  // ADR-0026「時刻ずれは前後 1 ステップ（30 秒）まで許容」に対応する値。
  // ★ Keycloak の窓が [-n, +n] の対称であることは実装コードで確認していない（管理コンソールの
  //    説明文は先読みのみとも読める）。片側だけなら要件未達になるため、対称性は実機で確かめる
  //    —— docs/tests/SC-14_otp-mfa.md の T-07（1 ステップ前／後）と T-08（2 ステップ前）が担う。
  otpPolicyLookAheadWindow: 1,
  otpPolicyCodeReusable: false,
  bruteForceProtected: true,
  permanentLockout: false,     // 15 分で解除される一時ロックであること
  failureFactor: 5,
  waitIncrementSeconds: 900,   // 15 分
  maxFailureWaitSeconds: 900,  // 15 分（増分が頭打ちになる上限も 15 分）
  actionTokenGeneratedByUserLifespan: 1800, // 30 分（リセットリンクの有効期限）
  rememberMe: true,
  ssoSessionIdleTimeoutRememberMe: 2592000, // 30 日
  ssoSessionMaxLifespanRememberMe: 2592000, // 30 日
};

// requiredActions の期待（alias → 期待するフラグ）。
// `enabled` だけでは未登録者は誘導されない —— **誘導は `defaultAction` が担う**。
const AUTH_POLICY_REQUIRED_ACTIONS = {
  CONFIGURE_TOTP: { enabled: true, defaultAction: true },
  UPDATE_PASSWORD: { enabled: true }, // ADR-0045 決定 9-b（メール停止時の代替）が要る
  // ADR-0026「リカバリーコードは登録完了時に 1 回のみ表示し SC-16 から再発行できる」の実現手段。
  // provider を realm に登録しないと管理コンソールの Required actions にも現れず、後から有効化できない。
  CONFIGURE_RECOVERY_AUTHN_CODES: { enabled: true },
};

/*
 * realm export に `requiredActions` を **書いた瞬間、Keycloak は既定の必須アクションを一切登録しない**
 * （`RepresentationToModel.importRealm` は `rep.getRequiredActions() != null` のとき
 * `DefaultRequiredActions.addActions(realm)` を呼ばない）。つまり「必要なものだけ書く」は
 * **書かなかったものを黙って消す**ことを意味する。
 *
 * #578 の初版は 7 件しか列挙せず、**ADR-0026 が要求するリカバリーコード
 * （`CONFIGURE_RECOVERY_AUTHN_CODES`）を落としていた**（PR #746 の ADR 監査が検出）。
 * 同じ取りこぼしを防ぐため、**既定 13 件がすべて宣言されていること**を検査する
 * （使わないものは `enabled: false` で明示する。省略は「消す」と同義だからである）。
 */
const AUTH_POLICY_REQUIRED_ACTION_ALIASES = [
  'CONFIGURE_TOTP', 'TERMS_AND_CONDITIONS', 'UPDATE_PASSWORD', 'UPDATE_PROFILE', 'VERIFY_EMAIL',
  'delete_account', 'webauthn-register', 'webauthn-register-passwordless', 'VERIFY_PROFILE',
  'delete_credential', 'idp_link', 'CONFIGURE_RECOVERY_AUTHN_CODES', 'update_user_locale',
];

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// 文字列の「文字数」（コードポイント数）を返す。null/undefined は 0。
function charLen(s) {
  return s == null ? 0 : [...String(s)].length;
}

// realm オブジェクトから、長さ検査対象の { path, value } を列挙する（純粋関数）。
// path は違反表示用の人間可読なパス。value は検査対象の文字列。
function collectFields(realm) {
  const out = [];
  const push = (p, v) => { if (v != null) out.push({ path: p, value: String(v) }); };

  for (const f of ['realm', 'displayName', 'displayNameHtml']) push(`realm.${f}`, realm && realm[f]);

  for (const c of (realm && realm.clients) || []) {
    const id = (c && c.clientId) || '(no clientId)';
    for (const f of ['clientId', 'name', 'description']) push(`clients[${id}].${f}`, c && c[f]);
    for (const pm of (c && c.protocolMappers) || []) {
      push(`clients[${id}].protocolMappers[${(pm && pm.name) || '?'}].name`, pm && pm.name);
    }
  }

  for (const cs of (realm && realm.clientScopes) || []) {
    const nm = (cs && cs.name) || '(no name)';
    for (const f of ['name', 'description']) push(`clientScopes[${nm}].${f}`, cs && cs[f]);
    for (const pm of (cs && cs.protocolMappers) || []) {
      push(`clientScopes[${nm}].protocolMappers[${(pm && pm.name) || '?'}].name`, pm && pm.name);
    }
  }

  const roles = (realm && realm.roles) || {};
  for (const r of roles.realm || []) {
    const n = (r && r.name) || '(no name)';
    for (const f of ['name', 'description']) push(`roles.realm[${n}].${f}`, r && r[f]);
  }
  const clientRoles = roles.client || {};
  for (const cid of Object.keys(clientRoles)) {
    for (const r of clientRoles[cid] || []) {
      const n = (r && r.name) || '(no name)';
      for (const f of ['name', 'description']) push(`roles.client[${cid}][${n}].${f}`, r && r[f]);
    }
  }

  const walkGroups = (gs, prefix) => {
    for (const g of gs || []) {
      const nm = (g && g.name) || '';
      push(`groups[${prefix}${nm}].name`, g && g.name);
      walkGroups(g && g.subGroups, `${prefix}${nm}/`);
    }
  };
  walkGroups(realm && realm.groups, '');

  return out;
}

// 収集済みフィールドのうち maxLen を超えるものを違反として返す（純粋関数）。
function findViolations(fields, maxLen = MAX_LEN) {
  const out = [];
  for (const f of fields) {
    const len = charLen(f.value);
    if (len > maxLen) out.push({ path: f.path, len, maxLen });
  }
  return out;
}

// realm JSON テキストを検査し、違反配列を返す（パース失敗は throw）。
function checkRealmText(text, maxLen = MAX_LEN) {
  const realm = JSON.parse(text);
  return findViolations(collectFields(realm), maxLen);
}

// realm から「必須 URL の欠落」を { path, url } で列挙する（純粋関数）。
// 対象 client が realm に存在しなければ、その client の必須 URL は検査しない。
function collectMissingUrls(realm, required = REQUIRED_CLIENT_URLS) {
  const out = [];
  const clients = (realm && realm.clients) || [];
  for (const clientId of Object.keys(required)) {
    const client = clients.find((c) => c && c.clientId === clientId);
    if (!client) continue;
    for (const field of Object.keys(required[clientId])) {
      const present = new Set(((client[field] || []).filter((u) => u != null)).map(String));
      for (const url of required[clientId][field]) {
        if (!present.has(url)) out.push({ path: `clients[${clientId}].${field}`, url });
      }
    }
  }
  return out;
}

// realm JSON テキストから必須 URL の欠落を返す（パース失敗は throw）。
function checkRealmUrlsText(text, required = REQUIRED_CLIENT_URLS) {
  return collectMissingUrls(JSON.parse(text), required);
}

// パスワードが「4 種のうち 3 種以上」を満たすかを返す（純粋関数）。
// Keycloak の regexPattern は Java の Pattern.matches ＝ 全体一致なので、JS 側も全体一致で評価する。
function satisfiesPasswordClasses(password, pattern = PASSWORD_CLASS_REGEX) {
  // 全体一致（Java の Pattern.matches 相当）。pattern 自身が ^…$ を持つが、^/$ は零幅なので二重でも等価。
  return new RegExp(`^(?:${pattern})$`).test(String(password ?? ''));
}

// realm から ADR-0026 の確定要件との差分を { path, expected, actual } で列挙する（純粋関数）。
// 対象 realm（既定 `platform`）以外は検査しない —— 別プロジェクトの realm は ADR-0026 の射程外。
function collectPolicyDeviations(
  realm,
  {
    scalars = AUTH_POLICY_SCALARS,
    actions = AUTH_POLICY_REQUIRED_ACTIONS,
    aliases = AUTH_POLICY_REQUIRED_ACTION_ALIASES,
    realmName = AUTH_POLICY_REALM,
  } = {},
) {
  const out = [];
  if (!realm || realm.realm !== realmName) return out;

  for (const key of Object.keys(scalars)) {
    const actual = realm[key];
    if (actual !== scalars[key]) {
      out.push({ path: `realm.${key}`, expected: scalars[key], actual: actual === undefined ? '«未設定»' : actual });
    }
  }

  const present = new Map(((realm.requiredActions) || []).map((a) => [a && a.alias, a]));

  // 既定 13 件の宣言漏れ。`requiredActions` を書くと既定が一切登録されないため、
  // 「書かなかった」＝「消した」になる（使わないものは enabled:false で明示する）。
  for (const alias of aliases) {
    if (!present.has(alias)) {
      out.push({
        path: `realm.requiredActions[${alias}]`,
        expected: '宣言されていること（requiredActions を書くと Keycloak の既定は登録されない）',
        actual: '«宣言なし»',
      });
    }
  }

  for (const alias of Object.keys(actions)) {
    const entry = present.get(alias);
    if (!entry) {
      out.push({ path: `realm.requiredActions[${alias}]`, expected: '存在すること', actual: '«未設定»' });
      continue;
    }
    for (const flag of Object.keys(actions[alias])) {
      if (entry[flag] !== actions[alias][flag]) {
        out.push({
          path: `realm.requiredActions[${alias}].${flag}`,
          expected: actions[alias][flag],
          actual: entry[flag] === undefined ? '«未設定»' : entry[flag],
        });
      }
    }
  }
  return out;
}

// realm JSON テキストから確定要件との差分を返す（パース失敗は throw）。
function checkRealmPolicyText(text, opts) {
  return collectPolicyDeviations(JSON.parse(text), opts);
}

// --- I/O（副作用は main / checkFiles に閉じる） --------------------------------

// 既定の検査対象（REALM_DIR 配下の *-realm.json）をリポジトリ相対で列挙する。
function defaultRealmFiles() {
  const dir = path.join(REPO_ROOT, REALM_DIR);
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir)
    .filter((n) => n.endsWith('-realm.json'))
    .map((n) => `${REALM_DIR}/${n}`);
}

function checkFiles(relPaths) {
  const results = [];
  for (const rel of relPaths) {
    const abs = path.isAbsolute(rel) ? rel : path.join(REPO_ROOT, rel);
    const text = fs.readFileSync(abs, 'utf8');
    results.push({
      file: rel,
      violations: checkRealmText(text),
      missing: checkRealmUrlsText(text),
      deviations: checkRealmPolicyText(text),
    });
  }
  return results;
}

// --- 自己試験 -----------------------------------------------------------------

function selfTest() {
  const cases = [];
  const long = 'a'.repeat(256);
  const ok255 = 'b'.repeat(255);
  const jaLong = 'あ'.repeat(300); // マルチバイトでも 1 文字 = 1 で数える

  cases.push({
    name: '255 文字ちょうどは合格',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x', description: ok255 }] })).length === 0,
  });
  cases.push({
    name: '256 文字の description は違反',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x', description: long }] })).length === 1,
  });
  cases.push({
    name: 'マルチバイト（あ×300）も文字数で 255 超を検出',
    pass: charLen(jaLong) === 300
      && findViolations(collectFields({ clients: [{ clientId: 'x', description: jaLong }] })).length === 1,
  });
  cases.push({
    name: 'realm role / client role / group / realm も走査する',
    pass: findViolations(collectFields({
      realm: 'r', displayName: long,
      roles: { realm: [{ name: 'a', description: long }], client: { c: [{ name: 'b', description: long }] } },
      groups: [{ name: 'g', subGroups: [{ name: long }] }],
    })).length === 4,
  });
  cases.push({
    name: 'clientScopes / protocolMappers（client・scope 双方）も走査する',
    pass: findViolations(collectFields({
      clients: [{ clientId: 'x', protocolMappers: [{ name: long }] }],
      clientScopes: [{ name: 'ok', description: long, protocolMappers: [{ name: long }] }],
    })).length === 3,
  });
  cases.push({
    name: '欠損フィールドは無視（例外を投げない）',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x' }], roles: {}, groups: null })).length === 0,
  });
  cases.push({
    name: 'JSON パース→検査（checkRealmText）が通る',
    pass: checkRealmText(JSON.stringify({ clients: [{ clientId: 'x', description: long }] })).length === 1,
  });

  // --- 必須 URL の欠落検査（Issue #385）---
  const req = { 'wiki-js': { redirectUris: ['http://localhost:3300/*', 'http://localhost:3001/*'] } };
  cases.push({
    name: '必須 URL が揃っていれば欠落なし',
    pass: collectMissingUrls(
      { clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*', 'http://localhost:3300/*'] }] },
      req,
    ).length === 0,
  });
  cases.push({
    name: '必須 URL（3300）が欠けていれば検出する',
    pass: (() => {
      const m = collectMissingUrls({ clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*'] }] }, req);
      return m.length === 1 && m[0].url === 'http://localhost:3300/*';
    })(),
  });
  cases.push({
    name: '対象 client が realm に無ければ検査しない（誤検出しない）',
    pass: collectMissingUrls({ clients: [{ clientId: 'other' }] }, req).length === 0,
  });
  cases.push({
    name: 'redirectUris 欠損（undefined）は全件欠落として検出する',
    pass: collectMissingUrls({ clients: [{ clientId: 'wiki-js' }] }, req).length === 2,
  });
  cases.push({
    name: '既定表（REQUIRED_CLIENT_URLS）で実 realm 形と突合できる',
    pass: checkRealmUrlsText(JSON.stringify({
      clients: [{
        clientId: 'wiki-js',
        redirectUris: REQUIRED_CLIENT_URLS['wiki-js'].redirectUris,
        webOrigins: REQUIRED_CLIENT_URLS['wiki-js'].webOrigins,
      }],
    })).length === 0,
  });

  // --- ADR-0026 の確定要件からの逸脱検査（Issue #578 / IADR-0197）---
  // 確定要件を満たす realm を定数から組み立てる。realm.json 側はこの形と一致していなければ違反になる。
  const conformingRealm = () => ({
    realm: AUTH_POLICY_REALM,
    ...AUTH_POLICY_SCALARS,
    requiredActions: AUTH_POLICY_REQUIRED_ACTION_ALIASES.map((alias) => ({
      alias,
      enabled: alias === 'CONFIGURE_TOTP' || alias === 'UPDATE_PASSWORD'
        || alias === 'CONFIGURE_RECOVERY_AUTHN_CODES',
      defaultAction: alias === 'CONFIGURE_TOTP',
    })),
  });

  cases.push({
    name: '確定要件を満たす realm は逸脱なし',
    pass: collectPolicyDeviations(conformingRealm()).length === 0,
  });
  // ↓ 以降は「変異させたら検出する」を確かめる。変異なしの正例だけでは、
  //   走査対象に入っていなくても「逸脱なし」が成立してしまう（#742 で踏んだ型）。
  cases.push({
    name: '変異: passwordPolicy を落とすと検出する',
    pass: (() => {
      const r = conformingRealm(); delete r.passwordPolicy;
      const d = collectPolicyDeviations(r);
      return d.length === 1 && d[0].path === 'realm.passwordPolicy' && d[0].actual === '«未設定»';
    })(),
  });
  cases.push({
    name: '変異: otpPolicyLookAheadWindow を 2 にすると検出する（前後 1 ステップの境界）',
    pass: (() => {
      const r = conformingRealm(); r.otpPolicyLookAheadWindow = 2;
      const d = collectPolicyDeviations(r);
      return d.length === 1 && d[0].path === 'realm.otpPolicyLookAheadWindow' && d[0].actual === 2;
    })(),
  });
  cases.push({
    name: '変異: CONFIGURE_TOTP を enabled のみ（defaultAction=false）にすると検出する',
    pass: (() => {
      const r = conformingRealm();
      // 13 件はそのまま残し、CONFIGURE_TOTP の defaultAction だけを倒す
      // （alias の宣言漏れ検査と混ざらないようにする）。
      r.requiredActions = r.requiredActions.map((a) => (
        a.alias === 'CONFIGURE_TOTP' ? { ...a, defaultAction: false } : a
      ));
      const d = collectPolicyDeviations(r);
      return d.length === 1 && d[0].path === 'realm.requiredActions[CONFIGURE_TOTP].defaultAction';
    })(),
  });
  cases.push({
    name: '変異: UPDATE_PASSWORD を落とすと検出する（ADR-0045 決定 9-b の代替が成立しない）',
    pass: (() => {
      const r = conformingRealm();
      r.requiredActions = r.requiredActions.filter((a) => a.alias !== 'UPDATE_PASSWORD');
      const d = collectPolicyDeviations(r);
      return d.some((x) => x.path === 'realm.requiredActions[UPDATE_PASSWORD]');
    })(),
  });
  cases.push({
    name: '変異: CONFIGURE_RECOVERY_AUTHN_CODES を落とすと検出する（ADR-0026 のリカバリーコード）',
    pass: (() => {
      const r = conformingRealm();
      r.requiredActions = r.requiredActions.filter((a) => a.alias !== 'CONFIGURE_RECOVERY_AUTHN_CODES');
      const d = collectPolicyDeviations(r);
      return d.some((x) => x.path === 'realm.requiredActions[CONFIGURE_RECOVERY_AUTHN_CODES]');
    })(),
  });
  cases.push({
    name: '変異: 既定 13 件のうち 1 件（VERIFY_PROFILE）を書き忘れると検出する（省略＝削除であるため）',
    pass: (() => {
      const r = conformingRealm();
      r.requiredActions = r.requiredActions.filter((a) => a.alias !== 'VERIFY_PROFILE');
      const d = collectPolicyDeviations(r);
      return d.length === 1 && d[0].path === 'realm.requiredActions[VERIFY_PROFILE]';
    })(),
  });
  cases.push({
    name: '変異: displayName を落とすと検出する',
    pass: (() => {
      const r = conformingRealm(); delete r.displayName;
      return collectPolicyDeviations(r).length === 1;
    })(),
  });
  cases.push({
    name: '変異: permanentLockout を true にすると検出する（15 分の一時ロックであること）',
    pass: (() => {
      const r = conformingRealm(); r.permanentLockout = true;
      return collectPolicyDeviations(r).length === 1;
    })(),
  });
  cases.push({
    name: '別プロジェクトの realm（realm 名が違う）は検査しない',
    pass: collectPolicyDeviations({ realm: 'ai-stock-trading' }).length === 0,
  });
  cases.push({
    name: 'JSON パース→検査（checkRealmPolicyText）が通る',
    pass: checkRealmPolicyText(JSON.stringify(conformingRealm())).length === 0,
  });

  // パスワードの「4 種のうち 3 種以上」の境界。2 種は拒否・3 種は受理。
  const pwCases = [
    ['Abcdefghij12', true, '小+大+数 ＝ 3 種'],
    ['Abcdefghij!@', true, '小+大+記号 ＝ 3 種'],
    ['ABCDEFGHIJ1!', true, '大+数+記号 ＝ 3 種'],
    ['abcdefghi1!@', true, '小+数+記号 ＝ 3 種'],
    ['Abcdefghi1!@', true, '4 種'],
    ['abcdefghij12', false, '小+数 ＝ 2 種'],
    ['abcdefghij!@', false, '小+記号 ＝ 2 種'],
    ['ABCDEFGHIJ12', false, '大+数 ＝ 2 種'],
    ['aaaaaaaaaaaa', false, '小のみ ＝ 1 種'],
  ];
  for (const [pw, want, note] of pwCases) {
    cases.push({
      name: `パスワード分類: ${note} → ${want ? '受理' : '拒否'}`,
      pass: satisfiesPasswordClasses(pw) === want,
    });
  }
  cases.push({
    name: 'passwordPolicy の regexPattern 引数が PASSWORD_CLASS_REGEX と一致する（単一情報源）',
    pass: AUTH_POLICY_SCALARS.passwordPolicy.endsWith(`regexPattern(${PASSWORD_CLASS_REGEX})`),
  });
  cases.push({
    name: 'passwordPolicy に " and " 区切りを壊す文字列が正規表現へ混入していない（Keycloak のパーサ制約）',
    pass: !PASSWORD_CLASS_REGEX.includes(' and ') && AUTH_POLICY_SCALARS.passwordPolicy.endsWith(')'),
  });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-realm-constraints] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-realm-constraints] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) { selfTest(); return; }

  const targets = argv.filter((a) => !a.startsWith('--'));
  const files = targets.length ? targets : defaultRealmFiles();
  if (files.length === 0) {
    console.log('[check-realm-constraints] 検査対象の realm JSON が見つかりません（skip）。');
    process.exit(0);
  }

  const results = checkFiles(files);
  const total = results.reduce((n, r) => n + r.violations.length, 0);
  const totalMissing = results.reduce((n, r) => n + r.missing.length, 0);
  const totalDeviations = results.reduce((n, r) => n + r.deviations.length, 0);
  if (total === 0 && totalMissing === 0 && totalDeviations === 0) {
    console.log(`[check-realm-constraints] OK: ${files.length} ファイルに ${MAX_LEN} 文字超のフィールド・必須 URL の欠落・ADR-0026 からの逸脱はありません。`);
    process.exit(0);
  }

  if (total > 0) {
    console.error(`[check-realm-constraints] ${MAX_LEN} 文字（varchar(${MAX_LEN})）超のフィールド ${total} 件を検出しました:`);
    for (const r of results) {
      for (const v of r.violations) {
        console.error(`\n  ${r.file}\n    ${v.path}: ${v.len} 文字（上限 ${v.maxLen}）`);
      }
    }
    console.error('\nrealm import は SQLSTATE 22001 で失敗します。該当フィールドを 255 文字以内へ短縮してください（Issue #18）。');
  }

  if (totalMissing > 0) {
    console.error(`[check-realm-constraints] 経路ごとに必須の URL の欠落 ${totalMissing} 件を検出しました:`);
    for (const r of results) {
      for (const m of r.missing) {
        console.error(`\n  ${r.file}\n    ${m.path}: ${m.url} が未登録`);
      }
    }
    console.error('\n当該経路の OIDC が invalid_redirect_uri で完了しなくなります。経路の対応は IADR-0095 の追記（#385）を参照してください。');
  }

  if (totalDeviations > 0) {
    console.error(`[check-realm-constraints] ADR-0026（認証UXとアカウント管理）の確定要件からの逸脱 ${totalDeviations} 件を検出しました:`);
    for (const r of results) {
      for (const d of r.deviations) {
        console.error(`\n  ${r.file}\n    ${d.path}: 期待 ${JSON.stringify(d.expected)} / 実際 ${JSON.stringify(d.actual)}`);
      }
    }
    console.error('\nSC-14（OTP／多要素認証）・SC-15（パスワードリセット）が計画の確定要件を満たさなくなります。'
      + '\n確定値の正は planning の ADR-0026 であり、実装側の記録は IADR-0197（#578）です。');
  }
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  charLen,
  collectFields,
  findViolations,
  checkRealmText,
  collectMissingUrls,
  checkRealmUrlsText,
  collectPolicyDeviations,
  checkRealmPolicyText,
  satisfiesPasswordClasses,
  MAX_LEN,
  REQUIRED_CLIENT_URLS,
  PASSWORD_CLASS_REGEX,
  AUTH_POLICY_REALM,
  AUTH_POLICY_SCALARS,
  AUTH_POLICY_REQUIRED_ACTIONS,
  AUTH_POLICY_REQUIRED_ACTION_ALIASES,
};
