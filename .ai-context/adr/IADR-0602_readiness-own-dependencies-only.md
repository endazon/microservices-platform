---
title: IADR-0602 readiness は自プロセス必須の依存だけを見る —— BFF と AI 分析の readiness から下流サービスの疎通を外して /health/dependencies で報告し、BFF のセッションストアは readiness に残す
type: impl-adr
status: Accepted
related_ids: [NFR-06, ADR-0032, ADR-0052, IADR-0522]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-06）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md（§結果「Redis がセッションの単一障害点」）
  - planning:projects/microservices-platform/07_adr/ADR-0052_wolverine-migration-completion-criteria.md（決定 4 と §根拠）
related_specs:
  - ../specs/20261023_2105_readiness-downstream-isolation.md
---

# IADR-0602: readiness は自プロセス必須の依存だけを見る（#2105）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2105。親 #2047 の指摘 M-8）

## 起点・関連

- 関連する計画書 ID: NFR-06（障害時の縮退運転「検索は継続、AI 回答のみ一時停止を許容」/ 実現手段「LLM 依存部の分離」）。
  計画 ADR-0032（BFF セッション方式。セッションストアは Redis〔IADR-0522 で Valkey へ差し替え〕）、
  計画 ADR-0052 決定 4（readiness の適用範囲は「そのサービスが依存するもの」に限る。無関係なサービスまで落とすと readiness の意味が逆向きに壊れる）。
- 前提とする実装 ADR: [IADR-0522](./IADR-0522_session-store-valkey-auth-required.md) 決定 5（セッションストアのヘルスチェックはセッションと同じ構成から組む）。
- 作業仕様書: [20261023_2105_readiness-downstream-isolation](../specs/20261023_2105_readiness-downstream-isolation.md)。

## コンテキストと課題

BFF の readiness（`/health/ready`＝`ready` タグ）はセッションストアに加え、下流 4 サービス（retrieval・aianalysis・feedback・dashboard）の
`/health/live` を含んでいた。AiAnalysisService の readiness も retrieval・llm-gateway を含んでいた。helm はすべての HTTP サービスに
`readinessProbe: /health/ready` を付けるので、次の連鎖が起きる。

1. LLM ゲートウェイが落ちる → AI 分析が NotReady（Service のエンドポイントから外れる）
2. BFF の aianalysis 検査が赤 → BFF の**全レプリカ**が NotReady
3. エッジ（Gateway）から BFF へ届かず、**検索を含む全画面が 503**

NFR-06 は「検索は継続し AI 回答のみ止める」ことを求めており、実装は逆向きだった。下流の停止は全レプリカに同時に効くので、
readiness で外しても「健全なレプリカへ寄せる」効果は 1 つも無い（外れるのは全員）。

## 決定

### 決定 1: 下流サービスの疎通は readiness に入れない。`dependencies` タグで `/health/dependencies` に報告する

- BFF の下流 4 件、AiAnalysisService の下流 2 件（retrieval・llm-gateway）を `ready` から `dependencies` タグへ移し、
  `failureStatus: Degraded` にする（不達は「縮退」として報告する）。
- 共有 `HealthCheckExtensions.MapPlatformDependencyHealthChecks()`（opt-in）が `/health/dependencies` を張る。
  **常に 200** で、本文 `{"status","entries":{名前:状態}}` に状態を出す。例外の文言は出さない。
  状態コードで縮退を表さないのは、誤ってこの経路を probe に向けたときに本件の連鎖が戻らないようにするためである。
- liveness（`/health/live`）には入れない（既存どおり依存を一切見ない）。startupProbe へ移す案も採らない —— 下流停止中に BFF の
  Pod が再起動・増設されると起動が完了せず、同じ連鎖を起動時に持ち込む。
- 下流の不調は各エンドポイントの責務で返す（BFF は 502・縮退表示、AI 分析は gRPC の UNAVAILABLE からの縮退）。

### 決定 2: BFF のセッションストア（Valkey）は readiness に残す

比較:

| 観点 | 残す（採用） | 外す |
| --- | --- | --- |
| 性質 | BFF **自身の**状態の置き場（セッションの実体・DataProtection の鍵リング・SC-22 の書き込み記録。IADR-0522 の 3 用途）。ADR-0052 決定 4 の「そのサービスが依存するもの」に当たる | 同左 |
| ストア全停止時（全レプリカ共通） | 全レプリカ NotReady → エッジで 503。どの画面も使えない | 全レプリカ Ready のまま。Cookie を復号・解決できずログインもできない（401 → ログイン → 保存失敗の繰り返し）。**どの画面も使えない点は同じ**で、症状だけが分かりにくくなる |
| 1 レプリカだけ届かない（ネットワーク分断・接続の不調） | **そのレプリカだけ外れ、他が捌く**（readiness が本来役に立つ場面） | 届かないレプリカへ要求が流れ続け、そこへ当たった利用者だけが認証を失う |
| NFR-06（検索継続・AI のみ停止） | 影響しない。検索も認証（セッション）を前提とし、ストア停止で検索だけを継続させる道は無い | 同左 |
| ADR-0032 | 「Redis がセッションの単一障害点」をそのまま表に出す（可用性はストア側の設計で担う） | 同左（隠れるだけ） |

**残す。** 失うのは「ストア全停止時に無人の主体（Bearer）の要求だけは通る」点だけで、これは NFR-06 の縮退の対象（検索・AI 回答）ではない。

### 決定 3: AiAnalysisService の readiness に載る依存は 0 件とする

自前のデータストア・ブローカを持たないため、readiness に載せるべき自プロセス必須の依存が無い（`/health/ready` は常に 200 になり、
プロセスが要求を受けられることだけを表す）。

## 母集合

`git grep -n '"ready"' -- 'src/*.cs'`（AST submodule・試験を除く）で、下流**サービス**を readiness に載せていたのは BFF と AI 分析だけだった。
他（PostgreSQL・ブローカ・Qdrant と索引・pandoc / pdftotext）は自プロセス必須の依存であり本決定と整合する（触らない）。

## 結果

- 良い影響: 下流 1 つの停止で全画面が 503 にならない。検索は継続し、AI 回答だけが縮退する（NFR-06）。下流の状態は
  `/health/dependencies` で引き続き観測できる。
- 悪い影響 / トレードオフ: k8s の Ready 列だけでは下流の不調が見えなくなる（`/health/dependencies` かメトリクス・アラートで見る）。
  セッションストア全停止時は従来どおり全画面 503 になる（決定 2。可用性はストア側で担う）。
- helm の probe パスは変えない（`helm template` の差分はコメントのみ）。
- **`/health/dependencies` は外部から到達しない**（既存の `/health/live`・`/health/ready` と同じ扱い。認証は掛けない）。
  エッジが BFF へ通すのは `/bff`・`/bff/*` だけである —— Istio のエッジ（`templates/edge.yaml` の `exact: /bff` / `prefix: /bff/`）、
  ローカルの Traefik（`deploy/local/edge/platform-frontend-ingress.yaml` の `path: /bff`・`pathType: Prefix`＝パス要素単位）、
  frontend の Caddy（`handle /bff/*`）のいずれでも `/health/*` は BFF に当たらず SPA の catch-all（index.html）へ落ちる。
  AI 分析はエッジにルートを持たない。compose の BFF の公開は `127.0.0.1:5000`（ループバック）だけである。

## 検証

| # | 内容 | 結果 | 根拠 |
| --- | --- | --- | --- |
| 1 | 下流 4 件が Unhealthy でも BFF の `/health/ready`・`/bff/search` が 200 | 通った | `BffReadinessIsolationTests` |
| 2 | `/health/dependencies` が 200 で下流ごとに `Degraded` | 通った | 同上・`ReadinessIsolationTests`（AI 分析） |
| 3 | AI 分析の `/health/ready` が retrieval・llm-gateway の停止で 200 | 通った | `ReadinessIsolationTests` |
| 4 | セッションストアが Unhealthy で BFF の `/health/ready` が 503 | 通った | `BffReadinessIsolationTests` |
| 5 | 変異: aianalysis を `ready` へ戻す → 3 件赤／Redis を `ready` から外す → 3 件赤／llm-gateway を `ready` へ戻す → 3 件赤 | 赤を確認 | 手元実行（2026-10-23） |
