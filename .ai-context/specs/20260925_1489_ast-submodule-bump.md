---
title: submodule src/ai-stock-trading を AST#903 を含む develop へ進め、Integration の TradeExecutionPipelineE2ETests の赤を回収し、合成ビルドのチャンク床を実測で確かめる
type: spec
status: done
related_ids: [NFR, FR-14, ADR-0031, IADR-0120, IADR-0134, IADR-0441]
author: Claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: AST submodule の前進（AST#903 の取り込み）とチャンク床の再測

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 起票: #1489（`Refs #1483`。MinIO 側の失敗が残るため #1483 は閉じない）
- 機能要求（FR）: `FR-14`（可変ユニットの合成）
- 非機能要件（NFR）: 無採番（submodule の前進・床の再測はメタ作業。`.claude/rules/traceability.md`
  §起点 ID の種別 の場合 2。環流しない。#1439 / #1443 と同じ扱い）
- ユースケース（UC）: なし
- 画面（SC）: `AST/SC-02` / `AST/SC-03`（AST 側の画面。前進で遅延チャンクが動いた。本リポジトリの画面ではない）
- 関連 ADR: `ADR-0031`（フロントエンドスタック）/ `IADR-0120`（AST は本リポジトリから変更しない）/
  `IADR-0134`（初期ロードの ratchet）/ `IADR-0441`（AST の合成）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md`

## 目的・背景

#1483（Integration が赤）には原因が 2 つある（同 issue のトリアージコメント）。

1. MinIO のイメージが匿名取得で 401。計画 ADR-0014 / ADR-0015 の改定が先で、本作業の対象外。
2. #1484（dependabot）で submodule を `8a0b3e1` → `84026a4` へ進めて以降、AST の
   `TradeExecutionPipelineE2ETests` が落ちる。AST 側の fixture が前取引日の口座照会（基準資金のシード）を
   届けていなかったためで、AST#893 として起票され AST#903（`03737bd3`）で是正済み。

`84026a4` は AST#903 を含まない（`git merge-base --is-ancestor 03737bd3 84026a4` が偽。実測）。本リポジトリからは
AST を直さない（IADR-0120）ので、是正を含む AST develop へ進めて 2 を回収する。

前回までの手動 bump（#1439 / #1443）と同じく、前進のたびに合成ビルドの初期ロードを実測し直し、動いたときだけ
床（`scripts/chunk-budget-baseline.json`）を実測値へ更新する。

🔴 **#1484 は dependabot の自動 bump で、チャンクの再測も記録も無しに入った**（`.github/dependabot.yml` の
`gitsubmodule` が週次で PR を作る）。#1443 の仕様書の「submodule を前進させる自動化は無い」は当時の記述であり、
現状とは合わない（凍結記録なので書き換えない。本書に観察として残す）。

## 取り込む SHA の選定

`gh run list --repo endazon/ai-stock-trading --workflow integration.yml`（2026-09-25 12:3x UTC 時点）:

| SHA | AST Integration E2E | 判断 |
| --- | --- | --- |
| **`471cbf31`**（AST#1003。develop の先端） | **success**（run 36133286691。13 分） | **これを取り込む**（AST#903 を含む。実測） |
| `c01e99e8`（AST#999） | cancelled（run 36131213391。30 分の timeout で打ち切り） | 先端が AST#999 を含んで緑なので問題にしない |
| `a35ae3f7`（AST#998） | success（run 36127527269） | 先端の run が終わるまでの暫定候補（最初の実測はこれで行った） |

最初は先端の run が pending だったため `a35ae3f7` で実測し、先端が緑になった時点で `471cbf31` へ差し替えて
再測した（§検証記録）。`a35ae3f7` → `471cbf31` の差分に `frontend/` は無く（`git diff --stat` が空。実測）、
合成ビルドの成果物はファイル名のハッシュまで同一だった。差分は backend（Risk の読み取りの gRPC 化・proto・
csproj）と `.ai-context/` / `docs/` で、submodule を読む検査器と AST の Release ビルドを先端で再走した。

## 対象範囲

- 対象:
  - `src/ai-stock-trading` を develop `84026a4` → `471cbf31`（68 コミット）へ
  - `scripts/chunk-budget-baseline.json` に再測の記録 `$comment_initialTotalBytes_20260925_1489_ast-bump`
    （**`initialTotalBytes` は実測一致のため据え置き**）
- 対象外:
  - `src/pnpm-lock.yaml`（AST の `package.json` は不変で `pnpm install` に差分なし。実測）
  - orval 生成物（`pnpm run codegen` に差分なし。入力は `docs/api/openapi.yaml` の `/bff/` 配下で AST を読まない）
  - Lingui カタログ（`src/lingui.config.ts` は AST を含めない。`pnpm run i18n` に差分なし。AST のカタログは AST が持つ）
  - `docs/api/openapi.yaml`（AST を入力にしない）
  - `src/vitest.config.ts` の `coverage.thresholds`（AST のテストを横断収集するが、実測は床を上回ったまま）
  - コードの変更は無い（IADR-0120）

## 設計

1. submodule を GitHub から初期化し（`git submodule update --init src/ai-stock-trading`）、まず `84026a4` のまま
   `pnpm install --frozen-lockfile` → `pnpm run build` → `check-chunk-budget` で A 側を測る。
2. `git -C src/ai-stock-trading checkout <SHA>`、dist を退避（古い dist を測らない。#1438 の教訓）→
   `pnpm install`（差分なしを確認）→ `pnpm run build` → `check-chunk-budget` → `--update` で B 側を測る
   （`a35ae3f7` と `471cbf31` の 2 回）。
3. `index.html` が読む JS（entry ＋ modulepreload）と全チャンクを、ハッシュを除いた名前で A/B 突き合わせる。
4. submodule を読む検査器（下の母集合）と frontend の CI ゲートを実走する。

### 母集合（規則 1〜10）

- **submodule の SHA を書く文書**: `84026a4` / `8a0b3e1` で全ファイルを走査した（submodule 配下を除く）。
  ヒットは `CHANGELOG.md`（生成物）・`scripts/chunk-budget-baseline.json` の `$comment_…_ast-bump-793`・
  `.ai-context/specs/20260912_ast-submodule-bump-ui-ux.md`・`20260912_ast-submodule-bump-793.md` のみ。
  いずれも**その時点の観察記録**で書き換え対象ではない。今回の SHA は本書と新しい `$comment` が持つ。
- **submodule を読む検査器**: `scripts/` と `.github/workflows/` を `ai-stock-trading` で走査した結果から、
  前進で結果が変わり得るものを全部実走した（§検証記録）: image-mapping（`--require-submodule`）・
  realm-copy-drift・realm-constraints・unit-service-ownership・unit-dependencies・backend-libraries・
  cpm-versions・event-topology・secret-injected-options・default-credentials・plan-id-qualification・
  cross-repo-refs・check-knip（AST は `ignoreWorkspaces`）・coverage（AST のテストを横断収集）。
  実走していないもの: `check-deploy-manifests`（helm / kubeconform がこのホストに無い。AST の helm 変更は
  `values.yaml` / `deployment.yaml` で、同検査は本リポジトリの chart と overlay を描画する）・
  `check-coverage-floor`（Integration の成果物を読む。後述）。
- **`check-plan-id-qualification` の submodule 除外**: 除外は `.gitmodules` から導出される
  （`scripts/lib/excluded-units.js`）。前進で `.gitmodules` は変わらないので影響なし（実走 OK）。

## 受け入れ基準

- [x] `git -C src/ai-stock-trading rev-parse HEAD` が `471cbf318edc261973c31ea3e4d32869cd85c8ad`（AST develop の先端。AST#903 を含み、AST Integration E2E が緑）
- [x] `pnpm install` が差分なしで完了する
- [x] `pnpm run build` OK・`check-chunk-budget --require` OK（床 605,160 B = 実測。`--update` が差分を出さない）
- [x] typecheck / lint / lint:templates / format / format:templates / knip 床どおり / codegen・i18n 差分なし・未訳 0 /
  Storybook / Obsidian plugin / static-egress / route-manifest / test:coverage / E2E が緑（§検証記録）
- [x] submodule を読む静的検査が緑（§母集合）

## テスト方針

新規テストは書かない（コードの変更が無い）。合成ビルドの実測（chunk）と既存の単体・E2E が検証である。
AST の単体テスト（AST#994 の `*.stopLossMethod.test.tsx` を含む）は `src/vitest.config.ts` の `include` により
本リポジトリの `test:coverage` で横断実行される。

## 検証記録

### チャンク（dist 退避 → `pnpm run build` → `check-chunk-budget`。1000 進）

| 項目 | develop（AST `84026a4`・床） | 本作業（AST `a35ae3f7` / `471cbf31`。成果物は同一） | 差 |
| --- | --- | --- | --- |
| 初期ロード合計（`index.html` が読む 4 本） | 605,160 B | **605,160 B** | **0** |
| `index-*.js` | 同サイズ（遅延チャンク名の参照 509 バイトだけが替わる） | 同サイズ | 0 |
| `ui-*.js` | 内容がバイト一致（相方 `ui-*.css` 36,637 → 36,680 B でハッシュだけ替わる） | — | 0 |
| `vendor-react` / `vendor-query` | ハッシュまで同一 | — | 0 |
| 最大チャンク / 1 kB 未満の遅延 | 586.04 kB / 10 本 | 同値 | 0 |
| `RiskSettingsPage-*.js`（AST/SC-02） | 33,099 B | 35,795 B | +2,696 B |
| `queries-*.js`（AST 画面共有の遅延チャンク） | 35,369 B | 38,003 B | +2,634 B |
| `ControlStatusPage-*.js`（AST/SC-03） | 16,335 B | 16,524 B | +189 B |
| `OpendAuthPage-*.js` | 8,485 B | 8,490 B | +5 B |

A/B は同一環境（Node 22.23.2 / pnpm 10.33.0）で、A を `84026a4` のままビルド → B を `a35ae3f7` でビルドして突き合わせた。
続けて `471cbf31` で dist を退避して再ビルドし、`a35ae3f7` の成果物と全 56 ファイルがファイル名（ハッシュ）まで一致した
（`--update` も差分なし）。AST 側のフロント差分は AST#994（損切り手法の選択と表示）と AST#972（文言 4 行）の 2 件で、
いずれも遅延チャンクに入った。

### ゲート

`test:coverage` / E2E / typecheck / lint / format / knip / codegen / i18n / Storybook / Obsidian / static-egress は
`a35ae3f7` で実走した（`471cbf31` とフロントの差分なし・成果物同一）。chunk・scripts-tests・静的検査・AST の
Release ビルドは `471cbf31` でも再走した。

| コマンド | 結果 |
| --- | --- |
| `pnpm install --frozen-lockfile` / `pnpm install` | OK・`pnpm-lock.yaml` 差分なし |
| `pnpm run typecheck` / `pnpm run lint` / `pnpm run format:check` | EXIT 0 |
| lint:templates / format:templates（Windows の cmd では npm script の `cd .. && src/...` が解決できないため、同じ eslint / prettier を bash から直接実行） | EXIT 0 |
| `node scripts/check-knip.js --require`（Windows で `.bin/knip` を spawn できないため、knip の JS 入口へ差し替える先読みを付けて実行。判定は無改変） | 床どおり 36 件 |
| `pnpm run codegen` / `pnpm run i18n` → `git status` | 差分なし |
| `node scripts/check-i18n-catalogs.js` | 未翻訳・fuzzy・obsolete 0 |
| `pnpm run build` → `check-chunk-budget --require` / `--self-test` | 605.16 kB（床 605.16）/ 13 件通過 |
| Storybook / Obsidian plugin build → `check-static-egress --require ×3` | EXIT 0 / 外部取得なし |
| `pnpm run test:coverage` | 148 files / **1775 passed** / 98.21 / 92.8 / 95.12 / 98.21（床 93 / 88 / 89 / 93） |
| `pnpm run test:e2e` | **73 passed** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 782 passed |
| ci.yml static-checks / static-checks-units / image-mapping ほかの Node 検査 37 回（`--self-test` 2 回を含む） | すべて EXIT 0（`a35ae3f7`・`471cbf31` の両方） |
| `dotnet build src/ai-stock-trading/backend/backend.slnx -c Release`（合成時は AST の `Directory.Build.props` が本リポジトリの `src/Directory.Build.props` を import するため、単独 CI と条件が違う） | 両 SHA で成功・警告 0 / エラー 0（45 出力）。テストは走らせていない（Integration カテゴリは Docker が要る） |

### 検証していないもの

- **本リポジトリの Integration（`integration.yml`）の実走**: このホストに Docker が無い。AST の
  `TradeExecutionPipelineE2ETests` を含む Integration E2E が `471cbf31` で緑であることは AST 側の run 36133286691 で確かめた。
  本リポジトリの Integration は MinIO（#1483 の原因 1）で引き続き赤になる見込みで、本 PR の効果はマージ後の
  Integration のログで `TradeExecutionPipelineE2ETests` が通ることで確かめる。

## 計画書との差異

- 差異: なし

## 未決事項

- なし
