---
title: 作業仕様書 — 確定済み決定の追随（pnpm workspace のグロブ 2 → 3 本／整形範囲の単一情報源の例外）（#802）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - FR-14
  - IADR-0056
  - IADR-0060
  - IADR-0121
  - IADR-0141
  - IADR-0171
  - IADR-0183
  - IADR-0188
  - IADR-0190
  - IADR-0191
  - IADR-0203
  - IADR-0205
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
related_specs:
  - "../adr/IADR-0121_spa-stack-migration-staging.md"
  - "../adr/IADR-0203_renovate-husky-hook-scope.md"
  - "../adr/IADR-0056_repo-unit-structure-platform-knowledge.md"
  - "20260816_chore_unit-template-frontend-drift.md"
---

# 作業仕様書: 確定済み決定の追随（#802）

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（文書統制のメタ作業）。無採番の根拠は `.claude/rules/traceability.repo.md`
  「`NFR` の採番」——計画側の `NFR-01`〜`NFR-27` は稼働する製品の要件であり、**決定文書の追随に
  当たる番号は 1 件も無い**（[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2 / [IADR-0188](../adr/IADR-0188_unnumbered-nfr-applies-to-all-work.md)）。派生して `FR-14`（可変機能
  ユニットの追加＝雛形の射程）に触れる。
- 関連する実装 ADR: [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 2（pnpm workspace のメンバ）／[IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3
  （lint-staged の却下理由＝整形範囲の単一情報源）／[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 4（旧 npm workspaces）／
  [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)（雛形を CI の射程へ入れる）
- 関連 issue / PR: #802（本作業）／#777（`dca76ce`。追随の対象となった変更）／#784（雛形のずれが
  顕在化した事故）／#562（整形ゲート）／#768（[IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md)）

## 2. 目的・背景

PR #777 が入れた 2 つの変更は**妥当**である。しかし**確定済みの決定側が追随していない**。

1. `src/pnpm-workspace.yaml` のグロブが 2 → 3 本（`'../templates/*/frontend'` 追加）になったが、
   [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 2（`Accepted`）は「`'*/frontend'` と `'packages/*'` を**列挙する**」の形で
   2 本を確定したままである。
2. `src/package.json` に整形・lint の 2 本目の入口（`format:templates` / `lint:templates`）が入ったが、
   [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3 は lint-staged を「**整形範囲の単一情報源を 2 本に割る**」という理由で却下して
   いる。**却下された案と、入った例外が、規約上は区別できない。**

本作業は**実体を変えず、決定文書と派生記述の側を追随させる**。

## 3. 対象範囲

- 対象: `docs/adr/`（IADR-0121 / IADR-0056 / IADR-0203 / README 索引）、`CLAUDE.md`、
  `src/README.md`、`src/platform/frontend/README.md`、`docs/how-to/`、`docs/tech/`、
  `.github/workflows/frontend.yml`（コメント）、`.husky/pre-commit`（コメント）
- 対象外（指示による）: `scripts/` 配下（並行 #799 と交差）／`src/pnpm-workspace.yaml` と
  `src/package.json` の実体（#777 の変更は正しい）／`planning/`（submodule）

## 4. 母集合（自分で引いた。走査語・件数・除外理由）

**issue 本文の「8 箇所」は転記していない。** 誤りの側の文字列で、拡張子を絞らず、追跡下の全ファイルから
引いた（除外パスは `planning/`・`src/ai-stock-trading/`・`docs/specs/`・`src/pnpm-lock.yaml` のみ）。
**issue 本文の走査は `--include=*.md` ＋ 単一引用符形に絞られており、取りこぼしがあった**（後述）。

| 軸 | 走査語 | ヒット | 是正 | 除外 |
| --- | --- | --- | --- | --- |
| 1 | `*/frontend`（引用符を問わず） | 31 | 7 | 24 |
| 2 | `pnpm-workspace` / `workspace メンバ` / `workspaces:` | 20 | 0（軸 1 と重複） | 20 |
| 3 | `packages/*` | 19 | 2 | 17 |
| 4 | `prettierignore` / `整形範囲` / `prettierrc` / `format:check` / `lint-staged` / `pnpm run format` | 27 | 4 | 23 |
| 5 | `npm workspaces` / `workspaces は` | 14 | 1（軸 1 と重複） | 13 |

軸 3 と軸 5 は **issue の走査（軸 1 の単一引用符形・`*.md` 限定）では出ない追加分**を出した
（規則 2「あり得る形をすべて列挙してから引く」／規則 3「拡張子で絞らない」）。

### 是正する箇所（14 件）

| # | 箇所 | 理由 |
| --- | --- | --- |
| 1 | `docs/adr/IADR-0121_…:171` | ★ 決定本文。日付つき追記で 3 本目を載せる |
| 2 | `docs/adr/IADR-0056_…:120` | 「現行値は 2 本」と書いた 2026-08-04 追記。新しい日付つき追記で現行値を更新 |
| 3 | `CLAUDE.md:119` | 必読規約。列挙をやめ正本を指す |
| 4 | `src/README.md:16` | 構成図の注記が 2 本を列挙 |
| 5 | `src/README.md:108-109` | ビルド節が 2 本を列挙 |
| 6 | `docs/how-to/adding-a-unit-submodule.md:94` | 自動認識の説明。雛形の 3 本目に触れていない |
| 7 | `src/platform/frontend/README.md:43` | **issue の 8 箇所に無い**。`workspaces は "*/frontend"` と二重引用符＋ npm 用語のまま |
| 8 | `docs/tech/tech-requirements.md:64` | **issue の 8 箇所に無い**。メンバを 2 領域で列挙 |
| 9 | `docs/how-to/local-development.md:63` | **issue の 8 箇所に無い**。同上 |
| 10 | `docs/adr/IADR-0203_…` 決定 3 | ★ 例外の線引き（日付つき追記） |
| 11 | `CLAUDE.md:127` | 必読規約。例外の存在と正本を指す（バイト予算のため最小） |
| 12 | `.github/workflows/frontend.yml:122` | 「グロブをここへ複写しない」の直上に例外の入口がある |
| 13 | `.husky/pre-commit:4` | 「単一情報源は `.prettierignore` ただ 1 つ」がフックの射程と食い違う |
| 14 | `docs/adr/README.md` | IADR-0203 の索引行に例外を反映（セル 200 字以内） |

### 除外した箇所と理由（黙って落とさない）

- `src/pnpm-workspace.yaml` / `src/package.json`（`:6` の description・`:21` の `//format` コメント含む）:
  **指示により変更禁止**。`src/package.json:21` の「整形の対象範囲は `.prettierignore` **ただ 1 つ**が持つ」
  は `format:templates` の追加で厳密には古い。**別 issue の起票案として報告する**（本 PR では触らない）。
- `scripts/chunk-budget-baseline.json` / `scripts/scripts.repo.test.js`: 並行 #799 と交差するため変更禁止。
  内容も CI の `paths:` に関する記述で、workspace メンバの列挙ではない。
- `.github/workflows/*.yml` の `"src/*/frontend/**"`・`docs/tests/TEST_STRATEGY.md:68,265`:
  **CI の起動条件と E2E の置き場**であり、workspace メンバの列挙ではない。
- `docs/adr/IADR-0056_…:110,116`・`:125`: 決定本文と既存の日付つき追記の**原文**。
  「旧条文は消さない」方針により保存し、新しい追記で上書きする。
- `docs/how-to/adding-a-unit-submodule.md:135,147`: `lingui.config.ts` の `include` が**自動認識しない**
  ことを `'*/frontend'` と対比する記述。内容は正しい。
- `templates/unit-template/frontend/package.json:6`: 「複製して `src/<unit>/frontend` へ置いたときに
  `'*/frontend'` が拾う」の意味で正しい（雛形自身が 3 本目で拾われることとは別の話）。
- `docs/adr/IADR-0205_…:60` の正本表（整形の対象範囲＝`.prettierignore`）: `src/` 内については依然正しい。
  **例外は [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 側 1 箇所に置く**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md): 2 箇所に置くと片方が古くなる）。
- `README.md:95`（ルート）: 既に「メンバは `pnpm-workspace.yaml`」と正本を指しており列挙していない。
- `docs/tech/tech-requirements.md:214`・`docs/how-to/local-development.md:67` 付近のコマンド列挙:
  `format:check` 自体を挙げていないため、`format:templates` の追加は追随事項にならない。
- `src/platform/frontend/Dockerfile:19-24`（workspace メンバの manifest を COPY する箇所）:
  `../templates/*/frontend` は COPY されていないが、**`dca76ce` の `build (frontend)` は success**
  （GitHub の check-runs で実測）。`pnpm install --frozen-lockfile` は落ちていないため欠陥ではない。
  記述の追随も不要（当該コメントはメンバの網羅ではなく COPY の理由を述べている）。

## 5. 設計

### A. [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 2 —— 追記か Superseded か

**追記（`［2026-08-16 追記 / #802］`）とし、状態は `Accepted` のまま**とする。根拠:

1. 変更は決定 2 の**拡張**であり反転ではない。「単一情報源を `src/` に置く」「submodule 配置で自動認識」
   「版は `packageManager` で固定」「lock は 1 本」はすべて生きている。
2. 本 IADR は決定 1〜6 を持つ。1 本のグロブ追加で全体を `Superseded` にすると、生きている 5 決定の
   参照先が新旧に割れる。**同 IADR は決定 1・4 でも同型の部分改定を追記で処理した先例**がある。
3. [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 4 が pnpm へ置換されたときも「置換されたのは名前と lock 形式だけ」として
   `Accepted` を維持した。本件はそれより小さい。

追記には**なぜ雛形だけ `../` を跨ぐのか**を書く: 雛形は `src/` の外にあり、pnpm workspace・ESLint の
flat config・Prettier の設定探索の**どの射程にも構造的に入らない**。メンバにしないと `pnpm -r` の
typecheck から外れ、現行スタックからずれても誰も気付かない（#784 が実際に踏んだ）。

### B. 整形範囲の例外 —— 新 IADR か既存への追記か

**[IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3 への日付つき追記**とする。根拠:

1. 書くべきは「lint-staged は却下・`format:templates` は許可」の**線引き**であり、却下の理由が
   置かれている場所と**同じ 1 箇所**で読めなければ意味がない（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)）。
2. 新 IADR にすると、読者は却下理由（0203）と例外（新 IADR）を別々に見る。**片方だけ見て
   「lint-staged 相当を足してよい」と読む余地**が生まれる。
3. 実務上の理由: `IADR-0206` は**未マージの PR #792 が既に確保している**（実測）。採番規約は
   「先着＝先にマージした側」であり、欠番を作れない以上 `0207` も取れない。**番号の取り合いを
   避けられるなら避ける**（これは第 3 の理由であって、1・2 が主である）。

線引きの条文（3 条件をすべて満たすときだけ 2 本目の入口を許す）:

- (a) 対象が **`src/` の外**にあり、`.prettierignore` / `.prettierrc.json` の探索射程に
  **構造的に**入らないこと（設定を移せば解決する問題ではないこと）
- (b) **規則の情報源を増やさない**こと。設定は同じ `src/.prettierrc.json` を `--config` で明示する
- (c) 入口ごとに対象が**重ならない**こと（同じファイルを 2 つの入口が別ルールで整形しない）

lint-staged が却下されたのは、対象が **`src/` 内**＝既に単一情報源のある領域であり、(a) を満たさない
ためである。**「グロブを書いた」ことが理由ではない。**

### C. `CLAUDE.md`（必読規約）の書き方 —— バイト予算

着手時実測 **50,082B / 51,200B（余白 1,118B）**。[IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md) 決定 4 の余白下限 1,000B により
**足せるのは 118B まで**。

- `:119` は**列挙をやめて正本（`pnpm-workspace.yaml`）を指す**。[IADR-0205](../adr/IADR-0205_reading-budget-reduction-for-kit-catchup.md) が「本文自身が
  『単一情報源』と書きながら列挙を複写していた」と指摘した型と同じであり、**3 本目を足すのではなく
  複写自体をやめる**のが正しい（差 −5B）。
- `:127` は例外の**存在と正本**だけを指す（`（`src/` の外は例外。[[IADR-0203]] 決定 3）`。+約 48B）。
  理由・条件は ADR 側に置く。

## 6. 受け入れ基準

- [x] [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 2 が 3 本目を含む（**旧条文は残置**・日付つき追記）
- [x] [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3 に例外の線引き（3 条件）が書かれ、lint-staged の却下理由と読み分けられる
- [x] `CLAUDE.md` の 2 行が追随し、**例外の存在と正本**が読める
- [x] 母集合の是正対象 14 件がすべて追随している（12 ファイル。`CLAUDE.md` と `src/README.md` は各 2 箇所）
- [x] 必読規約の余白が **1,000B 以上**（[IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md) 決定 4）——**実績 50,130B / 51,200B・余白 1,070B**
      （着手時 50,082B・余白 1,118B から **+48B**。内訳: `:119` −5B / `:127` +53B）
- [x] `node scripts/scripts.test.js`（621 tests・exit 0）／`REQUIRE_REPO_TESTS=1` 版（621 tests・exit 0）。
      `check-kit-sync` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
      `check-doc-type-vocabulary` / `check-adr-numbering` / `check-reading-budget` すべて exit 0
- [x] コミット後に `check-doc-updated.js` / `check-commit-messages.js` が緑（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）。
      **初回は `check-doc-updated.js` が 3 件（`IADR-0056` / `local-development` / `tech-requirements`）の
      `updated:` 据え置きを検出したため、frontmatter を前進させて amend した**

## 7. テスト方針

**回帰テストは足さない。** 「決定文書に 3 本目が書かれていること」を機械検査するには
`pnpm-workspace.yaml` と ADR 本文の突合器が要り、それは `scripts/` への追加になる。
**本 issue は `scripts/` 変更禁止**（並行 #799 と交差）である。
また [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)／運用ガイドの「検査器の追加は同型の事故が 2 回起きたら」に照らしても、
**本件は 1 回目**である（記録に留める段階）。

**別 issue の起票案として報告する**（本 PR では起票しない）:

1. **`src/pnpm-workspace.yaml` と決定文書の突合器**（同型の事故が 2 回目に起きたら）。
2. **`src/package.json:21` の `//format` コメントの是正**（「ただ 1 つ」が古い。`src/package.json`
   は本 issue で変更禁止のため触っていない）。

既存の検査（`check-doc-links` / `check-adr-numbering` / `check-reading-budget` 等）で退行が無いことを確認する。

## 8. 計画書との差異

- 差異: なし。計画 `ADR-0031`（SPA スタック）と `13_frontend-stack` は pnpm workspace の採用までを
  定めており、**メンバのグロブ本数は実装に委ねられている**（実装 ADR の射程）。環流は不要。

## 9. 未決事項

- なし。採番の取り合い（`IADR-0206` は PR #792）は設計 B で回避した。
