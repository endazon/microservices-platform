import { test, expect } from './support/cspGuard';
import type { DriftReportDto, EffectiveConfigDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-11 (#504 / #1139): 構成ビューア（`/admin/config-viewer`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// ドリフト明細・適用履歴・段の描画は Vitest（単体）が引き続き担う。

const effective: EffectiveConfigDto = {
  version: { gitCommit: 'abc1234', appliedAt: '2026-08-01T00:00:00Z', appliedBy: 'ops' },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
  mcpTools: [],
};

const drift: DriftReportDto = { hasDrift: false, checkedAt: '2026-08-02T00:00:00Z', findings: [] };

const handlers = {
  'GET /admin/config': effective,
  'GET /admin/config/drift': drift,
  'GET /admin/config/history': [],
};

test('unauthenticated visit to /admin/config-viewer redirects to /login', async ({ page }) => {
  await page.goto('/admin/config-viewer');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-11: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers,
  });

  await page.goto('/admin/config-viewer');

  // ★ 陽性対照: 画面が描かれ、左ナビ「構成ビューア」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '実効構成', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '構成ビューア' })).toBeVisible();
  // ★ 陽性対照: **0 件は 0 件として言う。** 取得失敗を空表示へ寄せる実装はここで区別される。
  // 見るのは既定で開いている区画 (1) の中身にする（適用履歴の区画は既定で畳まれており、
  // **DOM には在るが不可視**である —— `toBeVisible` で実測した）。
  await expect(page.getByText('イベント接続はありません。')).toBeVisible();

  // ★ 陰性対照: ヘッダの版・ドリフトのバッジは**実効構成が取れたときだけ**出す（IADR-0129 決定 5）。
  // ここでは取れているので出る —— 逆に「取れていないのにバッジだけ残る」実装はこの対で落ちる。
  await expect(page.getByText('abc1234')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-11: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/config-viewer');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '実効構成' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '構成ビューア' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/config');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-098（#2022 / E-17）: ドリフトは契約の値のまま・参照のみ（入力要素 0） ─────────────
//
// 期待値は計画 05_screens §SC-11 から起こした。
// - ドリフトは 5 分類（MissingApply / UndeclaredSubscription / StaleStage / BindingMismatch / Unverifiable）、
//   深刻度は 2 値（Warning / Info）、対象は常に段名。**深刻度と種別は契約が返す値をそのまま表示する（日本語に訳さない）**
//   （2026-08-05 確定・Q29。運用者・管理者向けで、実装との突合を容易にするため）。
// - 入力 / バリデーション: **参照のみ**（本画面から構成は変更しない。変更は Git 経由に限る）。

const KINDS = [
  'MissingApply',
  'UndeclaredSubscription',
  'StaleStage',
  'BindingMismatch',
  'Unverifiable',
] as const;
const driftAll: DriftReportDto = {
  hasDrift: true,
  checkedAt: '2026-10-24T00:00:00Z',
  findings: KINDS.map((kind, i) => ({
    kind,
    severity: i % 2 === 0 ? 'Warning' : 'Info',
    target: `stage-${i + 1}`,
    detail: `${kind} の説明`,
  })),
};

test('M-098: ドリフトの種別 5 値と深刻度 2 値を、契約の値のまま描く', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { ...handlers, 'GET /admin/config/drift': driftAll },
  });

  await page.goto('/admin/config-viewer');
  const rows = page.getByRole('row').filter({ hasText: /stage-\d/ });
  await expect(rows).toHaveCount(5);
  for (const [i, kind] of KINDS.entries()) {
    const row = rows.filter({ hasText: `stage-${i + 1}` });
    // ★ 種別・深刻度は契約の値そのもの（訳さない）。列の順（種別・深刻度・対象・詳細）ごと 1 回で見る
    // （#2038: `nth()` で列を引かない。基準書 §3）。
    await expect(row.getByRole('cell')).toHaveText([
      kind,
      i % 2 === 0 ? 'Warning' : 'Info',
      `stage-${i + 1}`,
      `${kind} の説明`,
    ]);
  }

  expectBffTrafficIsComplete(traffic);
});

test('M-098: 構成ビューアは参照のみで、入力要素が 1 つも無く、書き込みの要求を出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: { ...handlers, 'GET /admin/config/drift': driftAll },
  });

  await page.goto('/admin/config-viewer');
  // 陽性対照: ドリフトの明細まで描けている。
  await expect(page.getByRole('row').filter({ hasText: 'stage-1' })).toBeVisible();
  // ★ 入力要素（テキスト・選択・チェック・ラジオ・数値）が 0 件。
  for (const role of [
    'textbox',
    'combobox',
    'checkbox',
    'radio',
    'spinbutton',
    'switch',
  ] as const) {
    await expect(page.getByRole('main').getByRole(role)).toHaveCount(0);
  }
  await expect(page.getByRole('main').locator('input, select, textarea')).toHaveCount(0);
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
