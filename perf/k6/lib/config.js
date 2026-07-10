// NFR (#196): 負荷試験ハーネス共有設定・認証ヘルパー（k6）。
// 環境非依存の準備物。実測はデプロイ済み環境（compose / k3s stg）が用意でき次第、下記 env で実行する。
//
// 必須 env:
//   BASE_URL   … BFF エッジの URL（例 http://localhost:5000）
// 認証（いずれか）:
//   TOKEN      … 事前取得済みの Bearer アクセストークン（最優先）
//   もしくは Keycloak パスワードグラント（dev realm の poc-user 等）:
//     KC_TOKEN_URL  … 例 http://localhost:8080/realms/knowledge-platform/protocol/openid-connect/token
//     KC_CLIENT_ID  … 既定 spa-web（public client。direct access grants 有効化が前提）
//     KC_USERNAME / KC_PASSWORD … 例 poc-user / （dev シード）
//
// 秘密情報はスクリプトに埋め込まない（env 経由・コミット禁止。docs/security）。

import http from 'k6/http';
import { fail } from 'k6';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// トークン取得: TOKEN があればそれを、無ければ Keycloak パスワードグラントで取得する。
// セットアップ関数（setup()）で一度だけ呼び、VU へ配布する運用を想定。
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
      client_id: __ENV.KC_CLIENT_ID || 'spa-web',
      username: __ENV.KC_USERNAME || 'poc-user',
      password: __ENV.KC_PASSWORD || '',
    },
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } },
  );

  if (res.status !== 200) {
    fail(`Keycloak トークン取得に失敗 (status ${res.status})。direct access grants の有効化と資格情報を確認してください。`);
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
