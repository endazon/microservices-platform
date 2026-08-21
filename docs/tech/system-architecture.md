---
title: システム構成図（microservices-platform 基盤 + knowledge ユニット）
type: tech-architecture
status: draft
created: 2026-07-16
updated: 2026-08-21
author: endazon (with Claude Code)
---
<!-- trace:
ids: [SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11]
adrs: [ADR-0001, ADR-0002, ADR-0003, ADR-0004, ADR-0005, ADR-0007, ADR-0008, ADR-0009, ADR-0010, ADR-0011, ADR-0018, ADR-0019, ADR-0020, ADR-0027]
iadrs: [IADR-0017, IADR-0026, IADR-0048, IADR-0056, IADR-0121]
specs: []
issues: [#497, #580, #591]
-->

# システム構成図: microservices-platform（基盤 + knowledge ユニット）

> 本書はマイクロサービスプラットフォーム基盤（platform ユニット）と、それに付随する可変機能
> （knowledge ユニット）を俯瞰するシステム構成図である。上流計画書
> `01_architecture-overview.md`（計画リポ）（fixed）
> と `02_service-decomposition.md`（計画リポ）
> を、現時点の実装（**11 サービス + BFF**・ユニット第一のリポジトリ構成・.NET 10）に合わせて詳細化した。

## 起点となる計画書（トレーサビリティ）

- 技術検討: `06_technical/01_architecture-overview.md`、`02_service-decomposition.md`、`10_composability-design.md`
- 計画 ADR: マイクロサービス採用、サービス境界・Database per Service（実装で 11 サービス確定）、メッセージング（MassTransit/RabbitMQ。後継の Wolverine 採用により Superseded・注記は #580）、ABAC 認可、
  ベクトル DB=Qdrant、LLM ゲートウェイ、Wiki エンジン、コンポーザブルアーキテクチャ、ユニット第一のリポジトリ構成
- 実装 ADR: Istio STRICT mTLS をサービス間認証の第一防御とする（先行していた「内部サービス認証・ネットワーク隔離」を Superseded し、ネットワーク隔離は多層防御へ格下げ）、ユニット構成
- 補足: 実装は `.NET 10 / C# 13` に統一済みで、**計画側の制約も `.NET 10` である**（03_tech-stack-selection.md（計画リポ）の確定スタック一覧と、.NET 10 アップグレードの計画 ADR〔Accepted・2026-07-23〕）。
  実装が先行していた経緯は、バックエンドの .NET 10 採用を決めた実装 ADR と draft/feedback/20260709_dotnet10-target-framework-deviation（計画リポ） に残る（**旧「計画は `.NET 8`」の記述は 2026-08-05 / #497 で是正した**。[tech-requirements.md](tech-requirements.md) の「差異: なし（解消済み）」と一致させた）。

## 読み方（凡例）

- **platform ユニット（基盤・主成果物）** = 太枠。認証・認可（ABAC）・LLM エグレス統制・エッジ集約（BFF）・
  SPA 基盤・共有契約/横断基盤を、機能ドメインから独立した再利用可能な土台として提供する。
- **knowledge ユニット（付随する可変機能）** = 社内ナレッジの横断検索・AI 回答・Wiki 連携を提供する 9 サービス。
  基盤の上で組み替え可能な一機能ユニットであり、追加の可変機能ユニット（例: ai-stock-trading）と同じ枠組みで載る。
- **依存規則**: 可変ユニット → platform は共有契約・横断基盤のみ参照可。platform → 可変ユニットは
  合成点（フロント features 合成点・BFF エンドポイント合成点）のみ。
- 実線 = 同期 API / 実装済みの連携。破線 = イベント・認可判定・エグレス等の非同期/横断連携。

## 全体システム構成図

```mermaid
flowchart TB
  User(("社内利用者<br/>Web / モバイル"))
  Ingress["NGINX Ingress<br/>TLS 終端 / ルーティング / レート制限"]

  subgraph platform["platform ユニット（基盤・主成果物）"]
    direction TB
    FE["SPA 基盤（frontend）<br/>foundation + アプリホスト / features 合成点"]
    BFF["BFF（エッジ・唯一の入口）<br/>Keycloak JWT 検証 / 集約 / 構成情報 API / ドリフト検出"]
    AUTHZ["AuthorizationService<br/>ABAC 属性ポリシー・認可判定"]
    GW["LlmGateway<br/>LLM/埋め込みエグレス集約・機密区分別ルーティング"]
    SHARED["Shared<br/>Contracts / Infrastructure（横断基盤）"]
  end

  subgraph knowledge["knowledge ユニット（付随する可変機能・9 サービス）"]
    direction TB
    DOC["DocumentService<br/>文書カタログ・版管理（DB 所有）"]
    DS["DataSourceService<br/>データソース登録・同期"]
    CONV["ConversionService<br/>正規化・変換（Worker）"]
    ING["IngestionService<br/>埋め込み・索引投入（Worker）"]
    RET["RetrievalService<br/>ハイブリッド検索・AI 回答編成"]
    AI["AiAnalysisService<br/>データ範囲分析"]
    WIKI["WikiService<br/>Wiki.js 同期・ABAC ゲートウェイ"]
    FB["FeedbackService<br/>AI 回答フィードバック収集"]
    DASH["DashboardService<br/>利用状況・検索傾向集計"]
  end

  subgraph infra["共有インフラ / 横断的関心事"]
    direction LR
    MQ[["RabbitMQ<br/>MassTransit"]]
    PG[("PostgreSQL<br/>Database per Service")]
    QD[("Qdrant<br/>ベクトル DB")]
    OBJ[("MinIO<br/>オブジェクトストレージ")]
    REDIS[("Redis<br/>BFF キャッシュ")]
    KC["Keycloak（IdP）"]
    OBS["可観測性<br/>OTel / Prometheus / Grafana / Loki"]
    WJS["Wiki.js<br/>（既存 OSS 閲覧/編集）"]
  end

  subgraph ext["外部システム"]
    direction TB
    SRC["社内データソース<br/>ファイルサーバー / Wiki / SaaS / 業務 DB"]
    LLM["LLM/埋め込みプロバイダ<br/>Claude / Copilot / セルフホスト OSS"]
  end

  %% 利用者導線（同期）
  User --> Ingress --> FE
  FE -->|/bff/*| BFF
  BFF --> DOC & RET & AI & WIKI & FB & DASH & DS
  FE -. OIDC/PKCE .-> KC
  BFF -. JWT 検証 .-> KC
  BFF --- REDIS

  %% 取り込み・正規化パイプライン（非同期）
  SRC --> DS
  DS -. 原本取得イベント .-> MQ
  MQ -. 変換 .-> CONV
  CONV -. 図変換 .-> GW
  CONV --> DOC
  DOC -. 更新イベント .-> MQ
  MQ -. 取り込み .-> ING
  ING --> QD
  DOC --- OBJ

  %% 検索・RAG（同期＋ストリーム）
  RET --> QD
  AI -. RAG 参照 .-> RET
  RET -. 回答生成 .-> GW
  AI -. 回答生成 .-> GW
  GW --> LLM

  %% Wiki 連携
  WIKI --> WJS
  DOC -. Markdown 同期 .-> WIKI

  %% 認可（ABAC・横断）
  AUTHZ -. 認可判定 .-> DOC & RET & AI & WIKI

  %% インフラ結線（ユニット単位に集約。DB は Database per Service）
  platform === PG
  knowledge === PG
  platform -. OTLP .-> OBS
  knowledge -. OTLP .-> OBS

  classDef plat fill:#e8f0fe,stroke:#4285f4,stroke-width:2px;
  classDef unit fill:#fff4e5,stroke:#fb8c00,stroke-width:2px;
  classDef inf fill:#f1f3f4,stroke:#9aa0a6,stroke-width:1px;
  classDef extn fill:#fce8e6,stroke:#ea4335,stroke-width:1px;
  class FE,BFF,AUTHZ,GW,SHARED plat;
  class DOC,DS,CONV,ING,RET,AI,WIKI,FB,DASH unit;
  class MQ,PG,QD,OBJ,REDIS,KC,OBS,WJS inf;
  class SRC,LLM extn;
```

## 主要データフロー

### 1. 取り込み・正規化（非同期）

```mermaid
sequenceDiagram
  autonumber
  participant DS as DataSourceService
  participant MQ as RabbitMQ
  participant CV as ConversionService
  participant GW as LlmGateway
  participant DOC as DocumentService
  participant IG as IngestionService
  participant QD as Qdrant
  participant WK as WikiService

  DS->>MQ: 原本取得イベント
  MQ->>CV: 変換ジョブ
  CV->>GW: 図→PlantUML/Mermaid 変換（pandoc は本文）
  CV->>DOC: 正規化文書を登録
  DOC->>MQ: 文書更新イベント
  MQ->>IG: 取り込みジョブ
  IG->>QD: チャンク埋め込みを索引投入
  DOC->>WK: Markdown を Wiki.js へ同期
```

### 2. RAG 検索・AI 回答（同期＋ストリーミング）

```mermaid
sequenceDiagram
  autonumber
  participant U as 利用者
  participant BFF as BFF
  participant AI as AiAnalysis/Retrieval
  participant AZ as AuthorizationService
  participant RET as RetrievalService
  participant QD as Qdrant
  participant GW as LlmGateway
  participant LLM as LLM プロバイダ

  U->>BFF: 質問（/bff）
  BFF->>AI: 集約リクエスト
  AI->>AZ: 利用者属性で権限スコープ解決（ABAC）
  AZ-->>AI: 許可スコープ
  AI->>RET: 属性フィルタ付きハイブリッド検索
  RET->>QD: ベクトル＋全文検索
  QD-->>RET: 関連チャンク
  RET->>GW: 出典つき回答生成
  GW->>LLM: エグレス（機密区分別ルーティング）
  LLM-->>GW: 生成結果
  GW-->>BFF: ストリーム応答
  BFF-->>U: 出典（Wiki/原本リンク）つき回答
```

## サービス責務（実装 11 サービス + BFF）

| ユニット | サービス | 責務 | 主な通信 |
| --- | --- | --- | --- |
| platform | `Bff` | エッジ集約（フロントの唯一の入口）・構成情報 API・ドリフト検出 | REST / Redis |
| platform | `AuthorizationService` | ABAC 属性ポリシー管理・認可判定 | REST（同期） |
| platform | `LlmGateway` | LLM/埋め込みプロバイダへのエグレス集約・機密区分別ルーティング | REST / 外部 API |
| knowledge | `DocumentService` | 文書カタログ・版管理（DB 所有）・更新イベント発行・Wiki 同期 | REST / イベント |
| knowledge | `DataSourceService` | データソース登録・定期同期・原本取得 | REST / イベント |
| knowledge | `ConversionService` | 正規化（pandoc + LLM 図変換）・変換パイプライン | イベント / GW |
| knowledge | `IngestionService` | パース→チャンク→埋め込み→Qdrant 索引投入 | イベント / Qdrant |
| knowledge | `RetrievalService` | ABAC フィルタ付きハイブリッド検索・AI 回答編成 | REST（同期） |
| knowledge | `AiAnalysisService` | データ範囲分析・対話 | REST / ストリーム |
| knowledge | `WikiService` | Wiki.js 同期・ABAC ゲートウェイ（閲覧/編集は Wiki.js に委譲） | REST / GraphQL |
| knowledge | `FeedbackService` | AI 回答へのフィードバック収集 | REST / イベント |
| knowledge | `DashboardService` | 利用状況・検索傾向のダッシュボード集計 | REST |

> knowledge フロントエンドは 11 画面（検索・結果・文書・Wiki・データソース・変換・分析・ABAC 管理・
> 運用・構成）を features として持ち、platform の SPA 基盤（合成点）へ登録する。

## デプロイ構成

- **ローカル（dev）**: docker-compose（`deploy/docker-compose.yml`・`scripts/compose-up.sh`）。内部サービスは
  `expose` のみでホスト非公開。公開は frontend(:3100) / BFF(:5000) / Keycloak(:8080) /
  Wiki.js(:3001) / Grafana(:3000)。
- **stg / prod**: Kubernetes（k3s）＋ Helm / ArgoCD（GitOps）、Istio サービスメッシュ
  （サービスメッシュの決定に基づき、STRICT mTLS をサービス間認証の第一防御とする）、NGINX Ingress。秘匿は Vault。イメージレジストリは Harbor。
- **ビルド**: ユニット別 slnx（`dotnet build src/platform/backend/backend.slnx` /
  `src/knowledge/backend/backend.slnx`・.NET 10）、フロントは pnpm workspace（`src/`・Node 22。
  #591: 従前は「npm workspaces」と書いていたが、SPA 新スタック移行の実装 ADR により pnpm workspace へ移行済み）。

## 関連仕様

- 上流計画（fixed）: アーキテクチャ概要（計画リポ）、サービス分割設計（計画リポ）
- ユニット規約: [`src/README.md`](../../src/README.md)
- 拡張ユニットの例: ai-stock-trading（`src/ai-stock-trading/` へ submodule リンク）の
  システム構成図（計画リポ）
