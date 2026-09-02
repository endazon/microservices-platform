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
 * 検査4: realm が指すテーマの実体が解決できるか（SC-13 / SC-16・IADR-0261 決定 1）。
 * 背景: `loginTheme` / `accountTheme` は**名前の文字列**でしかない。実体が無くても realm import は成功し、
 * Keycloak は既定テーマへ黙って落ちる —— 画面は出るのでヘルスチェックも E2E のログインも緑のまま、
 * **ブランド適用だけが静かに外れる**。styles が挙げる css の欠落も同型で、404 を出すだけでログインは成功する。
 * したがって「テーマを実装した」ことは realm.json の 1 行では担保されず、実体との突合が要る。
 * 併せて parent の宣言（テンプレート非複製の方針）と、言語切替に要る i18n 設定も静的に確かめる。
 *
 * 検査5: MFA が実効的に強制されているか、監査イベントが記録されるか（#438・IADR-0294）。
 * 背景: IADR-0197 が検査 3 を置いた時点で、同 IADR 自身が 2 つの未達を明記して #438 へ送っていた。
 *   ①`CONFIGURE_TOTP` の `defaultAction: true` は**新規に作られる利用者にしか付かない**。realm import で
 *     作られる利用者は `users[].requiredActions` が未設定のままで、既定 browser フローの OTP サブフローは
 *     Conditional（`Condition - user configured`）であるため、**OTP を一度も登録しない者はパスワードだけで
 *     ログインできる**。「MFA を必須にした」と realm に書いてあることと、実際に効いていることは別である。
 *   ②`eventsEnabled` / `adminEventsEnabled` / `eventsListeners` がいずれも未設定で、**ログイン失敗も
 *     管理操作も 1 件も残らない**。ADR-0026「SC-17 の操作を監査ログに記録する」と ADR-0045 決定 9-b の
 *     「申請者・承認者・実行者を残す」が成立しない。
 * さらに本検査の母集合走査で 3 つ目が出た —— **`directAccessGrantsEnabled` は browser フローを丸ごと
 * 迂回する**。利用者名・パスワード・client secret があれば OTP を一切問われずにトークンが出る。
 * いずれも realm JSON だけで静的に判定できるため、import 前に止める。
 *
 * 検査6: **サーバ間の口**の宛先が、pod から到達し得ない host になっていないか（#1115）。
 * 背景: realm の URL 欄はほとんどが「ブラウザが開く URL」で、裸の `localhost` が正しい。例外は
 * `backchannel.logout.url` —— **認可サーバが pod の中から叩く**宛先である。ここにブラウザ向けの値を
 * 書くと、Keycloak は自分自身の :443 へ POST して `Connection refused` になり、**BFF には一度も届かない**。
 * 失敗は静かで、Keycloak 側に `KC-SERVICES0057` が 1 行出るだけ、管理者の画面には「ログアウトさせた」
 * と映る。到達可能性そのものは静的に測れないので、**到達し得ない形**（裸の localhost / ループバック /
 * `*.localhost`）だけを止める。詳細は collectServerSideUrlGaps の注記。
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
// テーマ実体の置き場（IADR-0261 決定 1）。realm の宣言と突き合わせる。
const THEME_ROOT = 'deploy/keycloak/themes';
// realm のテーマ宣言フィールド → テーマ種別のディレクトリ名。
const THEME_FIELDS = { loginTheme: 'login', accountTheme: 'account' };
// Keycloak の該当カラムはいずれも varchar(255)。閾値は 1 箇所に集約する。
const MAX_LEN = 255;

// 経路ごとに必須の redirect URI / web origin / post-logout URI（Issue #385・#780 再発防止）。
// 落とすとその経路の OIDC が invalid_redirect_uri で完了しなくなるため、宣言的に列挙して
// CI で欠落を検出する。経路の対応は IADR-0095 の追記（2026-07-26・#385）の表を単一情報源とする。
//
// 🔴 #780: **宣言は `wiki-js` 1 件だけだった。** ブラウザ OIDC を持つ 7 クライアントのうち
//    6 つが無検査であり、「片方の経路だけ足して片方を忘れる」事故を止める仕掛けが
//    その事故を最も起こしやすい 6 件を見ていなかった。本表を 7 クライアント＋`bff` へ広げる。
//
// 🔴 **`attributes.post.logout.redirect.uris` は `##` 区切りの 1 本の文字列である。**
//    redirect / origin と別フィールドなので、片方だけ足す事故がここでも起きる（#780 本文が
//    「3 フィールドすべてに追記が要る」と名指ししていた箇所）。`attributes.` 接頭辞つきで宣言し、
//    検査側が `##` で割って突合する。
//
// 対象外: `abac-seeder` / `ai-stock-trading-kb-writer`（service account 専用。redirect を持たない）。
const REQUIRED_CLIENT_URLS = {
  'wiki-js': {
    redirectUris: [
      'https://wiki.localhost:50000/*', // edge 集約（IADR-0091・LOCALEDGE=1）
      'http://localhost:3300/*',       // k8s の port-forward（非 edge・svc/wiki-js 3300:3000）
      'http://localhost:3001/*',       // compose(dev) の host 公開（IADR-0032・ports 3001:3000）
      'http://wiki-js:3000/*',         // in-cluster
    ],
    webOrigins: [
      'https://wiki.localhost:50000',
      'http://localhost:3300',
      'http://localhost:3001',
    ],
  },
  // SPA（public client・PKCE）。origin 由来で redirect を組むため、経路の数だけ登録が要る。
  'platform-spa': {
    redirectUris: [
      'https://localhost/*',      // edge（LOCALEDGE=1・443）
      'http://localhost:3100/*',  // compose(dev) の host 公開
      'http://localhost:8081/*',  // 非 edge の port-forward
    ],
    webOrigins: ['https://localhost', 'http://localhost:3100', 'http://localhost:8081'],
    'attributes.post.logout.redirect.uris': [
      'https://localhost/*', 'http://localhost:3100/*', 'http://localhost:8081/*',
    ],
  },
  // BFF セッション方式（ADR-0032・Token Handler）の confidential client。**ブラウザは
  // BFF の callback へ戻る**ので、SPA とは別の URL 集合を持つ（#439 3a）。
  bff: {
    redirectUris: [
      'https://localhost/bff/auth/callback',      // edge
      'http://localhost:3100/bff/auth/callback',  // compose(dev)
      'http://localhost:5000/bff/auth/callback',  // 非 edge の port-forward
    ],
    webOrigins: ['https://localhost', 'http://localhost:3100', 'http://localhost:5000'],
    'attributes.post.logout.redirect.uris': [
      'https://localhost/*', 'http://localhost:3100/*', 'http://localhost:5000/*',
    ],
  },
  headlamp: {
    redirectUris: ['https://headlamp.localhost:50000/*', 'http://localhost:4466/*'],
    webOrigins: ['https://headlamp.localhost:50000', 'http://localhost:4466'],
  },
  // Grafana は root_url から redirect を一意生成する（IADR-0090）。パスは固定。
  grafana: {
    redirectUris: [
      'https://grafana.localhost:50000/login/generic_oauth',
      'http://localhost:3000/login/generic_oauth',
    ],
    webOrigins: ['https://grafana.localhost:50000', 'http://localhost:3000'],
  },
  argocd: {
    redirectUris: [
      'https://argocd.localhost:50000/auth/callback',
      'http://localhost:8083/auth/callback',
    ],
    webOrigins: ['https://argocd.localhost:50000', 'http://localhost:8083'],
  },
  minio: {
    redirectUris: [
      'https://minio.localhost:50000/oauth_callback',
      'http://localhost:9001/oauth_callback',
    ],
    webOrigins: ['https://minio.localhost:50000', 'http://localhost:9001'],
  },
  // Vault UI の callback パスは `/ui/vault/auth/<mount>/oidc/callback` 固定。
  // `http://localhost:8250/oidc/callback` は **CLI（vault login -method=oidc）のローカル待受**で
  // あり、エッジを経由しない（IADR-0220 の注記）。両方が要る。
  vault: {
    redirectUris: [
      'https://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback',
      'http://localhost:8250/oidc/callback',
    ],
    webOrigins: ['https://vault.localhost:50000'],
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

/*
 * 検査5 の期待値（#438 / IADR-0294）。
 *
 * 🔴 **サービスアカウントには `CONFIGURE_TOTP` を付けてはならない。** サービスアカウントは対話ログインを
 * 行わないため、必須アクションが残っているとトークン取得が `Account is not fully set up` で失敗する。
 * 判定は `serviceAccountClientId` の有無で行う（`service-account-` という名前の慣習に依存しない
 * —— 名前は人が付けるもので、機械が保証する事実ではない）。
 */
const MFA_REQUIRED_ACTION = 'CONFIGURE_TOTP';

// 監査イベントの期待。`eventsExpiration` は値そのものを固定しない（保持期間は運用の選択であり、
// ADR-0026 が数値を確定していない）。**「記録される」ことだけを不変条件にする。**
const AUDIT_EVENT_SCALARS = {
  eventsEnabled: true,
  adminEventsEnabled: true,
  adminEventsDetailsEnabled: true,
};

// 少なくともこれだけは記録されていること。ADR-0026（SC-17 の操作記録）と
// ADR-0045 決定 9-b（申請者・承認者・実行者）が要求する事象を最小集合として置く。
// 網羅ではない —— 足りない事象が見つかったらここへ足す。
const AUDIT_REQUIRED_EVENT_TYPES = [
  'LOGIN', 'LOGIN_ERROR', 'LOGOUT',
  'UPDATE_PASSWORD', 'UPDATE_TOTP', 'REMOVE_TOTP',
  'RESET_PASSWORD', 'REMOVE_CREDENTIAL',
];

// イベントを実際に外へ出すリスナ。空配列だと `eventsEnabled: true` でも DB に溜まるだけになる。
const AUDIT_REQUIRED_LISTENERS = ['jboss-logging'];

/*
 * 登録した OTP を利用者自身が削除できると、強制登録を通った後で MFA 無しの状態へ戻れる。
 * ADR-0026 は再発行を SC-16／管理者側に置いているため、自己都合の削除口は閉じる。
 */
const MFA_DISABLED_REQUIRED_ACTIONS = ['delete_credential'];

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
      // `attributes.<key>` は **`##` 区切りの 1 本の文字列**（Keycloak の post-logout の持ち方）。
      // 配列フィールドと同じ形へ正規化してから突合する。空要素は落とす（末尾 `##` に耐える）。
      const values = field.startsWith('attributes.')
        ? String((client.attributes || {})[field.slice('attributes.'.length)] ?? '')
          .split('##').filter((u) => u !== '')
        : (client[field] || []).filter((u) => u != null);
      const present = new Set(values.map(String));
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

// 利用者がサービスアカウントかどうか。`serviceAccountClientId` を持つ利用者は Keycloak が
// client のために自動生成したものであり、対話ログインしない。
function isServiceAccountUser(user) {
  return typeof (user && user.serviceAccountClientId) === 'string' && user.serviceAccountClientId !== '';
}

// realm から「MFA が実効的に強制されていない／監査イベントが残らない」箇所を { path, detail } で列挙する。
// 純粋関数。対象 realm（既定 `platform`）以外は検査しない —— 別プロジェクトの realm は ADR-0026 の射程外。
function collectMfaAuditGaps(realm, { realmName = AUTH_POLICY_REALM } = {}) {
  const gaps = [];
  if (!realm || realm.realm !== realmName) return gaps;

  // --- (1) 対話ログインする利用者は全員 CONFIGURE_TOTP を要求されること ---
  for (const user of realm.users || []) {
    const name = (user && user.username) || '«無名»';
    const actions = (user && user.requiredActions) || [];
    const hasTotp = actions.includes(MFA_REQUIRED_ACTION);
    if (isServiceAccountUser(user)) {
      // 陰性側: サービスアカウントに付いていたら、それはそれで壊れる。
      if (hasTotp) {
        gaps.push({
          path: `realm.users[${name}].requiredActions`,
          detail: `サービスアカウント（serviceAccountClientId=${user.serviceAccountClientId}）に ${MFA_REQUIRED_ACTION} が付いています。`
            + ' 対話ログインしないため、トークン取得が Account is not fully set up で失敗します。',
        });
      }
      continue;
    }
    if (user && user.enabled === false) continue; // 無効な利用者はログインできないので対象外
    if (!hasTotp) {
      gaps.push({
        path: `realm.users[${name}].requiredActions`,
        detail: `${MFA_REQUIRED_ACTION} が含まれていません。requiredActions プロバイダ側の defaultAction は`
          + ' **新規に作られる利用者にしか付かない**ため、realm import で作られるこの利用者は'
          + ' OTP 未登録のままログインできます（既定 browser フローの OTP は Conditional）。',
      });
    }
  }

  // --- (2) browser フローを迂回する direct access grant が無いこと ---
  for (const client of realm.clients || []) {
    if (client && client.directAccessGrantsEnabled === true) {
      gaps.push({
        path: `realm.clients[${(client && client.clientId) || '«無名»'}].directAccessGrantsEnabled`,
        detail: 'true です。パスワードグラントは browser フローを通らないため、OTP を一切問われずに'
          + 'トークンが出ます（MFA のバイパス口）。',
      });
    }
  }

  // --- (3) 登録済み OTP を利用者自身が消せないこと ---
  const declared = new Map((realm.requiredActions || []).map((a) => [a && a.alias, a]));
  for (const alias of MFA_DISABLED_REQUIRED_ACTIONS) {
    const entry = declared.get(alias);
    if (entry && entry.enabled !== false) {
      gaps.push({
        path: `realm.requiredActions[${alias}].enabled`,
        detail: 'true です。利用者が自分の資格情報を削除できると、強制登録を通った後に MFA 無しの状態へ'
          + '戻れます（再発行は ADR-0026 が SC-16／管理者側に置いています）。',
      });
    }
  }

  // --- (4) 監査イベントが記録されること ---
  for (const key of Object.keys(AUDIT_EVENT_SCALARS)) {
    if (realm[key] !== AUDIT_EVENT_SCALARS[key]) {
      gaps.push({
        path: `realm.${key}`,
        detail: `期待 ${JSON.stringify(AUDIT_EVENT_SCALARS[key])} / 実際 ${realm[key] === undefined ? '«未設定»' : JSON.stringify(realm[key])}。`
          + ' Keycloak の既定は「記録しない」であり、書かなければ 1 件も残りません。',
      });
    }
  }
  const listeners = realm.eventsListeners || [];
  for (const l of AUDIT_REQUIRED_LISTENERS) {
    if (!listeners.includes(l)) {
      gaps.push({
        path: 'realm.eventsListeners',
        detail: `${l} が含まれていません。リスナが無いとイベントは外へ出ず、監査の実体になりません。`,
      });
    }
  }
  const types = realm.enabledEventTypes || [];
  for (const t of AUDIT_REQUIRED_EVENT_TYPES) {
    if (!types.includes(t)) {
      gaps.push({
        path: 'realm.enabledEventTypes',
        detail: `${t} が含まれていません（ADR-0026 の SC-17 操作記録・ADR-0045 決定 9-b が要求する事象）。`,
      });
    }
  }

  return gaps;
}

// realm JSON テキストから MFA / 監査イベントの齟齬を返す（パース失敗は throw）。
function checkRealmMfaAuditText(text, opts) {
  return collectMfaAuditGaps(JSON.parse(text), opts);
}

// realm が宣言するテーマ（loginTheme / accountTheme）と、ディスク上の実体との齟齬を列挙する。
// I/O は reader（{ exists, read }）として注入し、この関数自体は純粋に保つ（自己試験できるようにする）。
// 返り値は [{ path, detail }]。path は realm JSON 内のフィールドか、解決したテーマのパス。
function collectThemeGaps(realm, reader, themeRoot = THEME_ROOT) {
  const gaps = [];
  for (const [field, kind] of Object.entries(THEME_FIELDS)) {
    const name = realm && realm[field];
    // 宣言していないテーマは検査しない（既定テーマのままにするのは正当な選択である）。
    if (typeof name !== 'string' || name === '') continue;
    const dir = `${themeRoot}/${name}/${kind}`;
    const props = `${dir}/theme.properties`;
    if (!reader.exists(props)) {
      gaps.push({ path: field, detail: `テーマ "${name}" の実体がない（期待: ${props}）。realm import は成功し Keycloak は既定テーマへ黙って落ちる` });
      continue;
    }
    const text = reader.read(props);
    const parent = /^\s*parent\s*=\s*(\S+)\s*$/m.exec(text);
    if (!parent) {
      gaps.push({ path: props, detail: 'parent= の宣言がない。継承しないテーマは Keycloak 本体のテンプレート更新（セキュリティ修正・新フロー）から切り離される' });
    }
    const styles = /^\s*styles\s*=\s*(.+?)\s*$/m.exec(text);
    if (styles) {
      for (const href of styles[1].split(/\s+/).filter(Boolean)) {
        // 親テーマが供給する css（自テーマの resources/ に無くてよい）は、親を持つ場合に限り許す。
        const own = `${dir}/resources/${href}`;
        if (reader.exists(own)) continue;
        if (parent) continue; // 親から解決され得る
        gaps.push({ path: props, detail: `styles の ${href} が ${own} に無く、親テーマも宣言されていない` });
      }
    }
  }
  // 言語切替（SC-13 の主要素）は realm 設定だけで完結する。既定ロケールが supportedLocales に
  // 無いと Keycloak は既定言語へ落ちるため、包含まで確かめる。
  if (realm && realm.internationalizationEnabled === true) {
    const supported = Array.isArray(realm.supportedLocales) ? realm.supportedLocales : [];
    const def = realm.defaultLocale;
    if (typeof def === 'string' && def !== '' && !supported.includes(def)) {
      gaps.push({ path: 'defaultLocale', detail: `defaultLocale "${def}" が supportedLocales ${JSON.stringify(supported)} に含まれていない` });
    }
    if (supported.length < 2) {
      gaps.push({ path: 'supportedLocales', detail: `internationalizationEnabled が true なのに supportedLocales が ${supported.length} 件しかない（切替先が無い）` });
    }
  }
  return gaps;
}

function checkRealmThemeText(text, reader, themeRoot = THEME_ROOT) {
  return collectThemeGaps(JSON.parse(text), reader, themeRoot);
}

/*
 * 検査6: **サーバ間の口**に、pod から到達し得ない host を書いていないか（#1115）。
 *
 * realm の URL 欄はほとんどが「ブラウザが開く URL」で、そこは裸の `localhost`（エッジ host）が正しい。
 * 例外が `backchannel.logout.url` である —— これは **Keycloak 自身が pod の中から叩く**宛先であり、
 * ブラウザ向けの値をそのまま書くと**一度も届かない**。しかも失敗は静かで、Keycloak 側に
 * `KC-SERVICES0057` が 1 行出るだけ、管理者の画面には「ログアウトさせた」と映る。
 *
 * 「realm に書いた URL が実際に到達可能か」は静的には測れない（DNS もメッシュも実行時の性質である）。
 * 測れるのは「**到達し得ない形をしていないか**」であり、本検査はそこだけを見る:
 *
 *   - 裸の `localhost` / `127.0.0.1` / `::1`: pod の `/etc/hosts` が必ず **pod 自身**へ向ける。
 *     CoreDNS には届かないので、書き換え規則をどう広げても宛先にはならない（#1115 実測）。
 *   - `*.localhost`（エッジ host）: CoreDNS は答えるが、**Keycloak イメージ（UBI9 / glibc）の
 *     名前解決が引かない**。同じ pod の netns に musl のコンテナを足すと同じ名前が解決する（#1115 実測）。
 *
 * ブラウザ向けの欄（redirectUris / webOrigins / post.logout.redirect.uris）は**対象にしない**。
 */
const SERVER_SIDE_URL_ATTRS = ['backchannel.logout.url'];
const POD_SELF_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '0:0:0:0:0:0:0:1']);

function collectServerSideUrlGaps(realm, attrs = SERVER_SIDE_URL_ATTRS) {
  const gaps = [];
  for (const client of (realm && realm.clients) || []) {
    const attributes = (client && client.attributes) || {};
    for (const attr of attrs) {
      const raw = attributes[attr];
      // 宣言していない client は検査しない（バックチャネルログアウトを使わないのは正当な選択である）。
      if (typeof raw !== 'string' || raw === '') continue;
      const where = `clients[${client.clientId}].attributes["${attr}"]`;
      let host;
      try {
        host = new URL(raw).hostname.toLowerCase().replace(/^\[|\]$/g, '');
      } catch {
        gaps.push({ path: where, detail: `URL として解釈できない: ${raw}` });
        continue;
      }
      if (POD_SELF_HOSTS.has(host)) {
        gaps.push({
          path: where,
          detail: `host が "${host}" —— pod の /etc/hosts が必ず pod 自身へ向けるため、サーバ間の宛先にならない（${raw}）`,
        });
      } else if (host.endsWith('.localhost')) {
        gaps.push({
          path: where,
          detail: `host が "${host}"（エッジ host）—— 認可サーバの pod の名前解決が *.localhost を引かない（${raw}）`,
        });
      }
    }
  }
  return gaps;
}

function checkRealmServerSideUrlsText(text, attrs = SERVER_SIDE_URL_ATTRS) {
  return collectServerSideUrlGaps(JSON.parse(text), attrs);
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

// ディスクを見る reader。collectThemeGaps へ注入して、ロジック側を純粋に保つ。
function diskReader() {
  return {
    exists: (rel) => fs.existsSync(path.join(REPO_ROOT, rel)),
    read: (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8'),
  };
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
      themeGaps: checkRealmThemeText(text, diskReader()),
      mfaGaps: checkRealmMfaAuditText(text),
      serverUrlGaps: checkRealmServerSideUrlsText(text),
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

  // --- #780: post-logout（`##` 連結）と、7 クライアントへ広げた宣言の検査 ---
  const reqPl = { 'platform-spa': { 'attributes.post.logout.redirect.uris': ['https://localhost/*', 'http://localhost:3100/*'] } };
  cases.push({
    name: 'post-logout（## 連結）が揃っていれば欠落なし',
    pass: collectMissingUrls({
      clients: [{ clientId: 'platform-spa', attributes: { 'post.logout.redirect.uris': 'http://localhost:3100/*##https://localhost/*##https://localhost' } }],
    }, reqPl).length === 0,
  });
  cases.push({
    name: '変異: post-logout から https 版を落とすと検出する（片方だけ足す事故・#780）',
    pass: (() => {
      const m = collectMissingUrls({
        clients: [{ clientId: 'platform-spa', attributes: { 'post.logout.redirect.uris': 'http://localhost:3100/*' } }],
      }, reqPl);
      return m.length === 1 && m[0].url === 'https://localhost/*'
        && m[0].path === 'clients[platform-spa].attributes.post.logout.redirect.uris';
    })(),
  });
  cases.push({
    name: 'post-logout 属性が無ければ全件欠落として検出する',
    pass: collectMissingUrls({ clients: [{ clientId: 'platform-spa' }] }, reqPl).length === 2,
  });
  cases.push({
    name: '#780: ブラウザ OIDC を持つ 7 クライアント＋bff がすべて宣言されている',
    pass: ['wiki-js', 'platform-spa', 'bff', 'headlamp', 'grafana', 'argocd', 'minio', 'vault']
      .every((c) => Object.prototype.hasOwnProperty.call(REQUIRED_CLIENT_URLS, c)),
  });
  cases.push({
    name: '#780: 宣言された URL のうち edge 経路（*.localhost / localhost 443）は必ず https である',
    pass: Object.values(REQUIRED_CLIENT_URLS).every((spec) => Object.entries(spec).every(([, urls]) => urls.every(
      (u) => !/^http:\/\/(?:[a-z-]+\.)?localhost(?::50000)?\//.test(u),
    ))),
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

  // --- 検査4（テーマ参照の解決可能性・SC-13 / SC-16） --------------------------
  // reader をメモリ上の擬似 FS で与える。ディスクに触らないので、実データの状態に依存しない。
  const fakeReader = (files) => ({
    exists: (rel) => Object.prototype.hasOwnProperty.call(files, rel),
    read: (rel) => files[rel],
  });
  const okTheme = 'parent=keycloak\nimport=common/keycloak\n\nstyles=css/login.css css/platform.css\n';
  const themedRealm = {
    loginTheme: 'platform', accountTheme: 'platform',
    internationalizationEnabled: true, supportedLocales: ['ja', 'en'], defaultLocale: 'ja',
  };
  const fullFiles = {
    'deploy/keycloak/themes/platform/login/theme.properties': okTheme,
    'deploy/keycloak/themes/platform/account/theme.properties': okTheme,
  };

  cases.push({
    name: 'テーマ: 実体・parent・i18n が揃っていれば齟齬なし（陽性対照）',
    pass: collectThemeGaps(themedRealm, fakeReader(fullFiles)).length === 0,
  });
  cases.push({
    name: 'テーマ: realm が指すのに実体が無いと検出する（既定テーマへ黙って落ちる事故）',
    pass: (() => {
      const g = collectThemeGaps(themedRealm, fakeReader({
        'deploy/keycloak/themes/platform/account/theme.properties': okTheme,
      }));
      return g.length === 1 && g[0].path === 'loginTheme';
    })(),
  });
  cases.push({
    name: 'テーマ: parent= が無いと検出する（本体のテンプレート更新から切り離される）',
    pass: (() => {
      const g = collectThemeGaps({ loginTheme: 'platform' }, fakeReader({
        'deploy/keycloak/themes/platform/login/theme.properties': 'styles=css/platform.css\n',
      }));
      return g.length === 2 && g.some((x) => /parent=/.test(x.detail)) && g.some((x) => /styles の/.test(x.detail));
    })(),
  });
  cases.push({
    name: 'テーマ: 宣言していないテーマ種別は検査しない（既定のままにするのは正当）',
    pass: collectThemeGaps({ loginTheme: 'platform' }, fakeReader(fullFiles)).length === 0
      && collectThemeGaps({}, fakeReader({})).length === 0
      && collectThemeGaps({ loginTheme: '' }, fakeReader({})).length === 0,
  });
  cases.push({
    name: 'テーマ: defaultLocale が supportedLocales に無いと検出する（既定言語へ落ちる）',
    pass: (() => {
      const g = collectThemeGaps({ internationalizationEnabled: true, supportedLocales: ['en', 'de'], defaultLocale: 'ja' }, fakeReader({}));
      return g.length === 1 && g[0].path === 'defaultLocale';
    })(),
  });
  cases.push({
    name: 'テーマ: 切替先が 1 つしか無い i18n を検出する（言語切替が成立しない）',
    pass: (() => {
      const g = collectThemeGaps({ internationalizationEnabled: true, supportedLocales: ['ja'], defaultLocale: 'ja' }, fakeReader({}));
      return g.length === 1 && g[0].path === 'supportedLocales';
    })(),
  });
  cases.push({
    name: 'テーマ: i18n が無効なら supportedLocales は問わない（無効化は正当な選択）',
    pass: collectThemeGaps({ internationalizationEnabled: false, supportedLocales: [], defaultLocale: 'ja' }, fakeReader({})).length === 0,
  });
  cases.push({
    name: 'テーマ: 実データの realm と themes/ が実際に解決できる（実データ・ラチェット）',
    pass: (() => {
      const realmPath = path.join(REPO_ROOT, REALM_DIR, 'microservices-platform-realm.json');
      if (!fs.existsSync(realmPath)) return true; // realm が無い配布物では skip
      const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
      // 実データ側が「そもそも宣言していない」状態で緑になるのを防ぐ（0 件走査の門）。
      if (realm.loginTheme !== 'platform' || realm.accountTheme !== 'platform') return false;
      return collectThemeGaps(realm, diskReader()).length === 0;
    })(),
  });

  // --- 検査5（MFA の実効的な強制・監査イベント。#438 / IADR-0294）------------------
  //
  // 🔴 **正例だけでは検出力を測れない。** ここは「不変条件を 1 つずつ壊した変異が、
  // それぞれ 1 件だけ検出されること」を測る（陽性対照）。併せて、壊していない不変条件が
  // 巻き添えで落ちないこと（1 件だけ、という数え）も同時に見ている。
  const mfaOk = {
    realm: AUTH_POLICY_REALM,
    users: [
      { username: 'human', enabled: true, requiredActions: [MFA_REQUIRED_ACTION] },
      { username: 'service-account-x', enabled: true, serviceAccountClientId: 'x' },
    ],
    clients: [{ clientId: 'bff', directAccessGrantsEnabled: false }],
    requiredActions: [{ alias: 'delete_credential', enabled: false }],
    eventsEnabled: true,
    adminEventsEnabled: true,
    adminEventsDetailsEnabled: true,
    eventsListeners: [...AUDIT_REQUIRED_LISTENERS],
    enabledEventTypes: [...AUDIT_REQUIRED_EVENT_TYPES],
  };
  const mutate = (fn) => { const c = JSON.parse(JSON.stringify(mfaOk)); fn(c); return c; };

  cases.push({
    name: 'MFA: 不変条件をすべて満たす realm は 0 件（正例）',
    pass: collectMfaAuditGaps(mfaOk).length === 0,
  });
  cases.push({
    name: 'MFA: 陽性対照 — サービスアカウントは CONFIGURE_TOTP が無くても落とさない',
    // これが落ちると「サービスアカウントにも TOTP を付けろ」と読めてしまい、
    // 直した結果としてトークン取得が壊れる。除外が効いていることを名指しで測る。
    pass: collectMfaAuditGaps(mutate((c) => { c.users = [c.users[1]]; })).length === 0,
  });
  cases.push({
    name: 'MFA: 変異 1 — 対話利用者から CONFIGURE_TOTP を外すと 1 件',
    pass: collectMfaAuditGaps(mutate((c) => { c.users[0].requiredActions = []; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 2 — requiredActions キーごと消しても 1 件（未設定と空配列を同じに扱う）',
    pass: collectMfaAuditGaps(mutate((c) => { delete c.users[0].requiredActions; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 3 — サービスアカウントに CONFIGURE_TOTP が付いたら 1 件',
    pass: collectMfaAuditGaps(mutate((c) => { c.users[1].requiredActions = [MFA_REQUIRED_ACTION]; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 4 — direct access grant を開けると 1 件',
    pass: collectMfaAuditGaps(mutate((c) => { c.clients[0].directAccessGrantsEnabled = true; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 5 — delete_credential を有効へ戻すと 1 件',
    pass: collectMfaAuditGaps(mutate((c) => { c.requiredActions[0].enabled = true; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 6 — eventsEnabled を落とすと 1 件（未設定でも同じ）',
    pass: collectMfaAuditGaps(mutate((c) => { delete c.eventsEnabled; })).length === 1
      && collectMfaAuditGaps(mutate((c) => { c.eventsEnabled = false; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 7 — adminEvents 系を落とすと 2 件',
    pass: collectMfaAuditGaps(mutate((c) => {
      delete c.adminEventsEnabled; delete c.adminEventsDetailsEnabled;
    })).length === 2,
  });
  cases.push({
    name: 'MFA: 変異 8 — eventsListeners を空にすると 1 件（eventsEnabled だけでは外へ出ない）',
    pass: collectMfaAuditGaps(mutate((c) => { c.eventsListeners = []; })).length === 1,
  });
  cases.push({
    name: 'MFA: 変異 9 — 必須イベント種を 1 つ落とすと 1 件',
    pass: collectMfaAuditGaps(mutate((c) => { c.enabledEventTypes = c.enabledEventTypes.slice(1); })).length === 1,
  });
  cases.push({
    name: 'MFA: 無効化された利用者は対象外（ログインできないため）',
    pass: collectMfaAuditGaps(mutate((c) => { c.users[0].enabled = false; c.users[0].requiredActions = []; })).length === 0,
  });
  cases.push({
    name: 'MFA: 別プロジェクトの realm（realm 名が違う）は検査しない',
    pass: collectMfaAuditGaps(mutate((c) => { c.realm = 'other'; c.users[0].requiredActions = []; })).length === 0,
  });
  cases.push({
    name: 'MFA: JSON パース→検査（checkRealmMfaAuditText）が通る',
    pass: checkRealmMfaAuditText(JSON.stringify(mutate((c) => { c.users[0].requiredActions = []; }))).length === 1,
  });
  cases.push({
    name: 'MFA: 実データの realm が不変条件を満たす（実データ・ラチェット）',
    pass: (() => {
      const realmPath = path.join(REPO_ROOT, REALM_DIR, 'microservices-platform-realm.json');
      if (!fs.existsSync(realmPath)) return true; // realm が無い配布物では skip
      const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
      // 0 件走査の門: 対話利用者が 1 人も居ない realm を「違反なし」と読まない。
      const humans = (realm.users || []).filter((u) => !isServiceAccountUser(u));
      if (humans.length === 0) return false;
      return collectMfaAuditGaps(realm).length === 0;
    })(),
  });

  // --- 検査6: サーバ間の口の宛先（#1115）---
  const s2s = (url) => ({ clients: [{ clientId: 'bff', attributes: { 'backchannel.logout.url': url } }] });
  cases.push({
    name: 'S2S: in-cluster の素のサービス名は合格',
    pass: collectServerSideUrlGaps(s2s('http://bff-service:8080/bff/auth/backchannel-logout')).length === 0,
  });
  cases.push({
    name: 'S2S: FQDN（*.svc.cluster.local）も合格',
    pass: collectServerSideUrlGaps(
      s2s('http://bff-service.microservices-platform.svc.cluster.local:8080/bff/auth/backchannel-logout'),
    ).length === 0,
  });
  cases.push({
    name: 'S2S: 実在の公開 host（https://bff.example.com）も合格',
    pass: collectServerSideUrlGaps(s2s('https://bff.example.com/bff/auth/backchannel-logout')).length === 0,
  });
  cases.push({
    name: 'S2S: 裸の localhost を検出する（#1115 の事故そのもの）',
    pass: (() => {
      const g = collectServerSideUrlGaps(s2s('https://localhost/bff/auth/backchannel-logout'));
      return g.length === 1 && /pod 自身/.test(g[0].detail);
    })(),
  });
  cases.push({
    name: 'S2S: 127.0.0.1 / ::1 も同じ理由で検出する',
    pass: collectServerSideUrlGaps(s2s('http://127.0.0.1:8080/x')).length === 1
      && collectServerSideUrlGaps(s2s('http://[::1]:8080/x')).length === 1,
  });
  cases.push({
    name: 'S2S: エッジ host（*.localhost）を検出する',
    pass: (() => {
      const g = collectServerSideUrlGaps(s2s('https://app.localhost/bff/auth/backchannel-logout'));
      return g.length === 1 && /エッジ host/.test(g[0].detail);
    })(),
  });
  cases.push({
    name: 'S2S: 属性を持たない client は検査しない（誤検出しない）',
    pass: collectServerSideUrlGaps({ clients: [{ clientId: 'other' }, { clientId: 'x', attributes: {} }] }).length === 0,
  });
  cases.push({
    name: 'S2S: URL として壊れている値も落とす',
    pass: collectServerSideUrlGaps(s2s('not a url')).length === 1,
  });
  cases.push({
    name: 'S2S: ブラウザ向けの欄（redirectUris）は裸の localhost でも検出しない',
    pass: collectServerSideUrlGaps({
      clients: [{
        clientId: 'bff',
        redirectUris: ['https://localhost/bff/auth/callback'],
        webOrigins: ['https://localhost'],
        attributes: { 'post.logout.redirect.uris': 'https://localhost/*' },
      }],
    }).length === 0,
  });
  cases.push({
    name: 'S2S: JSON パース→検査（checkRealmServerSideUrlsText）が通る',
    pass: checkRealmServerSideUrlsText(JSON.stringify(s2s('https://localhost/x'))).length === 1,
  });
  cases.push({
    name: 'S2S: 実データの realm が不変条件を満たす（実データ・ラチェット／0 件走査の門つき）',
    pass: (() => {
      const realmPath = path.join(REPO_ROOT, REALM_DIR, 'microservices-platform-realm.json');
      if (!fs.existsSync(realmPath)) return true; // realm が無い配布物では skip
      const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
      // 0 件走査の門: サーバ間の口を 1 つも宣言していない realm を「違反なし」と読まない。
      const declared = (realm.clients || []).filter(
        (c) => c.attributes && typeof c.attributes['backchannel.logout.url'] === 'string',
      );
      if (declared.length === 0) return false;
      return collectServerSideUrlGaps(realm).length === 0;
    })(),
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
  const totalThemeGaps = results.reduce((n, r) => n + r.themeGaps.length, 0);
  const totalMfaGaps = results.reduce((n, r) => n + r.mfaGaps.length, 0);
  const totalServerUrlGaps = results.reduce((n, r) => n + r.serverUrlGaps.length, 0);
  if (total === 0 && totalMissing === 0 && totalDeviations === 0 && totalThemeGaps === 0 && totalMfaGaps === 0
      && totalServerUrlGaps === 0) {
    console.log(`[check-realm-constraints] OK: ${files.length} ファイルに ${MAX_LEN} 文字超のフィールド・必須 URL の欠落・ADR-0026 からの逸脱・テーマ参照の齟齬・MFA / 監査イベントの欠落・到達し得ないサーバ間 URL はありません。`);
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
  if (totalThemeGaps > 0) {
    console.error(`[check-realm-constraints] realm が指すテーマの解決に関する齟齬 ${totalThemeGaps} 件を検出しました:`);
    for (const r of results) {
      for (const g of r.themeGaps) {
        console.error(`\n  ${r.file}\n    ${g.path}: ${g.detail}`);
      }
    }
    console.error('\nいずれも realm import は成功し画面も出るため、E2E では気付けません（ブランド適用・言語切替だけが静かに外れます）。'
      + '\nテーマ実装の方針は IADR-0261 決定 1、要件の正は planning の ADR-0026 です。');
  }

  if (totalMfaGaps > 0) {
    console.error(`[check-realm-constraints] MFA の強制・監査イベントに関する欠落 ${totalMfaGaps} 件を検出しました:`);
    for (const r of results) {
      for (const g of r.mfaGaps) {
        console.error(`\n  ${r.file}\n    ${g.path}: ${g.detail}`);
      }
    }
    console.error('\nいずれも realm import は成功し、ログインも画面も動くため E2E では気付けません。'
      + '\n「MFA を必須と定めた」ことと「MFA が働いている」ことは別であり、本検査は後者を見ています。'
      + '\n要件の正は planning の ADR-0026・ADR-0045 決定 9-b、実装側の記録は IADR-0294（#438）です。');
  }

  if (totalServerUrlGaps > 0) {
    console.error(`[check-realm-constraints] pod から到達し得ないサーバ間 URL ${totalServerUrlGaps} 件を検出しました:`);
    for (const r of results) {
      for (const g of r.serverUrlGaps) {
        console.error(`\n  ${r.file}\n    ${g.path}: ${g.detail}`);
      }
    }
    console.error('\nブラウザ向けの URL 群（redirectUris / webOrigins / post.logout.redirect.uris）とは別系統です。'
      + '\nバックチャネルログアウトは**認可サーバが pod の中から叩く**口であり、届かなくても失敗は静かです'
      + '\n（Keycloak 側に KC-SERVICES0057 が 1 行出るだけで、管理者の画面には「ログアウトさせた」と映ります）。'
      + '\n「実際に到達するか」は静的には測れません。本検査は「到達し得ない形」だけを止めています（#1115）。');
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
  collectThemeGaps,
  checkRealmThemeText,
  isServiceAccountUser,
  collectMfaAuditGaps,
  checkRealmMfaAuditText,
  collectServerSideUrlGaps,
  checkRealmServerSideUrlsText,
  SERVER_SIDE_URL_ATTRS,
  satisfiesPasswordClasses,
  MAX_LEN,
  REQUIRED_CLIENT_URLS,
  PASSWORD_CLASS_REGEX,
  AUTH_POLICY_REALM,
  AUTH_POLICY_SCALARS,
  AUTH_POLICY_REQUIRED_ACTIONS,
  AUTH_POLICY_REQUIRED_ACTION_ALIASES,
  THEME_ROOT,
  THEME_FIELDS,
  MFA_REQUIRED_ACTION,
  AUDIT_EVENT_SCALARS,
  AUDIT_REQUIRED_EVENT_TYPES,
  AUDIT_REQUIRED_LISTENERS,
  MFA_DISABLED_REQUIRED_ACTIONS,
};
