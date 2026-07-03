---
title: IADR-0008 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する
type: impl-adr
status: Accepted
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
  - ../specs/20260703_FR-12_document-normalization-pipeline.md
  - ../functional/FR-12_document-normalization.md
  - ../tests/FR-12_document-normalization.md
  - ./IADR-0007_llm-egress-routing-config-driven.md
---

# IADR-0008: 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: FR-12（原本の正規化変換）、UC-06、ADR-0012（変換パイプライン）、ADR-0014（オブジェクトストレージ）、ADR-0010（LLMゲートウェイ）

## コンテキストと課題

FR-12 / UC-06 は「取得した原本を、AI が扱いやすい正規化形式（本文 Markdown＋資産）へ変換して管理する」を要求する。
本文は pandoc、図は LLM で PlantUML/Mermaid にコード化し、不可分な図は画像として保持する（ADR-0012、段階的に全面コード化）。
本文・資産はオブジェクトストレージへ保管する（ADR-0014）。変換時の LLM 呼び出しも機密区分で送信制御する（ADR-0010）。
実装にあたり、(1) 各外部依存（pandoc / LLMゲートウェイ / オブジェクトストレージ）の抽象化方針、
(2) 図コード化に失敗・拒否したときの縮退方針、(3) 再変換の冪等性の担保方法、を決める必要があった。

## 検討した選択肢

### A. 外部依存の抽象化

1. `NormalizationService` から pandoc / HTTP / ストレージを直接呼ぶ。実装は短いが単体テスト不能で、
   実クライアント未確定（ADR-0014）の現状ではモック化できない。
2. **用途別ポートへ分離**（本決定）: `IBodyConverter`（本文変換）/`IDiagramCoder`（図コード化）/
   `IObjectStore`（資産保管）に分け、`NormalizationService` はオーケストレーションに専念する。

### B. 図コード化の失敗・送信拒否時の扱い

1. コード化不能・送信拒否・呼び出し失敗を例外にし、メッセージ全体を再試行→デッドレターへ送る。
2. **すべて「画像として保持」へ収束**（本決定、deny-by-default）。変換パイプラインは常に完了させ、
   デッドレターは pandoc／保存の恒久失敗に限定する。

### C. 再変換の冪等性

1. 変換のたびに新しい `DocumentId` を採番する。再変換で重複文書が生まれる。
2. **`SourceId`＋原本パスから決定的に導出**（本決定、`DeterministicGuid`, RFC4122 v5 相当）。
   再変換で同一 `DocumentId` となり、文書管理側で重複登録を避けられる（ADR-0012「版で管理」）。

## 決定

- **A-2 を採用**。用途別ポートに分離し、実クライアント（MinIO/S3・pandoc 実行・LLMゲートウェイ）は
  背後の実装差し替えで後付けする。dev 環境では各実装がグレースフルデグレードする
  （pandoc 未導入／原本がローカル解決不能 → プレースホルダ本文、ストレージ未配備 → 決定的 URI 発行）。
- **B-2 を採用**。`IDiagramCoder` は「コード化不能」「機密区分による送信拒否（`Sent=false`）」
  「呼び出し失敗」をすべて `Retain(reason)` として返し、`NormalizationService` が画像保持へ振り分ける。
  送信可否ロジックは FR-11 の `/complete`（越境マトリクス、[IADR-0007](./IADR-0007_llm-egress-routing-config-driven.md)）へ委譲し、
  変換固有の送信制御を二重実装しない。
- **C-2 を採用**。`DeterministicGuid.ForDocument(SourceId, OriginalPath)` で `DocumentId` を導出する。
- **pandoc 実行**: `IBodyConverter` は pandoc が利用可能かつ原本がローカル解決可能な場合、
  `pandoc -f <fmt> -t gfm --extract-media <tmp> <src>` を実行し、抽出画像を `ExtractedFigure` に写す。
  恒久失敗（pandoc 非0終了）は例外を送出し、MassTransit の再試行→デッドレターへ委ねる。

## 理由

- ポート分離により、実クライアント未確定（ADR-0014 は Proposed）でも受け入れ基準の分岐
  （コード化成功／画像保持／送信拒否縮退／冪等 ID）を単体テストで検証できる。
- deny-by-default 縮退は ADR-0012 の「段階的に全面コード化（当面は画像保持を許容）」と、
  ADR-0010 の「送信不可時は縮退」の双方に整合し、変換パイプラインの完了性を保証する。
- 決定的 `DocumentId` は再投入・再変換に対する冪等性（UC-06 代替フロー＝人手補正後の再登録）を、
  文書管理側の状態に依存せず担保する。

## 結果

- 良い影響: 実クライアント差し替えが局所化。分岐が網羅的にテスト可能。再変換が冪等。
- トレードオフ: 図コード化の LLM 一時障害が画像保持へ縮退し、その図単体では再試行されない
  （下記「計画との差異」参照）。
- フォローアップ（含まないもの）:
  - 実オブジェクトストレージ（MinIO/S3）クライアントの実装（ADR-0014 製品確定後）。
  - LLMゲートウェイのマルチモーダル（Vision）画像入力対応（現状はキャプション/抽出テキストをプロンプト化）。
  - pandoc の入力形式判定の拡充と、実ストレージからの原本フェッチ（現状は file://／ローカルパスのみ）。

## 計画との差異（要環流）

- `04_workflows/03_conversion-flow.md` の例外処理は「LLM 一時障害は再試行する」と定めるが、
  本実装は図コード化の呼び出し失敗を deny-by-default で**画像保持へ縮退**させるため、
  その図についてはメッセージ再試行が発火しない（デッドレターは pandoc／保存の恒久失敗に限定）。
- これは「変換パイプラインを常に完了させ、人手補正で後から再登録する」という段階的コード化方針
  （ADR-0012）を優先した実装判断である。計画書（draft）との差異は
  `feedback/20260703_conversion-retry-vs-image-fallback.md` として `/plan-feedback` で計画側へ環流する。

## 前提リスク（計画ドキュメントの確定状況）

- 本決定が根拠とする **ADR-0010 / ADR-0012 / ADR-0014 はいずれも `Proposed`** であり正式確定していない。
  pandoc＋LLM 構成・オブジェクトストレージ方式・送信制御が確定後に変われば、本実装も追随する。
  確定内容が本決定と矛盾する場合は新 IADR で更新する。
