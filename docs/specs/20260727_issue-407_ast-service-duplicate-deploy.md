---
title: 経路B の AST 3 サービス重複デプロイを除去し、取引判断の取りこぼしを止める（Issue #407・IADR-0107）
type: spec
status: in-progress
related_ids:
  - FR-14
  - IADR-0107
  - IADR-0066
  - IADR-0071
  - IADR-0072
  - IADR-0076
author: claude
created: 2026-07-27
updated: 2026-07-27
related_specs:
  - "../adr/IADR-0107_ast-owned-service-single-deployment.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "../adr/IADR-0072_ast-monitor-bff-integration.md"
  - "../../deploy/local/values-local.yaml"
  - "../../deploy/local/aliases/microservices-platform-externalnames.yaml"
  - "../../deploy/local/README.md"
  - "../../scripts/check-unit-service-ownership.js"
---

# 仕様書: 経路B の AST 3 サービス重複デプロイ除去（Issue #407）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-14（可変ユニットの組み込み・宣言的構成）。
- 決定: [[IADR-0107]]（本作業で新規）。前提は [[IADR-0066]]（MSP+AST 連結ローカル k8s dev 環境）・
  [[IADR-0071]]（AST/SC-02・AST/SC-03 のリスク統制 BFF 連携）・[[IADR-0072]]（AST/SC-02 watchlist の BFF 連携）・
  IADR-0076（経路B の AST 画面系有効化）。
- Issue: #407（本 issue）。原因B の対応は endazon/ai-stock-trading#258。

## 背景と問題（実測）

取引フェーズ2 検証で、`TradeDecisionMade` が `OrderApproved` / `OrderRejected` / error / skipped の
**いずれにも現れず消失**する事象を観測した。RabbitMQ 上で `TradeDecisionMade` キューの **consumers=4**。

原因は**独立した 2 つの欠陥の積**であり、`consumers=4` はその内訳（2 サービス × 2 namespace）と一致する。

### 原因A（本リポジトリ所有・本仕様書のスコープ）

本番像 [`values.yaml`](../../deploy/helm/microservices-platform/values.yaml) は AST 3 サービス
（`configuration` / `risk-management` / `market-monitor`）を **`enabled: false`（fail-safe 既定）**で持つ。
一方 [`values-local.yaml`](../../deploy/local/values-local.yaml)（経路B・Issue #284 / IADR-0076）が
同 3 サービスを **`enabled: true`** へ上書きし、DB 接続文字列と `RabbitMq__ConnectionString` を注入していた。

AST chart（`src/ai-stock-trading/deploy/helm/ai-stock-trading/values.yaml`）は**同じ 3 サービスを
`ai-stock-trading` namespace に常時デプロイ**する。両 namespace の `rabbitmq` / `postgres` は ExternalName で
**同一の `platform-infra` 実体**を指すため（MSP 側 `deploy/local/aliases/`、AST 側 `infraAliases`）、
**同一 broker・同一 vhost `/`・同一 DB** を共有する 2 つの writer が並走していた。
MSP 側 image は AST ソースから同一 Dockerfile・同一プロジェクトでビルドされた**同一バイナリ**
（`scripts/k8s-local-images.sh` の MAPPING）であり、別実装ではなく純粋な複製である。

### 原因B（`endazon/ai-stock-trading` 所有・スコープ外）

`RiskManagementService` と `MarketMonitorService` が**同名クラス** `TradeDecisionMadeConsumer` を持ち、
両者とも `IEndpointNameFormatter` 未設定で `cfg.ConfigureEndpoints(ctx)` を呼ぶ。MassTransit 8.4.1 の
`DefaultEndpointNameFormatter` は**エンドポイント名をクラス名のみから導く（namespace を含まない）**ため、
両サービスが**同一キュー `TradeDecisionMade` を宣言**して competing consumer になる。
MarketMonitor 側は基準値を更新して ack するだけで何も発行しないため、そこへ配送された判断は無言で消える。

**原因A のみを直しても取りこぼしは半減に留まる**（consumers 4→2）。完全解消には endazon/ai-stock-trading#258 が必須。
本 PR は原因A（MSP が所有する構成不備）を扱い、原因B との依存関係を PR 本文と本仕様書に明記する。

## 方針

**AST が所有するサービスは AST chart（`ai-stock-trading` namespace）が単一の所有者である**という設計を、
経路B の構成に反映する。詳細な判断根拠と代替案の棄却理由は [[IADR-0107]] を参照。

1. **経路B の重複デプロイを除去する**: `values-local.yaml` の AST 3 サービスを `enabled: false` に戻し、
   本番像 `values.yaml` の fail-safe 既定と一致させる（`enabled: true` と DB/RabbitMQ の extraEnv を削除）。
2. **MSP BFF の到達を ExternalName alias で保つ**: `microservices-platform` namespace に
   `configuration-service` / `risk-management-service` / `market-monitor-service` の ExternalName を置き、
   `<svc>.ai-stock-trading.svc.cluster.local` へ解決させる。BFF のコード既定
   （`Platform.Bff/Program.cs` の `http://<svc>-service:8080`）が**無改修のまま**AST ns の単一実体へ届く。
   これは同ファイルが `postgres` / `rabbitmq` / `keycloak` 等に対して既に用いている確立パターンの踏襲である。
3. **CI で再発を止める**: AST 所有サービスが MSP 側の values で有効化されていないことを機械検査する
   `scripts/check-unit-service-ownership.js` を追加し、`ci.yml` の必須チェックに載せる。

## 変更対象

| ファイル | 変更 |
| --- | --- |
| `deploy/local/values-local.yaml` | AST 3 サービスを `enabled: false` へ（本番像と一致）。理由をコメントで明記 |
| `deploy/local/aliases/microservices-platform-externalnames.yaml` | AST 3 サービスの ExternalName alias を追加 |
| `scripts/check-unit-service-ownership.js` | 新規。所有権違反（MSP values での AST サービス有効化）を検査 |
| `scripts/scripts.test.js` | 上記検査ロジックの単体テストを追加 |
| `.github/workflows/ci.yml` | `unit-service-ownership` ジョブを追加（`--self-test` ＋ 実検査） |
| `deploy/local/README.md` | AST 3 画面系の到達手順（AST chart 適用が前提）へ更新 |
| `docs/adr/IADR-0107_*.md` | 新規（所有権の決定） |

**`deploy/helm/microservices-platform/values.yaml` は変更しない**（本番像バイト等価）。

## 受け入れ基準（Issue #407 より写像）

| # | 基準 | 検証方法 |
| --- | --- | --- |
| AC1 | 経路B で AST 3 サービスの Pod が `ai-stock-trading` ns にのみ存在する | `helm template` 回帰（MSP chart に当該 Deployment/Service が描画されないこと） |
| AC2 | 本番像 `values.yaml` がバイト等価で不変 | `git diff` で当該ファイルに差分が無いこと |
| AC3 | MSP BFF の `/bff/risk-controls/*`・`/bff/monitor/*`・`/bff/assumptions/*` が到達する | ExternalName alias の存在と externalName の値を検査（live 疎通は #407 の live 手順） |
| AC4 | 同種の構成ドリフトを CI が止める | `check-unit-service-ownership.js` が違反 values に対し exit 1、正常 values に対し exit 0 |
| AC5 | fail-safe 既定・実弾 OFF が不変 | AST 未デプロイ時は alias が解決先を持たず BFF が 502 へ縮退（既存設計どおり）。取引系の既定値は無変更 |
| AC6 | `TradeDecisionMade` の consumers が各 1・判断の消失ゼロ | **本 PR だけでは達成不可**。endazon/ai-stock-trading#258 とあわせて live 検証（#407 の受け入れ手順） |

## テスト方針（TDD）

1. `check-unit-service-ownership.js` の純粋ロジック（values テキスト → 有効化されている AST サービス一覧）に対し、
   **先に失敗するテスト**を `scripts.test.js` と `--self-test` へ書く。
2. 実装して green にする。
3. **helm template 回帰**: `helm template` 相当の描画条件（`services.<name>.enabled`）を values テキストから
   判定する検査として、修正前の `values-local.yaml`（3 サービス有効）で exit 1、修正後で exit 0 を確認する。
   実 `helm` バイナリは CI 前提に置かない（既存 checker 群と同じく外部依存ゼロ・Node のみ）。

## 未対応・別課題

- **原因B（キュー名衝突）**: endazon/ai-stock-trading#258。本 PR マージ後、AST 側 PR がマージされてから
  submodule pin を更新する（本 PR では pin を変更しない）。
- 本番像 `values.yaml` の AST 3 サービス定義（`enabled: false`）と `k8s-local-images.sh` の MAPPING は
  据え置く。MSP chart 経由で AST サービスを稼働させる将来の選択肢を閉じない（opt-in のまま）。
