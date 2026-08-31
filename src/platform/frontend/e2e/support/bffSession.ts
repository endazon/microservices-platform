import type { Page, Route } from '@playwright/test';
import type { SessionUser } from '../../src/lib/auth/AuthContext';

// SC-12, SC-17, SC-19, SC-20, UC-11 (#1099) / NFR, ADR-0031, ADR-0032, IADR-0330:
// **ブラウザ E2E でセッションを成立させるための土台。**
//
// ── なぜ成立するのか（推定ではなく実測。#1099 の作業仕様書 §実測）
//   SPA から出る HTTP は `foundation/api/apiClient` の 1 箇所に収束し、宛先は**同一オリジンの
//   相対パス `/bff/*`** である（`bffBaseUrl`。絶対 URL は `assertSameOriginBffBaseUrl` が禁じる）。
//   認証状態の一次情報は **`GET /bff/auth/me` の応答だけ**であり（`AuthProvider`）、
//   ブラウザ側は HttpOnly の Cookie を読まない。したがって**応答を差し替えればセッションは成立する。**
//   Keycloak も BFF も後段サービスも 1 つも要らない。
//
// ── 何を測る土台で、何を測らない土台か
//   🔴 **これは「契約の写し」であって後段ではない。** 応答形が `docs/api/openapi.yaml` と食い違えば、
//   この層は**食い違ったまま緑**になる。食い違いを防いでいるのは、スタブを**生成型
//   （`bff.schemas.ts`）で組む**ことだけである。実応答との一致を固定するのは **#466**
//   （実 BFF ＋ Keycloak）と後段のテストであり、**ここで「後段まで固定した」とは書かない。**
//
// ── 空振りを緑にしない
//   応答を用意していない `/bff/*` の呼び出しは `unhandled` へ積み、**500 を返す**。
//   テストは `expectBffTrafficIsComplete` で「1 件も観測していない」「応答を用意し忘れた」の
//   両方を落とす —— どちらも「何も見ずに緑」になる経路である（`bundle-splitting.smoke.spec.ts`
//   の ① と同じ理由）。

/** `/bff` を除いたパスと、その要求の中身。 */
export interface BffCall {
  /** `GET /private-notes` の形。ハンドラの引き当てキーでもある。 */
  key: string;
  method: string;
  /** `/bff` を除いたパス（例 `/private-notes/n1/exposure`）。 */
  path: string;
  /** クエリ文字列（`?` を含む。無ければ空文字）。 */
  search: string;
  /** JSON 本文（無ければ `undefined`）。 */
  body: unknown;
}

/**
 * 状態コードまで指定したい応答。
 *
 * 🔴 **既定の 200 で済ませられない面がある。** 例えば端末登録は生成フックが
 * `response.status === 201` を見て平文トークンを描くため、200 を返すと**画面は静かに何も出さない**
 * （#1099 で実測した）。契約の状態コードは応答本文と同じく「写すべき値」である。
 */
export interface BffReply {
  readonly __bffReply: true;
  readonly status: number;
  readonly body: unknown;
}

export function reply(status: number, body: unknown): BffReply {
  return { __bffReply: true, status, body };
}

function isReply(value: unknown): value is BffReply {
  return typeof value === 'object' && value !== null && '__bffReply' in value;
}

/**
 * 応答本文（200 で返す）、`reply()`、またはそれらを組み立てる関数。
 *
 * **`unknown` と union にしない。** `unknown` は union を丸ごと飲み込むため、関数を書いたときに
 * 引数へ文脈型が付かず `any` になる（`noImplicitAny` が拾えなくなる）。契約の応答は必ず
 * オブジェクトか配列なので `object` で足りる。
 */
export type BffHandler = ((call: BffCall) => unknown) | object;

export interface BffTraffic {
  /** 観測した `/bff/*` の呼び出し（順序つき）。 */
  readonly calls: BffCall[];
  /** 応答を用意していなかった呼び出しのキー。**空であることをテストが確かめる。** */
  readonly unhandled: string[];
}

export interface BffSessionOptions {
  /**
   * `GET /bff/auth/me` が返す身元。**`null` は 401（＝未認証）** であり、
   * `RequireAuth` が `/login` へ誘導する経路になる。
   */
  user: SessionUser | null;
  /** `METHOD /path`（`/bff` を除く）→ 応答本文。 */
  handlers?: Record<string, BffHandler>;
}

/** テスト内で身元を組み立てるときの既定（`roles` だけを差し替えて使う）。 */
export function sessionUser(roles: string[], name = 'ハンドウ タロウ'): SessionUser {
  return { name, subject: 'e2e-subject', roles };
}

function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body ?? null),
  });
}

/**
 * `/bff/*` をブラウザの手前で受け切る。**`page.goto` より前に呼ぶこと**
 * （身元の取得はアプリの初期ロードで走るため、後から張っても間に合わない）。
 */
export async function installBffSession(
  page: Page,
  options: BffSessionOptions,
): Promise<BffTraffic> {
  const calls: BffCall[] = [];
  const unhandled: string[] = [];
  const handlers = options.handlers ?? {};

  await page.route('**/bff/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.replace(/^\/bff/, '');
    const raw = request.postData();
    const call: BffCall = {
      key: `${request.method()} ${path}`,
      method: request.method(),
      path,
      search: url.search,
      body: raw ? (JSON.parse(raw) as unknown) : undefined,
    };
    calls.push(call);

    const handler = handlers[call.key];
    if (handler !== undefined) {
      const value =
        typeof handler === 'function' ? (handler as (c: BffCall) => unknown)(call) : handler;
      if (isReply(value)) await fulfillJson(route, value.body, value.status);
      else await fulfillJson(route, value);
      return;
    }

    // 身元。**401 は「未認証」という正常な答え**であり、`AuthProvider` は例外にしない。
    if (call.key === 'GET /auth/me') {
      if (options.user === null) {
        await fulfillJson(route, {}, 401);
        return;
      }
      await fulfillJson(route, options.user);
      return;
    }
    // FR-22: 共通シェルが全画面で読むアプリ内通知。画面ごとの関心ではないので既定は空にする
    // （個別に見たい spec は handlers で上書きできる）。
    if (call.key === 'GET /notifications') {
      await fulfillJson(route, []);
      return;
    }

    unhandled.push(call.key);
    await fulfillJson(route, { detail: 'no stub' }, 500);
  });

  return { calls, unhandled };
}

/**
 * 観測が空振りしていないことと、応答の用意漏れが無いことを対で確かめる。
 *
 * 🔴 **どちらか片方だけでは「何も見ずに緑」を区別できない** ——
 * ルーティングが張れていなければ `calls` は空のまま、用意漏れがあれば画面は
 * 「取得できませんでした」を出したまま、いずれも他のアサーションが素通りし得る。
 */
export function expectBffTrafficIsComplete(traffic: BffTraffic): void {
  if (traffic.calls.length === 0) {
    throw new Error('/bff/* の呼び出しを 1 件も観測していません（ルーティングが張れていない）');
  }
  if (traffic.unhandled.length > 0) {
    throw new Error(
      `応答を用意していない /bff 呼び出しがあります: ${traffic.unhandled.join(', ')}`,
    );
  }
}
