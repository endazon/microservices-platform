// NFR (#196): 負荷試験ハーネス共有設定・認証ヘルパー（k6）。
// 環境非依存の準備物。実測はデプロイ済み環境（compose / k3s stg）が用意でき次第、下記 env で実行する。
//
// 必須 env:
//   BASE_URL   … BFF エッジの URL（例 http://localhost:5000）
// 認証（いずれか）:
//   SESSION_COOKIE      … **BFF セッション Cookie の値**（利用者として測るときはこれ。推奨）。
//                         ブラウザで BFF にログインし、開発者ツールの Cookie 一覧から値を写す
//                         （HttpOnly なので JS からは読めない。値は資格情報である —— コミット・ログ禁止）。
//   SESSION_COOKIE_NAME … 既定 `__Host-msp-session`（BFF の `BffSession:CookieName`）
//   CSRF_HEADER         … 既定 `X-MSP-CSRF`（BFF の `BffSession:CsrfHeaderName`）。POST に要る
//   TOKEN               … 事前取得済みの Bearer アクセストークン。**無人の主体（client credentials）に限る**
//
// 🔴 NFR-09, ADR-0032, IADR-0429 (#1535): **BFF は利用者のトークンを Bearer で受理しない**（401）。
//    利用者の資格情報で `/bff/*` に入る口はセッション Cookie だけである。従前は
//    「`verify-oidc-edge-flow.sh` の認可コード導線で取ったトークンを TOKEN に与える」と案内していたが、
//    その形（BFF の client 名義の利用者トークン）は受理しなくなった。Keycloak のパスワードグラントの経路も
//    撤去した —— 得られるのは利用者トークンなので、どの client で取っても `/bff/*` では 401 になる
//    （そもそも realm の全 client で直接付与は無効。`check-realm-constraints.js` 検査 5）。
//
// 秘密情報はスクリプトに埋め込まない（env 経由・コミット禁止。docs/security）。

import { fail } from 'k6';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

const SESSION_COOKIE = __ENV.SESSION_COOKIE || '';
const SESSION_COOKIE_NAME = __ENV.SESSION_COOKIE_NAME || '__Host-msp-session';
const CSRF_HEADER = __ENV.CSRF_HEADER || 'X-MSP-CSRF';

// 資格情報の確認: セットアップ関数（setup()）で一度だけ呼び、VU へ配布する運用を想定。
// セッション Cookie を使うときはトークンを持たないので空文字を返す（authHeaders が Cookie を載せる）。
export function obtainToken() {
  if (SESSION_COOKIE) return '';
  if (__ENV.TOKEN) return __ENV.TOKEN;
  fail(
    '認証情報がありません。SESSION_COOKIE（BFF セッション Cookie の値）を env で指定してください。' +
      'TOKEN は無人の主体のトークンに限る（利用者のトークンは BFF が 401 で拒む）。',
  );
}

// 認証ヘッダを組み立てる。セッション Cookie があれば Cookie ＋ CSRF ヘッダ（状態を変える要求に要る。
// 値は検査されず、存在することに意味がある）、無ければ Bearer（無人の主体）。
export function authHeaders(token) {
  if (SESSION_COOKIE) {
    return {
      headers: {
        Cookie: `${SESSION_COOKIE_NAME}=${SESSION_COOKIE}`,
        [CSRF_HEADER]: '1',
        'Content-Type': 'application/json',
      },
    };
  }
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
