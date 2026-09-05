---
title: 機能仕様書 — FR-12 原本の正規化変換（pandoc＋LLMコード化＋画像保持）
type: functional-spec
status: in-progress
created: 2026-07-03
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [FR-01, FR-02, FR-11, FR-12, SC-07, UC-06]
adrs: [ADR-0010, ADR-0012, ADR-0014, ADR-0053, ADR-0070]
iadrs: [IADR-0007, IADR-0008, IADR-0137, IADR-0154, IADR-0298, IADR-0320, IADR-0351, IADR-0356]
specs: [20260703_FR-12_document-normalization-pipeline, 20260831_issue-1097_pandoc-runtime-image-and-fail-closed, 20260903_issue-1120_extract-media-path-rewrite, 20260903_issue-1192_pdf-text-layer-extraction]
issues: [#533, #543, #1097, #1120, #1192]
-->

# 機能仕様書: 原本の正規化変換

## 起点

- 機能要求: 原本を AI が扱いやすい正規化形式へ変換して管理する ／ ユースケース: 文書を正規化変換する
- 関連 ADR: 変換パイプライン（pandoc＋LLM）、本文・資産はオブジェクトストレージ、LLM ゲートウェイで送信制御
- 関連する実装 ADR: ポート分離＋deny-by-default 縮退＋決定的 `DocumentId`、設定駆動のエンドポイント定義＋越境マトリクスによる LLM 送信制御

## 機能概要

`ConversionService` が `RawDocumentFetched` イベントを購読し、取得済みの原本を
正規化形式（本文 Markdown＋資産）へ変換する。本文は **pandoc** で Markdown 化し
（**PDF だけは例外**で、pandoc の外に置いたテキスト層の抽出器 `pdftotext` が本文を取り出す。
2026-09-03 の計画裁定による部分改定）、図は
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
3. **本文変換**（`IBodyConverter` / `PandocConversionService`）: 原本をオブジェクトストレージから
   取り寄せて（`file` スキーム・ローカルパスはそのまま）pandoc で GFM へ変換し、
   `--extract-media` で図（画像）を抽出する。本文 Markdown ＋ 抽出図一覧を得る。
   pandoc は**実行時イメージへ導入済み**であり、その存在は readiness（`pandoc` チェック）が見る。
   🔴 `--extract-media` は**本文中の画像参照を一時ディレクトリの絶対パスへ書き換える**が、
   そのディレクトリは変換直後に消える。よって変換器は返す前に、その参照を
   **図の目印（`![figureId](figure:figureId)`）へ書き換える**——`<img>` タグや `![](…)` を
   **構文まるごと**置き換え、写像できない参照は落とす。**一時パスは 1 件も残さない**
   （残渣の走査つき）。目印の綴りは `FigureMarkdown` を単一情報源とする。
   - **PDF は pandoc ではなくテキスト層の抽出器へ振り分ける**（`FormatRoutingBodyConverter` →
     `PdfTextLayerConverter`。`pdftotext -enc UTF-8 -nopgbrk` を外部プロセスとして起動する。
     ローカル完結・外部送信なし・図は抽出しない）。抽出結果が**空白のみ**なら「テキスト層なし」とし、
     `HasBody = false` の本文なし結果を返す（E6）。**振り分けの判定は `PandocInputFormat` の
     1 箇所**であり、PDF は `null`、計画の対応形式表に無い未知の形式は E5 で拒否する。
4. 冪等 `DocumentId` を `SourceId`＋`OriginalPath` から決定的に導出する（`DeterministicGuid`）。
5. 各図について（`IDiagramCoder` / `LlmGatewayDiagramCoder`）:
   1. LLMゲートウェイ `/complete` に `confidentiality` ＋ `purpose="diagram-coding"` を渡してコード化を依頼する。
   2. **成功**（```mermaid / ```plantuml を抽出）: 本文へコードブロックを埋め込む。
   3. **不可**（送信拒否 `Sent=false`／コード化不能／呼び出し失敗）: 画像を `IObjectStore` へ保存し、
      本文へ `![figureId](uri)` を埋め込む（deny-by-default で画像保持へ収束）。
   4. 埋め込み先は**変換器が置いた目印の位置**、すなわち**原本で図があった場所**である。
      目印を持たない本文（縮退したプレースホルダ本文など）では**末尾へ足す**。
      🔴 従前は無条件に末尾へ足しており、本文中には消えたディレクトリへの壊れた参照が
      残ったまま、**同じ図が 2 度出ていた**（変換器を実走させて初めて観測された）。
      **この埋め込み形は人手補正が置換する目印でもある**——形は `FigureMarkdown` を単一情報源とし、
      埋め込む側と置換する側が別々に書かないようにする（人手補正の実装判断による。片方だけ変えると
      置換が静かに空振りする）。
6. **正規化 Markdown を保管**（`IObjectStore.SaveMarkdownAsync`）して参照 URI を得る。
7. `DocumentNormalized`（`DocumentId`／`MarkdownUri`／`AssetUris` 等）を発行する。

### 例外フロー

- **E1（pandoc 未導入／原本を読み出せない）**: 🔴 **既定は失敗する**（fail-closed）。
  `PandocConversionService` が `BodyConversionUnavailableException` を送出し、E2 と同じく
  再試行 → デッドレターへ委ねる。

  > **［2026-08-31 追記］従前ここは無条件にプレースホルダ本文（図0件）を返して「成功」していた。**
  > ところが実行時イメージが pandoc を持っておらず、**配備した実物がその縮退のまま成功を返し続けていた** ——
  > 変換ジョブ画面には成功として並び、「変換した」と「変換したふりをした」が区別できなかった。
  > 縮退そのものは残してある（単体テストは pandoc の無い CI・開発機で走る必要がある）が、
  > **`Conversion:AllowDegradedBodyConversion=true` を明示した場合に限る**。既定は `false` であり、
  > 配備（helm / compose）はこの値を注入しない。
- **E2（pandoc 恒久失敗）**: pandoc が非0終了した場合は例外を送出し、メッセージ再試行→
  デッドレター（`<queue>_error`）へ委ねる。
- **E5（どの変換器の入力にもならない形式）**: 計画の対応形式表に無い未知の形式
  （未知の MIME ＋未知の拡張子）は、既定形式へ落として pandoc に食わせず、
  `UnsupportedSourceFormatException` として**明示的に拒否**する（従前の既定 `markdown` は
  対応していない形式を静かに壊れた本文にするため、計画裁定で頼らないことになった）。
  再試行しても結果が変わらないため、コンシューマは**再送出せず**恒久失敗として記録する
  （`status = failed` ／ `deadLettered = true`。デッドレターキューへは流さない）。
  変換ジョブ画面には理由つきの失敗として並び、`POST /retry` で再変換できる。

  > **［2026-09-03 追記］PDF はもう E5 ではない。** 従前は「pandoc が入力に取れない」として PDF を
  > ここで拒否していた（2026-08-31 の暫定処置）が、計画側の裁定で **PDF はテキスト層の抽出器で
  > 本文を取り出す**ことになった（処理フロー 3 の振り分け）。「取り込めるが変換できない」状態は解消した。
- **E6（テキスト層を持たない PDF）**: スキャン等でテキスト層が無い PDF は、抽出結果が**空白のみ**で
  あることを確かめたうえで「**本文なし・原本参照のみの文書**」として変換を**完了**させる。
  🔴 **失敗ではない**——再試行もデッドレターもしない（何度やっても結果は変わらない）。
  `status = succeeded` のまま `hasBody = false` を内訳として記録し（状態値の 5 値目にしない）、
  変換ジョブ画面には「本文なしで完了」と理由つきで表示する（再変換の対象に並ばない）。
  `document.md` は空の内容で保管し、`DocumentNormalized.HasBody = false` で後続へ伝える。
  **カタログ（DocumentService）がこれを台帳へ保持し、`DocumentUpdated` へ写して SC-03 の
  「本文なし（原本を参照）」の材料にする**（#1254）。本文由来のチャンクは作らず、
  索引側はメタデータ 1 点で検索に載せる（#1193）。
  **［2026-09-05 / #1254］項目名は否定形 `BodyAbsent` から肯定形 `HasBody` へ改名し、極性を反転した**
  （読み替え表は `docs/data/conversion-job.md` §本文の有無の語彙）。
  抽出器そのものが無い・原本が読めない・`pdftotext` が非 0 終了する（壊れた PDF・暗号化）場合は
  **本文があるのに作れない失敗**であり、E1 / E2 と同じく fail-closed のまま（縮退しない）。
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

- LLMゲートウェイのマルチモーダル（Vision）画像入力。現状はキャプション/抽出テキストをプロンプト化。
- ~~PDF の本文抽出。pandoc は PDF を入力に取れないため、現状は E5 として明示的に拒否する
  （別経路を足すかどうかは計画側の裁定事項）。~~ **［2026-09-03］スコープ外ではなくなった。**
  計画側の裁定でテキスト層の抽出器（`pdftotext`）を pandoc の外に置き、実行時イメージへ
  `poppler-utils` を同梱した（処理フロー 3・E6）。残るのは **OCR**（テキスト層を持たない PDF の
  本文を作る手段。計画が「将来の別判断」として留保）と、**本文なし文書のメタデータ索引・検索結果の
  表示**（別作業）である。PDF 内画像の図抽出も行わない（計画に無い）。

> **［2026-08-31 追記］「実ストレージからの原本フェッチ（pandoc 入力は `file` スキームと
> ローカルパスのみ）」はスコープ外ではなくなった。** オブジェクトストレージの原本は
> `IObjectStorageClient` で取り寄せて一時ファイルへ落とし、pandoc に食わせる。
> 🔴 これが無いと、**pandoc を実行時イメージへ入れても原本が解決できず縮退したままになる**
> （取り込み経路が発行する原本参照は常にオブジェクトストレージの参照である）。

## トレーサビリティ

- コード: `ConversionService`（`RawDocumentFetchedConsumer`, `NormalizationService`,
  `PandocConversionService`, `LlmGatewayDiagramCoder`, `StorageObjectStore`, `DeterministicGuid`）。
  各所に `// FR-12, UC-06` 等を付す。
- テスト: `ConversionService.Tests`（[テスト仕様書](../tests/FR-12_document-normalization.md)）。
