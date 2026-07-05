---
title: IADR-0017 mesh 導入までのサービス間認証はネットワーク分離を第一防御とする
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - NFR
  - ADR-0004
  - ADR-0005
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../CLAUDE.md（自動化・検証・安全）"
related_specs:
  - ../specs/20260704_NFR_internal-service-auth-network-isolation.md
  - ../security/security.md
related_adrs:
  - IADR-0000 (実装判断を記録する)
  - IADR-0012 (Retrieval /search の fail-closed で ABAC を強制する)
---

# IADR-0017: mesh 導入までのサービス間認証はネットワーク分離を第一防御とする

- 状態: Accepted
- 日付: 2026-07-04
- 決定者: claude（実装）
- 関連: FR-05、NFR（機密性）、ADR-0004、ADR-0005、Issue #62（親 #48、関連 #55）

## コンテキストと課題

#48 の横断監査（`adr-guardian`）で、複数の内部サービス API が**無認証で到達可能**であると検出された。
コード上の「サービス間呼び出しのため認証対象外」という判断（例: `AuthzEndpoints.cs` の
`/authz/scope`・`/authz/attributes/validate`）は **Istio mTLS（ADR-0005）を前提**にしていたが、
Istio は未実装であり、防御に空白がある。

無認証で到達可能な内部 API（抜粋）:

- DocumentService `/documents`（全文書メタデータ＋ABAC 属性を無認証で列挙可能）
- LlmGateway `/complete`・`/embed`（無認証で LLM 呼び出しが可能）
- DataSourceService `/datasources` ほか
- `deploy/docker-compose.yml` で上記サービスのポートがホスト公開されている

### 制約: 内部呼び出しは現状トークンを持たない

実装のサービス間 HTTP 呼び出しは、**いずれも呼び出し元の JWT を付与していない**。とりわけ
`IngestionService` / `ConversionService` は **ユーザーコンテキストの無いバックグラウンドワーカー**であり、
ユーザー Bearer トークンを保持し得ない（`LlmGateway /embed`・`/complete` を無トークンで呼ぶ）。
`RagOrchestrator`（AiAnalysis）・`WikiAccessResolver` も名前付き `HttpClient` で `Authorization` を伝播していない。

したがって、内部 API へ素朴に `RequireAuthorization()`（JWT 必須化）を適用すると、
**全呼び出し元が 401** となり RAG 回答・取り込み・Wiki 閲覧の各フローが破綻する。
client credentials トークンの取得・伝播を全呼び出し元（ワーカー含む）へ実装するのは規模・リスクが大きく、
最終的には mTLS（ADR-0005）で不要になる作業である。

## 決定

mesh（mTLS, ADR-0005）導入までは **「ネットワーク分離を第一防御（primary control）」**とする。

### 1. 内部サービス API をホスト公開しない（ネットワーク分離）

`deploy/docker-compose.yml` では **BFF（エッジ）のみ** を host-published（`5000:8080`）とし、
他のアプリサービス（document / datasource / retrieval / aianalysis / authorization / wiki /
llm-gateway / feedback / dashboard）は host `ports:` を撤去して `expose:` へ変更する。
これによりホストからは無認証で内部 API へ到達できなくなり、サービス間通信はコンテナネットワーク内に閉じる。
Kubernetes では ClusterIP + NetworkPolicy（デフォルト拒否）を前提とする（helm 追補はフォローアップ）。

インフラ系（postgres / rabbitmq / keycloak / qdrant / grafana など）のローカル公開ポートは
**開発利便のため維持**するが、共有・ステージング・本番では公開しない（security.md に明記）。

### 2. 認証はエッジ（BFF）で担保する

外部からの入口は BFF に一本化し、BFF が Keycloak（OIDC/JWT）でユーザーを認証する（ADR-0004）。
ユーザーコンテキストを要する下流呼び出しには BFF が `Authorization` を伝播する（既存動作）。
内部サービスは信頼済みネットワーク内でのみ到達可能とする前提に立つ。

### 3. アプリ層のサービス間 JWT（client credentials）は本 IADR では見送る

内部 API への JWT 必須化は、トークン非保持のバックグラウンドワーカーを含む全呼び出し元への
client credentials 実装を要し、mTLS 導入で不要になる。よって本 IADR では見送り、
**残余リスク（ネットワーク内からの無認証到達）をネットワーク分離で受容**し、フォローアップ Issue で追跡する。

### 4. RetrievalService `/search` は #55 で別管理

`/search` は ABAC バイパスに直結する重大案件のため本 IADR の対象外（#55）。
ただしホスト公開停止（決定 1）は retrieval-service にも一律適用する。

## 検討した選択肢

- **A. 内部 API すべてに JWT（client credentials）必須化**: 認証の観点では最も強いが、
  トークン非保持のワーカー（Ingestion/Conversion）を含む全呼び出し元へトークン取得・伝播・キャッシュを
  実装する必要があり規模・リスクが大きい。mTLS 導入で大半が不要になるため、暫定対応としては過大。**不採用（mTLS で対応）**。
- **B. ネットワーク分離を第一防御（本決定）**: ホスト公開停止で「無認証かつホストから到達可能」という
  最大の露出を即座に閉じられ、既存フローを壊さない。テストで回帰も固定できる。**採用**。
- **C. 何もせず mTLS を待つ**: mesh 未実装の期間、無認証到達の空白が残る。**不採用**。

## 結果

- 良い影響: ホストからの無認証到達という監査指摘の中核を即座に解消。既存のサービス間フロー
  （無トークンの内部呼び出し・ワーカー）を壊さない。回帰は `NetworkIsolationTests` で固定。
- トレードオフ（残余リスク）: **同一ネットワーク内**からは依然として内部 API へ無認証で到達可能。
  これは mTLS（ADR-0005）到達までの受容リスクとし、Kubernetes では NetworkPolicy で補う前提。
  client credentials／mTLS の実装はフォローアップとして追跡する。
- 影響範囲: 変更は `deploy/docker-compose.yml`（公開ポート）とドキュメント・テストに限定。
  アプリケーションコードの認証挙動は変更しない（既存テストへ影響なし）。

## 残余リスク解消の前提（依拠する計画 ADR の状態）

本 IADR の残余リスク（同一ネットワーク内からの内部 API 無認証到達）の**恒久的な解消**は、以下の計画 ADR の
Accepted 化と実装に依存する。ただし**いずれも計画リポジトリ（`project-planning`）では現在 `status: Proposed`（未 Accepted）**である。

- **ADR-0004（ABAC 認可）= Proposed**: エッジ（BFF）での OIDC/JWT 認証・認可の恒久方針の前提。
- **ADR-0005（Service Mesh / Istio mTLS）= Proposed**: サービス間の相互認証＋暗号化による残余リスク解消の前提。

したがって、本 IADR は「未確定の計画決定が Accepted 化・実装されるまでの**暫定（第一防御＝ネットワーク分離）**」であり、
残余リスクの解消時期は ADR-0004/0005 の確定に従属する。ネットワーク分離のみで恒久運用することを意図しない。
この従属関係の解消を促すため、ADR-0005（および NFR「全 API OIDC/JWT／サービス間 mTLS」草案との整合）の確定を
優先課題として計画側へ環流する（`feedback/20260705_internal-service-auth-nfr-deviation.md`、`/plan-feedback`）。
