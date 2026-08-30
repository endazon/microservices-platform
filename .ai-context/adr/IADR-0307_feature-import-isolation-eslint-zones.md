---
title: IADR-0307 feature 境界の機械強制は eslint-plugin-import の no-restricted-paths で行い、zones は実ディレクトリから生成する
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, SC-05, SC-06, SC-08, ADR-0031, ADR-0066, IADR-0056, IADR-0057, IADR-0120, IADR-0121, IADR-0125, IADR-0146, IADR-0262]
author: Claude
created: 2026-08-30
updated: 2026-08-30
related_specs:
  - ../specs/20260830_issue-1065_feature-import-isolation.md
---

# IADR-0307: feature 境界の機械強制の実現手段

## 文脈

計画 **ADR-0066**（Accepted 2026-08-30）決定 3 が「決定 1・2 は ESLint の
`import/no-restricted-paths` で強制する」と定めた。**規則名まで計画が名指ししている**。

本リポジトリには当該プラグインが無かった（`src/package.json` にも `src/pnpm-lock.yaml` にも
`eslint-plugin-import` が 0 件。実測 2026-08-30）。

## 決定

### 決定 1 — `eslint-plugin-import` を追加する（`eslint-plugin-import-x` を採らない）

**依存が 1 つ増える**（`eslint-plugin-import@^2.32.0`。推移依存を含めて **97 パッケージ**が
`node_modules` に増える。いずれも devDependency であり、**成果物（`dist/`）には 1 バイトも入らない**）。

検討した代替:

| 案 | 判断 |
| --- | --- |
| `eslint-plugin-import`（採用） | 規則名がそのまま `import/no-restricted-paths` になる。**計画 ADR-0066 決定 3 と受け入れ基準が名指しした文字列と一致する** |
| `eslint-plugin-import-x` | 保守は活発で依存も軽いが、規則名は `import-x/no-restricted-paths` になる。プラグイン名を `import` へ付け替えれば一致させられるが、**「設定を読んだ人が探す名前」と「実体」がずれる**形を作る |
| 自前の検査スクリプト（`scripts/check-*.js`） | 本リポジトリは検査器を多数持つが、`eslint.config.js` 冒頭が「**対象が import の静的検査＝ESLint の守備範囲そのものであり、検査器を増やすほど走らせ忘れと二重メンテが増える**」（IADR-0121 決定 8）と既に述べている。同じ理由で採らない |

### 決定 2 — zones は `features/` を**実ファイルから読んで生成する**

`import/no-restricted-paths` の慣用（Bulletproof React の配布設定を含む）は
**feature 1 つにつき zone を 1 つ手書きする**形である。これを採らない。

**ADR-0066 §理由 が選択肢 2（共有 feature の例外区分）を退けた理由がそのまま当てはまる** ——
「許可リストの保守が人に戻り、伸ばし忘れが規則の穴になる」。しかも本リポジトリには
**実際にその形が在る**（lingui の `files` を画面のたびに伸ばす運用。同 §理由 が名指ししている）。
同じ形を 2 つ目として増やさない。

```js
const featureNamesOf = (unitSrcRel) =>
  readdirSync(path.join(CONFIG_DIR, unitSrcRel, 'features'), { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort();
```

**画面を足しても `eslint.config.js` は触らなくてよい。** 新しい feature は次回の lint から
自動的に「他の feature から参照されない側」かつ「他の feature を参照しない側」になる。

### 決定 3 — 解決器の限界を明示し、エイリアス経由の穴を `no-restricted-imports` で塞ぐ

🔴 **`import/no-restricted-paths` は「解決できた import」しか見ない。解決に失敗した import は
黙って素通りする**（規則の実装が `if (!absoluteImportPath) return;` で早期に抜ける）。
ここには**静かに 0 件で通る**罠が 2 つある。

1. **既定の node resolver は `.mjs / .js / .json / .node` しか試さない。**
   拡張子を足さないと本リポジトリの `.ts` / `.tsx` は 1 件も解決されず、**規則は配備されているのに
   何も検出しない**。よって `settings['import/resolver'].node.extensions` を明示する。
   `eslint-import-resolver-node` は `eslint-plugin-import` の依存なので**追加の依存は要らない**。
2. **エイリアス（`@knowledge/*`）は解決できない。** TypeScript の `paths` を解くには
   `eslint-import-resolver-typescript` が要り、**依存がもう 1 つ増える**。
   代わりに **knowledge ユニット内からの `@knowledge` 参照そのものを `no-restricted-imports` で禁じる**。
   実測（2026-08-30）: **knowledge ユニット内からの `@knowledge` 利用は 0 件**であり、
   唯一の利用は platform の合成点 `platform/frontend/src/features/index.ts` の `@knowledge/features`
   （別ブロックの管轄で、そちらは従来どおり許可）。**自ユニット内は相対パスで引く**を規則にすれば、
   解決器を増やさずに穴が閉じる。

**`basePath` も明示する。** 既定は `process.cwd()` であり、`eslint` をどこから起こしたかで
zone の解決先がずれる（`pnpm run lint` は `src/`、`lint:templates` はリポジトリルートから起きる）。
`import.meta.dirname` で設定ファイルの位置に固定する。

### 決定 4 — 規則は**既存の knowledge ブロックへ同居させる**

`eslint.config.js` の flat config は同一ルールを**後勝ちで置換**する。
`import/no-restricted-paths` は `no-restricted-imports` と別規則なので新しいブロックを立てても
置換は起きないが、**ユニットの import 規約が 2 ブロックに散る**形を作らない
（同ファイルは冒頭と当該ブロックの両方でこの型を警告している）。

### 決定 5 — 適用は **knowledge ユニットのみ**。platform には掛けない

**platform に決定 2（依存の向き）を掛けると 16 件が error になる。実測の内訳:**

| 経路 | 件数 | 実体 |
| --- | --- | --- |
| shared → app | 13 | `components/ui/Layout.tsx` ほかが `@foundation/routing` `@foundation/config` `@foundation/i18n` を引く |
| features → app | 3 | 合成点 `features/index.ts` が `@foundation/routing/shell` `@foundation/routing/featureRegistry` を引く |

**16 件すべてが `@foundation/*` 経由である。** `@foundation` は基盤の公開面（IADR-0057）であり、
その実現位置が `platform/frontend/src/app/` 配下（`app/routing` / `app/config` / `app/i18n`）である、
という**設計上の事実**に由来する。**画面が間違って app を引いているのではない。**

これを是正するには `@foundation` の実体を `app/` の外へ動かすことになり、
**ADR-0066 が扱っていない範囲の設計変更**である。#1065 の射程（feature 境界）ではないので掛けない。
**この差は計画へ環流する**（PR 本文の申し送り）。

**`ai-stock-trading/**` は従来どおり適用外**（ADR-0066 決定 3 の但し書き / IADR-0120）。
本リポジトリの ESLint から submodule の中身は是正できない。AST 側は自リポジトリで同じ規則を持つ
（ADR-0066 フォローアップ 3）。

### 決定 6 — `abac` / `scope-filter` は `lib/` へ**平坦**に置き、`index.ts` は残す

置き場所は ADR-0066 決定 1 が名指しする `src/lib/`。
**feature 内部の 6 分割（`api/ components/ hooks/ routes/ stores/ types/`）は持ち込まない** ——
あれは 13_frontend-stack §ディレクトリ構成 が **feature に対して**課した規範であり、
`features/` を出た時点で適用対象ではない。knowledge の共有ディレクトリは既に平坦である
（`components/` の 9 ファイルはすべて直下）。**同じリポジトリで共有物の置き方を 2 通りにしない。**

**`index.ts`（公開面）は両方とも残す。** ADR-0066 決定 4 は feature の barrel を維持すると定めた。
feature でなくなった後も「外から触ってよい面を宣言する」意味は変わらず、
公開面があるから呼び出し側 7 ファイルの書き換えが**パス 1 行**で済んだ（深い参照は 0 件だった）。

## 実測（規則が働くことの証跡）

**規則を置いただけでは「働いている」と言えない**（決定 3 の罠 1 がまさにそれである）。
違反を注入して実測した。

注入（3 ファイル・一時。コミットしない）→ `pnpm run lint`:

```
features/sc01-search/__violation-probe.ts
  3:32  error  Unexpected path "../sc02-results" imported in restricted zone. feature どうしを import しない（ADR-0066 決定 1）…            import/no-restricted-paths
  4:33  error  Unexpected path "../../app/__violation-probe-target" imported in restricted zone. features から app を参照しない（ADR-0066 決定 2）。  import/no-restricted-paths
  5:1   error  '@knowledge/features/sc02-results' import is restricted from being used by a pattern. 自ユニット内は相対パスで参照する…              no-restricted-imports
lib/__violation-probe.ts
  3:32  error  Unexpected path "../features/sc02-results" imported in restricted zone. 共有層…から features・app を参照しない（ADR-0066 決定 2）…   import/no-restricted-paths
  4:33  error  Unexpected path "../app/__violation-probe-target" imported in restricted zone. 共有層…                                        import/no-restricted-paths

✖ 14 problems (5 errors, 9 warnings)
 ELIFECYCLE  Command failed with exit code 1.
```

除去後 → `pnpm run lint`:

```
✖ 9 problems (0 errors, 9 warnings)
exit=0
```

**warnings は本作業の前から在るものだけである**（`react-refresh/only-export-components` ＋
`sc12-mcp-clients` の未使用 eslint-disable。develop でも同数であることを stash して確認した）。
**上の 2 回の実行は submodule 未取得の状態**で、その条件では 9 件。
`src/ai-stock-trading` を取得すると同 submodule の `e2e/harness/main.tsx` の分が足されて **10 件**になる
（本作業とは無関係であり、error は両条件とも 0 件）。**件数を引用するときは submodule の有無を併記すること。**

## 結果

- **良い影響**: feature 境界が「守る約束」から「破れない制約」になった。画面を足しても
  設定ファイルの追随が要らない（決定 2）。
- **悪い影響 / トレードオフ**:
  - devDependency が 1 つ（推移依存 97 パッケージ）増える。
  - **エイリアス経由の参照は `import/no-restricted-paths` からは見えない**。塞いでいるのは
    `no-restricted-imports` の別規則であり、**2 つの規則の組でようやく閉じている**。
    片方だけ外すと穴が開く（決定 3）。
  - platform ユニットには掛かっていない（決定 5）。**「配備した」と読んだ人が
    platform も守られていると誤解しないよう、本 IADR を引くこと。**
- **フォローアップ**:
  1. platform の `@foundation` の実現位置（`app/` 配下）と ADR-0066 決定 2 の差 → 計画へ環流。
  2. lingui の適用範囲に `lib/scope-filter/**` が入っていない（**#1065 が作った穴ではなく既存**。
     `abac` は入っていたが `scope-filter` は移送前から入っていなかった）。
  3. `sc18`〜`sc21` の feature 内部分割の欠け（ADR-0066 フォローアップ 2。別 issue）。

## 関連

- 計画: ADR-0066 決定 1〜4 / ADR-0031 / 13_frontend-stack §ディレクトリ構成
- 実装: [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 8（禁止の ESLint 強制）／
  [IADR-0057](./IADR-0057_unit-dependency-machine-check.md)（ユニット境界）／
  [IADR-0146](./IADR-0146_apifetch-reentry-guard.md)（画面からの `apiFetch` 禁止）／
  [IADR-0262](./IADR-0262_bulletproof-react-directory-conformance.md) 決定 5（公開面を置いて feature 跨ぎを barrel へ寄せた前段）
- issue: #1065（環流元 planning#490）
