---
title: 本番 chart で LlmGateway の Llm__ApiKey を Secret 経由で配線する（Issue #308）
type: spec
status: done
related_ids:
  - FR-02
  - ADR-0010
  - IADR-0025
  - IADR-0066
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0025_embedding-provider-routing-and-model-collections.md"
  - "../security/security.md"
  - "../../deploy/helm/microservices-platform/values.yaml"
  - "../../deploy/local/values-local.yaml"
  - "../../deploy/local/README.md"
---

# 仕様書: 本番 chart の LlmGateway に Llm__ApiKey を Secret 経由で配線（Issue #308）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-02（LLM 連携／RAG 回答生成の基盤）。
- 関連 ADR: [ADR-0010]（外部マネージド API 主体の LLM ゲートウェイ＝呼び出し・鍵を MSP 側の
  LlmGateway で集中管理する）。egress/セルフホスト埋め込みの実装配線は [[IADR-0025]]。
- Issue: #308（監査で発見・priority:should）。関連: #310（平文資格情報→Vault / External Secrets 移行の一元追跡）。

## 背景と問題

経路B ローカル（`deploy/local/values-local.yaml`）では `services.llmgateway.extraEnv` で
`Llm__ApiKey` を Secret `llm-provider-credentials` の `anthropic-api-key` から注入し、実 LLM 疎通が可能。
一方 **本番 chart（`deploy/helm/microservices-platform/values.yaml`）の `services.llmgateway`（293–305 行）は
`extraEnv` / `Llm__ApiKey` / `llm-provider-credentials` の参照を一切持たず未配線**であり、本番像では
LlmGateway に鍵が渡らない。`deploy/local/README.md` も「本番 chart はこの Secret を未参照であり、本番側の
配線は別課題」と既知ギャップとして明記していた。

ADR-0010（鍵は MSP 側で集中管理）に沿い、本番 chart でも `Llm__ApiKey` を Secret 経由で配線する必要がある。

### 配線経路（実コード / テンプレで特定）

```
values.yaml services.llmgateway.extraEnv[].secretKeyRef
  → templates/deployment.yaml（96–105 行）: extraEnv を range し
      .secretKeyRef があれば valueFrom.secretKeyRef.{name,key} を描画
  → Pod env Llm__ApiKey ← Secret llm-provider-credentials / anthropic-api-key
```

- 先例（同型）: 本番 `values.yaml` の `services.wiki.extraEnv`（282–288 行）が `WikiJs__ApiKey` を
  Secret `wikijs-sync` から `secretKeyRef` で注入している（Secret は事前に kubectl/ArgoCD で作成＝リポにコミットしない）。
- 経路B 側（`values-local.yaml` 80–89 行）と同一キー・同一 Secret 名／キー名。

## 対応方針

本番 `values.yaml` の **`services.llmgateway` ブロックにのみ** `extraEnv` を追加し、`Llm__ApiKey` を
Secret `llm-provider-credentials` / `anthropic-api-key` から `secretKeyRef` で注入する（wiki と同型・追加のみ）。

- **平文禁止**: 実キーはコミットしない。Secret は秘匿管理（Vault / External Secrets Operator）で本番環境へ供給する
  （#310 の一元追跡と整合）。既存 `wikijs-sync` と同じ「Secret は環境側で事前供給」方針。
- **実キー投入・実 LLM 疎通の検証は go-live（実環境）＝対象外**（分離）。
- **後方互換**: `extraEnv` の追加のみで既存キー・他サービスブロックは無改変。経路B の
  `values-local.yaml` は同一 `services.llmgateway.extraEnv` を上書き（配列置換・同値）するため挙動不変。

### スコープ外（並行作業・本 PR では触れない）

- 他サービスブロック・`deploy/local/values-local.yaml`・`realm.json`・frontend・BFF の `Services__*`。
- `templates/deployment.yaml`（既に `secretKeyRef` 描画対応済み＝無改変）。
- Vault / External Secrets の実オブジェクト定義（#310・go-live 側）。

## 実装ADR

純 config 配線（既設計キー・既存テンプレ描画・wiki の先例に倣う追加のみ）であり、新規の設計判断は
無いため **IADR は起票しない**（最新は IADR-0089）。

## 受け入れ基準

- [x] 本番 `values.yaml` の `services.llmgateway` が Secret `llm-provider-credentials` を参照し、
      `Llm__ApiKey` を env（`secretKeyRef`）として LlmGateway に配線する。
- [x] `helm template deploy/helm/microservices-platform` で llmgateway Deployment の env に
      `Llm__ApiKey` の `valueFrom.secretKeyRef`（name=`llm-provider-credentials` / key=`anthropic-api-key`）が描画される。
- [x] 平文の API キーをコミットしない（Secret 供給は Vault / External Secrets 経由・#310）。
- [x] 他サービスブロック・`values-local.yaml`・realm・frontend・BFF は無改変（後方互換）。
- [x] `check-image-mapping.js`（#275 ドリフト）が緑（イメージ不変）。
- [ ] 実キー投入・実 LLM 疎通は **go-live（実環境）**（本 issue の対象外）。
