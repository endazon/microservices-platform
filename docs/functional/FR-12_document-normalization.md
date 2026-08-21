---
title: 機能仕様書 — FR-12 原本の正規化変換（pandoc＋LLMコード化＋画像保持）
type: functional-spec
status: in-progress
created: 2026-07-03
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-02, FR-11, FR-12, SC-07, UC-06]
adrs: [ADR-0010, ADR-0012, ADR-0014]
iadrs: [IADR-0007, IADR-0008, IADR-0137, IADR-0154]
specs: [20260703_FR-12_document-normalization-pipeline]
issues: [#533, #543]
-->

# 機能仕様書: 原本の正規化変換

## 起点

- 機能要求: 原本を AI が扱いやすい正規化形式へ変換して管理する ／ ユースケース: 文書を正規化変換する
- 関連 ADR: 変換パイプライン（pandoc＋LLM）、本文・資産はオブジェクトストレージ、LLM ゲートウェイで送信制御
- 関連する実装 ADR: ポート分離＋deny-by-default 縮退＋決定的 `DocumentId`、設定駆動のエンドポイント定義＋越境マトリクスによる LLM 送信制御

## 機能概要

`ConversionService.Worker` が `RawDocumentFetched` イベントを購読し、取得済みの原本を
正規化形式（本文 Markdown＋資産）へ変換する。本文は **pandoc** で Markdown 化し、図は
**LLM（LLMゲートウェイ `/complete` 経由）** で PlantUML/Mermaid にコード化する。コード化できない図は
**画像として保持**し（変換パイプラインの決定。段階的に全面コード化）、本文・資産は **オブジェクトストレージ**へ
保管する。完了時に `DocumentNormalized` を発行し、文書管理・取り込みへ連鎖する。

## 入力 / 出力

### 入力イベント: `RawDocumentFetched`

| フィールド | 型 | 用途 |
| --- | --- | --- |
| `FetchId` | Guid | 取得イベント識別子（ログ相関）。 |
| `SourceId` | Guid | データソース識別子。冪等 `DocumentId` の基。 |
| `SourceType` | string | ソース種別（filesystem 等）。 |
| `OriginalPath` | string | 原本パス。タイトル・冪等 `DocumentId` の基。 |
| `StorageUri` | string | 原本の所在（pandoc 変換の入力）。 |
| `ContentType` | string | 原本の MIME。pandoc 入力形式の判定に使う。 |
| `Attributes` | Dictionary<string,string> | ABAC 属性。`confidentiality` を図コード化の送信制御に使う。 |
| `Tags` | List<string> | タグ。`DocumentNormalized` へ引き継ぐ。 |
| `FetchedAt` | DateTimeOffset | 取得時刻。 |

### 出力イベント: `DocumentNormalized`

`DocumentId`（冪等）/ `SourceId` / `Title` / `MarkdownUri` / `AssetUris` / `Attributes` / `Tags` /
`NormalizedAt` を発行し、DocumentService の登録 → 取り込み→ Wiki 同期へ連鎖する。

## 処理フロー（正規化変換の基本フロー）

1. `RawDocumentFetched` を受信する。
2. `Attributes["confidentiality"]`（機密区分）を取り出す（図コード化の送信制御に使う）。
3. **本文変換**（`IBodyConverter` / `PandocConversionService`）: 原本を pandoc で GFM へ変換し、
   `--extract-media` で図（画像）を抽出する。本文 Markdown ＋ 抽出図一覧を得る。
4. 冪等 `DocumentId` を `SourceId`＋`OriginalPath` から決定的に導出する（`DeterministicGuid`）。
5. 各図について（`IDiagramCoder` / `LlmGatewayDiagramCoder`）:
   1. LLMゲートウェイ `/complete` に `confidentiality` ＋ `purpose="diagram-coding"` を渡してコード化を依頼する。
   2. **成功**（```mermaid / ```plantuml を抽出）: 本文へコードブロックを埋め込む。
   3. **不可**（送信拒否 `Sent=false`／コード化不能／呼び出し失敗）: 画像を `IObjectStore` へ保存し、
      本文へ `![figureId](uri)` を埋め込む（deny-by-default で画像保持へ収束）。
      **この埋め込み形は人手補正が置換する目印でもある**——形は `FigureMarkdown` を単一情報源とし、
      埋め込む側と置換する側が別々に書かないようにする（人手補正の実装判断による。片方だけ変えると
      置換が静かに空振りする）。
6. **正規化 Markdown を保管**（`IObjectStore.SaveMarkdownAsync`）して参照 URI を得る。
7. `DocumentNormalized`（`DocumentId`／`MarkdownUri`／`AssetUris` 等）を発行する。

### 例外フロー

- **E1（pandoc 未導入／原本がローカル解決不能）**: `PandocConversionService` がプレースホルダ本文
  （図0件）へグレースフルデグレードする（dev 環境での動作保証。ポート分離と縮退の実装判断による）。
- **E2（pandoc 恒久失敗）**: pandoc が非0終了した場合は例外を送出し、MassTransit の再試行→
  デッドレター（`<queue>_error`）へ委ねる。
- **E3（図コード化の LLM 一時障害・送信拒否・コード化不能）**: 例外を送出せず**画像保持へ縮退**する
  （パイプラインを完了させる）。この経路はメッセージ再試行を発火させない。計画書（draft）との差異は
  `feedback/20260703_conversion-retry-vs-image-fallback.md` で計画側へ環流する。

  > **［2026-08-10 追記 / #543］E3 で縮退した図は記録し、後から人手補正できる。**
  > 正規化変換のユースケースは縮退を「**後日の人手補正・再登録でコード化する**」と定めており、**縮退はジョブの失敗ではない**
  > （変換は成功し、`status` は `succeeded` になる）。従前は縮退した図が**どこにも記録されておらず**、
  > 「どの図が画像で残っているか」を後から引けなかった——`NormalizationResult` は図ごとの結果を
  > 返していたが、コンシューマがログ行へ出して捨てていた。
  > 現在は `NormalizationResult.Figures` を `ConversionJobFigures` へ記録し、
  > `GET /bff/conversion/jobs/{id}/figures` から引ける（**Phase 1 = 図のコード化のやり直し**）。
  > **`Figures` に既定値は置いていない** —— 既定値があると、また黙って落とせてしまうためである。
- **E4（保存の恒久失敗）**: `IObjectStore` が例外を送出した場合は E2 と同様に再試行→デッドレターへ委ねる。
- **E2 / E4 のデッドレター標識（2026-08-06 / #533。変換ジョブ画面の裁定 Q13）**: 再試行を使い切った失敗は
  読み取りモデルへ `DeadLettered = true` として記録し、`ConversionJobDto.deadLettered` /
  `maxAttempts` として契約に載せる。**状態値は `failed` のままである**（デッドレターは `failed` の
  内訳であって 5 番目の状態ではない）。判定・生存期間はデッドレターの実装判断が、列は
  [データ仕様書](../data/conversion-job.md)。E3（図コード化の縮退）は再試行を発火させないため
  **標識の対象にならない**。

## 機密制御

- 図コード化の LLM 呼び出しは、LLM 送信先切替機能の `/complete`（設定駆動のエンドポイント定義＋越境マトリクス）へ委譲し、
  変換固有の送信可否ロジックを二重実装しない。
- 応答 `Sent=false`（機密区分による送信拒否）は画像保持へ縮退する。

## 冪等性

- `DocumentId = DeterministicGuid.ForDocument(SourceId, OriginalPath)`（RFC4122 v5 相当）。
  再変換・イベント再投入でも同一 `DocumentId` となり、文書管理側で重複登録を避けられる。

## スコープ外（フォローアップ）

- 実オブジェクトストレージ（MinIO/S3）クライアント（製品確定後）。現状は dev 決定的 URI。
- LLMゲートウェイのマルチモーダル（Vision）画像入力。現状はキャプション/抽出テキストをプロンプト化。
- 実ストレージからの原本フェッチ。現状の pandoc 入力は `file://`／ローカルパスのみ対応。

## トレーサビリティ

- コード: `ConversionService.Worker`（`RawDocumentFetchedConsumer`, `NormalizationService`,
  `PandocConversionService`, `LlmGatewayDiagramCoder`, `StorageObjectStore`, `DeterministicGuid`）。
  各所に `// FR-12, UC-06` 等を付す。
- テスト: `ConversionService.Worker.Tests`（[テスト仕様書](../tests/FR-12_document-normalization.md)）。
