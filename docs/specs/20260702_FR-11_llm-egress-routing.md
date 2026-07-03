---
title: 作業仕様書 — FR-11 LLM 呼び出し先の用途・機密度による切り替え
type: work-spec
status: in-progress
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
  - ./20260627_FR-04_ai-answer-citations.md
  - ./20260627_FR-07_data-range-analysis.md
  - ./20260627_FR-05_abac-deny-by-default.md
related_adrs:
  - ADR-0010 (外部マネージドAPI主体のLLMゲートウェイ)
  - ADR-0004 (ABAC / deny-by-default)
---

# 作業仕様書: FR-11 LLM 呼び出し先の用途・機密度による切り替え

## 目的

FR-11「LLM の呼び出し先（外部マネージドAPI／セルフホスト）を**用途・機密度に応じて切り替えられる**」（UC-02）を実装する。
呼び出し先の切り替えは、ADR-0010 のとおり **LLMゲートウェイで一元化**し、
[08_data-egress-policy.md](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)
の「**機密区分 × 送信先ティア**」越境マトリクスに基づいて送信可否・送信先・モデルを決定する。

## 背景・現状（調査結果）

- `LlmGateway.Api` は `ILlmProvider`（`ClaudeProvider`）と `/complete` `/embed` を持つが、
  **呼び出し先は Claude 固定**で、機密区分・用途による切り替えが未実装だった。
- ABAC（FR-05/FR-09）で文書は `confidentiality ∈ {public, internal, confidential, restricted}` を持つ
  （`AuthorizationService` の属性辞書、`SearchResultDto.Attributes`）。
- `RagOrchestrator`（AiAnalysisService）は検索結果を文脈に `/complete` を呼ぶが、
  機密区分・用途を渡していなかった。

## 越境マトリクス（08_data-egress-policy.md より）

| 機密区分 | ティアA セルフホスト | ティアB 保護契約済み外部API | ティアC 標準外部API |
| --- | --- | --- | --- |
| `public` | 可 | 可 | 可 |
| `internal` | 可 | 可 | 条件付き可（要承認） |
| `confidential` | 可 | 可 | 不可 |
| `restricted` | 可 | 可（追加統制下） | 不可 |

- 既定は安全側。許容ティアに送信可能なエンドポイントが無い場合は**送信せず縮退／拒否**する。
- 未知・未指定の機密区分は安全側（`restricted` 相当）に倒す。

## 作業範囲

### 含むもの（本 PR）

- **ルーティング層**（`LlmGateway.Api/Routing`）
  - `SensitivityClass`（public/internal/confidential/restricted）と文字列パース・最高区分算出。
  - `ProtectionTier`（A/B/C）と `EgressMatrix`（機密区分→許容ティア、internal×C の要承認判定）。
  - `LlmRoutingOptions` / `LlmEndpointOptions`（`appsettings` の `Llm:Endpoints` からエンドポイント定義を読む）。
  - `ILlmRouter` / `LlmRouter`: (機密区分, 用途, 要求モデル) から
    **許容ティアのエンドポイント＋モデルを選択**、または**送信拒否の判定**を返す。用途→モデルは設定で切替。
- **プロバイダ切替**: `ILlmProvider` をキー付き DI（`claude` / `selfhosted`）で登録し、
  ルーターの決定に従い呼び出し先を切り替える。セルフホスト（ティアA, OpenAI互換）は
  ADR-0010 のとおり「後付け可能」とし、既定は無効。
- **`/complete` 拡張**: `confidentiality`・`purpose` を受け取り、ルーティング判定→送信 or 拒否。
  レスポンスに `Sent`・`Endpoint`・`RoutingReason` を含める。
- **監査ログ**: 送信判定（機密区分・選択ティア・エンドポイント・モデル・許否・理由）を記録（ADR-0010）。
- **統合**（`RagOrchestrator`）: 文脈文書の**最高機密区分**と**用途**（rag-answer / analysis）を `/complete` へ渡す。
  送信拒否（`Sent=false`）時は検索結果（出典）のみ返す縮退。
- **テスト**: 越境マトリクス・ルーター選択・拒否・`/complete` の切替をユニット/統合で検証。

### 含まないもの

- 実セルフホスト LLM 基盤（GPU）の構築（ADR-0010: 後付け）。既定は無効エンドポイントとして定義のみ。
- 例外送信（区分の一時ダウングレード）の申請・承認ワークフロー UI（08 章「例外運用」）。本 PR は要承認ゲートのみ。
- 埋め込み（`/embed`）の切替（FR-03/ADR-0013 の領域）。
- **`restricted × ティアB` の「追加統制下」の具体化**（越境マトリクスの `restricted` はティアBを「追加統制下」で許容する）。
  本 PR では `confidential × B` と同等（送信可）として扱い、追加統制（承認フラグ・特別な監査マーカー・
  匿名化/最小化要件等）は未実装。08_data-egress-policy の値集合・マトリクス確定（下記リスク）後にフォローアップする。
  → フォローアップは [IADR-0007](../adr/IADR-0007_llm-egress-routing-config-driven.md) の「フォローアップ」に記載。

## リスク・前提（計画ドキュメントの確定状況）

- 本実装が根拠とする **ADR-0010 は `Proposed`、08_data-egress-policy.md は `draft`** であり、いずれも `Accepted`/`fixed` に
  至っていない。08_data-egress-policy 自身も「機密区分の値集合と越境マトリクスの最終確定（セキュリティ部門レビュー）」を
  未決事項として残している。
- したがって本 PR は **現時点のドラフト表を安全側（deny-by-default）で実装**したものであり、値集合・マトリクスが
  確定した際は `EgressMatrix` / `SensitivityClass` / `PurposeModels` の追従が必要になる可能性がある。
  越境マトリクスは設定ではなくコード（テスト可能な固定表）に置いているため、確定時は差分レビュー付きで追従する。
- 上記の未確定リスクと追従方針は [IADR-0007](../adr/IADR-0007_llm-egress-routing-config-driven.md) にも記録する。

## 受け入れ基準の写像（FR-11 固有）

- 用途・機密度に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替えられる → `LlmRouter` + `/complete`。
- 権限の無い文書は AI 回答に現れない（FR-05 で担保済み）＋機密区分の高い文書は許容ティア外へ送信しない → `EgressMatrix`。
- 各サービスを個別にデプロイ・ロールバックできる → ゲートウェイ内で完結、契約は後方互換（追加フィールド）。

## 実装判断（IADR 候補）

- 呼び出し先切替をプロバイダ enum 直書きでなく、**設定駆動のエンドポイント定義＋越境マトリクス**で行う
  （契約改定・ティア再判定に運用で追従できるため）。→ `docs/adr/IADR-00xx` に記録予定。
