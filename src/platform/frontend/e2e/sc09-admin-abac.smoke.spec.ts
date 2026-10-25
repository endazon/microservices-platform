import { test, expect } from './support/cspGuard';
import type { Page } from '@playwright/test';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-09 (#504 / #1139): 管理者設定（ABAC）（`/admin/abac`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin 限定である**（05_screens §SC-09。**運用者も不可**）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// 🔴 **陰性対照に「ロール無し」ではなく運用者を使う。** SC-05〜SC-07 / SC-10 / SC-11 は
// 運用者にも開くため、`anyOf` へ operator が紛れ込んでも「ロール無し」では落ちない ——
// **区別できるのは運用者を当てたときだけ**である。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 属性辞書・ポリシー定義・検証結果は Vitest（単体）が引き続き担う。

const handlers = { 'GET /admin/authz/attributes': [], 'GET /admin/authz/policies': [] };

test('unauthenticated visit to /admin/abac redirects to /login', async ({ page }) => {
  await page.goto('/admin/abac');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-09: a platform administrator reaches the screen and its navigation entry', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers,
  });

  await page.goto('/admin/abac');

  // ★ 陽性対照: 画面が描かれ、左ナビ「ABAC設定」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'ABAC設定' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

// SC-09, FR-17, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): **辺の型辞書の区画を実ブラウザで固定する。**
//
// 計画 §SC-09 は 4 区画を定めており、**辺の型だけが長く欠けていた**（前提 ADR の保留が
// 2026-08-07 に解けたのに、解除を実行する主体が居なかった）。**区画の実在をここで固定する** ——
// 単体テストは合成したツリーを見るが、こちらは**実ビルド成果物とルータの上で**見る。
//
// 🔴 **画面が引くのは `/bff/edge-types`（使用件数つき・admin ＋ operator）であって、
// SC-03 / SC-18 / SC-21 が使う `/bff/graph/edge-types`（描画用カタログ・認証のみ・件数なし）ではない。**
// 後者しか用意しないので、**取り違えた実装は `unhandled` に載って落ちる**
// （`expectBffTrafficIsComplete` が担保する）。
test('SC-09: the edge-type dictionary tab reads the admin dictionary, not the drawing catalog', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /admin/authz/attributes': [],
      'GET /admin/authz/policies': [],
      'GET /edge-types': [
        {
          id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
          name: 'cites',
          layer: 'core',
          isSymmetric: false,
          isSeed: true,
          usageCount: 342,
        },
      ],
    },
  });

  await page.goto('/admin/abac');
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）', level: 1 })).toBeVisible();

  // ★ 陽性対照: 4 区画のうち「辺の型」が在る（＝下の主張が「画面が描けていないだけ」ではない）。
  await page.getByRole('tab', { name: '辺の型' }).click();

  await expect(page.getByRole('table', { name: '辺の型辞書の一覧' })).toBeVisible();
  await expect(page.getByText('cites')).toBeVisible();
  // **使用件数が届く。** カタログ（件数なし）へ向いていればここで落ちる。
  await expect(page.getByRole('cell', { name: '342' })).toBeVisible();

  // ★ 陰性対照: 契約の無い「逆向きの表示語」の列は作っていない（#1241 / [[IADR-0387]] 決定 5）。
  await expect(page.getByRole('columnheader', { name: '逆向きの表示語' })).toHaveCount(0);

  // 取り違えの検査: 描画用カタログは 1 度も呼ばれていない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /graph/edge-types');

  expectBffTrafficIsComplete(traffic);
});

// FR-05, SC-09, ADR-0121 決定 1・ADR-0036 D-02 (#1666): **所有者の read ポリシーを画面の操作だけで作る。**
// 属性辞書は空（owner は辞書に載らない）。実ビルド成果物の上で、選択 → 条件の追加 → 保存の順に押し、
// BFF へ届いた本文が ADR-0121 決定 1 の形そのものであることを見る。
test('SC-09: an administrator creates the owner read policy by selection only', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...handlers,
      'POST /admin/authz/policies': reply(201, {
        id: 'cccccccc-dddd-eeee-ffff-000000000000',
        name: '所有者は自分の文書を読める',
        action: 'read',
        userConditions: {},
        documentConditions: { owner: ['${current_user}'] },
        isActive: true,
      }),
    },
  });

  await page.goto('/admin/abac');
  const form = page.getByRole('form', { name: 'ポリシー登録' });
  await expect(form).toBeVisible();

  await form.getByLabel('名前（必須）').fill('所有者は自分の文書を読める');
  await form.getByLabel('対象属性').selectOption('document:owner');
  // 値は選択だけ。束縛は「動的束縛」の文言で示される（色だけにしない）。
  await form
    .getByLabel('条件の値')
    .selectOption({ label: '動的束縛: 操作する利用者本人（${current_user}）' });
  await form.getByRole('button', { name: '条件を追加' }).click();
  await expect(
    form.getByText('文書 owner = 動的束縛: 操作する利用者本人（${current_user}）'),
  ).toBeVisible();
  await form.getByRole('button', { name: '保存' }).click();

  await expect(
    page.getByText('ポリシーを保存しました。認可判定へ即時反映されます。'),
  ).toBeVisible();
  const posted = traffic.calls.filter((c) => c.key === 'POST /admin/authz/policies');
  expect(posted.map((c) => c.body)).toEqual([
    {
      name: '所有者は自分の文書を読める',
      action: 'read',
      userConditions: {},
      documentConditions: { owner: ['${current_user}'] },
    },
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('SC-09: an operator gets the same not-found page and never learns the screen exists', async ({
  page,
}) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser(['platform-operator']) });

  await page.goto('/admin/abac');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: 'ABAC設定' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/authz/policies');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-088・M-089（#2020 / E-15）: ポリシーの多キー拒否・dry-run 検証・辞書の削除拒否と重複 ─────────────
//
// 期待値は計画 05_screens §SC-09 から起こした。
// - ポリシー定義のバリデーション（2026-08-23 確定）: **文書条件に 2 つ以上の属性キーを持つポリシーの保存を拒否する。**
//   判定は後段の検証（保存と dry-run が同じ検証を通る）。画面は拒否の理由を示し、保存できたと告げない。
// - dry-run の検証 API（2026-08-05 確定）: 保存せず検証だけを行い、結果を示す。
// - 辺の型・タグの削除: **使用件数が 0 件のときだけ実行できる。** 1 件以上なら拒否し「N 件で使われている」と件数を示す。
// - 辺の型の追加: 型名は既存値と重複しない（契約は 409）。
//
// 🔴 スタブは契約の写しである。多キーの判定そのもの（どの形を拒むか）は AuthorizationService の単体テストが固定する。
// ここで固定するのは「画面が組み立てた 2 キーの条件がそのまま送られ、拒否の理由が画面に届く」経路である。

const DOC_ATTRIBUTES = [
  {
    id: '11111111-1111-4111-8111-111111111111',
    key: 'confidentiality',
    label: '機密区分',
    allowedValues: ['public', 'internal'],
    required: true,
    scope: 'document',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  },
  {
    id: '22222222-2222-4222-8222-222222222222',
    key: 'project',
    label: 'プロジェクト',
    allowedValues: ['alpha'],
    required: false,
    scope: 'document',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  },
];
const MULTI_KEY_MESSAGE =
  '文書の条件は 1 つの属性キーに限ります（2 つ以上のキーを持つポリシーは保存できません）。';

async function addCondition(
  form: ReturnType<Page['getByRole']>,
  attributeId: string,
  value: string,
): Promise<void> {
  await form.getByLabel('対象属性').selectOption(attributeId);
  await form.getByLabel('条件の値').selectOption(value);
  await form.getByRole('button', { name: '条件を追加' }).click();
}

test('M-089: 検証は保存せずに結果を示し、1 キーの条件なら「矛盾なし」で保存の要求を出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /admin/authz/attributes': DOC_ATTRIBUTES,
      'GET /admin/authz/policies': [],
      'POST /admin/authz/policies/validate': { valid: true, errors: [] },
    },
  });

  await page.goto('/admin/abac');
  await page.getByRole('tab', { name: 'ポリシー定義' }).click();
  const form = page.getByRole('form', { name: 'ポリシー登録' });
  await form.getByLabel('名前（必須）').fill('社内限までを読める');
  await addCondition(form, 'document:confidentiality', 'internal');
  await form.getByRole('button', { name: '検証' }).click();

  const result = page.getByLabel('検証結果');
  await expect(result.getByText('矛盾なし')).toBeVisible();
  await expect(result.getByText('矛盾はありません。まだ保存していません。')).toBeVisible();
  // ★ 検証は保存しない: 送ったのは dry-run だけで、本文は保存と同じ組み立てである。
  const writes = traffic.calls.filter((c) => c.method !== 'GET');
  expect(writes.map((c) => c.key)).toEqual(['POST /admin/authz/policies/validate']);
  expect(writes[0]?.body).toMatchObject({
    name: '社内限までを読める',
    action: 'read',
    documentConditions: { confidentiality: ['internal'] },
  });

  expectBffTrafficIsComplete(traffic);
});

test('M-088: 文書条件が 2 キーのポリシーは、検証で「矛盾あり」と理由を示し、保存は拒否されて成功を告げない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /admin/authz/attributes': DOC_ATTRIBUTES,
      'GET /admin/authz/policies': [],
      'POST /admin/authz/policies/validate': { valid: false, errors: [MULTI_KEY_MESSAGE] },
      'POST /admin/authz/policies': reply(400, {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { errors: [MULTI_KEY_MESSAGE] },
      }),
    },
  });

  await page.goto('/admin/abac');
  await page.getByRole('tab', { name: 'ポリシー定義' }).click();
  const form = page.getByRole('form', { name: 'ポリシー登録' });
  await form.getByLabel('名前（必須）').fill('alpha の社内限');
  await addCondition(form, 'document:confidentiality', 'internal');
  await addCondition(form, 'document:project', 'alpha');

  // ★ 検証: 矛盾ありと、その理由（後段の文言）を示す。
  await form.getByRole('button', { name: '検証' }).click();
  const result = page.getByLabel('検証結果');
  await expect(result.getByText('矛盾あり')).toBeVisible();
  await expect(result).toContainText(MULTI_KEY_MESSAGE);

  // ★ 保存: 400 で拒否され、理由を示す。保存できたとは告げない。
  await form.getByRole('button', { name: '保存' }).click();
  await expect(page.getByRole('alert').filter({ hasText: MULTI_KEY_MESSAGE })).toBeVisible();
  await expect(page.getByText('ポリシーを保存しました。認可判定へ即時反映されます。')).toHaveCount(
    0,
  );

  // 画面は 2 キーの条件をそのまま送った（画面で黙って 1 キーへ削っていない）。
  const posted = traffic.calls.filter((c) => c.key === 'POST /admin/authz/policies');
  expect(posted).toHaveLength(1);
  expect(posted[0]?.body).toMatchObject({
    documentConditions: { confidentiality: ['internal'], project: ['alpha'] },
  });

  expectBffTrafficIsComplete(traffic);
});

test('M-088: 使用中のタグの削除は拒否され、使用件数を示し、タグは辞書に残る', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...handlers,
      'GET /tags': {
        tags: [
          { id: '33333333-3333-4333-8333-333333333333', name: '経費', usageCount: 3 },
          { id: '44444444-4444-4444-8444-444444444444', name: '未使用', usageCount: 0 },
        ],
      },
      'DELETE /tags/33333333-3333-4333-8333-333333333333': reply(409, {
        error: 'tag_in_use',
        message: 'tag is in use',
        usageCount: 3,
      }),
    },
  });

  await page.goto('/admin/abac');
  await page.getByRole('tab', { name: 'タグ辞書' }).click();
  const table = page.getByRole('table', { name: 'タグ辞書の一覧' });
  // ★ 削除の前から使用件数が見えている（陽性対照を兼ねる）。
  const row = table.getByRole('row').filter({ hasText: '経費' });
  await expect(row.getByRole('cell', { name: '3', exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'タグを削除: 経費' }).click();
  await expect(
    page
      .getByRole('alert')
      .filter({ hasText: 'このタグは 3 件の文書で使われているため削除できません。' }),
  ).toBeVisible();
  await expect(row).toBeVisible();
  expect(traffic.calls.filter((c) => c.method === 'DELETE').map((c) => c.key)).toEqual([
    'DELETE /tags/33333333-3333-4333-8333-333333333333',
  ]);

  expectBffTrafficIsComplete(traffic);
});

const EDGE_TYPE = {
  id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  name: 'cites',
  layer: 'core',
  isSymmetric: false,
  isSeed: true,
  usageCount: 342,
};

test('M-088: 使用中の辺の型の削除は拒否され、使用件数を示す', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...handlers,
      'GET /edge-types': [EDGE_TYPE],
      [`DELETE /edge-types/${EDGE_TYPE.id}`]: reply(409, {
        error: 'edge_type_in_use',
        message: 'edge type is in use',
        usageCount: 342,
      }),
    },
  });

  await page.goto('/admin/abac');
  await page.getByRole('tab', { name: '辺の型' }).click();
  const table = page.getByRole('table', { name: '辺の型辞書の一覧' });
  await expect(table.getByRole('cell', { name: '342' })).toBeVisible();

  await page.getByRole('button', { name: '辺の型を削除: cites' }).click();
  await expect(
    page
      .getByRole('alert')
      .filter({ hasText: 'この辺の型は 342 本の辺で使われているため削除できません。' }),
  ).toBeVisible();
  await expect(table.getByText('cites')).toBeVisible();
  expect(traffic.calls.filter((c) => c.method === 'DELETE').map((c) => c.key)).toEqual([
    `DELETE /edge-types/${EDGE_TYPE.id}`,
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-088: 既存と同じ名前の辺の型の追加は 409 で拒否され、辞書は増えない', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...handlers,
      'GET /edge-types': [EDGE_TYPE],
      'POST /edge-types': reply(409, {
        title: 'Conflict',
        status: 409,
        detail: '同名の辺の型が既に辞書にあります: cites',
      }),
    },
  });

  await page.goto('/admin/abac');
  await page.getByRole('tab', { name: '辺の型' }).click();
  const table = page.getByRole('table', { name: '辺の型辞書の一覧' });
  await expect(table.getByText('cites')).toBeVisible();

  await page.getByLabel('型名（必須）').fill(' cites ');
  await page.getByRole('button', { name: '追加', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  // ★ 一覧は 1 行のまま（見出し行 ＋ 1 行）。成功を装って行を足していない。
  await expect(table.getByRole('row')).toHaveCount(2);
  expect(traffic.calls.filter((c) => c.key === 'POST /edge-types')).toHaveLength(1);

  expectBffTrafficIsComplete(traffic);
});
