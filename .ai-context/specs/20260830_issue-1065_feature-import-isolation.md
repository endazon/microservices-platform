---
title: 作業仕様書 feature 間 import の解消と import/no-restricted-paths の配備（#1065）
type: spec
status: done
related_ids:
  - NFR
  - SC-01
  - SC-05
  - SC-06
  - SC-08
  - ADR-0031
  - ADR-0066
  - IADR-0056
  - IADR-0057
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0146
  - IADR-0262
  - IADR-0307
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md (§ディレクトリ構成)
related_specs:
  - ./20260823_issue-785_bulletproof-react-structure.md
  - ./20260822_issue-954_scope-filter-casing.md
issue: "#1065"
---

# 仕様書: feature 境界の機械強制（#1065）

> **本作業は画面を 1 枚も作らない。** 直すのは **feature どうしが素通りで参照し合える状態**と、
> **その状態が誰にも見えないこと**（`import/no-restricted-paths` が 0 件配備）である。

## 起点

計画 **ADR-0066**（Accepted 2026-08-30）決定 1〜3。

- 決定 1: `features/<A>/` から `features/<B>/` を参照しない。共有語彙は `src/lib/` `src/components/` へ出す。
  **`abac` / `scope-filter` は feature ではない**（ドメイン語彙）。
- 決定 2: 依存の向きは `shared → features → app` の一方向。
- 決定 3: 決定 1・2 は `import/no-restricted-paths` で機械強制する。
- 決定 4: feature の `index.ts`（barrel）は**維持する**（Bulletproof React 現行版からの意図的逸脱）。

## 母集合の引き方（`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方）

**issue 本文の「7 ファイル」を転記していない。** 自分で引き直した。

### 1. feature 間 import の母集合（規則 2: 誤りの側の文字列で全文書を走査）

`git ls-files` の追跡下 `.ts` / `.tsx` 全件（`src/ai-stock-trading/**` は submodule ゆえ除外。
IADR-0120）から `features/<unit>/<feat>/` 配下のファイルを取り、**各 import 指定子を実際に
解決して**「自分以外の feature 配下へ落ちるか」で判定するスクリプトを書いた（相対パス・
`@features` / `@knowledge` エイリアスの双方を見る）。**目 grep でも `../../` の列挙でもない。**

```
cross-feature import statements: 9
distinct files: 7
```

| # | import 元 | 指定子 | 解決先 |
| --- | --- | --- | --- |
| 1-2 | `features/sc01-search/components/SearchChatPage.tsx` | `../../scope-filter` ×2 | `features/scope-filter` |
| 3 | `features/sc05-documents/components/DocumentForm.tsx` | `../../abac` | `features/abac` |
| 4 | `features/sc05-documents/components/DocumentManagementPage.tsx` | `../../abac` | `features/abac` |
| 5 | `features/sc06-datasources/components/DataSourceAttributesForm.tsx` | `../../abac` | `features/abac` |
| 6 | `features/sc06-datasources/components/DataSourceForm.tsx` | `../../abac` | `features/abac` |
| 7-8 | `features/sc08-analysis/components/AnalysisDashboardPage.tsx` | `../../scope-filter` ×2 | `features/scope-filter` |
| 9 | `features/sc08-analysis/types/analysisRange.ts` | `../../scope-filter` | `features/scope-filter` |

**7 ファイル・9 文（issue の 7 ファイルと一致）。深い参照は 0 件で、すべて barrel（`index.ts`）
経由である**（#785 決定 5 が寄せた結果）。よって移送先での公開面をそのまま保てば、
呼び出し側の書き換えはパス 1 行で済む。

### 2. 依存の向き（決定 2）の母集合

同じ走査器を「shared（`components`/`hooks`/`lib`/`types`/`utils`/`stores`）→ `features`/`app`」と
「`features` → `app`」で引き直した（エイリアス `@foundation/*` も実体パスへ展開して数えた）。

| ユニット | 違反 |
| --- | --- |
| `knowledge/frontend` | **0 件** |
| `platform/frontend` | **16 件**（うち shared→app 13 / features→app 3） |

**platform の 16 件はすべて `@foundation/*` 経由である。** `@foundation/i18n` `@foundation/routing`
`@foundation/config` の 3 つが `platform/frontend/src/app/` 配下を指しており、
`components/ui/Layout.tsx` や `lib/api/apiClient.ts` がそれを引いている。
**これは基盤の公開面（`@foundation`）の実現位置が `app/` である、という設計上の事実**であって、
#1065 の射程（feature 境界）ではない。→ §射程外 へ。

### 3. 追随の母集合（規則 9・10: 誤りの側の文字列で走査し、是正後に新たに誤りになる自分の記述を引き直す）

`git grep -n -E "features/abac|features/scope-filter"` を追跡下の全ファイルへ掛けた
（`src/ai-stock-trading` は除外。`.ai-context/specs/` と `CHANGELOG.md` は**確定済み記録なので
書き換えない**——`traceability.repo.md` §Superseded / Deprecated な ADR を引用するときの書式）。

| 追随先 | 件数 | 扱い |
| --- | --- | --- |
| `src/eslint.config.js:344`（lingui の `files` 許可リスト） | 1 | **書き換える**（移送で静かに外れる） |
| `src/knowledge/.../sc03-document/types/attributes.ts` コメント | 1 | 書き換える |
| `src/knowledge/.../sc08-analysis/{components,types}` コメント | 2 | 書き換える |
| `docs/screens/SC-01`・`SC-08` | 2 | 書き換える |
| `docs/tests/SC-01`・`SC-05`・`SC-06`・`SC-08` | 7 | 書き換える |
| `scripts/check-route-manifest.js`（コメント 2・自己テスト 1） | 3 | 書き換える |
| `src/platform/frontend/src/locales/{ja,en}/messages.po` | 18 | **再生成**（手で書かない） |
| `.ai-context/specs/*`・`CHANGELOG.md` | 多数 | **書き換えない**（凍結記録） |

**規則 10 で引き直して新たに見つかった追随**（是正前の語では捕まらなかったもの）:

- `src/eslint.config.js` の knowledge ブロック冒頭コメント
  「**`knowledge/frontend/src/` で中身を持つのは `features/` だけである**（実測 2026-08-23）……
  **枠へ実体が入ったらこの前提を引き直すこと**」——
  **この記述は本作業の前から既に誤り**（`components/` に 9 ファイルある）であり、本作業で
  `lib/` にも実体が入る。前提そのものを引き直して書き換える。
- `scripts/check-route-manifest.js` の「画面でない feature ディレクトリ（`scope-filter` 等）」——
  移送後、knowledge の `features/` 配下は**全部が画面**になる。除外ロジック自体は残す
  （将来また非画面が現れ得る）が、例示を実在しないものにしない。

## 決定

### 決定 A — `abac` / `scope-filter` は `knowledge/frontend/src/lib/` へ、**平坦**に置く

移送先は ADR-0066 決定 1 と受け入れ基準がともに名指しする `src/lib/`。

**feature 内部の 6 分割（`api/ components/ hooks/ routes/ stores/ types/`）は持ち込まない。**
あれは 13_frontend-stack §ディレクトリ構成 が **feature に対して**課した規範であり、
`features/` を出た時点で適用対象ではない。実際 knowledge の共有ディレクトリは既に平坦である
（`components/DataTable.tsx` 等、9 ファイルすべて直下）。**同じリポジトリの中で共有物の置き方を
2 通りにしない。**

| 移送前 | 移送後 |
| --- | --- |
| `features/abac/index.ts` | `lib/abac/index.ts` |
| `features/abac/types/{confidentiality,department,lifecycle}.ts`（＋各 `.test.ts`） | `lib/abac/{同名}` |
| `features/abac/{api,components,hooks,routes,stores}/.gitkeep` | **削除**（feature の枠であり lib では無意味） |
| `features/scope-filter/index.ts` | `lib/scope-filter/index.ts` |
| `features/scope-filter/components/ScopeFilter.{tsx,test.tsx}` | `lib/scope-filter/{同名}` |
| `features/scope-filter/types/scopeSelection.{ts,test.ts}` | `lib/scope-filter/{同名}` |
| `features/scope-filter/api/useScopeCandidates.ts` | `lib/scope-filter/useScopeCandidates.ts` |
| `features/scope-filter/{hooks,routes,stores}/.gitkeep` | **削除** |

**`index.ts`（公開面）は両方とも残す。** ADR-0066 決定 4 は feature の barrel を維持すると
定めており、feature でなくなった後も「外から触ってよい面」を宣言する意味は変わらない。
呼び出し側は `../../../lib/abac` / `../../../lib/scope-filter` で引く（既存の
`../../../components/DataTable` と同じ書き方）。

🔴 **`ScopeFilter.tsx` と `scopeSelection.ts` を同一ディレクトリへ置くが、#954 の
大小非区別 FS 衝突は起きない**（衝突したのは `ScopeFilter.tsx` と `scopeFilter.ts` という
**大小しか違わない 2 名**であり、`scopeSelection` は既に別名である）。`tsc --listFiles` で確認する。

### 決定 B — 検査器は `eslint-plugin-import` の `import/no-restricted-paths`

理由・代替案・解決器の限界は [IADR-0307](../adr/IADR-0307_feature-import-isolation-eslint-zones.md) に置く。
本仕様書では**設定の形**だけを書く。

- zones は **`features/` を実際に読んで生成する**（手書きの許可リストを作らない）。
  ADR-0066 §理由 が「許可リストの保守が人に戻ると伸ばし忘れが規則の穴になる」と述べた形を作らない。
- `basePath` を `import.meta.dirname` で固定する（既定は `process.cwd()` であり、
  どこから `eslint` を起こしたかで zone がずれる）。
- **既存の knowledge ブロックへ足す。新しいブロックを立てない。**
  `import/no-restricted-paths` は `no-restricted-imports` と別規則なので後勝ち置換は起きないが、
  **「knowledge ユニットの import 規約は 1 ブロックに集める」という現状の形を割らない**
  （`eslint.config.js` 冒頭と同ブロックの注意書きが警告している型）。

### 決定 C — エイリアス経由の抜け道を `no-restricted-imports` で塞ぐ

`import/no-restricted-paths` は**解決できた import しか見ない**。本設定の解決器（node resolver ＋
`.ts/.tsx` 拡張子）は `@knowledge/*` を解決できないため、
`@knowledge/features/abac` と書けば規則を素通りする。

**実測: knowledge ユニット内からの `@knowledge` 利用は 0 件**（唯一の利用は platform の合成点
`platform/frontend/src/features/index.ts` の `@knowledge/features`。別ブロックの管轄）。
よって knowledge ブロックの `no-restricted-imports` へ `@knowledge` / `@knowledge/*` を足しても
既存コードは壊れない。**自ユニット内は相対パスで引く**、を規則にする。

## 射程外（黙って飛ばさず開示する）

1. **platform の 16 件（shared/features → `app/`）。** `@foundation` の実現位置が `app/` 配下である
   という設計に由来し、直すには `@foundation` の置き場所そのものを動かすことになる。
   ADR-0066 決定 2 と実装の間の**未裁定の差**であり、**計画リポジトリへ環流すべき事項**として
   PR 本文で申し送る（本 PR では起票しない）。
2. **`ai-stock-trading/**` は従来どおり ESLint の適用外**（ADR-0066 決定 3 の但し書き・IADR-0120）。
   AST 側は自リポジトリで同じ規則を持つ（ADR-0066 フォローアップ 3）。
3. **lingui の適用範囲に `scope-filter` を加えること。** 移送前から `scope-filter` は lingui の
   `files` 許可リストに**入っていなかった**（`abac` は入っている）。これは #1065 が作った穴ではなく
   既存の穴であり、本 PR では位置の追随（`features/abac/**` → `lib/abac/**`）だけを行う。
4. **feature 内部分割の欠け**（`sc18`〜`sc21` の `hooks/` `stores/` `types/`）。ADR-0066
   フォローアップ 2 の別 issue。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | feature 間 import が 0 件 | 上記の走査スクリプトを再実行して `0` |
| 2 | `abac` / `scope-filter` が `lib/` 配下・`features/` の下でない | `git ls-files` |
| 3 | `import/no-restricted-paths` が (a)(b)(c) を error にする | `eslint.config.js` の zones |
| 4 | 🔴 **違反を注入すると lint が error で落ちる** | 一時ファイルを置いて `pnpm run lint`。**除去して緑に戻ることまで**記録する |
| 5 | `lint` / `typecheck` / `test` / `build` / `format:check` が通る | 実行ログ |
| 6 | `check-chunk-budget` が予算内 | 実行ログ |
| 7 | `ai-stock-trading/**` が適用外のまま | `eslint.config.js` の `ignores` と zones の target が knowledge のみであること |

## 影響ファイル領域（並列判定）

`src/knowledge/frontend/src/**` / `src/eslint.config.js` / `src/package.json` / `src/pnpm-lock.yaml` /
`src/platform/frontend/src/locales/**`（再生成） / `scripts/check-route-manifest.js` /
`docs/screens/SC-01,SC-08` / `docs/tests/SC-01,SC-05,SC-06,SC-08` / `.ai-context/`
