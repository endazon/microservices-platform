// NFR (#196): 負荷試験ハーネス共有設定・認証ヘルパー（k6）。
// 環境非依存の準備物。実測はデプロイ済み環境（compose / k3s stg）が用意でき次第、下記 env で実行する。
//
// 必須 env:
//   BASE_URL   … BFF エッジの URL（例 http://localhost:5000）
// 認証（いずれか）:
//   TOKEN      … 事前取得済みの Bearer アクセストークン（最優先）
//   もしくは Keycloak パスワードグラント（dev realm の poc-user 等）:
//     KC_TOKEN_URL  … 例 http://localhost:8080/realms/platform/protocol/openid-connect/token
//     KC_CLIENT_ID  … 既定 platform-spa（public client。direct access grants 有効化が前提）
//     KC_USERNAME / KC_PASSWORD … 例 poc-user / （dev シード）
//
// 秘密情報はスクリプトに埋め込まない（env 経由・コミット禁止。docs/security）。

import http from 'k6/http';
import { fail } from 'k6';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// トークン取得: TOKEN があればそれを、無ければ Keycloak パスワードグラントで取得する。
// セットアップ関数（setup()）で一度だけ呼び、VU へ配布する運用を想定。
//
// 🔴 **パスワードグラントの経路は MFA 必須化（#438 / IADR-0294）で実質使えない。**
// realm の対話利用者（poc-user 等）は `CONFIGURE_TOTP` を必須アクションとして持つため、
// direct access grant が `Account is not fully set up` で拒まれる。**TOKEN を与えて使うこと。**
// 経路自体は残す —— MFA を課さない計測専用クライアントを別途用意する選択肢を閉じないためである。
export function obtainToken() {
  if (__ENV.TOKEN) return __ENV.TOKEN;

  const tokenUrl = __ENV.KC_TOKEN_URL;
  if (!tokenUrl) {
    fail('認証情報がありません。TOKEN もしくは KC_TOKEN_URL/KC_USERNAME/KC_PASSWORD を env で指定してください。');
  }

  const res = http.post(
    tokenUrl,
    {
      grant_type: 'password',
      client_id: __ENV.KC_CLIENT_ID || 'platform-spa',
      username: __ENV.KC_USERNAME || 'poc-user',
      password: __ENV.KC_PASSWORD || '',
    },
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } },
  );

  if (res.status !== 200) {
    // 🔴 MFA を必須にしたため（#438 / IADR-0294）、**パスワードグラントは realm の
    // 対話利用者では通らない**。`CONFIGURE_TOTP` が必須アクションとして残っている利用者は
    // `invalid_grant: Account is not fully set up` になる。ここを「direct access grants の
    // 設定漏れ」とだけ案内すると、原因を取り違えたまま realm 設定を触ることになる。
    // **計測時は TOKEN に取得済みのアクセストークンを与えるのが正である**
    // （`scripts/verify-oidc-edge-flow.sh` が通す認可コード導線などで取る）。
    fail(
      `Keycloak トークン取得に失敗 (status ${res.status})。` +
        'MFA 必須化により realm の対話利用者はパスワードグラントで取得できない（#438）。' +
        'TOKEN に取得済みのアクセストークンを与えるか、MFA を課さない計測専用クライアントを用意すること。',
    );
  }
  return res.json('access_token');
}

// 認証ヘッダを組み立てる。
export function authHeaders(token) {
  return { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } };
}

// 代表的な検索クエリ（合成。機密を含まない）。実測時は対象データに合わせて差し替える。
export const SEARCH_QUERIES = [
  '就業規則 休暇',
  '経費精算 フロー',
  'セキュリティ ポリシー',
  '開発 環境 構築',
  '新入社員 オンボーディング',
];
