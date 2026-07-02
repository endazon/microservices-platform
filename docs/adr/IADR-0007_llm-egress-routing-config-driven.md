---
title: IADR-0007 LLM 呼び出し先の切替は設定駆動のエンドポイント定義＋越境マトリクスで行う
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - UC-02
author: claude
created: 2026-07-02
updated: 2026-07-02
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-11)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-02)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260702_FR-11_llm-egress-routing.md
---

# IADR-0007: LLM 呼び出し先の切替は設定駆動のエンドポイント定義＋越境マトリクスで行う

- 状態: Accepted
- 日付: 2026-07-02
- 決定者: claude（実装）
- 関連: FR-11（LLM 呼び出し先の切替）、UC-02、ADR-0010（LLMゲートウェイ）、08_data-egress-policy

## コンテキストと課題

FR-11 は「LLM の呼び出し先（外部マネージドAPI／セルフホスト）を**用途・機密度に応じて切り替えられる**」を要求する。
ADR-0010 は切替を **LLMゲートウェイで一元化**し、08_data-egress-policy は「**機密区分 × 送信先ティア**」の
越境マトリクスで送信可否を決めると定める。実装で、この切替の「持ち方」を決める必要があった。

## 検討した選択肢

1. **プロバイダを enum/switch で直書き**（機密区分→プロバイダを固定ロジック化）。実装は最短だが、
   契約改定・ティア再判定・エンドポイント追加のたびにコード変更・再デプロイが必要。
2. **設定駆動のエンドポイント定義＋越境マトリクス**（本決定）。エンドポイント（名前・ティア・プロバイダ・
   モデル・有効/優先度）を `appsettings` の `Llm:Routing` で定義し、機密区分→許容ティアの越境マトリクスは
   ポリシー由来の固定表として実装。ルーターが「許容ティア ∩ 有効エンドポイント」から用途に応じて選択する。
3. **外部ポリシーエンジン（OPA 等）へ委譲**。柔軟だが当初要件に対し過大で、運用・依存が増える。

## 決定

選択肢2を採用する。

- 越境マトリクス（機密区分→許容ティア、`internal×C` の要承認）は `EgressMatrix` に固定表として実装する
  （08_data-egress-policy の表がそのまま根拠）。
- 呼び出し先エンドポイントは `Llm:Routing:Endpoints`（名前・ティアA/B/C・プロバイダキー・モデル・有効/優先度）で定義する。
- 用途→モデルは `Llm:Routing:PurposeModels` で切替（例: rag-answer→sonnet, analysis→opus, diagram→haiku）。
- プロバイダはキー付き DI（`claude`＝ティアB / `selfhosted`＝ティアA）で登録し、ルーターの決定で切り替える。
- 許容ティアに送信可能なエンドポイントが無い場合は**送信せず縮退**（`Sent=false`）とし、呼び出し側は出典のみ返す。
- すべての送信判定（機密区分・ティア・エンドポイント・モデル・許否・理由）を監査ログに記録する（ADR-0010）。

## 理由

- 契約条件の改定・ティア再判定・エンドポイント追加を**設定変更で運用に追従**でき、コード変更を要しない
  （08_data-egress-policy「ティア定義変更は変更管理の対象」に整合）。
- 越境マトリクスはセキュリティ要件そのものなのでコード（テスト可能な固定表）に置き、可監査性を担保する。
- セルフホスト（ティアA）は ADR-0010 のとおり「後付け可能」を、無効エンドポイント定義＋キー付きプロバイダで実現する。

## 結果

- 良い影響: 用途・機密度による切替が一元化・テスト可能・設定追従可能になる。送信拒否時の縮退が明示化される。
- トレードオフ: 越境マトリクスの変更はコード変更（＋レビュー）を要する（意図的に安全側へ倒す）。
- フォローアップ: 実セルフホスト基盤（GPU）の構築、例外送信（区分の一時ダウングレード）の申請・承認ワークフロー、
  エンドポイントのティア判定根拠（契約・保持・学習・所在・監査）のメタデータ化。
