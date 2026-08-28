// @vitest-environment node
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, it, expect } from 'vitest';

// FR-22, IADR-0215 決定 2 / テスト仕様書 docs/tests/FR-22_user-notifications.md T-01。
//
// ★ **契約そのものの不変条件を固定する。**
// 計画 FR-22 の受け入れ基準は「**通知の本文が件数と期限のみで構成される。資料のタイトル・本文・
// 検索語・回答内容を含まない**」である（メールは本システムの ABAC の外側へ出るため）。
//
// これを「実装が気をつける」形にすると守られているかを機械で見られない。よって
// **契約（`docs/api/openapi.yaml`）に自由文のフィールドを 1 つも置かない**という形へ翻訳し、
// 本テストがその項目集合を固定する。**`title` / `body` を足した瞬間に CI が落ちる。**
//
// 生成物（`bff.schemas.ts`）も併せて見るのは、**契約 → 生成 → 画面の経路が実際に閉じている**ことを
// 1 か所で確かめるためである（契約だけを見ると、生成が壊れていても緑になる）。

// 本ファイルは **node 環境**で走らせる（冒頭の `@vitest-environment node`）。jsdom では
// `import.meta.url` が `http://localhost/…` になり `fileURLToPath` が使えない（実測）。
// Vitest の cwd は設定ファイルのあるワークスペースルート（`src/`）なので、そこから 1 階層上がリポジトリルート。
const REPO_ROOT = resolve(process.cwd(), '..');

const OPENAPI = readFileSync(resolve(REPO_ROOT, 'docs/api/openapi.yaml'), 'utf8');
const GENERATED = readFileSync(
  resolve(REPO_ROOT, 'src/platform/frontend/src/lib/api/generated/bff.schemas.ts'),
  'utf8',
);

/**
 * `components.schemas.<name>.properties` のキーを取り出す。
 *
 * YAML ライブラリを入れずに**行の字下げ**で読む（本リポの検査器と同じ作法。外部依存を増やさない）。
 * スキーマは字下げ 4、その中の `properties:` は 6、プロパティ名は 8 である。
 */
function schemaProperties(name: string): string[] {
  const lines = OPENAPI.split('\n');
  const start = lines.findIndex((l) => l === `    ${name}:`);
  expect(start, `${name} が openapi.yaml に見つからない`).toBeGreaterThanOrEqual(0);

  const keys: string[] = [];
  let inProperties = false;
  for (const line of lines.slice(start + 1)) {
    // 字下げ 4 以下の非空行に当たったら、そのスキーマは終わり。
    if (line.trim() !== '' && !line.startsWith('     ') && !line.startsWith('    #')) break;
    if (line === '      properties:') {
      inProperties = true;
      continue;
    }
    // `properties:` と同じ階層（字下げ 6）の別キー（`required:` 等）に当たったら終わり。
    if (inProperties && /^ {6}\S/.test(line)) break;
    const m = inProperties ? /^ {8}([A-Za-z][A-Za-z0-9]*):/.exec(line) : null;
    if (m) keys.push(m[1]);
  }
  return keys;
}

/**
 * 自由文が入り得るフィールド名。**「タイトル・本文・検索語・回答内容」を運べる形**を列挙する。
 * 1 つでも `NotificationDto` に現れたら受け入れ基準が破れている。
 */
const FREE_TEXT_PROPERTY_NAMES = [
  'title',
  'body',
  'message',
  'subject',
  'text',
  'summary',
  'detail',
  'details',
  'content',
  'description',
  'name',
  'excerpt',
  'preview',
  'documentTitle',
  'query',
  'answer',
];

describe('FR-22 通知の契約（IADR-0215 決定 2）', () => {
  // FR-22 受け入れ基準: 本文が件数と期限のみで構成される。
  it('NotificationDto は種別・件数・閾値・期限・発生時刻・既読の 7 項目だけを持つ', () => {
    expect(schemaProperties('NotificationDto').sort()).toEqual(
      ['count', 'deadline', 'id', 'kind', 'occurredAt', 'read', 'thresholdPercent'].sort(),
    );
  });

  // ★ 受け入れ基準そのもの: タイトル・本文に相当する項目が契約に存在しない。
  it('NotificationDto に自由文のフィールドが 1 つも無い', () => {
    const properties = schemaProperties('NotificationDto');
    for (const forbidden of FREE_TEXT_PROPERTY_NAMES) {
      expect(properties, `${forbidden} は通知の契約に置いてはならない`).not.toContain(forbidden);
    }
  });

  // 封筒の側にも自由文を置かない（本文を「一覧の外」へ逃がす抜け道を塞ぐ）。
  //
  // ★ **禁止語の照合ではなく完全一致で閉じる**（[[IADR-0215]] 決定 2）。禁止語は列挙した語しか
  //   止められず、`label` / `note` / `reason` / `caption` / `snippet` のような**列挙し忘れた語**が
  //   素通りする。封筒は 2 項目しか持たないので、完全一致で「増えたら落ちる」形にできる。
  //   `NotificationDto` 本体を 7 項目の完全一致で閉じているのと同じ守り方に揃える。
  it('NotificationListDto / NotificationReadResultDto は宣言した項目だけを持つ', () => {
    expect(schemaProperties('NotificationListDto').sort()).toEqual(['items', 'unreadCount']);
    expect(schemaProperties('NotificationReadResultDto').sort()).toEqual(['id', 'unreadCount']);
  });

  // BFF_bff-surface.md §横断の規約 4: 種別の文字列を**閉じた `enum` にしない**。
  // 閉じると、後段が種別を増やした瞬間に、まだ再デプロイされていない SPA が
  // **既存の値も解釈できなくなる**（union に無い値は型の上で存在しないことになる）。
  // 値集合は description が持ち、対応づけは notificationMessages.ts の既定枝つき純関数が持つ。
  it('kind は閉じた enum ではない（後段が種別を増やしても壊れない）', () => {
    const generated = GENERATED.slice(
      GENERATED.indexOf('export interface NotificationDto {'),
      GENERATED.indexOf('\n}', GENERATED.indexOf('export interface NotificationDto {')),
    );
    // 閉じていれば orval は union 型 `NotificationDtoKind` を作って kind へ充てる。
    expect(generated).toContain('kind: string');
    expect(GENERATED).not.toContain('NotificationDtoKind');
  });

  // IADR-0132: required の無い応答スキーマは orval が全プロパティを省略可で生成し、型検査の網にならない。
  it('応答スキーマに required が付いている（nullable な 3 項目は入らない）', () => {
    expect(OPENAPI).toContain('required: [id, kind, occurredAt, read]');
    expect(OPENAPI).toContain('required: [items, unreadCount]');
    expect(OPENAPI).toContain('required: [id, unreadCount]');
  });

  // 契約 → 生成 → 画面の経路が閉じていること。生成が壊れていれば契約だけ見ても気づけない。
  it('生成された NotificationDto にも自由文のフィールドが無い', () => {
    const start = GENERATED.indexOf('export interface NotificationDto {');
    expect(start).toBeGreaterThanOrEqual(0);
    const block = GENERATED.slice(start, GENERATED.indexOf('\n}', start));
    for (const forbidden of FREE_TEXT_PROPERTY_NAMES) {
      expect(block, `生成物に ${forbidden} が現れている`).not.toMatch(
        new RegExp(`^\\s*${forbidden}[?]?:`, 'm'),
      );
    }
  });
});
