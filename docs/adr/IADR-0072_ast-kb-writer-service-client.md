---
title: IADR-0072 AST の KB 書き込みは microservices-platform レルムの専用 confidential client（service-account に platform-operator）で受ける
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - IADR-0030
  - IADR-0041
  - IADR-0044
  - ADR-0018
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# IADR-0072: AST の KB 書き込み用サービスクライアント（platform-operator）を realm に追加する

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-06**（文書管理＝ナレッジベースへの保存）、ADR-0018（合成可能アーキテクチャ・可変ユニットの拡張は基盤無改修）
- 関連 ADR: [[IADR-0030]]（platform-operator ロール新設）／[[IADR-0041]]（文書書き込みの BFF ABAC ゲート）／
  [[IADR-0044]]（バックエンド書き込みの多層防御＝`POST /documents` は platform-admin/operator 必須）
- Issue: AST endazon/ai-stock-trading#18（FR-08 ナレッジベース保存の実 s2s 配線）。AST 側は
  `feat/FR-08-kb-writer-cross-realm-s2s`（AST IADR-0093）。
- 対応する AST 側決定: AST `docs/adr/IADR-0093`（KB の s2s を本レルムの専用クライアントでクロスレルム認証する）。

## 背景・課題

`ai-stock-trading`（AST）ユニットは、確定報告書・収集情報を DocumentService（`POST /documents`）へ保存する（AST FR-08）。
しかし本エンドポイントは [[IADR-0044]] の多層防御で `platform-admin`/`platform-operator` ロール必須であり、かつ
DocumentService は **`microservices-platform` レルム**の Authority で JWT を検証する。AST の既存 s2s クライアント
（AST レルムの `ai-stock-trading-svc` / `trading-service`）が発行するトークンは:

- issuer が **AST レルム**のため、本レルムの Authority 検証を通らない（401）。
- 仮に通っても `trading-service` は `platform-admin`/`platform-operator` に該当しない（403）。

よって AST は本レルムのクライアント資格を持たない限り KB へ書き込めない。基盤無改修（ADR-0018）の下で AST に
書き込み経路を与えるには、**本レルム側に AST 用のサービスクライアントを用意する**のが妥当（ロールの付与主体は
基盤＝本レルムが握る）。

## 決定

`deploy/keycloak/microservices-platform-realm.json` に、AST の KB 書き込み専用の機密クライアントを追加する:

- **client `ai-stock-trading-kb-writer`**（`publicClient: false`・`serviceAccountsEnabled: true`・
  `standardFlowEnabled: false`・`directAccessGrantsEnabled: false`）＝ client_credentials のみ。
- **service-account に realm role `platform-operator` を付与**（`service-account-ai-stock-trading-kb-writer` ユーザ）。
  これで本クライアントの client_credentials トークンは `POST /documents` の書き込みゲート（[[IADR-0044]]）を通過する。
- dev export のシークレットはプレースホルダ。本番シークレットは config（Vault/Secrets）から供給し、AST 側は
  k8s Secret として受け取る（値は Git に載せない）。

### 権限の最小化

- 付与は `platform-operator` のみ（`platform-admin` は付与しない）。書き込み（作成・更新・メタデータ）に必要な最小権限で、
  ABAC 管理（AdminOnly）等の管理系は与えない（[[IADR-0030]] の operator 定義に沿う）。
- 既存クライアント（wiki-js/bff/spa-web）や既存ロールには一切触れない。追加のみ。

## 影響・リスク

- realm import の追加のみ。既存の認証・認可・ユーザーへの影響なし。読み取り（一般利用者の文書閲覧）にも影響しない。
- 本クライアントのシークレットが AST 環境へ配布されるまでは何も起きない（AST 側は Secret 空なら no-op）。
- 削除（アーカイブ/公開）まで含む write グループを通過できる点は operator 権限どおり。AST が実際に呼ぶのは作成
  （カタログ登録）が中心で、危険操作は AST 側の利用範囲に閉じる（本レルムは権限の器のみを提供する）。

## 検討した代替案

- **A: DocumentService の write ロール要件を緩めて AST レルムのトークンを通す** — 却下。[[IADR-0044]] の多層防御を弱める。
- **B: AST レルムに platform-operator 相当を作る** — 却下。issuer が AST レルムのままで本レルムの Authority 検証を通らない。
- **C: 専用ロールを新設して write グループへ足す** — 却下（過剰）。既存の `platform-operator` が write 要件を満たすため、
  ロールを増やさず最小の追加（サービスクライアント1つ）で足りる。
