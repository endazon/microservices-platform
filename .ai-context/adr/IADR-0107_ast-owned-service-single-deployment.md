---
title: IADR-0107 AST 所有サービスは AST chart を単一の所有者とし、MSP からは ExternalName alias で到達する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0066
  - IADR-0071
  - IADR-0072
  - IADR-0076
  - IADR-0056
author: claude
created: 2026-07-27
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (FR-14 可変ユニットの組み込み・宣言的構成)
---

# IADR-0107: AST 所有サービスの単一デプロイ（重複デプロイの禁止）

- 状態: Accepted
- 日付: 2026-07-27
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（可変ユニットの組み込み・宣言的構成）
- 関連 ADR: [IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md)（platform／可変ユニットのリポ構成＝所有権の原則）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B＝MSP+AST 連結ローカル k8s dev 環境）／
  [IADR-0071](./IADR-0071_ast-risk-controls-bff-integration.md)（AST/SC-02・AST/SC-03 のリスク統制 BFF 連携）／[IADR-0072](./IADR-0072_ast-monitor-bff-integration.md)（AST/SC-02 watchlist の BFF 連携）／
  [IADR-0076](./IADR-0076_edge-bff-routing-and-oidc-hostname.md)（経路B の AST 画面系有効化＝本 ADR が是正する構成の出所）
- 関連仕様書: `docs/specs/20260727_issue-407_ast-service-duplicate-deploy.md`
- Issue: #407（bug/infrastructure・priority:must）。原因B の対応は endazon/ai-stock-trading#258

## コンテキストと課題

取引フェーズ2 検証で、`TradeDecisionMade` が `OrderApproved` / `OrderRejected` / error / skipped の
**いずれにも現れず消失**する事象を観測した。RabbitMQ 上で `TradeDecisionMade` キューの **consumers=4**。

内訳は「2 サービス × 2 namespace」であり、**独立した 2 つの欠陥の積**である。

| # | Pod | namespace | 配送されたときの挙動 |
| --- | --- | --- | --- |
| 1 | risk-management | ai-stock-trading | 承認/拒否を発行（正しい所有者） |
| 2 | risk-management | microservices-platform | 承認/拒否を発行（意図しない複製が判断） |
| 3 | market-monitor | ai-stock-trading | 基準値更新のみ・判断が消失 |
| 4 | market-monitor | microservices-platform | 基準値更新のみ・判断が消失 |

- **原因A（本リポジトリ所有・本 ADR のスコープ）**: 本番像 `values.yaml` は AST 3 サービス
  （`configuration` / `risk-management` / `market-monitor`）を `enabled: false`（fail-safe 既定）で持つ。
  一方 `deploy/local/values-local.yaml`（経路B・[IADR-0076](./IADR-0076_edge-bff-routing-and-oidc-hostname.md) / #284）が同 3 サービスを `enabled: true` へ
  上書きし、AST chart が `ai-stock-trading` namespace へ常時デプロイする同じサービスと**二重化**していた。
  両 namespace の `rabbitmq` / `postgres` は ExternalName で同一の `platform-infra` 実体を指すため、
  **同一 broker・同一 vhost・同一 DB** を共有する 2 つの writer が並走する。MSP 側 image は AST ソースから
  同一 Dockerfile・同一プロジェクトでビルドされた**同一バイナリ**（`k8s-local-images.sh` の MAPPING）であり、
  別実装ではなく純粋な複製である。
- **原因B（AST 所有・スコープ外）**: `RiskManagementService` と `MarketMonitorService` が同名クラス
  `TradeDecisionMadeConsumer` を持ち、両者とも `IEndpointNameFormatter` 未設定で `ConfigureEndpoints` を呼ぶ。
  MassTransit の `DefaultEndpointNameFormatter` は**エンドポイント名をクラス名のみから導く**ため、
  両サービスが同一キューを宣言し competing consumer になる。→ endazon/ai-stock-trading#258。

課題は「**AST が所有するサービスの実体を、どの namespace に、いくつ置くのが正しいか**」であり、
そこから「MSP BFF はその実体へどう到達するか」が従属して決まる。

## 決定

**AST が所有するサービスは AST chart（`ai-stock-trading` namespace）を単一の所有者とし、
MSP namespace には実体を置かない。MSP BFF は ExternalName alias 経由で AST ns の実体へ到達する。**

1. `deploy/local/values-local.yaml` の AST 3 サービスを `enabled: false` に戻し、本番像 `values.yaml` の
   fail-safe 既定と一致させる。DB 接続文字列・`RabbitMq__ConnectionString` の注入も削除する。
2. `deploy/local/aliases/microservices-platform-externalnames.yaml` に
   `configuration-service` / `risk-management-service` / `market-monitor-service` の ExternalName を追加し、
   `<svc>.ai-stock-trading.svc.cluster.local` へ解決させる。
3. `scripts/check-unit-service-ownership.js` で所有権違反を機械検査し、`ci.yml` の必須チェックに載せる。

**本番像 `deploy/helm/microservices-platform/values.yaml` は変更しない（バイト等価）。**
同ファイルの AST 3 サービス定義は `enabled: false` のまま据え置き、将来 MSP chart 経由で稼働させる
選択肢（opt-in）を閉じない。本 ADR が禁じるのは「AST chart と**同時に**MSP 側でも有効化すること」である。

## 根拠

- **取引ドメインの所有者は AST である**（[IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md) の platform／可変ユニット分離）。risk-management は
  専用 DB `risk_management_svc`・取引台帳射影（`OrderApprovedLedgerConsumer` 等）・時価評価・kill switch を持つ
  **状態を持つドメインサービス**であり、同一 DB を共有する 2 インスタンスは定義上ひとつの集約に対する
  二重 writer になる。namespace で分けても分離にならない。
- **MSP 側の実体は BFF の pass-through 先を満たすためだけに存在していた**。`Platform.Bff/Program.cs` の
  named client 既定は `http://risk-management-service:8080` で、この名前が MSP ns で解決できればよい。
  ExternalName alias はまさにそのための機構であり、**BFF のコードも本番像 values も無改修**で済む。
- **同ファイル内の確立パターンの踏襲**である。`deploy/local/aliases/microservices-platform-externalnames.yaml` は
  既に `postgres` / `rabbitmq` / `redis` / `keycloak` / `qdrant` / `otel-collector` に対して
  「素のサービス名を別 namespace の FQDN へ解決させる」ことを行っている（[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)）。新機構を持ち込まない。
- **fail-safe が保たれる**。AST 未デプロイ時、alias の解決先には Service が無く BFF は不達→502 へ縮退する。
  これは [IADR-0071](./IADR-0071_ast-risk-controls-bff-integration.md) / [IADR-0072](./IADR-0072_ast-monitor-bff-integration.md) が明記した既存の設計（「AST 未デプロイ時の不達で BFF の可用性を
  左右させないよう readiness の UriHealthCheck には含めない」）と一致し、挙動を変えない。

## 検討した代替案

### 案B: キュー名 / vhost をユニット毎に分離する（棄却）

MSP ns と AST ns の risk-management に別々の vhost（例 `/msp` と `/ast`）を割り当て、衝突を無くす。

**棄却理由**: 衝突は消えるが**重複そのものが残る**。同一 DB `risk_management_svc` を共有したまま
2 つの独立したリスクエンジンが並走し、`TradeDecisionMade` を**それぞれ**承認して `OrderApproved` を
二重に発行する構成になる。取りこぼし（安全側）が**二重発注（危険側）**に置き換わり、明確に悪化する。
DB も分ければ「リスク統制の真実がどちらか分からない」状態になり、統制の意味が失われる。
さらに、この案は原因B（AST ns 内部での risk-management ↔ market-monitor 衝突）を**一切解決しない**。

### 案C: MSP BFF の `Services__*` を values-local の `bff.extraEnv` で AST ns へ向ける（棄却）

`bff.extraEnv` に `Services__RiskManagementService: http://risk-management-service.ai-stock-trading:8080` を置く。

**棄却理由**: Helm の値マージは**マップは deep-merge するがリストは置換する**。`values.yaml` の
`bff.extraEnv` は Introspection 収集先と下流 URL を合わせて 20 件以上持つ**リスト**であり、
`values-local.yaml` で同キーを定義すると**基底の全エントリを消し飛ばす**。全件を複製すればドリフト源になり、
`check-bff-downstreams.js`（`values.yaml` のみを読む）の検査からも外れる。採用した ExternalName 案は
この罠を構造的に回避する。

### 案D: MSP chart から AST 3 サービスの定義自体を削除する（棄却）

**棄却理由**: 本番像 `values.yaml` のバイト等価が崩れ、影響が経路B に限定されなくなる。現状の
`enabled: false` は既に正しい fail-safe 既定であり、壊れているのは経路B の上書きだけである。
最小の是正は上書き側を戻すことであって、基底の選択肢を消すことではない。

## 影響

- **経路B**: MSP ns から AST 3 サービスの Deployment / Service / Pod が消える。`TradeDecisionMade` の
  consumers は 4→2 になる。**取りこぼしは半減するが、原因B が残るためゼロにはならない**
  （endazon/ai-stock-trading#258 とあわせて初めて解消する）。
- **AST 3 画面系（AST/SC-01 前提条件・AST/SC-02 リスク設定/watchlist・AST/SC-03 統制状態）**: 経路B での到達に
  **AST chart の適用が前提**になる。AST 未適用時は BFF が 502 へ縮退する（既存の fail-safe 設計どおり）。
  `deploy/local/README.md` に前提を明記する。
- **本番像**: 影響なし（`values.yaml` 無変更）。`k8s-local-images.sh` の MAPPING も据え置き。
- **CI**: `unit-service-ownership` ジョブが 1 つ増える（外部依存ゼロ・Node のみ・数秒）。
- **fail-safe / 実弾**: 不変。取引系の既定値・ゲートには一切触れない。

## 再発防止

`scripts/check-unit-service-ownership.js` が、AST chart が所有するサービス名の集合と、MSP 側 values
（本番像 `values.yaml` ＋ 経路B `values-local.yaml` の合成）で `enabled: true` になっているサービスの集合の
**交差が空である**ことを検査する。交差があれば重複デプロイであり exit 1。所有サービス一覧は AST chart の
values から動的に読む（submodule 未取得時は既知の一覧へフォールバックし、検査を落とさない）。
`--self-test` を内蔵し、検査ロジックは `scripts/scripts.test.js` からも単体テストする
（[IADR-0068](./IADR-0068_image-mapping-drift-check.md) / [IADR-0057](./IADR-0057_unit-dependency-machine-check.md) と同型）。

### 運用注意: 所有権判定は「サービス名の完全一致」である

本検査は**サービス名の文字列完全一致**で所有権を判定する。名前空間（プレフィックス等）による構造的な
分離ではないため、**MSP が将来 AST と同名の無関係なサービスを追加すると誤検出しうる**。AST 側には
`audit` / `report` / `notification` / `backtest` のような汎用的な名前が含まれており、衝突の余地はある。

現時点で MSP（15 サービス）と AST（11 サービス）の名前が一致するのは、本 ADR が対象とする
`configuration` / `risk-management` / `market-monitor` の **3 件のみ**であることを確認済みである。

この判定を採ったのは、**Kubernetes の Service 名（＝BFF が引く DNS 名）自体が名前空間を持たない**ため。
MSP namespace の `report-service` と AST namespace の `report-service` は、BFF のコード既定
`http://report-service:8080` から見て**区別できない**。つまり名前の一致はそれ自体が到達先の曖昧さを意味し、
誤検出ではなく**設計上の警告**として扱うのが正しい。将来 MSP に同名サービスを追加する場合は、
Service 名の変更か、AST 側と到達経路を明示的に分ける設計判断を伴うべきであり、本検査が CI を止めることは
その判断を強制する意図と一致する。回避が必要になった場合は、本節を更新したうえで検査に除外リストを設ける。

> ［2026-08-28 追記 / #1025］**予見していた衝突が実際に起きたので、除外リストを設けた。**
> MSP が FR-22（利用者本人への通知）の `NotificationService` を配備するにあたり chart キーを
> `notification` としたため、本節が名指ししていた `notification` で衝突した。
> **一致するのは 3 件（`configuration` / `risk-management` / `market-monitor`）のみ、という上の記述は
> 本追記の時点で古い —— 4 件目 `notification` が加わった。**
>
> 採ったのは本節の 2 つ目の逃げ道（到達経路を明示的に分けたうえで除外）である。Service 名の変更
> （1 つ目）は棄却した —— 送出側（DocumentService の `HttpPrivateNoteNotifier`）が **fail-open** であり、
> 名前をずらして上書き env で繋ぐ形は「上書きが落ちても 502 にすらならない」経路を新設するためである。
> 到達経路の分離は次の 3 点で確認した: ①`deploy/local/aliases/microservices-platform-externalnames.yaml` に
> `notification-service` の alias が無い ②MSP の NotificationService は RabbitMQ を使わない
> （#407 の実害だったキュー競合が起きない）③DB は `notification_svc` で AST 専有 DB 一覧に含まれない。
>
> 実装は `scripts/check-unit-service-ownership.js` の `NAME_COLLISION_EXEMPT`。
> **除外が効いた同名は毎回 notice で出す**（`findNameCollisions`）—— 静かに消えない形にした。
> 判断の全文は [IADR-0288](./IADR-0288_notification-service-deployment-and-name-collision.md) 決定 2。
