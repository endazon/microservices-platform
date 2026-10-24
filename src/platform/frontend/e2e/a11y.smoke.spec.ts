import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test, expect, deadKnownViolationKeys } from './support/axe';
import type {
  AttributeValuesResponse,
  ConversionJobDto,
  DashboardSummaryDto,
  DocumentContentDto,
  DocumentDto,
  DocumentListPageDto,
  DocumentShareDto,
  DriftReportDto,
  EdgeTypeCatalogItem,
  EffectiveConfigDto,
  EffectiveToolsView,
  McpClientView,
  PlatformUserDto,
  PrivateNoteDto,
  PrivateNoteListResponse,
  SearchResponse,
  SecretItemGroupDto,
  SecretItemStatusDto,
  SyncConflictDetailDto,
  SyncConflictSummaryDto,
  SyncDeviceDto,
  SyncSettingsDto,
  TagDictionaryResponse,
  UserDepartmentDto,
  UserSummaryDto,
  WikiPageSummary,
  WikiPageView,
  AiSuggestion,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// NFR-30（WCAG 2.2 レベル AA）/ ADR-0031, ADR-0032 (#2015): **実ブラウザ・実ビルド成果物に対する
// アクセシビリティの機械検査**（axe-core。タグは計画 NFR-30 の `wcag2a`・`wcag2aa`・`wcag21a`・
// `wcag21aa`・`wcag22aa` ＋ `best-practice`。束ねる土台は `support/axe.ts`）。
//
// ■ 走査面は **SPA が描く全画面**（SC-01〜12・SC-13 の SPA 側の入口・SC-17〜22）＋共通シェル＋
//   存在秘匿の 404 ＋ **操作で現れる UI**（確認ダイアログ・共有先の listbox・緊急アクセスの
//   ダイアログ・編集パネル・タブ・競合の解決欄）。後ろの群は「畳まれた状態・操作の後でしか描かれない
//   DOM」であり、**通常の画面を何面足しても届かない**——面を足すときはこの軸（状態の種類）で選ぶ。
//   🔴 **Keycloak のテーマが描く画面（二要素認証・パスワード再設定・アカウント設定と、ログインの
//   Keycloak 側）は対象外**である。SPA のビルド成果物に含まれず、本層（基盤を 1 つも起動しない L1）では描けない
//   （作業仕様書 `20261024_2015_axe-all-screens` §判断 3）。
//
// ■ 静的検査（`eslint-plugin-jsx-a11y`）との役割分担
//   ESLint が見るのは **JSX のソースの形**だけで、**描いた結果**は 1 つも見ない。色のコントラスト比・
//   ランドマークの重複・`aria-labelledby` の指し先の実在・見出しの階層は、DOM と算出スタイルが揃って
//   初めて判定できる。**両方が要る。**
//
// ■ 何を測らない検査か（開示）
//   axe が自動で判定できるのは WCAG の一部である。**違反 0 件は適合の証明ではない**（NFR-30）。
//   「見出しが内容を表しているか」「操作順が意味に沿っているか」は人が見る。
//   キーボードだけで到達・退出できるかは `keyboard-navigation.smoke.spec.ts` が実際に踏む。
//
// ■ 各 test は**最も緩い許可ロール**で開く（網羅表 M-017）。ロールが要る画面だけロールを与える。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

// ───────────────────────────── 共有の応答（生成型で組む） ─────────────────────────────

/** 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。 */
const attributeValues: AttributeValuesResponse = { values: [] };

const DOC_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

const doc: DocumentDto = {
  id: DOC_ID,
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: ['tag-1'],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

const tags: TagDictionaryResponse = { tags: [{ id: 'tag-1', name: '経費', usageCount: 1 }] };

const edgeTypes: EdgeTypeCatalogItem[] = [
  { id: 'relates-to', name: '関連する', layer: 'core', isSymmetric: true },
];

const GB = 1024 ** 3;
const DAY = 86_400_000;

function note(overrides: Partial<PrivateNoteDto> = {}): PrivateNoteDto {
  return {
    id: 'note-1',
    title: '設計メモ',
    vaultPath: '設計メモ.md',
    version: 1,
    bytes: 12_288,
    includeInSearch: false,
    includeInGraph: false,
    includeInAi: false,
    deleted: false,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
    visibility: 'private',
    sharedUserCount: 0,
    sharedGroupCount: 0,
    syncState: 'target',
    tags: [],
    ...overrides,
  };
}

const noteList: PrivateNoteListResponse = {
  usage: { usedBytes: Math.round(0.01 * GB), limitBytes: GB, percent: 1 },
  notes: [note()],
};

// ───────────────────────────── 共通シェル・入口・例外 ─────────────────────────────

test('SC-13（SPA の入口）: 未認証で開いたログイン画面に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, { user: null });

  await page.goto('/');
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();

  await expectAxeClean('SC-13 ログイン');

  // ★ 陰性対照: 未認証では身元の問い合わせ以外を呼ばない。
  expect(traffic.calls.map((c) => c.key)).toEqual(['GET /auth/me']);
  expectBffTrafficIsComplete(traffic);
});

test('共通シェル: ヘッダ・左ナビ・パンくず・右レール・通知に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    // 🔴 **管理者で開く。** 左ナビの全項目（利用者・個人・管理・運用）が描かれるのは管理者だけである。
    user: sessionUser(['platform-admin', 'platform-operator']),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  // シェルの畳まれた部分も開いて測る。**閉じたままの器は axe の対象にならない**
  // （`hidden` な部分木は判定されない）ため、開かないと右レールと通知一覧は 1 度も測られない。
  await page.getByRole('button', { name: 'AI チャットを開く' }).click();
  await expect(page.getByRole('complementary', { name: 'AI チャットパネル' })).toBeVisible();
  await page.getByRole('button', { name: /通知（未読/ }).click();
  await expect(page.getByRole('region', { name: '通知一覧' })).toBeVisible();

  await expectAxeClean('共通シェル');

  expectBffTrafficIsComplete(traffic);
});

// #1438 / IADR-0442 決定 3: 存在秘匿の 404 は 9 本の spec が通るが、axe に掛けていたものは無かった。
test('存在秘匿の 404: 権限外で開いた管理画面（NotFound）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    // IADR-0035 / IADR-0009: 管理ロールを与えない利用者で管理画面を開く。
    // `RequireRole` が `NotFound` を**共通シェルの内側**に描く（＝入れ子が起きる面そのもの）。
    user: sessionUser([]),
  });

  await page.goto('/admin/config-viewer');
  await expect(page.getByRole('heading', { name: '見つかりませんでした', level: 1 })).toBeVisible();

  await expectAxeClean('存在秘匿の 404');

  // ★ 陰性対照: 権限外では画面の API を 1 件も呼ばない（呼ぶと応答の有無から存在が読める）。
  expect(traffic.calls.map((c) => c.key)).toEqual(['GET /auth/me', 'GET /notifications']);

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── 利用者の画面 ─────────────────────────────

test('SC-01: 検索・AI質問の画面に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-01 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  await expectAxeClean('SC-01');

  expectBffTrafficIsComplete(traffic);
});

test('SC-02: 検索結果一覧（結果 1 件）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const results: SearchResponse = {
    results: [
      {
        chunkId: 'chunk-1',
        documentId: DOC_ID,
        documentTitle: '経費精算マニュアル',
        text: '領収書を添付して申請する。',
        score: 0.82,
        attributes: { department: 'sales' },
        tags: ['tag-1'],
        updatedAt: '2026-08-02T00:00:00Z',
        hasBody: true,
      },
    ],
    totalHits: 1,
    elapsedMs: 3,
    degraded: false,
    degradedReasons: [],
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /search': results },
  });

  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');
  await expect(page.getByRole('heading', { name: '検索結果一覧', level: 1 })).toBeVisible();
  // 結果が描かれてから測る（空状態と一覧では検査される要素が別物である）。
  await expect(page.getByRole('link', { name: '経費精算マニュアル' })).toBeVisible();

  await expectAxeClean('SC-02');

  expectBffTrafficIsComplete(traffic);
});

test('SC-03: 文書詳細（本文・導線・AI 提案欄）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const content: DocumentContentDto = {
    id: DOC_ID,
    title: '経費精算マニュアル',
    markdown: '## 申請の手順\n\n領収書を添付して申請する。',
  };
  const wikiPage: WikiPageSummary = {
    id: 'page-1',
    documentId: DOC_ID,
    title: '経費精算マニュアル',
    slug: 'keihi-manual',
    wikiPath: `doc/${DOC_ID}`,
    status: 'Active',
    syncedAt: '2026-08-02T00:00:00Z',
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      [`GET /documents/${DOC_ID}`]: doc,
      [`GET /documents/${DOC_ID}/content`]: content,
      [`GET /documents/${DOC_ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': edgeTypes,
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto(`/docs/${DOC_ID}`);
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  await expect(page.getByText('申請の手順')).toBeVisible();

  await expectAxeClean('SC-03');

  expectBffTrafficIsComplete(traffic);
});

test('SC-04: Wiki 閲覧（ページツリーとページ本文）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const pages: WikiPageSummary[] = [
    {
      id: 'page-a',
      documentId: DOC_ID,
      title: '経費精算規程',
      slug: 'keihi-seisan',
      wikiPath: `doc/${DOC_ID}`,
      status: 'Active',
      syncedAt: '2026-09-01T00:00:00Z',
    },
  ];
  const view: WikiPageView = {
    ...pages[0],
    content: '<h2>申請の手順</h2><p><b>領収書</b>を添付して申請する。</p>',
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /wiki/pages': pages, 'GET /wiki/pages/keihi-seisan': view },
  });

  // ページを開いた状態で測る（ツリーだけの面と本文を持つ面では検査される要素が別物である）。
  await page.goto('/wiki?page=keihi-seisan');
  await expect(page.getByRole('heading', { name: 'Wiki 閲覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: '申請の手順' })).toBeVisible();

  await expectAxeClean('SC-04');

  expectBffTrafficIsComplete(traffic);
});

test('SC-08: AI 分析依頼の画面に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/analyze');
  await expect(page.getByRole('heading', { name: 'AI分析依頼', level: 1 })).toBeVisible();

  await expectAxeClean('SC-08');

  expectBffTrafficIsComplete(traffic);
});

test('SC-18: ナレッジグラフの画面に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /graph/edge-types': edgeTypes },
  });

  await page.goto('/graph');
  await expect(page.getByRole('heading', { name: 'ナレッジグラフ', level: 1 })).toBeVisible();
  await expect(page.getByTestId('graph-help')).toBeVisible();

  await expectAxeClean('SC-18');

  expectBffTrafficIsComplete(traffic);
});

test('SC-21: AI 提案一覧（提案 2 件）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const suggestions: AiSuggestion[] = [
    {
      id: 'sug-1',
      kind: 'link',
      sourceDocumentId: DOC_ID,
      targetDocumentId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      edgeTypeId: 'relates-to',
      rationale: '同じ申請様式を参照している',
      state: 'pending',
      rejectedCount: 0,
      sourceDocumentTitle: '経費精算マニュアル',
      targetDocumentTitle: '出張旅費規程',
    },
    {
      id: 'sug-2',
      kind: 'tag',
      sourceDocumentId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      tagValue: '経費',
      rationale: '本文に経費の語が頻出する',
      state: 'pending',
      rejectedCount: 0,
      sourceDocumentTitle: '稟議フロー',
    },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /graph/suggestions': suggestions, 'GET /graph/edge-types': edgeTypes },
  });

  await page.goto('/ai-suggestions?state=pending');
  await expect(page.getByRole('heading', { name: 'AI 提案一覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '文書詳細で確認' })).toHaveCount(2);

  await expectAxeClean('SC-21');

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── 個人の画面（SC-19 / SC-20） ─────────────────────────────

test('SC-19: 個人資料の一覧に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': noteList },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toBeVisible();
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();

  await expectAxeClean('SC-19');

  expectBffTrafficIsComplete(traffic);
});

test('SC-19: 確認ダイアログを開いた状態に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': noteList },
  });

  await page.goto('/my/notes');
  // 役割と表示名だけで引く（testid へ逃げない）。開き方は `keyboard-navigation.smoke.spec.ts` に倣う。
  await page.getByRole('button', { name: '削除する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  // **開き切ってから測る。** 開く途中の DOM を読むと、閉じ込め前の状態が判定される。
  await expect(dialog.getByRole('button', { name: 'やめる' })).toBeFocused();

  await expectAxeClean('SC-19 確認ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。破壊的操作の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('DELETE /private-notes/note-1');

  expectBffTrafficIsComplete(traffic);
});

test('SC-19 (#1445): 共有先ダイアログ（listbox の候補つき）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const shares: DocumentShareDto[] = [
    {
      subjectType: 'user',
      subjectId: 'hanako',
      grantedBy: 'e2e',
      createdAt: '2026-09-02T00:00:00Z',
    },
  ];
  const directory: UserSummaryDto[] = [
    { username: 'jiro', displayName: '次郎 ジロウ', enabled: true },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': noteList,
      'GET /private-notes/note-1/shares': shares,
      'POST /users/resolve': directory,
      'GET /users/lookup': directory,
    },
  });

  await page.goto('/my/notes');
  await page.getByRole('button', { name: '共有先を変更する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  // **開き切ってから測る**（初期フォーカスは検索入力である）。
  await expect(dialog.getByLabel('名前で検索する')).toBeFocused();

  // 🔴 **候補の listbox を出した状態で測る。** 閉じたままだと `aria-required-children` /
  // `aria-required-parent` / `aria-allowed-role` が 1 つも評価されず、この面を足した意味が消える。
  await dialog.getByLabel('名前で検索する').fill('次郎');
  await expect(dialog.getByRole('option')).toHaveCount(1);

  await expectAxeClean('SC-19 共有先ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。台帳を変える要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('POST /private-notes/note-1/shares');

  expectBffTrafficIsComplete(traffic);
});

test('SC-19 (#2046): 緊急アクセスの確認ダイアログ（管理者の候補つき）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const directory: UserSummaryDto[] = [
    { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': noteList,
      'GET /private-notes/note-1/break-glass': { active: null },
      'GET /users/lookup': directory,
      'POST /users/resolve': directory,
    },
  });

  await page.goto('/my/notes');
  await page.getByRole('button', { name: '緊急アクセス（Break-glass）' }).click();
  await page.getByLabel('対象の資料').selectOption('note-1');
  await page.getByRole('button', { name: 'この資料に緊急アクセスを発動する…' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/この操作は監査ログに記録されます。/)).toBeVisible();
  // 候補を出した状態で測る（閉じたままだと候補の要素が 1 つも評価されない）。
  await dialog.getByLabel('閲覧を許す管理者を名前で検索する').fill('ha');
  await expect(dialog.getByRole('button', { name: '花子 ハナコ' })).toBeVisible();

  await expectAxeClean('SC-19 緊急アクセスダイアログ');

  // ★ 陰性対照: 測っただけで降りる。発動の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('POST /private-notes/note-1/break-glass');

  expectBffTrafficIsComplete(traffic);
});

function device(overrides: Partial<SyncDeviceDto> = {}): SyncDeviceDto {
  return {
    id: 'device-1',
    deviceName: '自宅 PC',
    issuedAt: new Date(Date.now() - 3 * DAY).toISOString(),
    expiresAt: new Date(Date.now() + 27 * DAY).toISOString(),
    revoked: false,
    lastSyncAt: new Date(Date.now() - DAY).toISOString(),
    active: true,
    ...overrides,
  };
}

const syncSettings: SyncSettingsDto = {
  targetFolders: [
    { path: '仕事/メモ', noteCount: 12, lastSyncAt: new Date(Date.now() - DAY).toISOString() },
  ],
  updatedAt: new Date(Date.now() - DAY).toISOString(),
};

const syncConflict: SyncConflictSummaryDto = {
  id: 'conflict-1',
  noteId: 'note-1',
  title: '設計メモ',
  vaultPath: '仕事/メモ/設計メモ.md',
  detectedAt: new Date(Date.now() - DAY).toISOString(),
  deviceId: 'device-1',
  deviceName: '会議室の PC',
  localBaseVersion: 3,
  serverVersion: 5,
};

/** SC-20 の 5 区画（端末・同期対象・競合・競合の詳細・履歴）を、中身のある状態で返す。 */
const obsidianHandlers = {
  'GET /private-notes/devices': [device()],
  'GET /private-notes/sync-settings': syncSettings,
  'GET /private-notes/conflicts': [syncConflict],
  'GET /private-notes/conflicts/conflict-1': {
    ...syncConflict,
    localContent: '端末で書いた本文',
    serverContent: 'サーバに保存されている本文',
  } satisfies SyncConflictDetailDto,
  'GET /private-notes/sync-history': [],
};

test('SC-20: Obsidian 連携設定（端末・同期対象・競合の解決欄）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: obsidianHandlers,
  });

  await page.goto('/my/obsidian');
  await expect(page.getByRole('heading', { name: 'Obsidian 連携設定', level: 1 })).toBeVisible();
  await expect(page.getByRole('cell', { name: '自宅 PC' })).toBeVisible();

  // 競合の解決欄は押して初めて描かれる（本文の 2 版と 3 つの選択肢）。
  await page
    .getByRole('region', { name: '同期の競合' })
    .getByRole('button', { name: '本文を見て解決する' })
    .click();
  await expect(
    page.getByRole('region', { name: '競合の解決' }).getByLabel('ローカル版の本文'),
  ).toContainText('端末で書いた本文');

  await expectAxeClean('SC-20');

  expectBffTrafficIsComplete(traffic);
});

test('SC-20: 端末の失効の確認ダイアログを開いた状態に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: obsidianHandlers,
  });

  await page.goto('/my/obsidian');
  await page.getByRole('button', { name: 'この端末を失効する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'やめる' })).toBeFocused();

  await expectAxeClean('SC-20 失効の確認ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。失効の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.method)).not.toContain('DELETE');
  expect(traffic.calls.map((c) => c.method)).not.toContain('POST');

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── 運用者の画面 ─────────────────────────────

test('SC-05: 文書管理の画面（表・フィルタ）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const firstPage: DocumentListPageDto = {
    items: [doc],
    total: 1,
    offset: 0,
    limit: 50,
    unfilteredTotal: 1,
    statusCounts: { published: 1 },
    sourceCounts: { manual: 1 },
    projectCounts: { '(none)': 1 },
    indexStatuses: { [DOC_ID]: 'indexed' },
  };
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 表・操作列は権限のある利用者にしか描かれない（IADR-0039）。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /documents': firstPage, 'GET /tags': tags },
  });

  await page.goto('/admin/documents');
  await expect(page.getByRole('heading', { name: '文書一覧', level: 1 })).toBeVisible();
  // 表が描かれてから測る（空状態と表では検査される要素が別物である）。
  await expect(page.getByRole('cell', { name: '経費精算マニュアル' })).toBeVisible();

  await expectAxeClean('SC-05');

  expectBffTrafficIsComplete(traffic);
});

test('SC-06: データソース（一覧 1 件）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /datasources': [
        {
          id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
          name: '社内 Wiki',
          sourceType: 'wiki',
          connectionUri: 'https://wiki.example.test',
          status: 'active',
          lastSyncedAt: null,
          config: {},
          defaultAttributes: { confidentiality: 'internal' },
          createdAt: '2026-10-01T00:00:00Z',
        },
      ],
    },
  });

  await page.goto('/admin/sources');
  await expect(page.getByRole('heading', { name: 'データソース', level: 1 })).toBeVisible();
  await expect(
    page
      .getByRole('table', { name: '登録済みデータソースの一覧' })
      .getByRole('row')
      .filter({ hasText: '社内 Wiki' }),
  ).toBeVisible();

  await expectAxeClean('SC-06');

  expectBffTrafficIsComplete(traffic);
});

test('SC-07: 変換ジョブ（失敗ジョブ 1 件）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const job: ConversionJobDto = {
    id: 'job-1',
    sourceId: 'source-1',
    sourceType: 'file',
    originalPath: '規程/経費精算.docx',
    status: 'failed',
    error: 'pandoc failed',
    attempts: 3,
    deadLettered: true,
    maxAttempts: 3,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
    diagramsCoded: 0,
    diagramsRetained: 0,
    hasCorrection: false,
    hasBody: true,
  };
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /conversion/jobs': [job] },
  });

  await page.goto('/admin/conversions');
  await expect(
    page.getByRole('heading', { name: '変換ジョブ（pandoc＋LLM）', level: 1 }),
  ).toBeVisible();
  await expect(page.getByText('規程/経費精算.docx')).toBeVisible();

  await expectAxeClean('SC-07');

  expectBffTrafficIsComplete(traffic);
});

test('SC-10: 運用ダッシュボードに WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const summary: DashboardSummaryDto = {
    totalSearches: 0,
    totalAnswers: 0,
    usageTrend: [],
    topSearchTerms: [],
    quality: { up: 0, down: 0, total: 0, satisfactionRate: 0 },
    searchTermMinCount: 3,
  };
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': summary },
  });

  await page.goto('/admin/ops');
  await expect(page.getByRole('heading', { name: '運用ダッシュボード', level: 1 })).toBeVisible();
  await expect(page.getByText('期間内の利用はありません。')).toBeVisible();

  await expectAxeClean('SC-10');

  expectBffTrafficIsComplete(traffic);
});

test('SC-11: 構成ビューアに WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const effective: EffectiveConfigDto = {
    version: { gitCommit: 'abc1234', appliedAt: '2026-08-01T00:00:00Z', appliedBy: 'ops' },
    pipeline: [],
    eventBindings: [],
    ports: [],
    connectors: [],
  };
  const drift: DriftReportDto = {
    hasDrift: false,
    checkedAt: '2026-08-02T00:00:00Z',
    findings: [],
  };
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /admin/config': effective,
      'GET /admin/config/drift': drift,
      'GET /admin/config/history': [],
    },
  });

  await page.goto('/admin/config-viewer');
  await expect(page.getByRole('heading', { name: '実効構成', level: 1 })).toBeVisible();
  await expect(page.getByText('abc1234')).toBeVisible();

  await expectAxeClean('SC-11');

  expectBffTrafficIsComplete(traffic);
});

const secretItem: SecretItemStatusDto = {
  item: 'llm-provider-credentials',
  vaultPath: 'msp/llm-provider-credentials',
  properties: ['anthropic-api-key', 'openai-api-key'],
  status: 'notSet',
  currentVersion: null,
  lastUpdatedAt: null,
  lastUpdatedBy: null,
  supplySource: 'screen',
};

const credentialGroup: SecretItemGroupDto = {
  group: 'datasource-credentials',
  writable: false,
  members: [
    {
      memberId: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      displayName: '社内 Wiki',
      kind: 'wiki',
      vaultPath: 'datasource/3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      propertyDetails: [{ name: 'apiToken', kind: 'value', sensitive: true }],
      status: 'notSet',
      currentVersion: null,
      lastUpdatedAt: null,
      lastUpdatedBy: null,
      supplySource: 'screen',
    },
  ],
};

test('SC-22: 秘密情報・接続設定の管理（項目一覧と資格情報の群）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /secrets': [secretItem],
      'GET /secrets/groups/datasource-credentials': credentialGroup,
    },
  });

  await page.goto('/admin/secrets');
  await expect(
    page.getByRole('heading', { name: '秘密情報・接続設定の管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('table', { name: 'データソースの資格情報の一覧' })).toBeVisible();

  await expectAxeClean('SC-22');

  expectBffTrafficIsComplete(traffic);
});

test('SC-22: 書き込みの確認ダイアログを開いた状態に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /secrets': [secretItem],
      'GET /secrets/groups/datasource-credentials': credentialGroup,
    },
  });

  await page.goto('/admin/secrets');
  await page
    .getByRole('table', { name: '秘密情報の項目の一覧' })
    .getByRole('button', { name: '更新' })
    .click();
  await page.locator('#secret-value').fill('e2e-value');
  await page.locator('#secret-confirmation').fill('e2e-value');
  await page.getByRole('button', { name: 'このプロパティを更新する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'やめる' })).toBeFocused();

  await expectAxeClean('SC-22 書き込みの確認ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。書き込みの要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.method)).not.toContain('PUT');
  expect(traffic.calls.map((c) => c.method)).not.toContain('POST');

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── 管理者の画面 ─────────────────────────────

test('SC-09: ABAC 設定（ポリシー登録の条件つき）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: { 'GET /admin/authz/attributes': [], 'GET /admin/authz/policies': [] },
  });

  await page.goto('/admin/abac');
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）', level: 1 })).toBeVisible();
  // 条件を 1 つ足した状態で測る（条件の一覧は足して初めて描かれる）。
  const form = page.getByRole('form', { name: 'ポリシー登録' });
  await form.getByLabel('対象属性').selectOption('document:owner');
  await form
    .getByLabel('条件の値')
    .selectOption({ label: '動的束縛: 操作する利用者本人（${current_user}）' });
  await form.getByRole('button', { name: '条件を追加' }).click();
  await expect(
    form.getByText('文書 owner = 動的束縛: 操作する利用者本人（${current_user}）'),
  ).toBeVisible();

  await expectAxeClean('SC-09');

  expectBffTrafficIsComplete(traffic);
});

test('SC-09: 辺の型のタブを開いた状態に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
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
  await page.getByRole('tab', { name: '辺の型' }).click();
  await expect(page.getByRole('table', { name: '辺の型辞書の一覧' })).toBeVisible();

  await expectAxeClean('SC-09 辺の型タブ');

  expectBffTrafficIsComplete(traffic);
});

test('SC-12: MCP クライアント登録管理（一覧と許可ツール）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const client: McpClientView = {
    id: 'client-1',
    clientId: 'agent-alpha',
    displayName: '社内エージェント α',
    kind: 'service-account',
    enabled: true,
    attributes: { department: 'sales' },
    egressTier: 'self-hosted',
    registeredAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
  };
  const tools: EffectiveToolsView = {
    version: 3,
    tools: [
      {
        name: 'search_documents',
        service: 'SearchService',
        description: '横断検索',
        requiredScope: 'knowledge.read',
        egressClass: 'internal',
      },
    ],
    drifts: [],
  };
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /admin/mcp-clients': [client],
      'GET /admin/mcp-clients/tools': tools,
      'GET /admin/authz/attributes': [],
    },
  });

  await page.goto('/admin/mcp-clients');
  await expect(
    page.getByRole('heading', { name: 'MCP クライアント登録管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('cell', { name: 'agent-alpha' })).toBeVisible();

  await expectAxeClean('SC-12');

  expectBffTrafficIsComplete(traffic);
});

const platformUser: PlatformUserDto = {
  id: 'user-1',
  username: 'hando.taro',
  displayName: 'ハンドウ タロウ',
  enabled: true,
  roles: ['platform-user'],
  attributes: { department: '営業部' },
};

const usersHandlers = {
  'GET /admin/users': [platformUser],
  'GET /admin/users/assignable-roles': ['platform-user', 'platform-admin'],
  'GET /admin/authz/attributes': [],
  // #1610: 編集パネルは部門の割当を別に引く。
  'GET /admin/users/user-1/department': {
    departmentGroups: ['営業部'],
    departmentAttribute: '営業部',
    choices: ['営業部', '開発部'],
  } satisfies UserDepartmentDto,
};

test('SC-17: ユーザー管理（一覧と編集パネル）に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: usersHandlers,
  });

  await page.goto('/admin/users');
  await expect(
    page.getByRole('heading', { name: 'ユーザーアカウント管理', level: 1 }),
  ).toBeVisible();
  // 編集パネルは押して初めて描かれる（ロール・属性・部門の割当欄）。
  await page.getByRole('button', { name: '編集' }).click();
  await expect(page.getByRole('button', { name: '無効化（全セッション失効）' })).toBeVisible();

  await expectAxeClean('SC-17');

  expectBffTrafficIsComplete(traffic);
});

test('SC-17: 無効化の確認ダイアログを開いた状態に WCAG 2.2 A/AA ＋ best-practice の違反が無い', async ({
  page,
  expectAxeClean,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: usersHandlers,
  });

  await page.goto('/admin/users');
  await page.getByRole('button', { name: '編集' }).click();
  await page.getByRole('button', { name: '無効化（全セッション失効）' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'やめる' })).toBeFocused();

  await expectAxeClean('SC-17 無効化の確認ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。無効化の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.method)).not.toContain('POST');

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── 台帳の整合 ─────────────────────────────

// #2015: 既知の違反の台帳のキーは、本 spec が走査する「面 / テーマ」のどれかでなければならない。
// 綴り違い・改名で残ったキーは一度も比べられず、黙って死に行になる（`deadKnownViolationKeys` の注記）。
test('既知の違反の台帳に、どの走査にも当たらないキーが無い', () => {
  const source = readFileSync(fileURLToPath(import.meta.url), 'utf8');
  // ★ 陽性対照: 面名を本文から拾えていること（拾えなければ全キーが死に行と判定され、0 件の台帳では空振りする）。
  // 閾値は現在の面の数（28）である。**面を足したら上げる**（下げると、面名を拾い損ねた回を見逃す）。
  expect(source.match(/expectAxeClean\('/g)?.length ?? 0).toBeGreaterThanOrEqual(28);
  expect(deadKnownViolationKeys(source)).toEqual([]);
});
