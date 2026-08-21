---
title: 作業仕様書 — サービス間内部 API の認証方針（mesh 導入までの暫定・ネットワーク分離）
type: spec
status: review
related_ids:
  - FR-05
  - NFR
  - ADR-0004
  - ADR-0005
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../CLAUDE.md（自動化・検証・安全 / トレーサビリティ規約）"
related_specs:
  - ../../docs/security/security.md
related_adrs:
  - IADR-0017 (mesh 導入までのサービス間認証はネットワーク分離を第一防御とする)
  - IADR-0004 (ABAC の多値 allow-list・deny-by-default)
  - IADR-0012 (Retrieval /search の fail-closed)
---

# 作業仕様書 — サービス間内部 API の認証方針（mesh 導入までの暫定）

- 起点 ID: FR-05 / NFR（機密性）/ ADR-0004 / ADR-0005
- 関連 Issue: #62（親 #48、関連 #55）
- 状態: review

## 背景・課題

#48 の横断監査（`adr-guardian`）で、複数の内部サービス API が**無認証で到達可能**であることが検出された。
「サービス間呼び出しのため認証対象外」という設計判断（例: `AuthzEndpoints.cs` の `/scope` コメント）は
**Istio mTLS（ADR-0005）を前提**にしていたが、Istio は未実装であり防御に空白がある。

無認証で到達可能な内部 API（抜粋）:

- DocumentService `/documents` — 全文書メタデータ＋ABAC 属性を無認証で列挙可能
- LlmGateway `/complete` `/embed` — 無認証で LLM 呼び出しが可能
- DataSourceService `/datasources` ほか
- `deploy/docker-compose.yml` で各サービスのポートがホスト公開されている

※ RetrievalService `/search` は ABAC バイパスに直結する重大案件のため #55 で別管理（本作業では扱わない）。

## 調査結果（重要な制約）

実装のサービス間 HTTP 呼び出しは、**いずれも呼び出し元の JWT を付与していない**。

| 呼び出し元 | 宛先 | トークン | 備考 |
| --- | --- | --- | --- |
| `WikiAccessResolver` | `/authz/scope` | なし | ユーザー ID/属性のみ本文で渡す |
| `RagOrchestrator`（AiAnalysis） | `/authz/scope`・`/search`・`/complete` | なし | 名前付き HttpClient、ヘッダ伝播なし |
| `RetrievalService` | LlmGateway `/embed` | なし | |
| `IngestionService.Worker` | LlmGateway `/embed` | なし | **バックグラウンドワーカー＝ユーザーコンテキスト無し** |
| `ConversionService.Worker` | LlmGateway `/complete` | なし | **同上** |

BFF はエッジで Keycloak JWT を検証し、一部の下流（Feedback/Analysis/Dashboard）へ Bearer を伝播している。

→ 内部 API へ素朴に「JWT 必須化（`RequireAuthorization`）」を適用すると、**トークンを持たない
バックグラウンドワーカーを含む全呼び出し元**が 401 となり、RAG 回答・取り込み・Wiki 閲覧の各フローが破綻する。
client credentials トークンの取得・伝播を全呼び出し元へ実装するのは規模・リスクが大きく、
最終的には mTLS（ADR-0005）で不要になる。

## 決定（IADR-0017 に記録）

mesh（mTLS, ADR-0005）導入までは **「ネットワーク分離を第一防御」**とする。

1. **内部サービス API をホスト公開しない**。`docker-compose.yml` では **BFF（エッジ）のみ** host-published とし、
   他アプリサービスは `expose`（コンテナネットワーク内のみ到達可能）へ変更する。
   Kubernetes では ClusterIP + NetworkPolicy を前提とする（helm 追補はフォローアップ）。
2. **認証はエッジ（BFF）で担保**する（既存。Keycloak JWT を検証し下流へ Bearer 伝播）。
3. **アプリ層のサービス間 JWT（client credentials）は本 IADR では見送る**。mTLS 導入で解消する前提とし、
   残余リスクはネットワーク分離で受容してフォローアップ Issue で追跡する。
4. RetrievalService `/search` は #55 管理。

詳細と選択肢比較は `docs/adr/IADR-0017_*.md` を参照。

## 変更内容

- `deploy/docker-compose.yml`: 内部アプリサービス 9 種（document/datasource/retrieval/aianalysis/
  authorization/wiki/llm-gateway/feedback/dashboard）の host `ports:` を撤去し `expose:` へ変更。
  BFF（5000）のみ host 公開を維持する。
- `docs/adr/IADR-0017_internal-service-auth-network-isolation.md`: 決定を記録（Accepted）。
- `docs/adr/README.md`: 一覧へ IADR-0017 を追記。
- `docs/security/security.md`: サービス間認証方針・脅威・残余リスク・フォローアップを反映。
- テスト: `src/Tests/KnowledgePlatform.IntegrationTests/Deployment/NetworkIsolationTests.cs` を追加し、
  内部アプリサービスが host ポートを公開していないことを機械的に担保する（回帰防止）。

## 受け入れ基準

- [x] `docker-compose.yml` で内部アプリサービスが host `ports:` を公開していない（BFF のみ公開）。
- [x] 上記をテストで担保する（`NetworkIsolationTests`）。`dotnet test` はサンドボックス制約で本作業では未実走のため CI で確認する。
- [x] 既存の統合テスト・単体テストが破壊されない（in-process の WebApplicationFactory / Testcontainers を用いるため compose 変更の影響を受けない）。
- [x] IADR-0017 と security.md に方針・残余リスク・フォローアップが記載される。

## 対象外（フォローアップ）

- client credentials によるサービス間 JWT の実装（全呼び出し元＋ワーカーのトークン取得・伝播）。
- Istio mTLS（ADR-0005）の実装。
- Helm/k8s の NetworkPolicy 追補。
- インフラ系（postgres/rabbitmq/keycloak/qdrant/grafana 等）のローカル公開ポートは開発利便のため維持。
  共有・ステージング環境では公開しない旨を security.md に明記する。
