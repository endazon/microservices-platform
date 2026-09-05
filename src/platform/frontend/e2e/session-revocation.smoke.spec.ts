import { test, expect } from '@playwright/test';
import type { PlatformUserDto } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// NFR, SC-17, ADR-0026, ADR-0032, IADR-0251, IADR-0273, IADR-0330 (#439):
// **アカウントを無効化した直後、そのセッションで出る次の保護要求が 401 になり、SPA が即座に
// 再認証へ倒れること**を実ブラウザ・実ビルド成果物の上で固定する。
//
// ── なぜ要るのか
//   非機能要件（セキュリティ｜セッション管理）は「アカウント無効化時・退職時の**全セッション
//   即時失効**」を求める。失効の実体（境界層のチケット破棄・バックチャネルログアウトの受理）は
//   `Platform.Bff.Tests` が持ち、稼働クラスタでの疎通は別の作業が実測済みである。
//   **残っていたのはブラウザ側の反応** —— 失効したのに画面が動き続ければ、利用者から見た失効は
//   遅れる。ここを固定していないと、クライアント側のキャッシュ・猶予をひとつ足すだけで
//   「即時」が静かに壊れる。
//
// ── 🔴 この spec が測らないもの（土台の限界。「後段まで固定した」とは書かない）
//   本土台は認可サーバも境界層も起動しない。応答はネットワーク層のスタブであり、
//   **「契約の写しであって後段ではない」**（`support/bffSession.ts`）。したがって
//     - 認可サーバの利用者が実際に `enabled=false` になること
//     - 認可サーバから境界層へバックチャネル通知が届き、サーバ側のセッションが消えること
//   は**ここでは 1 つも測っていない**。測るのは「境界層がそのセッションを honour しなくなった
//   あとの SPA の振る舞い」だけである。実基盤を伴う往復は #439 に残す。
//
// ── 何を「即時」の機械的な意味とするか
//   無効化の要求（`POST /admin/users/{id}/disable`）を境に、**次に出る `/bff/*` が 401 になり、
//   その 401 で再認証への遷移が起きる**こと。観測した呼び出しの並びが
//   `disable → 保護要求 → /auth/login` であることを主張する ——
//   **間に成功する往復が 1 つも無い**のが「次の要求で」である。
//
// ── 陽性対照を同じテストの中に置く
//   無効化の**前に**同じ保護要求（`GET /admin/users`）が通り、表が描かれることを先に主張する。
//   これが無いと「そもそも画面が出ていないだけ」と区別できず、テストは何も証明しない。

/**
 * セッションを持っている当人。**`sessionUser()` の `subject` と同じ id を与える** ——
 * 1 つのブラウザセッションで失効を観測するには、**無効化される利用者がその席に座っている当人**
 * でなければならない（他人を無効化しても自分のセッションは失効しない。当たり前だが、
 * ここを取り違えると「無効化したのに 401 にならない」を仕様だと誤読することになる）。
 */
const self: PlatformUserDto = {
  id: 'e2e-subject',
  username: 'hando.taro',
  displayName: 'ハンドウ タロウ',
  enabled: true,
  roles: ['platform-admin'],
  attributes: { department: '営業部' },
};

const DISABLE_KEY = `POST /admin/users/${self.id}/disable`;

/**
 * 「無効化されるまでは通る／されたら以後すべて 401」を 1 つのフラグで表す土台。
 *
 * 🔴 **フラグを倒すのはテストではなく画面の操作である。** `POST /admin/users/{id}/disable` を
 * 観測したときにだけ倒す —— 実際の境界層で失効を起こすのがまさにこの要求だからであり、
 * テスト側で勝手に倒すと「画面の操作と失効の因果」が消える。
 */
function revocableSession() {
  let revoked = false;
  const gate =
    <T>(body: T) =>
    () =>
      revoked ? reply(401, {}) : body;

  return {
    isRevoked: () => revoked,
    handlers: {
      'GET /auth/me': () => (revoked ? reply(401, {}) : sessionUser(['platform-admin'])),
      'GET /admin/users': gate([self]),
      'GET /admin/users/assignable-roles': gate(['platform-user', 'platform-admin']),
      'GET /admin/authz/attributes': gate([]),
      'GET /notifications': gate([]),
      // 無効化＝全セッション失効。契約上の応答は更新後の利用者（200）である。
      [DISABLE_KEY]: () => {
        revoked = true;
        return { ...self, enabled: false };
      },
      // 401 を受けた SPA が飛ばす再認証の入口。**応答の中身に意味は無い**（本物は認可サーバへの
      // リダイレクトである）。用意しておかないと「応答の用意漏れ」として落ちてしまい、
      // 測りたい並びが読めなくなる。
      'GET /auth/login': {},
    },
  };
}

// NFR, SC-17, ADR-0032 (#439): 無効化 → **次の保護要求が 401** → 即座に再認証へ倒れる。
// 陽性対照（無効化の前に同じ要求が通ること）を同じテストに含める。
test('a disabled account is refused on the very next request through the BFF', async ({ page }) => {
  const session = revocableSession();
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: session.handlers,
  });

  await page.goto('/admin/users');

  // ★ 陽性対照: 失効の前は、**同じ `GET /admin/users`** が通り画面が描かれる。
  await expect(
    page.getByRole('heading', { name: 'ユーザーアカウント管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('cell', { name: 'hando.taro' })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain('GET /admin/users');
  expect(session.isRevoked()).toBe(false);

  // ここから先に出る `/bff/*` だけを見る（前段の往復と混ぜない）。
  const from = traffic.calls.length;

  await page.getByRole('button', { name: '編集' }).click();
  await expect(page.getByRole('heading', { name: /権限編集/ })).toBeVisible();
  await page.getByRole('button', { name: '無効化（全セッション失効）' }).click();

  // 🔴 **これが本 spec の芯である。** 無効化のあと最初に出た保護要求が 401 になり、
  // **その 401 で**再認証への遷移が起きる。並びを完全一致で主張するのは、
  // 「間に成功した往復が 1 つも無い」＝「次の要求で」を言うためである。
  await expect
    .poll(() => traffic.calls.slice(from, from + 3).map((c) => c.key))
    .toEqual([DISABLE_KEY, 'GET /admin/users', 'GET /auth/login']);

  // 再試行で 2 度目の往復を挟んでいない（挟めば「即時」ではなくなる）。
  expect(traffic.calls.slice(from).filter((c) => c.key === 'GET /admin/users')).toHaveLength(1);

  expectBffTrafficIsComplete(traffic);
});

// NFR, SC-17, ADR-0032 (#439): 失効後はセッション Cookie が honour されない ——
// 読み込み直しても未認証として扱われ、保護画面の中身は描かれない。
test('after revocation a reload is treated as unauthenticated and shows no protected content', async ({
  page,
}) => {
  const session = revocableSession();
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: session.handlers,
  });

  await page.goto('/admin/users');

  // ★ 陽性対照: 失効の前は保護画面の中身が見えている。
  await expect(page.getByRole('cell', { name: 'hando.taro' })).toBeVisible();

  await page.getByRole('button', { name: '編集' }).click();
  await page.getByRole('button', { name: '無効化（全セッション失効）' }).click();
  await expect.poll(() => session.isRevoked()).toBe(true);

  // 読み込み直し。**身元の問い合わせからやり直す**ので、Cookie が honour されるかどうかだけが効く。
  await page.goto('/admin/users');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
  // ★ 陰性: 保護画面の中身は 1 つも残らない（キャッシュで描き続けない）。
  await expect(page.getByRole('cell', { name: 'hando.taro' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'ユーザーアカウント管理' })).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});
