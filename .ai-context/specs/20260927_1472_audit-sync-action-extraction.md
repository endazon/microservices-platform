---
title: 監査の抽出が outcome=failed を拾うかを develop で引き直し、抽出条件を約束する文書の SC-22 の行に secret.item.sync を足す（#1472 項目 3）
type: spec
status: done
related_ids: [SC-22, FR-15, ADR-0095, ADR-0104, ADR-0110, IADR-0216, IADR-0453, IADR-0456]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 3（BFF が Vault へ書く。ESO は読み取り専用）
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-22（操作は監査ログに記録する・値は記録しない）
related_specs:
  - 20260925_1472_audit-failed-extraction.md
  - 20260915_issue-1477_screen-only-poc-setup.md
issue: "#1472"
---

# 作業仕様書: 監査の抽出が `outcome=failed` を拾うかを引き直し、文書の SC-22 の行を実装に揃える（#1472 項目 3）

## 起点

- issue #1472 本文の項目 3 ＝ IADR-0453 フォローアップ 4「監査の抽出クエリが `outcome=failed` を拾うか」。
- 同項目は PR #1494（仕様書 `20260925_1472_audit-failed-extraction.md`）で一度確かめて閉じている。ただし **#1472 に閉じた記録が無く**、
  その後 develop に 81 コミットが入った。**本作業は同じ走査を develop `8e5293e3` で引き直し、漏れがあれば直す。**
- **射程は項目 3 だけ**。issue コメントの項目 1・2（書いた値が Pod に届く速さ・AST の `ast-app-secrets`）は**分析だけ**を PR 本文に書き、
  コードは変えない。本文の項目 1・2・4 と、コメントの項目 3・4 は触らない（issue は閉じない。`Refs #1472`）。

## 走査した母集合（develop `8e5293e3`）

誤りの側＝「`outcome` を 2 値で列挙する書き方」「監査属性を名指しする箇所」「監査を抽出し得る設定」から引いた。

### 軸 1: 監査属性を名指しする箇所（拡張子で絞らない）

```
$ git grep -n -iE "AuditOutcome|audit_outcome|audit\.outcome|auditoutcome" -- . ':!CHANGELOG.md' ':!src/ai-stock-trading' ':!.ai-context'
src/platform/backend/Bff/Platform.Bff.Tests/PlatformLoggingTests.cs:159
src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Audit/AuditLoggerTests.cs:57 / :111
src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs:33
```

記録側とその試験だけ。抽出側は 0 件（#1494 時点と同じ）。

### 軸 2: 可観測性基盤の設定（`deploy/`・`docs/observability/`）

```
$ git grep -n -iE "audit" -- deploy docs/observability | grep -v audit_svc
（0 件）
$ git grep -n -iE "outcome|granted|denied|\"failed\"" -- deploy
```

ヒットは監査と無関係: DB 権限エラーの注記・ABAC の `Granted`・LLM 利用ダッシュボードの `egress_denied`・合成監視の
`usage_event_outcome`・reconcile スクリプトの `Failed`。**#1494 以降に増えたのは認可サービスの部門の同期の計器
`department_sync_outcome`**（`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・`deploy/prometheus/alerts.yml`・
`deploy/local/observability/{grafana,prometheus}.yaml`）で、`failed` を明示的に拾っている。監査のログではない。

Grafana ダッシュボード・アラート、Prometheus の記録／警報ルール、Loki の設定（`deploy/loki-config.yaml`・
`deploy/local/observability/loki.yaml`。ruler 無し）に**監査を抽出するものは無い**。

### 軸 3: 収集器のログ経路

`deploy/otel-collector-config.yaml`・`deploy/local/infra/otel-collector.yaml`・`deploy/local/observability/otel-collector-forward.yaml`
の `logs` パイプラインは processors が `memory_limiter, batch` だけ（filter / transform / attributes なし）。値で落とす段は無い。

### 軸 4: LogQL・抽出スクリプト・監査を読む端点

```
$ git grep -n -E "\| json|\| logfmt|\|= \"|\{service_name=|logql|LogQL|Audit=true" -- . ':!CHANGELOG.md' ':!src/ai-stock-trading' ':!*.lock' ':!.ai-context'
```

ヒットは `docs/security/security.md` の抽出条件の説明、記録側の注記、`scripts/verify-oidc-edge-flow.sh` の `json_field`（無関係）だけ。
**監査を読む BFF の端点・画面は無い**（SC-22 のテスト仕様書も「閲覧はログ基盤側」と書く）。

### 軸 5: 記録側の action × outcome（文書と突き合わせる）

```
$ grep -n "audit.Record(" src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/SecretItemBffEndpoints.cs
（20 行。action は ListAction / UpdateAction / SyncAction と、共通の拒否・失敗の関数の引数 action）
```

| action | outcome | 出どころ |
| --- | --- | --- |
| `secret.item.list` | `granted` / `denied` / `failed` | IADR-0453 決定 5・7 |
| `secret.item.update` | `granted` / `denied` / `failed` | IADR-0453 決定 5・7、IADR-0454 決定 1・2 |
| **`secret.item.sync`** | **`granted` / `failed`**（`sync-not-configured` / `sync-request-failed`） | **IADR-0456 決定 4（#1477）** |

`docs/security/security.md`「監査ログ」の SC-22 の行（抽出条件を約束する唯一の文書）:

```
$ git grep -n -E "secret\.item" -- docs
docs/security/security.md:377: … `action`（`secret.item.list` / `secret.item.update`）…
docs/tests/SC-22_secret-item-management.md:100-101（T-58 / T-59: 同期の監査は failed）
```

🔴 **文書の行に `secret.item.sync` が無い。** 同期の依頼が通らないとき、書き込みの行は `granted` のまま同期の `failed` が
**別の行**に残る（IADR-0456 決定 4「書き込みの監査行の outcome を汚さない」）。文書の 2 つの `action` で絞った抽出は、
**「書けたのに Pod へ届かない」記録を黙って落とす**。これが本項目の意図（`failed` を落とさない）に対する唯一の漏れである。

SC-22 の他の文書（`docs/api/BFF_bff-surface.md`・`docs/screens/SC-22_secret-item-management.md`・
`docs/operations/*runbook.md`）は `action` を列挙していない（「監査に残る」とだけ書く）ので追随の対象外。
FR-15 の構成情報 API の行（`granted` / `denied`）は `ConfigBffEndpoints` の記録が実際に 2 値（`denied` / `granted` の 2 か所だけ）で、正しい。

## 対象範囲

- **対象**: `docs/security/security.md`（SC-22 の行・抽出の注記・trace ブロック）／新規 xUnit `Platform.Bff.Tests/SecretItemAuditDocTests.cs`／
  IADR-0453 フォローアップ 4 への日付つき追記。
- **対象外**: 抽出クエリ・ダッシュボードの新設（計画外）。稼働クラスタの Loki での実測（#1472 項目 4 の T-40 の場）。
  コメントの項目 1・2 はコードを変えず、分析を PR 本文へ書く。

## 受け入れ基準

1. 母集合を全軸で引き直し、抽出側に 2 値前提が無いこと・文書の行の漏れを根拠つきで記録している（本書）。
2. `docs/security/security.md` の SC-22 の行が `secret.item.sync` と同期の失敗理由を挙げ、`action` を 2 つで列挙しないことを書く。
3. 表の行が、境界層の `*Action` 定数すべてと `audit.Record` の outcome のリテラルすべてを含むことを xUnit が固定する。
   **旧い文書に対して試験が落ちることを確かめる**（陽性対照）。
4. `dotnet test`（Platform.Bff.Tests）・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・
   trace ブロックの検査が通る。
5. `docs/` の表示テキストに計画 ID・IADR・仕様書名・修飾付き issue 参照を書かない（trace ブロックへ）。

## 検証（実行結果）

- 旧い `docs/security/security.md`（develop の版）に差し替えて `dotnet test --filter SecretItemAuditDocTests`:
  `Audit_row_lists_every_action_the_endpoints_record` が「`secret.item.sync` を含むこと」で**失敗**（合格 1・失敗 1）。
- 新しい文書で `dotnet test src/platform/backend/Bff/Platform.Bff.Tests`: 合格 775・スキップ 1・失敗 0。
- 残りの検証は PR 本文に証跡を載せる。
