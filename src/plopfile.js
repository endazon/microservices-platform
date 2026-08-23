import fs from 'node:fs';
import path from 'node:path';

// ADR-0031 / IADR-0275: 計画 13_frontend-stack §採用技術一覧「Generator = Plop.js（Feature 雛形生成）」
// の実体（issue #493 / IADR-0121 決定 1 の第 5 段）。
//
// ■ なぜ pnpm workspace ルート（`src/`）に置くのか
//   `plop` は 1 本の devDependency で足り、生成先はユニットを跨ぐ（`platform` / `knowledge` / 追加ユニット）。
//   ユニット側に置くと、ユニットを足すたびに plopfile が増えて雛形の形が割れる。
//   Knip は `plop` 依存があると `plopfile.{cjs,mjs,js,ts}` を config として解決するため、
//   `knip.jsonc` へ追加の `entry` は要らない（実測: `node_modules/knip/dist/plugins/plop/index.js`）。
//
// ■ 雛形が写しているもの
//   生成される形は**この repo に実在する feature の形**である。写した実物は
//   `knowledge/frontend/src/features/sc04-wiki`（最小の feature）と
//   `../templates/unit-template/frontend/src/features/sample`（雛形の正解形）で、
//   計画 §ディレクトリ構成 が定める Feature 単位の 6 区分（`api/ components/ hooks/ routes/ stores/ types/`）を
//   すべて作る。**空の区分は `.gitkeep` で枠だけ残す**——消すと次の実装者に「この区分は不要」と伝わる。
//
// ■ 合成点（`features/index.ts`）へは**自動で追記しない**
//   あちらはルートのタプル（`as const`）とナビ配列の 2 経路で、**タプルを壊すと型安全が丸ごと失われる**
//   （IADR-0124 決定 1）。機械で書き換えると壊れ方が静かなので、**貼る 2 行を出力して人に配線させる**。

/** 生成先の候補（`<unit>/frontend/src/features` を持つ workspace メンバ）を実走査で列挙する。 */
function discoverUnits(root) {
  const candidates = [];
  const scan = (baseDir, prefix) => {
    if (!fs.existsSync(baseDir)) return;
    for (const entry of fs.readdirSync(baseDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      // IADR-0120: `ai-stock-trading` は別プロジェクトの submodule であり、本リポジトリから
      // ファイルを足さない。候補から外す。
      if (entry.name === 'ai-stock-trading' || entry.name === 'node_modules') continue;
      const featuresDir = path.join(baseDir, entry.name, 'frontend', 'src', 'features');
      if (fs.existsSync(featuresDir)) {
        candidates.push(`${prefix}${entry.name}/frontend`);
      }
    }
  };
  scan(root, '');
  scan(path.join(root, '..', 'templates'), '../templates/');
  return candidates;
}

export default function plopfile(plop) {
  const root = plop.getPlopfilePath();
  const units = discoverUnits(root);

  plop.setGenerator('feature', {
    description: 'SPA の feature 雛形を生成する（api/ components/ hooks/ routes/ stores/ types/）',
    prompts: [
      {
        type: 'list',
        name: 'unit',
        message: '生成先のユニット',
        choices: units,
      },
      {
        type: 'input',
        name: 'name',
        message: 'feature 名（ケバブケース。例: sc12-notifications）',
        validate: (value) =>
          /^[a-z0-9]+(-[a-z0-9]+)*$/.test(value.trim()) ||
          'ケバブケース（半角英数とハイフン）で入力する（.claude/rules 命名規則）',
      },
      {
        type: 'input',
        name: 'title',
        message: '画面の表示名（日本語。見出しとナビ項目に使う）',
        validate: (value) => value.trim().length > 0 || '表示名は省略できない',
      },
      {
        type: 'input',
        name: 'routePath',
        message: 'ルート（05_screens のルート表の値。例: /notifications）',
        default: (answers) => `/${answers.name}`,
        validate: (value) => value.startsWith('/') || 'ルートは / で始める',
      },
      {
        // **`when`（条件付きプロンプト）を使わない。** plop の CLI はプロンプトの bypass 引数を
        // 順番で受け取るため、条件付きプロンプトがあると `plop feature <値> …` の非対話実行が
        // 「You can not bypass conditional prompts」で止まる（実測）。CI・スクリプトから叩けない
        // 生成器は使われなくなるので、**「出さない」を選択肢の 1 つにして 1 本のプロンプトに畳む。**
        type: 'list',
        name: 'navGroup',
        message: '左ナビのグループ（05_screens §共通シェル の 4 グループ）',
        choices: [
          { name: 'user（利用者）', value: 'user' },
          { name: 'personal（個人）', value: 'personal' },
          { name: 'admin（管理）', value: 'admin' },
          { name: 'ops（運用）', value: 'ops' },
          { name: 'none（左ナビへ出さない。一覧・検索からのみ到達する詳細画面）', value: 'none' },
        ],
      },
    ],
    actions(answers) {
      // 雛形からは `withNav` で分岐する（`navGroup` の値域に「出さない」を混ぜたのは上記の理由）。
      answers.withNav = answers.navGroup !== 'none';
      const base = `${answers.unit}/src/features/{{name}}`;
      const add = (target, templateFile) => ({
        type: 'add',
        path: `${base}/${target}`,
        templateFile,
      });

      return [
        add('index.ts', 'plop-templates/feature/index.ts.hbs'),
        add('routes/{{camelCase name}}Route.ts', 'plop-templates/feature/routes/route.ts.hbs'),
        add(
          'components/{{pascalCase name}}Page.tsx',
          'plop-templates/feature/components/Page.tsx.hbs',
        ),
        add(
          'components/{{pascalCase name}}Page.test.tsx',
          'plop-templates/feature/components/Page.test.tsx.hbs',
        ),
        // 計画 §ディレクトリ構成 の 6 区分の枠。中身が入るまで `.gitkeep` で残す
        // （`sc04-wiki` / `sc08-analysis` と同じ形）。
        add('api/.gitkeep', 'plop-templates/feature/gitkeep.hbs'),
        add('hooks/.gitkeep', 'plop-templates/feature/gitkeep.hbs'),
        add('stores/.gitkeep', 'plop-templates/feature/gitkeep.hbs'),
        add('types/.gitkeep', 'plop-templates/feature/gitkeep.hbs'),
        // 合成点への配線は人が行う（上のコメント参照）。貼る行をそのまま出す。
        () => {
          const pascal = plop.renderString('{{pascalCase name}}', answers);
          const camel = plop.renderString('{{camelCase name}}', answers);
          const wiring = [
            '',
            `次の配線は自動化していない。${answers.unit}/src/features/index.ts へ手で足すこと:`,
            `  import { create${pascal}Route${answers.withNav ? `, ${camel}Nav` : ''} } from './${answers.name}';`,
            `  createXxxRoutes のタプルへ   create${pascal}Route(shell),`,
            answers.withNav ? `  navItems の配列へ           ${camel}Nav,` : null,
            '',
            'そのあと: pnpm run i18n（カタログ再生成と翻訳）/ eslint.config.js の',
            'lingui 適用範囲（files）へ本 feature のパスを足す / pnpm run lint && pnpm run typecheck。',
          ]
            .filter((line) => line !== null)
            .join('\n');
          return wiring;
        },
      ];
    },
  });
}
