---
title: 作業仕様書 — FR-12 原本の正規化変換（pandoc＋LLMコード化＋画像保持）
type: work-spec
status: in-progress
related_ids:
  - FR-12
  - UC-06
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-12)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-06)"
  - "../../planning/projects/microservices-platform/04_workflows/03_conversion-flow.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0014_object-storage.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md"
related_specs:
  - ./20260627_FR-02_ingestion-pipeline.md
  - ./20260702_FR-11_llm-egress-routing.md
related_adrs:
  - ADR-0012 (文書正規化変換 pandoc＋LLM)
  - ADR-0014 (本文・資産はオブジェクトストレージ)
  - ADR-0010 (LLMゲートウェイで送信制御)
---

# 作業仕様書: FR-12 原本の正規化変換

## 目的

FR-12「取得した原本を、AIが扱いやすい正規化形式へ変換して管理する」（UC-06）を実装する。
本文は **pandoc** で Markdown 化し、図は **LLM（LLMゲートウェイ経由）** で PlantUML/Mermaid にコード化する。
コード化できない不可分な図は **画像として保持** する（ADR-0012、段階的に全面コード化）。
本文・資産は **オブジェクトストレージ**（S3互換、ADR-0014）へ保管し、参照 URI を
`DocumentNormalized` イベントで文書管理サービスへ引き継ぐ。

## 背景・現状（調査結果）

- `ConversionService.Worker` には `RawDocumentFetchedConsumer` と `PandocConversionService`
  （プレースホルダ：URI を `/raw/`→`/normalized/` へ差し替えるだけ）しかなく、
  FR-12 の核である **図のコード化・画像保持・オブジェクトストレージ・機密制御・冪等/再試行が未実装**だった。
- FR-11（[20260702_FR-11](./20260702_FR-11_llm-egress-routing.md)）で LLMゲートウェイ `/complete` が
  `confidentiality`・`purpose` による送信制御（越境マトリクス）を持つ。図のコード化もこれを経由し、
  ADR-0012「変換時のLLM呼び出しも機密区分に応じて送信制御する」を満たす。
- 共有契約 `CompletionApiRequest/Response`（`KnowledgePlatform.Shared.Contracts.Dtos`）を再利用する。
  `Sent=false`（機密区分による送信拒否）時は縮退＝**画像として保持**する。

## 業務フロー（[03_conversion-flow](../../planning/projects/microservices-platform/04_workflows/03_conversion-flow.md)）

`RawFetched` 受信 → pandoc で本文→Markdown → 図を LLM でコード化
（成功=コードブロック埋込 / 不可=画像保存し参照埋込）→ 資産保存 → 正規化 Markdown 保存 →
`DocumentNormalized` 発行（文書管理・取り込みへ連鎖）。

## 作業範囲

### 含むもの（本 PR）

- **本文変換ポート** `IBodyConverter`（`PandocConversionService`）: 原本 → Markdown 本文 ＋ 抽出図一覧。
  pandoc 未導入の dev 環境ではプレースホルダ本文へグレースフルデグレード（既存方針を踏襲）。
- **図コード化ポート** `IDiagramCoder`（`LlmGatewayDiagramCoder`）: 図を LLMゲートウェイ `/complete`
  （`confidentiality` ＋ `purpose="diagram-coding"`）へ送り PlantUML/Mermaid 化。
  - `Sent=false`（機密区分で送信拒否）・コード化不可・呼び出し失敗はいずれも **画像保持** に倒す（deny-by-default）。
  - 応答のフェンス済みコードブロック（```mermaid / ```plantuml）から言語とコードを抽出する。
- **オブジェクトストレージポート** `IObjectStore`（`StorageObjectStore`, ADR-0014）:
  正規化 Markdown・画像資産を保管し参照 URI を返す。dev では未配備のため決定的 URI を生成（グレースフル）。
- **正規化オーケストレータ** `NormalizationService`: 上記を束ね、図ごとに
  コード化成功→本文へコードブロック埋込 / 不可→画像を保存し `![figure](uri)` 埋込。資産 URI を集約。
- **冪等性**（ADR-0012「同一原本の再変換は版で管理し重複登録を避ける」）:
  `DocumentId` を `SourceId`＋`OriginalPath` から決定的に導出（`DeterministicGuid`, RFC4122 v5 相当）。
  再変換時も同一 `DocumentId` となり、文書管理側で重複登録を避けられる。
- **再試行・デッドレター**（UC-06 例外フロー）: 受信エンドポイントに `UseMessageRetry` を設定。
  再試行を使い切った継続失敗は MassTransit が自動で `_error`（デッドレター）キューへ送る。
- **テスト**: 図コード化成功/画像保持の分岐、`Sent=false`縮退、コード化失敗の縮退、冪等な `DocumentId`。

### 含まないもの（フォローアップ）

- 実オブジェクトストレージ（MinIO/S3）クライアントの実装（ADR-0014 は製品未確定）。本 PR は
  ポート＋dev グレースフル実装のみ。実クライアントは後付けする。
- LLMゲートウェイの **マルチモーダル（Vision）画像入力エンドポイント**。現行 `/complete` は
  テキスト契約のため、本 PR の `IDiagramCoder` は図のキャプション/抽出テキストをプロンプト化して送る。
  画像バイト列を直接送る Vision 対応はゲートウェイ拡張後のフォローアップ（`IADR` に記録）。
- pandoc の実コマンド実行と実図抽出。dev では未導入のためグレースフルデグレード（本文プレースホルダ・図0件）。
  → パイプラインの図分岐ロジックは `IBodyConverter` をフェイクした単体テストで検証する。
- 人手補正フロー UI（UC-06 代替フロー）。イベント再投入で再変換可能な冪等設計のみ用意する。

## 受け入れ基準の写像（FR-12 固有）

- 原本を正規化形式（Markdown＋資産）へ変換して管理できる → `NormalizationService` ＋ `IObjectStore`。
- 図は PlantUML/Mermaid 化、不可分は画像保持 → `IDiagramCoder` の成功/保持分岐（テストで検証）。
- 変換時の LLM 呼び出しも機密区分に応じて送信制御 → `confidentiality` を `/complete` へ渡し、
  `Sent=false` は画像保持に縮退（ADR-0012 / ADR-0010）。
- 各サービスを個別にデプロイ・ロールバックできる → 変換はワーカー内で完結、契約は後方互換（`AssetUris` は既存フィールド）。
- 更新の反映（15分以内）→ 変換完了で `DocumentNormalized` を即時発行し後続（取り込み・Wiki同期）へ連鎖。

## 実装判断（IADR 候補）

- 図コード化を専用ポート `IDiagramCoder` に分離し、送信制御は FR-11 の `/complete`
  （越境マトリクス）へ委譲する。変換固有の送信可否ロジックを二重実装しない。
- コード化不可・送信拒否・呼び出し失敗を **すべて画像保持へ収束**（deny-by-default）させ、
  変換パイプラインを常に完了させる（デッドレターは pandoc/保存の恒久失敗に限定）。
