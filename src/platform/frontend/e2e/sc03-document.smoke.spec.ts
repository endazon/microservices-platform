import { test, expect } from './support/cspGuard';
import type {
  AiSuggestion,
  DocumentContentDto,
  DocumentDto,
  DocumentVersionDto,
  PrivateNoteListResponse,
  UserSummaryDto,
  WikiPageSummary,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-03 (#502 / #1139): 文書詳細／プレビュー（`/docs/:id`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// **本画面は動的セグメント（`$id`）を持つため、取り違えの余地はいっそう大きい** ——
// ルートの実在とパラメータの受け渡しは、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 版・タグ・AI 提案パネルの中身は Vitest（単体）が引き続き担う。

const ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

const doc: DocumentDto = {
  id: ID,
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: [],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

// 🔴 **本文には Markdown の見出しと生の HTML を両方入れる。** 画面は Markdown レンダラを持たず、
// 原文を等幅・改行保持で**そのまま**出す設計であり、下の陰性対照がそれを固定する。
const content: DocumentContentDto = {
  id: ID,
  title: '経費精算マニュアル',
  markdown: '## 申請の手順\n\n<b>領収書</b>を添付して申請する。',
};

// #1200 / UC-07: 「Wiki で閲覧」は権限内の Wiki 台帳にこの文書が載っているときだけ出る。
const wikiPage: WikiPageSummary = {
  id: 'page-1',
  documentId: ID,
  title: '経費精算マニュアル',
  slug: 'keihi-manual',
  wikiPath: `doc/${ID}`,
  status: 'Active',
  syncedAt: '2026-08-02T00:00:00Z',
};

test('unauthenticated visit to /docs/:id redirects to /login', async ({ page }) => {
  await page.goto(`/docs/${ID}`);

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-03: the id in the path reaches the fetch, and the body renders as normalized Markdown', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-03 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    // 🔴 本文が届くと**続けて**版履歴と AI 提案パネルが走る（本文が 5xx の間は走らない）。
    // 用意し忘れると `expectBffTrafficIsComplete` が落とす —— 実際にこの 3 件で落ちて気づいた。
    handlers: {
      [`GET /documents/${ID}`]: doc,
      [`GET /documents/${ID}/content`]: content,
      [`GET /documents/${ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': [],
      // #1200: 本文の下の「Wiki で閲覧」が引く権限内の Wiki 台帳（用意し忘れると `unhandled` で落ちる）。
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto(`/docs/${ID}`);

  // ★ 陽性対照: 見出しは**文書の題名**である（静的な画面名ではない）。
  // これが出るのは、**パスの `$id` が取得へ渡り、応答が描画へ届いた**ときだけである。
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  await expect(page.getByText('正規化文書（Markdown）プレビュー')).toBeVisible();
  // 本文が届いている（`GET /documents/:id/content` の応答が描画へ繋がっている）。
  await expect(page.getByText('申請の手順')).toBeVisible();

  // ★ 陰性対照 1: **Markdown レンダラを置かない。** 原文を等幅・改行保持でそのまま出す設計であり、
  // `##` は見出しにならず、`<b>` はタグとして解釈されない —— **本文由来の HTML を描かない**という
  // 安全側の性質そのものである。レンダラを足すとここが落ちる。
  await expect(page.getByRole('heading', { name: '申請の手順' })).toHaveCount(0);
  await expect(page.locator('pre')).toContainText('## 申請の手順');
  await expect(page.locator('pre b')).toHaveCount(0);

  // ★ FR-06, SC-03 (#2121): 版履歴 0 件は**空として**描く（失敗と区別する）。従前は欄ごと消えていた。
  await expect(page.getByRole('heading', { name: 'バージョン', level: 2 })).toBeVisible();
  await expect(page.getByText('版履歴はまだありません。')).toBeVisible();

  // ★ 陰性対照 2: 取得は**パスの id で**行う。取り違え（別 id・空 id）はここで落ちる。
  expect(traffic.calls.map((c) => c.key)).toContain(`GET /documents/${ID}`);

  // ★ 陽性対照（#1200 / UC-07）: 台帳に載る文書なので「Wiki で閲覧」が SC-04 の**文書別ディープリンク**へ出る。
  await expect(page.getByRole('link', { name: 'Wikiで閲覧' })).toHaveAttribute(
    'href',
    `/wiki?doc=${ID}`,
  );

  // ★ 陽性対照（#1240 / FR-17 / UC-10）: SC-18（ナレッジグラフ）への導線が**起点つきで**出る。
  // 05_screens §SC-18 は「起点ありの近傍探索が主用途」であり、`root` の無い `/graph` は
  // 照会せず案内文を出す。**`to` だけを見ると「押しても何も見えない導線」が緑になる**ので href まで見る。
  await expect(page.getByRole('link', { name: 'ナレッジグラフで見る' })).toHaveAttribute(
    'href',
    `/graph?root=${ID}&hops=2&by=distance`,
  );

  expectBffTrafficIsComplete(traffic);
});

// SC-03 / SC-04, FR-17, UC-10 (#1240 / #449): **配置規約を実ブラウザで固定する。**
//
// 05_screens §SC-03「知識グラフ」（2026-08-02 の利用者裁定）は 2 つを同時に定めている。
//   (a) **SC-03 に置くのは SC-18 への導線と AI 提案の承認欄の 2 つのみ**である。
//   (b) **バックリンク欄・ローカルグラフは SC-04 のみ**に置き、**SC-03 には併置しない**。
//
// 🔴 **この非対称は意図である。** 本 spec が固定するのは **(b) のうち SC-03 側の不在だけ**であり、
// **「SC-04 に在ること」は固定しない** —— `docs/screens/SC-04_wiki-access.md` §未決事項 2 のとおり
// バックリンク欄・ローカルグラフの実現方式は計画側で未確定である。**未確定のものを E2E で
// 固定すると、計画が決めたときに実装ではなくテストが先に決めたことになる。**
//
// **併置は恒久の禁止ではない。** SC-04 側の実現性が確認できた時点で改めて判断する取り決めなので、
// **足すときにこのテストが落ちて気づける**形にしておく（Vitest 側 `DocumentDetailPage.test.tsx` と対）。
test('SC-03: the knowledge-graph entry is here, but the backlink panel and local graph are not', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      [`GET /documents/${ID}`]: doc,
      [`GET /documents/${ID}/content`]: content,
      [`GET /documents/${ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': [],
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto(`/docs/${ID}`);
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();

  // ★ 陽性対照: 置くと定められた 2 つのうち、導線は在る（＝下の不在アサーションが
  // 「画面がまだ描けていないだけ」で緑になっていないことの担保である）。
  await expect(page.getByRole('link', { name: 'ナレッジグラフで見る' })).toBeVisible();

  // ★ 陰性対照 1: バックリンク欄は SC-03 に無い。計画が定める 2 欄の見出しも含めて見る。
  await expect(page.getByText(/バックリンク/)).toHaveCount(0);
  await expect(page.getByText(/参照している文書/)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: /バックリンク|被参照|参照元/ })).toHaveCount(0);

  // ★ 陰性対照 2: ローカルグラフ（この文書を中心とした 1〜2 ホップの描画）も SC-03 に無い。
  // **語だけでなく描画面そのものを見る** —— SC-18 の面は canvas を敷くので、
  // 名前を変えて埋め込まれても捕まる。
  await expect(page.getByText(/ローカルグラフ/)).toHaveCount(0);
  await expect(page.locator('canvas')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-039〜M-043（#2018 / E-13）: AI 提案の承認欄・個人資料・版履歴・未知パス ─────────────
//
// 期待値は計画 05_screens §SC-03（「AI 提案の承認欄」「個人資料の表示」と ADR-0102 の描き分け・
// 版履歴は一覧のみで復元を置かない・`/docs/` は未知パスとして 404）から起こした。

/** SC-03 を描くのに要る応答の一式（提案・版・文書は差し替えて使う）。 */
function detailHandlers(overrides: Record<string, object | ((call: never) => unknown)> = {}) {
  return {
    [`GET /documents/${ID}`]: doc,
    [`GET /documents/${ID}/content`]: content,
    [`GET /documents/${ID}/versions`]: [],
    'GET /graph/suggestions': [],
    'GET /graph/edge-types': [],
    'GET /wiki/pages': [wikiPage],
    ...overrides,
  };
}

function suggestion(overrides: Partial<AiSuggestion>): AiSuggestion {
  return {
    id: 'sg-1',
    kind: 'tag',
    sourceDocumentId: ID,
    tagValue: '経理',
    rationale: '本文に経費精算の手順が繰り返し現れる。',
    state: 'pending',
    rejectedCount: 0,
    reinstatedReason: null,
    sourceDocumentTitle: '経費精算マニュアル',
    targetDocumentTitle: null,
    canDecide: true,
    ...overrides,
  };
}

test('M-039: AI 提案が 0 件なら欄ごと出さず、提案があれば承認で 1 件の承認を送って欄から消す', async ({
  page,
}) => {
  let pending: AiSuggestion[] = [suggestion({})];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: detailHandlers({
      'GET /graph/suggestions': () => pending,
      'POST /graph/suggestions/sg-1/approve': () => {
        pending = [];
        return { ...suggestion({}), state: 'approved' };
      },
    }),
  });

  await page.goto(`/docs/${ID}`);
  const heading = page.getByRole('heading', { name: 'AI 提案', level: 2 });
  await expect(heading).toBeVisible();
  // 各提案に 種類・相手（タグ）・根拠 を示す（05_screens §SC-03「AI 提案の承認欄」）。
  await expect(page.getByText('「経理」を付与')).toBeVisible();
  await expect(page.getByText('本文に経費精算の手順が繰り返し現れる。')).toBeVisible();

  await page.getByRole('button', { name: '承認', exact: true }).click();

  // ★ 承認後に 0 件になったので、見出しごと欄が消える（見出しだけを残さない）。
  await expect(heading).toHaveCount(0);
  // ★ 1 件ずつ送る（一括の口は無い）。却下は送らない。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys.filter((k) => k.startsWith('POST /graph/suggestions/'))).toEqual([
    'POST /graph/suggestions/sg-1/approve',
  ]);
  // 当該文書の承認待ちだけを引く（SC-03 の既定表示は pending）。
  const list = traffic.calls.find((c) => c.key === 'GET /graph/suggestions');
  expect(new URLSearchParams(list?.search)).toEqual(
    new URLSearchParams({ state: 'pending', documentId: ID }),
  );

  expectBffTrafficIsComplete(traffic);
});

test('M-039: 提案が 0 件の文書では「AI 提案」の見出しも承認・却下のボタンも出ない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: detailHandlers(),
  });

  await page.goto(`/docs/${ID}`);
  // 陽性対照: 画面は描けている（下の 0 件が「描けていない」でないこと）。
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  await expect(page.getByText('申請の手順')).toBeVisible();

  await expect(page.getByRole('heading', { name: 'AI 提案' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '承認', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '却下', exact: true })).toHaveCount(0);
  expect(traffic.calls.map((c) => c.key)).toContain('GET /graph/suggestions');

  expectBffTrafficIsComplete(traffic);
});

test('M-040: タグ提案を承認する資格が無ければ「権限が無い」と示して承認・却下を塞ぎ、資格があれば塞がず「準備中」も出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: detailHandlers({
      'GET /graph/suggestions': [
        suggestion({ id: 'sg-no', tagValue: '経理', canDecide: false }),
        suggestion({ id: 'sg-ok', tagValue: '規程', canDecide: true }),
      ],
    }),
  });

  await page.goto(`/docs/${ID}`);
  const rows = page.getByRole('main').getByRole('listitem');
  const denied = rows.filter({ hasText: '「経理」を付与' });
  const allowed = rows.filter({ hasText: '「規程」を付与' });

  // ★ 資格なし: 恒久の理由「この文書のタグを編集する権限がありません。」。承認と却下は同じ権限に従う。
  await expect(denied.getByText('この文書のタグを編集する権限がありません。')).toBeVisible();
  await expect(denied.getByRole('button', { name: '承認', exact: true })).toBeDisabled();
  await expect(denied.getByRole('button', { name: '却下', exact: true })).toBeDisabled();
  // ★ 資格あり: 理由を出さずに押せる。反映は実装済みなので「準備中」の暫定文言も出さない
  //   （05_screens §SC-03 の表「反映の実装後: 表示が消え、承認できるようになる」）。
  await expect(allowed.getByText(/権限がありません/)).toHaveCount(0);
  await expect(allowed.getByRole('button', { name: '承認', exact: true })).toBeEnabled();
  await expect(allowed.getByRole('button', { name: '却下', exact: true })).toBeEnabled();
  await expect(page.getByText(/準備中/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

/** 個人資料（`doc_scope=private-note`）。所有者は利用者名で持ち、画面は表示名へ引く。 */
const privateNoteDoc: DocumentDto = {
  ...doc,
  title: '調査メモ',
  attributes: { doc_scope: 'private-note', owner: 'hanako', confidentiality: 'restricted' },
};
const ownerDirectory: UserSummaryDto[] = [
  { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
];
const ownNotes: PrivateNoteListResponse = {
  usage: { usedBytes: 0, limitBytes: 1024 ** 3, percent: 0 },
  notes: [
    {
      id: ID,
      title: '調査メモ',
      vaultPath: '調査メモ.md',
      version: 1,
      bytes: 100,
      includeInSearch: false,
      includeInGraph: false,
      includeInAi: false,
      deleted: false,
      createdAt: '2026-08-01T00:00:00Z',
      updatedAt: '2026-08-02T00:00:00Z',
      visibility: 'users',
      sharedUserCount: 1,
      sharedGroupCount: 0,
      syncState: 'target',
      tags: [],
    },
  ],
};

test('M-041: 他人の個人資料を開くと、所有者は表示名で出るが公開範囲は欄ごと出ず、自分の資料一覧も引かない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 共有された相手（所有者ではない）。
    user: sessionUser([], 'jiro'),
    handlers: detailHandlers({
      [`GET /documents/${ID}`]: privateNoteDoc,
      'POST /users/resolve': ownerDirectory,
    }),
  });

  await page.goto(`/docs/${ID}`);
  await expect(page.getByRole('heading', { name: '調査メモ', level: 1 })).toBeVisible();

  // ★ 陽性対照: 個人資料であることと所有者（表示名。利用者名は出さない）は所有者以外にも出る。
  await expect(page.getByRole('heading', { name: '個人資料', level: 2 })).toBeVisible();
  await expect(page.getByText('花子 ハナコ')).toBeVisible();
  // ★ 本体: 公開範囲は所有者以外には欄ごと出さない（空欄・既定値でも描かない。ADR-0102 決定 1）。
  await expect(page.getByText('公開範囲')).toHaveCount(0);
  for (const label of ['非公開', '個人指定', 'グループ指定']) {
    await expect(page.getByText(label, { exact: true })).toHaveCount(0);
  }
  // 括弧書き「（自分のみ）」は所有者にだけ付す（同 決定 5）。
  await expect(page.getByText('個人資料（自分のみ）')).toHaveCount(0);
  // ★ 陰性対照: 所有者でない者は、公開範囲の供給元（本人の資料一覧）を引かない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /private-notes');

  expectBffTrafficIsComplete(traffic);
});

test('M-041: 所有者が自分の個人資料を開くと、「（自分のみ）」と公開範囲（本人の資料一覧から導いた区分）が出る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([], 'hanako'),
    handlers: detailHandlers({
      [`GET /documents/${ID}`]: privateNoteDoc,
      'POST /users/resolve': ownerDirectory,
      'GET /private-notes': ownNotes,
    }),
  });

  await page.goto(`/docs/${ID}`);
  await expect(page.getByText('個人資料（自分のみ）')).toBeVisible();
  await expect(page.getByText('公開範囲')).toBeVisible();
  await expect(page.getByText('個人指定', { exact: true })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain('GET /private-notes');

  expectBffTrafficIsComplete(traffic);
});

test('M-042: 版履歴は版の一覧だけを描き、過去版へ戻す（復元する）ボタンを置かない', async ({
  page,
}) => {
  const versions: DocumentVersionDto[] = [
    {
      documentId: ID,
      version: 2,
      title: '経費精算マニュアル',
      status: 'published',
      attributes: {},
      tags: [],
      changeNote: '締め日を改訂',
      createdAt: '2026-08-02T00:00:00Z',
    },
    {
      documentId: ID,
      version: 1,
      title: '経費精算マニュアル',
      status: 'published',
      attributes: {},
      tags: [],
      changeNote: '初版',
      createdAt: '2026-08-01T00:00:00Z',
    },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: detailHandlers({ [`GET /documents/${ID}/versions`]: versions }),
  });

  await page.goto(`/docs/${ID}`);
  // ★ 陽性対照: 版の一覧が描かれる（2 件）。
  await expect(page.getByRole('heading', { name: 'バージョン', level: 2 })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'v2', exact: true })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'v1', exact: true })).toBeVisible();
  // ★ 本体: 復元の操作は置かない（FR-06 の射程。05_screens §SC-03 主要素）。
  await expect(page.getByRole('button', { name: /復元|戻す|ロールバック/ })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /復元|戻す|ロールバック/ })).toHaveCount(0);
  // 書き込みの要求は 1 件も出ない（GET だけ）。
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

test('M-043: 文書 ID の無い /docs/ は未知パスとして「見つかりませんでした」を返し、文書を引かない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/docs/');

  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  // ★ 陰性対照: 文書の取得は 1 件も走らない（空 ID で GET /documents/ を叩かない）。
  expect(traffic.calls.map((c) => c.key).filter((k) => k.includes('/documents'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

test('SC-03: 属性・タグパネルの機密区分は用語集の表示名で出し、契約の生値を出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: detailHandlers({
      [`GET /documents/${ID}`]: { ...doc, attributes: { confidentiality: 'internal' } },
    }),
  });

  await page.goto(`/docs/${ID}`);
  await expect(page.getByRole('heading', { name: '属性・タグ', level: 2 })).toBeVisible();
  await expect(page.getByText('機密区分', { exact: true })).toBeVisible();
  // ★ 表示名（05_screens §SC-03「機密区分の表示名」。正は用語集）。生値は出さない。
  await expect(page.getByText('社内限', { exact: true })).toBeVisible();
  await expect(page.getByText('internal', { exact: true })).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});
