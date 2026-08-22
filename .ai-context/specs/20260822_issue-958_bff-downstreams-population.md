---
title: 作業仕様書 — 上流ポート検査の母集合をサービス間 named client へ広げる（#958）
type: spec
status: done
related_ids:
  - FR-05
  - FR-13
  - IADR-0089
  - IADR-0249
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0023（メッシュとポート規約）"
issue: "#958"
---

# 作業仕様書: 上流ポート検査の母集合拡張（#958）

## 起点

- `#958`。`check-bff-downstreams.js` の母集合が `Platform.Bff/Program.cs` **1 ファイル**に限定され、
  **サービス間（service → service）の named client を誰も見ていなかった**
- `#342` / `IADR-0089`（DataSourceService が :5002 のまま live へ出て 21 秒タイムアウト→502）と
  **同型の 2 回目**。CLAUDE.md「同型の事故が 2 回起きたら」を満たす
- 対処は**新しい検査器ではなく既存の母集合を広げること**（`#919` で `SKIP_DIRS` から `dist` を
  外したのと同じ型）

## 🔴 着手前の実測 —— 自分の測定を 2 度間違えた

**この 2 つは「検査器を実装するときに同じ取り違えをする」形なので、記録して実装で避けた。**

### 誤り 1: compose / values を全文検索し、「違反 0 件」と誤答した

`Services__AuthorizationService` をファイル全体から探すと、**BFF 用の上書きが見つかる**。
呼び出し元（wiki-service / aianalysis-service）のブロックには無いのに「上書きあり」と判定した。

🔴 **実装では `extractServiceBlock` を必ず通し、呼び出し元のブロック内でのみ探す。**

### 誤り 2: helm ブロックの抽出が `bff` で壊れた

`values.yaml` に `bff:` が **2 箇所**あり（`ingress` 配下と `services` 配下）、自作の粗い抽出が
**2 行しか取れず**、BFF 由来の違反 7 件を誤って計上した。**現行検査器は BFF について EXIT=0** であり、
7 件は私の測定の artifact だった。

🔴 **実装では検査器自身の `extractServiceBlock`（`services:` 直下を見る）を使う。**

### 切り分け後の実測

| 呼び出し元 → 相手 | compose | k8s (helm) |
| --- | --- | --- |
| **wiki-service → AuthorizationService** | 🔴 上書き無し → **:5005** | 🔴 上書き無し → **:5005** |
| **aianalysis-service → AuthorizationService** | :8080 ✓ | 🔴 上書き無し → **:5005** |
| graph-service → AuthorizationService | :8080 ✓ | :8080 ✓ |
| aianalysis → Retrieval / LlmGateway | :8080 ✓ | :8080 ✓ |

**違反は 3 インスタンス（2 論理箇所）。** baseline（grandfather）を持ち出す規模ではないので**全部直す**。

## 決定

### 決定 1: 母集合を「呼び出し元 4 件」へ広げる

`CALLERS` に `Platform.Bff` / `AiAnalysisService` / `GraphService` / `WikiService` を持つ。
**compose と helm でサービスキーの綴りが違う**（`wiki-service` / `wiki`）ので両方を持つ。

### 決定 2: 判定は「上書きが無い」ではなく「実効値が 8080 でない」

**後発サービスはコード既定が既に :8080** であり、**上書きが無くても正しい**。
`GraphService` / `ConversionService` / `ConfigurationService` / `RiskManagement` / `MarketMonitor` が
これに当たる。現行の `computeViolations` は既に実効値ベースなので**そのまま使える**
（母集合を広げるだけで済んだ）。

### 決定 3: 0 件走査で静かに緑にしない

呼び出し元のどこからも named client を導出できなかったら fail する（`#664` の門）。

## 受け入れ基準

1. 母集合を広げた直後に **3 件**が検出される（実測と一致）
2. 3 件を直したあと **EXIT=0**（偽陽性を持ち込んでいない）
3. 上書きを 1 つ外すと**落ち、呼び出し元を名指しする**
4. **BFF の既存の守備範囲が回帰していない**（元からの検出が効き続ける）
5. `--self-test` が EXIT=0

## 変異試験

| 変異 | 結果 |
| --- | --- |
| M1 compose の上書きを外す | ✓ EXIT=1・**実際に外れた呼び出し元**（`AiAnalysisService / docker-compose.yml`）を名指し |
| M2 wiki の helm 上書きを外す | ✓ EXIT=1・`WikiService / helm values.yaml` |
| M3 aianalysis の helm 上書きを外す | ✓ EXIT=1・`AiAnalysisService / helm values.yaml` |
| M4 上書きのポートだけ変える（:8080 → :5003） | ✓ EXIT=1・`AiAnalysisService` |
| M5 **BFF の既存上書きを外す**（回帰） | ✓ EXIT=1・`Platform.Bff` |
| 対照 素の状態 | ✓ EXIT=0 |

🔴 **M1 はハーネスが先頭一致で aianalysis の上書きを消していた**（wiki を狙ったつもりだった）。
検査器は**実際に消した側を正しく名指し**しており、**呼び出し元ごとの切り分けが効いている追加の証拠**になった。

## 🔴 `:5005` が実際に不達かは未確認

- **実測したのは構成値とコード経路まで** —— compose `expose: ["8080"]` / helm `port: 8080`、
  `WikiAccessResolver` / `RagOrchestrator` が `CreateClient("AuthorizationService")` で
  `POST /authz/scope` を叩いていること
- 🔴 **「5005 では届かない」は `IADR-0089` からの帰結であって、live での観測ではない**

### live で確かめる手段の検討

`.github/workflows/integration-stack.yml`（`Integration Stack`）は **k3d ＋ helm で
スタックを起こす**ので、原理的には確かめられる。ただし本 PR では行わない。

- **本 PR は既に直してある**ため、確かめられるのは「直った後に届くこと」であって
  「直す前に届かなかったこと」ではない。後者を見るには**わざと壊して回す**ことになる
- 追加には `.github/workflows/**` の編集が要る（射程が広がる）

**やるなら「ABAC の許可スコープ解決が実際に成功する」ことを正の側で見る**のが妥当で、
別 issue として起票する価値がある。**本 PR では「未確認」と明記するに留める。**

## 採番

当初 `IADR-0248` を仮取りしたが、**作業中に別セッションが `0248` を確保して develop へ着地した**
（`IADR-0248_integration-stack-ci-readiness-gate.md`）ため **`0249` へ付け替えた**。
付け替えは**自分のスラッグ（`upstream-port-check-population`）を持つ参照だけ**に限定し、
develop 側の `0248`（別作品）とその索引行を巻き込んでいない。
**マージ直前に develop の最大を実測して取り直すこと**（今日だけで 3 回、着地順で番号が動いている）。
