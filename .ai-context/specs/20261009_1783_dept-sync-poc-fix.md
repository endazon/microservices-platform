---
title: 作業仕様書 — 部門属性の同期を稼働 PoC で Fix にする段階手順と暫定手段の訂正を運用仕様書へ反映し、MCP 実行口の計画側の追記（planning#753）を追認する（planning#741 項目 4・5。#1783）
type: spec
status: done
related_ids: [FR-05, FR-09, FR-16, UC-05, SC-17, ADR-0115, ADR-0116, ADR-0117, IADR-0473, IADR-0479]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 1 の 2026-10-09 補完・決定 4 の表の直後の 2026-10-09 追記（3 点セット・暫定手段の訂正）・フォローアップ 5
  - planning:projects/microservices-platform/07_adr/ADR-0117_mcp-tool-destination-and-execution-context.md 決定 4（2026-10-08 追記。planning#753）
  - planning:projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md §裁定・§残るもの
issue: "#1783"
---

# 作業仕様書 — 部門属性の同期の稼働 PoC の配備値と MCP 実行口の追認（#1783）

> 本仕様書は変更の着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `c66c5641`。
> 計画は project-planning の隣接クローン（読み取り専用。`origin/main`）で読んだ。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働 PoC の値の切り替えは運用者が行う（本 PR は手順と記録だけ）。
> 新しい IADR は起こさない（決めることは「どこに値を置くか」と運用の手順だけで、IADR-0473 / IADR-0479 への日付つき追記で足りる）。

## 起点となる計画書（トレーサビリティ）

- 裁定: planning#741 項目 4・5（利用者裁定 2026-10-09。完了記録 `projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md`）。
- 計画 ADR: **ADR-0116 決定 1**（2026-10-09 補完: 稼働 PoC の配備値を `Fix`。コードの既定 `Off` は変えない。段階的な適用を踏む）・**決定 4**
  （2026-10-09 追記: 3 点セット・暫定手段の訂正〔例外 3〕）・フォローアップ 5。**ADR-0115 決定 3**（変更なし）。
  **ADR-0117 決定 4**（2026-10-08 追記。planning#753 で 3 点セットを「ある」へ。マージ済み `82be7dc`）。
- FR-05（ABAC）・FR-09・FR-16（MCP）、UC-05、SC-17。
- 実装 ADR: IADR-0473（部門の同期。既定 `Off`）・IADR-0479（MCP の実行口）。

## 裁定の要点（計画の文言から）

| 項目 | 裁定 | 本 PR の受け取り方 |
| --- | --- | --- |
| 4 | 稼働 PoC の配備値を `Fix`。コードの既定は `Off` のまま。運用仕様書の段階的な適用（`Report` → 試験利用者 1 人で `Fix`）を踏む。**コードの既定と helm・compose の既定は変えない** | コード・`values.yaml`・`values-local.yaml`・compose は変えない。稼働 PoC の切り替え手順を運用仕様書に書き、IADR-0473 に追記する |
| 4' | 暫定手段の訂正: 「SC-17 で属性を同じ値にそろえる」は成り立たない。訂正後は「属性を Keycloak の管理コンソールで手で直す。`Report` の後はログで食い違いが残っていないことを確かめる」 | 運用仕様書の「属性を別の手段でそろえる」を管理コンソールの手での修正へ具体化する |
| 5 | 本記録では何もしない（planning#753 で対応済み） | IADR-0479 に追認を日付つきで追記する（実装の変更なし） |

## 稼働 PoC の配備値の置き場所（実測）

| 事実 | 確かめ方 |
| --- | --- |
| 稼働 PoC は `scripts/k8s-local-up.sh --live` が `helm upgrade --install msp … -f deploy/local/values-local.yaml`（`--reuse-values` なし）で立てる | `scripts/k8s-local-up.sh:625-626` |
| `values-local.yaml` は **稼働 PoC 専用ではない**。CI の使い捨てスタック（`ci.yml`・`integration-stack.yml`・`cutover-rehearsal.yml`）も同じ起動器・同じ上書きで立てる | `grep -ln "k8s-local-up\|values-local" .github/workflows/*` |
| 稼働 PoC に固有の値（`ISTIO=1` 等）は起動時の環境変数で運用者が与え、リポジトリに PoC 専用の上書きファイルは無い | `deploy/local/` の一覧・`deploy/argocd/application.yaml:47-48`（`valueFiles` は無指定） |
| `DepartmentAttributeSync` の値は `values.yaml`・`values-local.yaml`・compose・appsettings に 0 件。コードの既定 `Off` | `grep -rn DepartmentAttributeSync deploy src --include=*.yaml --include=*.json` は警報の説明文だけ・`DepartmentAttributeSyncHostedService.cs` |
| authorization-service の `extraEnvAppend` はどの values にも無い。`--set 'services.authorization.extraEnvAppend[0]…'` は既存の `extraEnv`（IdentityAdmin 等）を消さずに env を 1 つ足す | `helm template` の描画（下の検証 V1） |
| 同期は起動直後に 1 周目を回し、以後は周期（既定 1 時間）ごとに回る | `DepartmentAttributeSyncHostedService.cs` の `do { … } while (WaitForNextTickAsync)` |

**結論**: 稼働 PoC の配備値はリポジトリに置き場所が無い（`values-local.yaml` に `Fix` を書くと CI の使い捨てスタックの既定も変わり、裁定の「helm の既定は変えない」に反する）。
よって **値の切り替えは運用者が稼働 PoC の helm リリースに対して行い**、本 PR は手順を運用仕様書に書く。

- 書き込み口は helm（`helm upgrade --reuse-values --set …`）に限る。`kubectl set env` / `kubectl patch` は使わない
  （Helm 4 のサーバサイド apply で field manager が奪われ、以後の `helm upgrade` が恒久的に落ちる。`scripts/lib/mesh-mtls-mode.sh` 冒頭の実測）。
- 🔴 **残る穴**: `k8s-local-up.sh` を再実行すると（`--reuse-values` なし）この上書きは外れ、同期は `Off` に戻る。
  `Off` では系列が無く `DepartmentSyncNotCorrecting` も鳴らない（黙って戻る）。運用仕様書に「再実行の後は §手順 3 を当て直す」と書く。
  起動器が現行の値を引き継ぐ形（`ISTIO` と同じ「明示 ＞ 現行 ＞ 初回の既定」。IADR-0488）は、`helm get values` の読みを 1 回に保つ既存の試験との
  整合が要る別作業であり、本 PR では作らない（報告に残す）。

## 受け入れ基準（issue #1783 の基準を裁定に合わせて読み替え）→ 確かめ方

| # | 受け入れ基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 項目 4: 稼働 PoC の配備値を `Fix` にする段階手順（`Report` → ログの確認 → `Fix` → 試験利用者 1 人の確認）が運用仕様書にあり、コマンドがそのまま実行できる | 運用仕様書の差分・`helm template` で `--set` の描画を確認（V1） |
| AC2 | コードの既定（`Off`）・`values.yaml`・`values-local.yaml`・compose は変えない（否定形） | `git diff --stat origin/develop` に `src/`・`deploy/` が無い |
| AC3 | 暫定手段の訂正: 「属性を別の手段でそろえる」を「Keycloak の管理コンソールで属性 `department` を手で直す（`Report` の後はログで食い違いが残っていないことを確かめる）」へ具体化する | 運用仕様書の差分 |
| AC4 | IADR-0473 に裁定（PoC の値 `Fix`・コードの既定 `Off`・暫定手段の訂正）を日付つきで追記する（本文は書き換えない） | IADR-0473 ［2026-10-09 追記 / #1783］ |
| AC5 | 項目 5: IADR-0479 に planning#753 の追認を日付つきで追記する（実装の変更なし） | IADR-0479 ［2026-10-09 追記 / #1783］ |
| AC6 | 稼働中のクラスタには何もしない（否定形） | 本 PR はファイルの変更だけ |

## 母集合（規則 9・10）

走査: `grep -rn "DepartmentAttributeSync" docs .ai-context src deploy`・`grep -rn "属性を別の手段\|属性を同じ値\|SC-17 で属性\|属性をそろえ\|属性を揃え" docs .ai-context src deploy`・
`grep -rn "planning#741\|#1783" docs .ai-context`。基点 `c66c5641`。

| 箇所 | 種別 | 該当 | 扱い |
| --- | --- | --- | --- |
| `docs/operations/operations.md` §利用者の部門属性を部門グループへ合わせる同期の有効化（:358-409） | 生きた文書 | 「既定無効」「helm values・compose には既定値を置いていない」・段階的な適用・「属性を別の手段でそろえる」（:397） | **書き換える**: 稼働 PoC の配備値（`Fix`。運用者が helm で入れる）・切り替え手順（コマンドつき）・再実行で外れる穴・暫定手段の具体化。既定 `Off` と helm/compose に値を置かないことは維持（事実のまま） |
| `docs/security/security.md:214`（有効化の行） | 生きた文書 | 「既定は `Off`」 | **追記する**: 稼働 PoC の配備値は `Fix`（運用仕様書の手順で入れる）。既定 `Off` の記述は事実のまま残す |
| `docs/screens/SC-17_user-account-management.md:65` | 生きた文書 | 「同期を有効にしていない環境では追随しない」 | 変えない（事実のまま。訂正対象の「SC-17 で属性をそろえる」は MSP の文書には無い） |
| `docs/tests/SC-17_user-account-management.md`（T-47 等） | 生きた文書 | 構成の値域の試験 | 変えない（コードは変えない） |
| `docs/operations/operations.md:577-583,1510` | 生きた文書 | 「画面で属性を差し替え」（MCP クライアント登録簿の話） | 対象外（別機能。語の一致だけ） |
| `.ai-context/adr/IADR-0473`（:64-65・:140「有効化は `Report` で確かめてから `Fix`」） | 凍結記録 | 既定 `Off`・稼働環境への適用 | **日付つき追記**（本文は書き換えない） |
| `.ai-context/adr/IADR-0479` | 凍結記録 | 計画 ADR-0117 決定 4 の写し | **日付つき追記**（追認） |
| `.ai-context/adr/IADR-0234:208` | 凍結記録 | planning#741 項目 6 | 対象外（項目 6 の受け皿は #1771） |
| `.ai-context/specs/20261007_1772_residual-ledger.md:51-68` | 確定済みの作業仕様書 | 「裁定待ち。受け皿 #1783」 | 変えない（受け皿の指し先は今も正しい。point-in-time の記録） |
| `.ai-context/specs/` の他 7 本（#1573・#1609・#1610 ほか） | 確定済みの作業仕様書 | `DepartmentAttributeSync` の当時の記述 | 変えない（point-in-time） |
| `src/`（`DepartmentAttributeSync*.cs`・試験） | コード | 既定 `Off` | 変えない（裁定どおり） |
| `deploy/`（`prometheus/alerts.yml`・`grafana/…`・`local/observability/…`） | 配備物 | 警報の説明文 | 変えない（値ではない） |
| `scripts/test-spec-coverage-baseline.json` | 検査器の基準 | 試験名 | 変えない |

規則 10（自分の記述が新たに誤りになるか）: 運用仕様書の「helm values・compose には既定値を置いていない」は本 PR の後も正しい（PoC の値はリリースの上書きで、values ファイルには入らない）。
security.md の「既定は `Off`」も正しい。運用仕様書の「現状: 未適用」は運用者が適用した後に古くなるため、**適用した日を書き足すのは運用者の作業**として手順の最後に置く。

## 変更する範囲

- `docs/operations/operations.md`（§部門属性の同期の有効化。trace ブロックへ #1783・planning#741・ADR-0117 は付けない〔本節は ADR-0117 に触れない〕・本仕様書）
- `docs/security/security.md`（有効化の行。trace ブロックへ #1783）
- `.ai-context/adr/IADR-0473_…`（追記・`updated:`・`plan_refs`・`related_specs`）
- `.ai-context/adr/IADR-0479_…`（追記・`updated:`・`related_specs`）
- 本仕様書

## 検証

- V1: `helm template msp deploy/helm/microservices-platform -f deploy/local/values-local.yaml --set 'services.authorization.extraEnvAppend[0].name=DepartmentAttributeSync__Mode' --set 'services.authorization.extraEnvAppend[0].value=Report'`
  が authorization-service の env に `DepartmentAttributeSync__Mode: "Report"` を 1 つだけ足し、既存の `IdentityAdmin__*` を残す。上書きなしでは 0 件（既定 `Off`）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-trace-blocks`・`check-commit-messages --base origin/develop`・`check-doc-updated --base origin/develop`・
  `gen-knowledge-graph --check`・`check-reading-budget`・`check-plan-id-qualification`・`check-cross-repo-refs`・`check-doc-links`。`deploy/` は触らないので `check-deploy-manifests` は対象外。
