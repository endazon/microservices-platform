---
title: 作業仕様書 — Knip（未使用コード・依存の検出）を baseline ラチェットで導入する（SPA 移行第 5 段の切り出し 2/2・#493）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - IADR-0056
  - IADR-0060
  - IADR-0115
  - IADR-0116
  - IADR-0121
  - IADR-0125
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0203
  - IADR-0209
  - IADR-0211
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md (採用技術一覧: Dead Code 検出 = Knip)
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../adr/IADR-0211_knip-scope-and-unused-ratchet.md"
  - "./20260815_issue-768_renovate-husky.md"
  - "./20260804_issue-446_spa-foundation-stack-migration.md"
  - "./20260808_issue-556_chunk-budget-check.md"
  - "./20260815_issue-454_open-issue-stocktake-and-waves.md"
---

# 作業仕様書: Knip を導入し、未使用の残件を baseline ラチェットで固定する（#493）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（成果物の機能を変えない）
- ユースケース（UC）/ 画面（SC）: なし
- 非機能要件: **`NFR`（無採番）** —— 検査基盤の追加というメタ作業であり、計画側の非機能要件表
  （`NFR-01`〜`NFR-27`）に当たる番号が無い（[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md)
  決定 1。**無いことは「実装側で採番してよい」ではない**＝同 決定 2。**環流しない**）。
- 関連 ADR:
  - 計画側: **`ADR-0031`**（SPA スタック。本作業の起点）。計画 `06_technical/13_frontend-stack.md`
    §採用技術一覧の「Dead Code 検出 = Knip（採用）」がそのまま要求である。
  - 実装側: [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1（**第 5 段 = 運用系**
    〔Knip / Plop / Renovate / Husky〕）・[IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md)
    （第 5 段の先行分 1/2 = Renovate / Husky）・
    [IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（プリミティブの移植範囲。
    **「Knip（第 5 段）の未使用検出 → 出る（利用者は #452）」と自ら予告している**）・
    [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)（**同じ値を 2 箇所に置かない**）・
    [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（検査器が「偽の緑」を返す条件は fail へ倒す）。
  - 本作業で新設: **[IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md)**。
- 関連 issue: **#493**（親。本作業は切り出し 2/2）/ #768（切り出し 1/2・着地済み）/ #452（第 4・5 段が待つ画面再実装）/
  #788（第 4 段 = Zustand / TanStack Table / ECharts）/ #816（**並行 PR**。IADR 番号が交差する。下記 §並行 PR）。
- 計画書リンク: `planning/.../13_frontend-stack.md`（計画リポ） /
  `planning/docs/ai-implementation-workflow-guide.md`（計画リポ）
  （フェーズ末監査は**証跡〔実行コマンドと出力〕必須**。宣言だけの検証は不合格）

## ★ 本 PR で issue #493 は閉じられない（先に書く）

issue #493 のスコープは **Knip / Plop / Renovate / Husky の 4 件**であり、受け入れ基準は
計画 `13_frontend-stack` §採用技術一覧と実装の**完全一致**である（[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md)
決定 1 の 2026-08-04 追記が「移行完了の定義」を計画本文へ寄せている）。内訳と現況は次のとおり。

| 第 5 段の要素 | 状態 | 根拠 |
| --- | --- | --- |
| Renovate | **着地済み** | #768 / [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md)。直下 `renovate.json` |
| Husky | **着地済み** | 同上。直下 `.husky/` ＋ `src/package.json` の `prepare` |
| **Knip** | **本 PR で導入** | 本仕様書 |
| **Plop.js** | **未着手・本 PR の対象外** | **第 4 段（#788 = Zustand / TanStack Table / ECharts）に従属**する |

**Plop が第 4 段に従属する理由**（2026-08-15 の #493 着手判定コメントで実測済み）: Plop の価値は
「feature の雛形を生成すること」であり、生成すべき feature は第 4 段の技術（Zustand のストア・
TanStack Table のテーブル・ECharts のチャート）を使う形をしている。第 4 段より前に雛形を書くと、
**計画外スタックの feature を量産する型**になる。#788 は大玉群であり本 PR の射程外である。

したがって:

- **PR 本文に `Closes #493` を書かない。`Refs #493` とする。**
- **#493 は Plop（＋ #788 の着地）を待って別 PR で閉じる。**
- 本 PR の受け入れ基準は「計画一覧との完全一致」ではなく「**Knip が導入され、未使用の増加を CI が止める**」である。

> **なお [`20260815_issue-768_renovate-husky.md`](./20260815_issue-768_renovate-husky.md) §対象範囲は
> Knip を「#452 待ち」と書いていた。** 本作業はその判断を**覆す**のではなく、**待っていた理由を
> 別の手段で解消する**。待っていた理由は「画面（#452）を作り直す前に走らせると未使用が大量に出る」
> （[IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) の表が予告した状態）である。
> **baseline ラチェットは「大量に出ている状態」を据え置いたまま「増加だけ」を止める**ので、
> #452 の完了を待つ必要が無い。むしろ **#452 の前に床を打っておかないと、#452 が新たに積む未使用を
> 誰も測れない。** この判断は IADR-0211 決定 3 に記録する。**確定済み仕様書（#768）は書き換えない**
> （`.claude/rules/traceability.repo.md` §Superseded の項。書き換え対象は live な権威文書とコード）。

## 目的・背景

計画 `13_frontend-stack` §採用技術一覧は **Dead Code 検出 = Knip を「採用」**と定めているが、
本リポジトリには**未使用のファイル・export・依存を検出する機構が 1 つも無い**。

- ESLint は `no-unused-vars` を持つが、**これはファイル内スコープの未使用しか見ない**。
  「export したが誰も import しないシンボル」「どこからも到達しないファイル」「`package.json` に
  宣言したが誰も使わない依存」は**素通りする**。
- 実際、SPA は第 1〜3 段で**旧スタックを丸ごと置き換えて**きた（`react-router-dom` の撤去・
  手書き HTTP クライアントの撤去・画面の再実装）。**置き換えの残骸が残っていても誰も赤くならない。**

## 対象範囲

- 対象:
  1. `src/knip.jsonc`（新規）— Knip のスコープ設定。
  2. `src/package.json` / `src/pnpm-lock.yaml` — `knip` を devDependency に足し、`knip` スクリプトを足す。
  3. `scripts/knip-baseline.json`（新規）— 残件のラチェット床（区分ごとの件数＋`$comment`）。
  4. `scripts/check-knip.js`（新規）— Knip を走らせて件数を床と突き合わせる検査器（`--self-test` つき）。
  5. `scripts/scripts.repo.test.js` — 上記の呼び出し（既存の作法どおり companion から）。
  6. `.github/workflows/frontend.yml` — CI 結線（1 ステップ追加）。
  7. `scripts/README.md` — 検査器の表へ 1 行。
  8. [IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md) ＋ `docs/adr/README.md` の索引へ 1 行。
  9. 本仕様書。
- 対象外:
  - **未使用と報告されたコードの削除**。本 PR は**検出の導入**であって片付けではない（別 issue）。
  - **Plop.js**（上記 §本 PR で #493 は閉じられない）。
  - **`scripts/scripts.test.js`**（キット配布物・分類 A。**触らない**）。
  - **`planning/`・`src/ai-stock-trading`**（別リポジトリの submodule）。
  - **`CLAUDE.md` / `.claude/rules/`**（必読規約は 50KB 予算。**0 バイト増**とする）。
  - **`deploy/` / `scripts/k8s-local-up.*`**（並行 OPEN な PR #816 が変更中。交差させない）。
  - **`.github/workflows/frontend-tests.yml`**（#801 / [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md)
    が `include` ⊆ `paths` を固定したばかりであり、触らない）。
  - **バックエンド（C#）の未使用検出**。Knip は JS/TS のツールであり射程外。

### 並行 PR との交差（FIFO の前提）

**OPEN な PR #816** が `deploy/**` / `scripts/k8s-local-up.*` / `docs/adr/IADR-0210_*` /
`docs/adr/README.md` を変更している。

- **資産の交差は `docs/adr/README.md`（ADR 索引）1 ファイルのみ**。両者とも**末尾へ 1 行足すだけ**である。
- **IADR 番号は `IADR-0210` を #816 が取り、本件は `IADR-0211` を使う。**
- **マージ順は FIFO で #816 → 本件**とする。
- **★ #816 が先に着地しなかった場合は本 PR の改番（0211 → 0210）が要る。**
  `scripts/check-adr-numbering.js` は**欠番**を fail 判定に持つため、本 PR 単独では
  **`IADR-0210 が欠番` で必ず fail する。これは想定内**である。それ以外の失敗が無いことは、
  一時プローブ（0211 → 0210 へ改番して実行し、直後に復元）で実測する（§検証 4）。

## 母集合の引き直し（着手時に自分で引いた。issue 本文・先行コメントの一覧は転記していない）

`.claude/rules/traceability.md` 規則 1〜8 ＋ `traceability.repo.md` 規則 9・10 に従い、
**「誤りの側の文字列」で全文書を走査してから**追随先を挙げる。走査は追跡下のファイル全体に対して行い、
除外は `planning/`（別リポ submodule）と `src/ai-stock-trading`（同）のみとした。**拡張子で絞っていない**（規則 3）。

| 軸 | 走査 | 件数 | 扱い |
| --- | --- | --- | --- |
| 1（パスから引く） | `git ls-files \| grep -Ei "knip\|unused\|dead.?code"` | **0 件** | Knip 関連のファイルは 1 つも無い＝新規導入である |
| 2（語から引く・英） | `git grep -ril "knip"` | **10 件** | 下表で 1 件ずつ判断 |
| 3（語から引く・和） | `git grep -lE "未使用\|デッドコード\|Dead Code"` | **68 件** | 大半は「未使用の変数」等の別文脈。Knip に関わるのは軸 2 の 10 件に含まれる |
| 4（先例の作法） | `git ls-files \| grep -E "baseline\|allowlist\|floor"` | **19 件**（うち `scripts/*.json` が 10 件） | ラチェット床の**書式の先例**として読む。追随の対象ではない |
| 5（登録簿） | `scripts/README.md` の検査器表 / `docs/adr/README.md` の ADR 索引 / `.github/workflows/frontend.yml` | 3 箇所 | **新しい検査器を足したら必ず載る 3 箇所**。追随する |

軸 2 の 10 件の判断:

| ファイル | 判断 |
| --- | --- |
| `docs/adr/IADR-0121_...md:122` | 「第 5 段 = 運用系（Knip / …）」。**事実として正しいまま**。追随不要 |
| `docs/adr/IADR-0125_...md:84` | 「Knip の未使用検出＝出る（利用者は #452）」。**予告として正しいまま**。追随不要 |
| `docs/adr/IADR-0203_...md:113` | 「Knip / Plop は親 #493」。**Knip が本 PR で着地すると古くなる**が、これは**フォローアップ欄の記述**であり、`docs/adr/` の確定済み記録である。**書き換えず、IADR-0211 側から #493 の残件を書く**（[IADR-0171](../adr/IADR-0171_backlink-obligation-one-way.md)：リンクの義務は片方向） |
| `docs/specs/20260804_issue-446_...md:72` / `20260804_issue-490_...md:96` / `20260804_issue-496_...md:91` | 段の割付表。確定済み仕様書。**書き換えない** |
| `docs/specs/20260808_issue-562_...md:122` | Husky の文脈。Knip 無関係 |
| `docs/specs/20260815_issue-454_...md:128` | 棚卸し表。**`src/knip.json` を予定資産として挙げている**（本 PR は同じ場所の `.jsonc` を置いた。理由は §設計 決定 1）。確定済み。**書き換えない** |
| `docs/specs/20260815_issue-768_...md:36,258` | 「Knip は #452 待ち」。**本 PR が理由を解消する**が、確定済み仕様書のため**書き換えず**、上記 §で本仕様書と IADR-0211 に判断を書く |
| `feedback/20260804_...md` | 段階分割の裁定の記録。追随不要 |

**結論: 既存文書への追随書き換えは 0 件。** 追加は軸 5 の 3 箇所（登録簿）＋新規ファイルのみ。

**規則 10（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）**の適用: 本 PR が新たに
誤りにする自分の記述は **`IADR-0203` のフォローアップ欄「Knip / Plop は親 #493」の Knip の部分**だけである。
上表のとおり `docs/adr/` の確定記録は書き換えず、**新しい ADR（IADR-0211）が現況を持つ**。

## 設計

決定の正本は [IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md)。ここには本作業での適用形だけを書く。

### 決定 1: 設定は `src/knip.jsonc`（コメント付き JSON）

- **`.json` では設定の隣に理由を書けない。** Knip は設定を厳格に検証し、未知のキーを弾く。
  実測（`"description": [...]` を足した場合）:

  ```console
  $ cd src && pnpm exec knip --no-progress
  ERROR: Invalid input (unrecognized_keys: description)
  $ echo "EXIT=$?"
  EXIT=2
  ```

  `renovate.json` が `description` 配列で理由を持つのと同じことを、Knip では **JSONC のコメント**で行う。
- **`knip.config.ts` を採らない。** 本設定は**宣言だけ**で済み、設定の実行を要求する理由が無い。
  `eslint.config.js` / `vite.config.ts` が JS/TS なのは**ロジックがあるから**である。
- 置き場は `src/`（pnpm workspace ルート。`.prettierignore` / `eslint.config.js` / `vitest.config.ts` と同じ階層）。
  #454 の棚卸し表が予定資産として挙げた `src/knip.json` と**同じ場所・別拡張子**である。
- `src/package.json` に `knip` スクリプトを足す。**これは CI のゲートではなく人が内訳を読む入口**である
  （素の Knip は残件 41 件で必ず exit 1 になるため、そのままゲートにすると常時赤になる）。

### 決定 2: 除外の単一情報源 —— 二重管理は 1 箇所残し、腐りは機械で見る

| 除外したい集合 | 既存の単一情報源 | Knip から読めるか | 本 PR の扱い |
| --- | --- | --- | --- |
| workspace のメンバ | `src/pnpm-workspace.yaml` | **読める**（Knip が pnpm workspace を自動認識） | **書かない** |
| `node_modules` / `dist` / `coverage` 等 | Knip 内蔵の既定 | **既定で除外** | **書かない** |
| **`src/ai-stock-trading`（別プロジェクト）** | `src/.prettierignore` ／ `src/eslint.config.js` ／ `.gitmodules`（`scripts/lib/excluded-units.js` が読む） | **読めない** | **`knip.jsonc` の `ignoreWorkspaces` に書く（＝二重管理が 1 箇所残る）** |
| orval 生成物 | `src/.prettierignore` ／ `src/eslint.config.js` の `ignores` | **読めない** | **`ignore` ではなく `entry`**（決定 3） |

**★ 二重管理は残った。避けられなかった理由**（IADR-0211 決定 2）:

1. Knip の設定は宣言であり、他ファイルを読む式を書けない。`knip.config.ts` にすれば
   `.gitmodules` から導出できるが、**導出ロジックそのものがテストされない新しいコード**になる。
   `src/eslint.config.js` 冒頭が同じ理由で専用検査スクリプトを作らないと述べている。
2. **AST を外す意図は既に 3 通りの書式で 3 箇所に存在する**（prettier は `ai-stock-trading/`、
   ESLint は `ai-stock-trading/frontend/test`、検査器群は `.gitmodules` からの導出）。
   **すでに単一情報源ではない。** 4 つ目の増分は「1 箇所増える」であって「単一情報源が壊れる」ではない。
3. **腐りは機械で見る。** `node scripts/check-knip.js --self-test` が
   **「`.gitmodules` の `src/<unit>` submodule が `knip.jsonc` の `ignoreWorkspaces` で外れているか」**を突き合わせる。
   `ci.yml` の `scripts-tests`（`REQUIRE_REPO_TESTS=1`）が毎 PR で走らせる。M3 で実測した（§変異試験）。

### 決定 3: orval 生成物と「発見できない入口」は `ignore` ではなく `entry`

`ignore` にすると Knip は生成物の中の import を辿らなくなり、**生成物からしか使われていない
本体の export と依存が「未使用」として湧く**（＝偽陽性）。`check-chunk-budget.js` 冒頭と同じ
判断軸（**検出漏れは開示してよいが、偽陽性は塞ぐ**）である。

同じ理由で、**Knip が発見できない実在の入口**も `entry` に置いた。

| ファイル | 誰が起動するか | `ignore` にした場合の副作用 |
| --- | --- | --- |
| `platform/frontend/src/foundation/api/generated/**/*.ts` | orval 生成物（`index.html` 側から到達しない経路がある） | `msw` / `@faker-js/faker` が未使用依存に化ける |
| `lingui.config.ts` | `pnpm run i18n`（lingui CLI） | `@lingui/format-po` が未使用依存に化ける（**実測: entry にした瞬間 devDeps 5 → 4**） |
| `eslint.templates.config.js` | `pnpm run lint:templates` | — |
| `platform/frontend/public/config.js` | `index.html` の `<script src="/config.js">` | — |

### 決定 4: CI ゲートは baseline ラチェット（増加も減少も fail・fail-closed）

- 床は `scripts/knip-baseline.json`（区分ごとの件数＋区分ごとの `$comment`）。
- `scripts/check-knip.js` が Knip を `--reporter json` で走らせ、区分ごとに突き合わせる。
  判定は **増加 / 減少 / 新区分** の 3 つ。
- **fail-closed**（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)）: Knip の終了コードが
  0 / 1 以外、JSON が読めない、`issues` 配列が無い、**床が 0 件でないのに走査結果が空**、
  床に未知の区分名がある —— いずれも **0 件で緑にせず落とす**。
- 段階ポリシーは `check-chunk-budget.js` / `check-static-egress.js` と同じ（引数なし = warn ＋ exit 0、
  `--require` = fail。**CI はこちら**）。

### 決定 5: CI 結線は `frontend.yml` の `build-test` へ 1 ステップ

- `paths:` には **`src/knip.jsonc` の 1 行だけ**を push / pull_request の両方へ足した
  （`.prettierignore` を足したときと同じ理由）。
- **`frontend-tests.yml` は 1 バイトも触っていない。** #801 / [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md)
  の不変条件は「`src/vitest.config.ts` の `test.include` が拾うパスが `frontend-tests.yml` の `paths:` に載る」であり、
  **`src/knip.jsonc` は `test.include`（`*.{test,spec}.{ts,tsx}`）に一致しない**ため影響しない。
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` の当該テストが緑であることで確認した（§検証）。
- 位置は `Format check (prettier)` の直後・`Build` の前（Knip は静的解析であり `dist` を要らない）。

### 決定 6: 検出しないこと（本検査は網羅ではない）

- **雛形（`templates` 配下の frontend）を走査しない。** pnpm workspace のメンバだが Knip の
  project root（`src/`）の外にあり射程へ入らない。**実測**（プローブして戻した）:

  ```console
  $ printf '\nexport const KNIP_PROBE_TEMPLATES = 1;\n' >> templates/unit-template/frontend/src/features/sample/hooks/useSampleFilter.ts
  $ cd src && pnpm exec knip --no-progress | grep -n "KNIP_PROBE_TEMPLATES\|^Unused"
  1:Unused dependencies (1)
  3:Unused devDependencies (4)
  10:Unused exports (18)
  29:Unused exported types (17)
  # → 件数は 1 件も動かず、KNIP_PROBE_TEMPLATES も出ない
  ```

  雛形は typecheck / lint / format / 単体テストが別途見ている（`frontend.yml` / `frontend-tests.yml`）。
- **バックエンド（C#）は対象外**（Knip は JS/TS のツール）。
- **どの識別子が未使用かは見ていない**（数だけの突合）。同じ区分で 1 件消えて 1 件増えると素通りする。

## 受け入れ基準

- [x] `src/knip.jsonc` があり、`cd src && pnpm run knip` が走る
- [x] Knip の走査から `src/ai-stock-trading` が外れている（**692 → 648 件**。§実測）
- [x] orval 生成物が偽陽性を生んでいない（**648 → 44 件**。§実測）
- [x] `scripts/knip-baseline.json` が区分ごとの件数と `$comment` を持つ
- [x] `node scripts/check-knip.js --require` が baseline と一致して exit 0
- [x] `node scripts/check-knip.js --self-test` が通る（15 件）
- [x] `scripts/scripts.repo.test.js` から呼ばれ、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る
- [x] `.github/workflows/frontend.yml` に結線され、`frontend-tests.yml` は無変更
- [x] 変異試験 M1〜M4 がすべて期待どおり fail（M4 は fail-closed）する
- [x] **未使用と報告されたコードを 1 行も削除していない**
- [x] `CLAUDE.md` / `.claude/rules/` が 0 バイト増（`git status` に現れない）
- [ ] PR 本文が `Refs #493`（`Closes` ではない）で、閉じない理由を書いている ← **PR 作成時に満たす**

## テスト方針

- `scripts/check-knip.js --self-test`（15 件）——出力の解析・床との突合という**純関数**を固定入力で試験し、
  加えて実データ 3 件（`.gitmodules` との突合・床の区分名・`src/package.json` の入口）を見る。
- `scripts/scripts.repo.test.js`（companion）から 2 件で呼ぶ。**`scripts/scripts.test.js` は触っていない。**
- 変異試験 M1〜M4（下記）。

## 実測

環境: `node v22.22.2` / `pnpm 10.33.0` / `knip 6.32.2` / `origin/develop = 49ec8e32`。
`planning` と `src/ai-stock-trading`（`7f69fb5`）はいずれも populate 済みで測った。

### 素の Knip（設定なし）

```console
$ cd src && pnpm exec knip --no-progress > /tmp/knip-bare.log 2>&1; echo "EXIT=$?"
EXIT=1
$ grep -n "^Unused" /tmp/knip-bare.log
1:Unused files (51)
53:Unused dependencies (1)
55:Unused devDependencies (7)
63:Unused exports (147)
211:Unused exported types (486)
```

**合計 692 件。** 先行コメント（2026-08-15）の **691 件**とは **+1**（`Unused files` が 50 → 51）である。

- **差の所在は `Unused files` の 1 件だけ**で、他の 4 区分は完全に一致した（1 / 7 / 147 / 486）。
- 内訳を自分で数えると **AST 28 件（`.claude/hooks` 3 ＋ `scripts` 19 ＋ `frontend/test/foundation-stub` 6）
  ＋ 本リポ 3 件 ＋ orval 生成物 20 件 = 51 件**である。先行コメントは
  「`ai-stock-trading/scripts` **18 件**ほか」と書いており、**実測の 19 件と 1 件ずれる**。
  差の 1 件は AST 側の `scripts` にあるとみられる。
- **原因は特定できなかった。** 最も確からしいのは **Knip の版差**である（先行コメントには版が記録されておらず、
  本作業は今日 `knip@6.32.2` を新規に解決した）。**先行コメントの数値は転記せず、以降はすべて自分の実測値を使う。**

### 設定を 1 つ変えるごとの件数の推移

| # | 設定 | files | deps | devDeps | unlisted | exports | types | **合計** | 差分 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 設定なし（素） | 51 | 1 | 7 | 0 | 147 | 486 | **692** | — |
| 1 | `ignoreWorkspaces: ["ai-stock-trading/**"]` | 23 | 1 | 7 | 0 | 141 | 476 | **648** | **−44** |
| 2 | ＋ `platform/frontend.entry` に orval 生成物 | 3 | 1 | 5 | 0 | 18 | 17 | **44** | **−604** |
| 3 | ＋ ルート `entry` に `lingui.config.ts` / `eslint.templates.config.js`、`platform/frontend.entry` に `public/config.js` | 0 | 1 | 4 | **1** | 18 | 17 | **41** | **−3** |

段 1 の補足（**どちらの書き方でも同じ結果になることを別々に測った**）:

| 書き方 | 合計 | 備考 |
| --- | --- | --- |
| `ignoreWorkspaces` のみ | 648 | 採用。`ai-stock-trading/scripts`（workspace 外のファイル）も消えた |
| `ignore: ["ai-stock-trading/**"]` のみ | 648 | 同じ |
| 両方 | 648 | Knip が `Configuration hints (1) … Remove from ignore` を出す（恒久的なノイズになるので採らない） |

段 3 で **`unlisted` が 0 → 1 件へ増えた**。これは退行ではなく、**`lingui.config.ts` を entry にしたことで
初めて見えた実在の欠落**である（`import type { CatalogFormatter } from '@lingui/conf'` が
`package.json` に宣言されていない。pnpm の hoist で `@lingui/cli` の推移依存として解決できてしまっていた）。
**本 PR では直さない**（検出の導入であって片付けではない）。床に理由つきで載せ、別 issue へ送る。

### 床の最終件数と、各区分がなぜ残っているのか

**合計 41 件。** 理由の正本は `scripts/knip-baseline.json` の `$comment_*`。要約:

| 区分 | 件数 | 中身 | なぜ残るのか |
| --- | --- | --- | --- |
| `dependencies` | 1 | `knowledge/frontend` の `oidc-client-ts` | **真の未使用**（`knowledge/frontend/src` に参照 0 件）。ADR-0032 の BFF セッション移行（#439 / #446）で platform 側ごと消える見込み。**先に消さない** |
| `devDependencies` | 4 | `@lingui/babel-plugin-lingui-macro` / `@vitejs/plugin-react`(packages/ui) / `@tanstack/router-core` / `tailwindcss`(platform) | いずれも**静的解析で追えない使われ方**（babel プラグインの文字列指定・`declare module` の型拡張・`@tailwindcss/vite` の peer）。**ただし `@vitejs/plugin-react`（packages/ui）だけは直接参照が 0 件**で、真に不要な可能性がある（下記 §計画書との差異） |
| `unlisted` | 1 | `@lingui/conf`（`lingui.config.ts`） | **真の宣言漏れ**。本 PR の設定変更で初めて可視化された |
| `exports` | 18 | knowledge features 11 ＋ platform foundation 7 | 大半は **#452（画面の再実装）が触る場所**。先に消すと衝突する |
| `types` | 17 | knowledge features 10 ＋ platform foundation 7 | 同上。型は実行時の挙動を変えないぶん残りやすい |

`IADR-0125` は着手前から「**Knip（第 5 段）の未使用検出は出る（利用者は #452）**」と予告していた。
本 PR はその予告どおりの状態を**床として固定する**ものである。

## 変異試験

**すべて実行し、生の出力を残し、バイト単位で復元を確認した。**

### M1: baseline の件数を 1 減らす → 検査器が fail する

```console
$ python3 -c "import json;d=json.load(open('scripts/knip-baseline.json'));d['counts']['exports']=17;open('scripts/knip-baseline.json','w').write(json.dumps(d,ensure_ascii=False,indent=2)+'\n')"
$ node scripts/check-knip.js --require; echo "M1_EXIT=$?"
✗ [check-knip] 未使用コード・依存の床から外れました:
  - exports: 18 件 > 床 17 件（+1）。未使用が増えている。使うか消すこと。

内訳は `cd src && pnpm run knip` で確認できる。設計と残件の理由は scripts/knip-baseline.json の $comment と docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md を参照。
M1_EXIT=1
$ cp /tmp/backup/knip-baseline.json scripts/knip-baseline.json && cmp /tmp/backup/knip-baseline.json scripts/knip-baseline.json && echo "CMP_OK"
CMP_OK
$ node scripts/check-knip.js --require; echo "M1_RESTORED_EXIT=$?"
[check-knip] OK: 床どおり 41 件（dependencies 1 / devDependencies 4 / exports 18 / types 17 / unlisted 1）
M1_RESTORED_EXIT=0
```

### M2: 未使用の export を 1 つ**新たに足す** → 件数が実際に増えて fail する

```console
$ printf '\n// M2 変異試験（#493）: 誰も import しない export を 1 つ足す。直後に復元する。\nexport const KNIP_M2_PROBE = 1;\n' >> src/knowledge/frontend/src/features/sc11-config/driftView.ts
$ cd src && pnpm exec knip --no-progress | grep -n "^Unused\|KNIP_M2_PROBE"
1:Unused dependencies (1)
3:Unused devDependencies (4)
10:Unused exports (19)
21:KNIP_M2_PROBE                       knowledge/frontend/src/features/sc11-config/driftView.ts:98:14
30:Unused exported types (17)
$ node scripts/check-knip.js --require; echo "M2_EXIT=$?"
✗ [check-knip] 未使用コード・依存の床から外れました:
  - exports: 19 件 > 床 18 件（+1）。未使用が増えている。使うか消すこと。
M2_EXIT=1
$ cp /tmp/backup/driftView.ts src/knowledge/frontend/src/features/sc11-config/driftView.ts && cmp /tmp/backup/driftView.ts src/knowledge/frontend/src/features/sc11-config/driftView.ts && echo "CMP_OK"
CMP_OK
$ git --no-pager diff --stat -- src/knowledge/frontend/src/features/sc11-config/driftView.ts
（出力なし＝差分なし）
$ node scripts/check-knip.js --require; echo "M2_RESTORED_EXIT=$?"
[check-knip] OK: 床どおり 41 件（dependencies 1 / devDependencies 4 / exports 18 / types 17 / unlisted 1）
M2_RESTORED_EXIT=0
```

**「増えるはず」ではなく、`Unused exports` が 18 → 19 に動くことを実測した。**

### M3: 設定の除外を壊す（AST の除外を外す） → 件数が跳ね上がり fail する

```console
$ python3 -c "..."   # knip.jsonc の "ignoreWorkspaces": ["ai-stock-trading/**"] → []
$ cd src && pnpm exec knip --no-progress | grep -n "^Unused"
1:Unused files (28)
30:Unused dependencies (1)
32:Unused devDependencies (4)
39:Unused exports (24)
64:Unused exported types (27)
$ node scripts/check-knip.js --require; echo "M3_EXIT=$?"
✗ [check-knip] 未使用コード・依存の床から外れました:
  - 新しい区分 "files" が 28 件出た。内容を確認し、直すか baseline へ理由つきで追加すること（Knip の版が上がって検出種別が増えた場合もここへ出る）。
  - exports: 24 件 > 床 18 件（+6）。未使用が増えている。使うか消すこと。
  - types: 27 件 > 床 17 件（+10）。未使用が増えている。使うか消すこと。
M3_EXIT=1
$ node scripts/check-knip.js --self-test; echo "M3_SELFTEST_EXIT=$?"
（… 実データ突合が失敗）
+   'ai-stock-trading'
+ ]
- []
M3_SELFTEST_EXIT=1
$ cp /tmp/backup/knip.jsonc src/knip.jsonc && cmp /tmp/backup/knip.jsonc src/knip.jsonc && echo "CMP_OK"
CMP_OK
$ node scripts/check-knip.js --require; echo "M3_RESTORED_EXIT=$?"
[check-knip] OK: 床どおり 41 件（dependencies 1 / devDependencies 4 / exports 18 / types 17 / unlisted 1）
M3_RESTORED_EXIT=0
$ node scripts/check-knip.js --self-test | tail -2
[check-knip] self-test: 15 件すべて通過
```

**2 つの門が別々に落ちた**ことに意味がある —— 実データのラチェット（41 → 84 件）だけでなく、
**`node_modules` の無い CI 経路（`ci.yml` の `scripts-tests`）でも self-test の `.gitmodules` 突合が落ちる**。
`ignoreWorkspaces` を外しても走査結果は 692 件へは戻らない（84 件）。生成物の `entry` は生きているためである。

### M4: 検査器のパースを壊す → 0 件で緑にならず fail-closed する

**M4a: 区分キーの抽出を壊す**（`if (key === 'file') continue;` → 全キーを読み飛ばす）。

```console
$ node scripts/check-knip.js --require; echo "M4a_EXIT=$?"
✗ [check-knip] Knip の指摘が 1 件も無い一方、床は 41 件を期待している。走査が空振りしている疑いがあるため 0 件で緑にしない。
M4a_EXIT=1
$ cp /tmp/backup/check-knip.js scripts/check-knip.js && cmp /tmp/backup/check-knip.js scripts/check-knip.js && echo "CMP_OK_M4a"
CMP_OK_M4a
```

**M4b: `parsePayload` を典型的な fail-open（読めなければ `{issues: []}`）へ壊し、
Knip の実行ファイルを「非 JSON を返す」偽物へ差し替える。**

```console
$ printf '#!/bin/sh\necho "<html>gateway timeout</html>"\nexit 0\n' > src/node_modules/.bin/knip
$ node scripts/check-knip.js --require; echo "M4b_MUTANT_EXIT=$?"
✗ [check-knip] Knip の指摘が 1 件も無い一方、床は 41 件を期待している。走査が空振りしている疑いがあるため 0 件で緑にしない。
M4b_MUTANT_EXIT=1
$ cp /tmp/backup/check-knip.js scripts/check-knip.js   # 検査器だけ復元（偽 knip はそのまま）
$ node scripts/check-knip.js --require; echo "M4b_CORRECT_EXIT=$?"
✗ [check-knip] Knip の JSON 出力を解析できない（Unexpected token '<', "<html>gate"... is not valid JSON）。出力の形が変わったか、Knip が途中で落ちている。0 件として扱わない。
M4b_CORRECT_EXIT=1
```

**門が 2 枚ある**ことが実測できた —— JSON 解析の門を壊しても、**床が 0 件でないのに走査が空**という
2 枚目の門で止まる。**どちらの経路でも exit 0（緑）にはならない。**

**M4c: Knip がクラッシュ（exit 2）した場合。**

```console
$ printf '#!/bin/sh\necho "boom" >&2\nexit 2\n' > src/node_modules/.bin/knip
$ node scripts/check-knip.js --require; echo "M4c_EXIT=$?"
✗ [check-knip] Knip が異常終了した（exit 2）。0 件として扱わない。
boom
M4c_EXIT=1
$ cp /tmp/backup/knip-bin-real src/node_modules/.bin/knip && chmod 755 src/node_modules/.bin/knip && cmp /tmp/backup/knip-bin-real src/node_modules/.bin/knip && echo "CMP_OK_BIN"
CMP_OK_BIN
$ node scripts/check-knip.js --require; echo "M4_RESTORED_EXIT=$?"
[check-knip] OK: 床どおり 41 件（dependencies 1 / devDependencies 4 / exports 18 / types 17 / unlisted 1）
M4_RESTORED_EXIT=0
```

### `--update` の冪等性（床の書式が壊れないこと）

```console
$ node scripts/check-knip.js --update; echo "UPDATE_EXIT=$?"
[check-knip] 床を更新した: {"dependencies":1,"devDependencies":4,"exports":18,"types":17,"unlisted":1}（合計 41 件）
UPDATE_EXIT=0
$ cmp /tmp/backup/knip-baseline.json scripts/knip-baseline.json && echo "IDENTICAL"
IDENTICAL
```

## egress policy（08_data-egress-policy）への適合

**「テレメトリを送らない」を宣言ではなく実測で確かめた。** 3 つの面で見る。

1. **依存**: `knip@6.32.2` の `dependencies` は
   `fdir / formatly / get-tsconfig / jiti / oxc-parser / oxc-resolver / picomatch / smol-toml /
   strip-json-comments / tinyglobby / unbash / yaml / zod` の 13 個で、**analytics / エラー報告 SaaS のクライアントは 1 つも無い**。
   `install` / `postinstall` / `preinstall` スクリプトも**持たない**（`pnpm` の `onlyBuiltDependencies` は
   `esbuild` のみを許可しており、いずれにせよ走らない）。
2. **静的走査**: 配布物 `node_modules/knip/dist` をネットワーク API と既知の SaaS 名で走査すると 16 ファイルが当たるが、
   **中身を見るとすべて `segment`（パスの「セグメント」）という語と、Knip 自身が持つ
   「Sentry の設定ファイルを検出するプラグイン」の名前**であり、送信コードではない。
3. **実行時の見張り（決定的な証拠）**: `fetch` / `http.request` / `https.request` / `net.connect` /
   `net.createConnection` / `dns.lookup` を**呼ばれたら throw する**関数へ差し替えた preload 付きで Knip を走らせた。

```console
$ node --require /tmp/no-egress-preload.cjs node_modules/knip/bin/knip.js --no-progress --reporter json > /tmp/egress-run.log 2>&1
$ echo "EXIT=$?"; grep -c "EGRESS DETECTED" /tmp/egress-run.log
EXIT=1
0
$ # 見張り下でも件数は同じ（＝ネットワークを止めても走査は完走している）
{"dependencies":1,"devDependencies":4,"exports":18,"types":17,"unlisted":1}
```

**外部送信は 0 件。** よって無効化すべき設定は無い。`scripts/check-static-egress.js` の走査対象
（`dist` / `storybook-static`）も変わらない —— Knip は devDependency の静的解析ツールであり、
**ビルド成果物に何も混ぜない**。

## 検証

順序は [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) / `docs/DEFINITION_OF_DONE.md`
の「`git add -A` → 検査器 → コミット」に従った。**すべて `cmd > log 2>&1; echo "EXIT=$?"` の形で
終了コードを別途取っている**（`| tail` を挟むと終了コードが `tail` のものになり、クラッシュが
exit 0 に見える。本作業中に別の場面で 1 回起きた形である）。

### 1. 文書・メタの検査器（`git add -A` の後）

| コマンド | EXIT |
| --- | --- |
| `node scripts/check-doc-links.js` | **0** |
| `node scripts/check-doc-type-vocabulary.js` | **0** |
| `node scripts/check-doc-status-vocabulary.js` | **0** |
| `node scripts/check-cross-repo-refs.js` | **0** |
| `node scripts/check-plan-id-qualification.js` | **0** |
| `node scripts/check-adr-numbering.js` | **1**（★ `IADR-0210 が欠番`。**想定内**。下記 4） |
| `node scripts/check-reading-budget.js` | **0** |
| `node scripts/check-kit-sync.js` | **0** |
| `node scripts/check-knip.js` | **0** |
| `node scripts/check-knip.js --require` | **0** |
| `node scripts/check-knip.js --self-test` | **0**（16 件すべて通過） |

必読規約の総量は **50,130 バイト（予算 51,200 の 97.9%）で着手時から 1 バイトも動いていない**
（`CLAUDE.md` 19,981 ／ `traceability.md` 24,592 ／ `traceability.repo.md` 5,557）。**0 バイト増**を守った。

### 2. フロントエンドのゲート（`src/`）

| コマンド | EXIT |
| --- | --- |
| `pnpm run typecheck` | **0** |
| `pnpm run lint` | **0** |
| `pnpm run lint:templates` | **0** |
| `pnpm run format:check` | **0** |
| `pnpm run format:templates` | **0** |

> **整形で 1 度落ちた（実測 → 是正）。** prettier は `.jsonc` を整形対象に取り、本リポの
> `.prettierrc.json` は `trailingComma: "all"` であるため **`knip.jsonc` に末尾カンマを足す**。
> Knip 本体はこれを許すが `JSON.parse` は許さないため、**整形するたびに検査器だけが設定を
> 読めなくなる**形だった。`check-knip.js` に `stripTrailingCommas` / `parseJsonc` を足して閉じ、
> self-test に回帰試験（`parseJsonc: prettier が付ける末尾カンマを許す`）を置いた。
> **`.prettierignore` へ逃がさなかった** —— 同ファイル冒頭が「整形すると落ちるからを理由に
> 除外していくと、除外リストそのものがゲートの無効化装置になる」と警告している。

### 3. スクリプトのテスト

| コマンド | EXIT | 備考 |
| --- | --- | --- |
| `node scripts/scripts.test.js` | **1** | 失敗は `IADR-0210 が欠番` の 1 件のみ（下記 4） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **1** | 同上。失敗までに 321 件が通過 |

**`scripts/scripts.test.js`（キット配布物・分類 A）は 1 バイトも触っていない**（`check-kit-sync` が EXIT=0）。
固有テストは companion（`scripts.repo.test.js`）へ 2 件足した。

> **検査器の母集合のラチェットが設計どおり発火した。** `scripts.repo.test.js` の
> 「検査器の母集合が N 本」の固定が **37 → 38** で落ち、宣言を促した。コメントつきで 38 へ更新した。
> `check-knip.js` は **git を一切呼ばない**ため `TRACKED_CHECKERS` / `HEAD_CHECKERS` のどちらにも載らない
> （同テストの実挙動 ↔ 宣言の双方向突合が緑であることで確認）。
> **ADR 索引タイトルのラチェットも発火した**（`title-too-long`）。索引セルを 262 → 196 文字へ縮めた
> （`maxTitleChars` = 200。**baseline へ足していない**）。

### 4. ★ `IADR-0210 が欠番` 以外の失敗が無いことの実測（一時プローブ）

**本 PR 単独では `check-adr-numbering.js` が必ず落ちる**（`IADR-0210` は並行 OPEN な PR #816 が取る）。
**それ以外の失敗が無いこと**を、`IADR-0211` → `IADR-0210` へ**一時的に全面改番して測り、直後に復元**した。

改番の母集合は「`IADR-0211` を含む追跡下の全ファイル」を走査して引いた（**記憶で挙げていない**）。

```console
$ grep -rl "IADR-0211" --exclude-dir=node_modules --exclude-dir=.git --exclude-dir=planning --exclude-dir=ai-stock-trading .
.github/workflows/frontend.yml
docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md
docs/adr/README.md
docs/specs/20260816_issue-493_knip-unused-detection.md
scripts/README.md
scripts/check-knip.js
scripts/knip-baseline.json
scripts/scripts.repo.test.js
src/knip.jsonc
src/package.json
（10 ファイル。ファイル名の改番も含めて sed ＋ mv で 0210 へ寄せた）
```

プローブ時の結果:

| コマンド | プローブ時 EXIT | 本来（0211）の EXIT |
| --- | --- | --- |
| `node scripts/check-doc-links.js` | 0 | 0 |
| `node scripts/check-doc-type-vocabulary.js` | 0 | 0 |
| `node scripts/check-doc-status-vocabulary.js` | 0 | 0 |
| `node scripts/check-cross-repo-refs.js` | 0 | 0 |
| `node scripts/check-plan-id-qualification.js` | 0 | 0 |
| **`node scripts/check-adr-numbering.js`** | **0** | **1**（欠番） |
| `node scripts/check-reading-budget.js` | 0 | 0 |
| `node scripts/check-kit-sync.js` | 0 | 0 |
| `node scripts/check-knip.js --require` | 0 | 0 |
| `node scripts/scripts.test.js` | **0**（`✓ 636 tests passed`） | 1（欠番のみ） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **0**（`✓ 636 tests passed`） | 1（欠番のみ） |

```console
$ node scripts/check-adr-numbering.js --dir <改番した一時コピー>; echo "EXIT=$?"
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。
EXIT=0
```

**復元はバイト単位で確認した。**

```console
$ tar -xf /tmp/probe-backup3/files.tar   # 10 ファイルを一括で戻す
$ for f in $(cat /tmp/iadr0211-files.txt); do cmp -s "$f" "/tmp/cmpdir3/$f" || echo "DIFF: $f"; done
$ echo "CMP_ALL_OK=yes"
CMP_ALL_OK=yes
$ ls docs/adr/IADR-021*.md
docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md
```

**結論: `IADR-0210 が欠番` を除けば、すべての検査器とテストが緑である。**
**#816 が先に着地しなければ、本 PR は 0211 → 0210 へ改番する**（追随先は上の 10 ファイル ＋ **PR タイトル**）。

### 5. 触っていないことの確認

```console
$ git status --short
M  .github/workflows/frontend.yml
A  docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md
M  docs/adr/README.md
A  docs/specs/20260816_issue-493_knip-unused-detection.md
M  scripts/README.md
A  scripts/check-knip.js
A  scripts/knip-baseline.json
M  scripts/scripts.repo.test.js
A  src/knip.jsonc
M  src/package.json
M  src/pnpm-lock.yaml
```

- **`CLAUDE.md` / `.claude/rules/` … 変更なし**（0 バイト増）
- **`scripts/scripts.test.js` … 変更なし**（分類 A）
- **`.github/workflows/frontend-tests.yml` … 変更なし**（#801 / IADR-0209）
- **`deploy/` / `scripts/k8s-local-up.*` … 変更なし**（並行 PR #816 と交差させない）
- **`planning/` / `src/ai-stock-trading` … 変更なし**（別リポジトリ）
- **未使用と報告されたコード … 1 行も削除していない**

## 計画書との差異

- **差異: あり（1 件・実装に閉じる）。** 計画 `13_frontend-stack` §採用技術一覧は Knip を「採用」と定めるだけで、
  **ゲートの厳しさ（0 件か、ラチェットか）を定めていない**。本作業は**ラチェット**を採った
  （[IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md) 決定 4）。
  計画の要求は「Dead Code 検出ツールとして Knip を採用する」であり、**その要求は満たしている**。
  **計画側へ環流しない** —— ゲートの厳しさは実装に閉じた判断であり、
  本リポには同型の先例が 4 つある（`backend-library` / `chunk-budget` / `adr-index-title` / `test-spec-coverage`）。
- **計画の誤り・不足は見つからなかった。**
- **実装側で見つかった不整合（計画とは無関係）**:
  - `@lingui/conf` の宣言漏れ（`unlisted` 1 件）。
  - `packages/ui` の `@vitejs/plugin-react`（直接参照 0 件）。
    **先行コメントは 7 件の devDeps を「いずれも vite / vitest / PostCSS の設定で現に使われている」と書いていたが、
    自分で当たり直すとこれだけは当たらなかった**（`.storybook/main.ts` は `@storybook/react-vite` 経由で使い、
    `@vitejs/plugin-react` を直接 import していない）。**本 PR では触らず、床の `$comment` に書いて別 issue へ送る。**

## 未決事項

1. **#493 の残り（Plop.js）**。第 4 段（#788）に従属するため本 PR では着手しない。**#493 は open のまま残る。**
2. **床の 41 件の片付け**（別 issue）。とくに `@lingui/conf` の宣言追加と
   `packages/ui` の `@vitejs/plugin-react` の精査は、他の 39 件（#452 に従属）と違って**いま単独で直せる**。
3. **雛形（`templates`）を Knip の射程へ入れるか**。入れるには Knip をリポジトリルートから走らせるか、
   雛形専用にもう 1 回走らせる必要がある。**本 PR では穴として明記するに留めた**
   （`check-static-egress.js` / `check-chunk-budget.js` と同じ作法）。
4. **IADR 番号の前提**。本 PR は `IADR-0211` を採り、`IADR-0210` を並行 PR #816 が取る前提で
   **FIFO（#816 → 本件）**を置いている。**#816 が先に着地しないなら改番（0211 → 0210）が要る。**
