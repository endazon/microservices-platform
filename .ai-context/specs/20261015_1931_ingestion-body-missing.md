---
title: 取り込みが本文の NoSuchKey を一時障害として扱わず、再試行・DLQ に回さずに記録だけして終える（#1931 項目 2）
type: spec
status: done
related_ids: [FR-02, FR-05, UC-04, NFR-06, ADR-0016, ADR-0027, ADR-0070, ADR-0106, ADR-0127, IADR-0358, IADR-0536, IADR-0540]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-02 取り込み / NFR-06 障害時の縮退運転）
issue: "#1931"
---

# 仕様書: 本文の NoSuchKey を恒久的な欠落として扱う（#1931 項目 2）

## 起点となる計画書（トレーサビリティ）

- FR-02（取り込み）・UC-04・NFR-06（障害時の縮退運転）。FR-05 / ADR-0016（冒頭の全コレクション削除＝fail-closed）は**変えない**理由として引く。
- ADR-0027（Wolverine。共通既定の再試行・DLQ）・ADR-0106（SeaweedFS）・ADR-0070 決定 4（本文なしのメタデータ点）・ADR-0127（高機密は語彙索引だけ）。
- 実装判断の記録: [IADR-0540](../adr/IADR-0540_ingestion-body-missing-is-permanent.md)。
- 起点: #1931（PoC・2026-10-15 夜。再索引のカナリアで DLQ が 36 → 48）。本件は**項目 2 だけ**を扱う。項目 1（駆動スクリプトの `--created-after`）は別 PR、項目 3（本文の無い 5,803 件の扱い）はオーナーの裁定待ち。

## 背景

- 9/26 の SeaweedFS 切り替えより前に作られた文書は、旧ストレージの中身を破棄したため本文のオブジェクトが無い。
- ingestion-service は `DocumentUpdated` の処理で本文を読み、`Amazon.S3.Model.NoSuchKeyException` を受けて例外のまま上げる。
  Wolverine の共通既定（4 試行・2/10/30 秒）で再試行され、DLQ へ入る。再試行は無駄で、DLQ を汚す。

## 決めたこと（要約。詳細は IADR-0540）

1. 本文の取得で **NoSuchKey だけ**を恒久的な欠落（`DocumentBodyMissingException`）へ写す。判定は SDK の型（`NoSuchKeyException`）か
   S3 のエラーコード `NoSuchKey`。**状態コード 404 だけでは判定しない**（`NoSuchBucket` も 404）。メッセージ文字列も見ない。
2. 受け口はそれを捕まえ、**Warning 1 行**（文書 ID・本文の所在＝キー・更新日時）と**計数 `ingestion.body_missing` を 1**、正常に終える（ack）。
   再試行も DLQ もない。完了イベント（`IngestionCompleted`）は発行しない。
3. **索引への扱いは未決（#1931 項目 3）。** 点は 1 つも書かない（題名だけのメタデータ点も書かない）。
   冒頭の全コレクション削除は従来どおりの位置のまま（足しも外しもしない）。→ 「既存の点を残す」は本 PR では実現しない。理由は IADR-0540 決定 4。
4. 時間切れ・5xx・接続の失敗・バケットが無いは従来どおり例外のまま上げる（再試行 → DLQ）。高機密の分岐・総枠のデッドレター・IADR-0536 の待ちの点の経路は変えない。

## 母集合（規則 9。オブジェクトストレージの読み出しの呼び出し元を全部挙げる）

`git grep -n "GetTextAsync\|GetBytesAsync" -- 'src/**/*.cs'`（試験を除く）を `origin/develop` `8b06c8b7` で走査した結果。

| 呼び出し元 | 読むもの | 本件で変えるか | 理由 |
| --- | --- | --- | --- |
| `IngestionService/Infrastructure/ExternalServices/StorageDocumentContentReader.cs` | 正規化本文（`DocumentUpdated`） | **変える** | 本件の対象 |
| `WikiService/Infrastructure/ExternalServices/StorageMarkdownReader.cs` | 正規化本文（`DocumentUpdated` の fan-out） | 変えない | 射程外。**同じ NoSuchKey で再試行 → DLQ になり得る**（報告に残す） |
| `GraphService/Infrastructure/ExternalServices/StorageContentReader.cs` | 正規化本文（同上） | 変えない | 同上 |
| `DocumentService/Features/Documents/Catalog/DocumentNormalizedConsumer.cs` | 正規化本文（変換完了の取り込み） | 変えない | 再索引の経路ではない |
| `DocumentService/Features/SyncConflicts/Get/Endpoint.cs`・`ObsidianSync/Pull/Endpoint.cs` | 本文（HTTP の応答） | 変えない | 要求・応答の経路（DLQ が無い） |
| `ConversionService/Infrastructure/ExternalServices/StorageObjectStore.cs`・`RawSourceResolver.cs` | 原本・正規化本文 | 変えない | 変換の経路 |
| `Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`・`ConversionBffEndpoints.cs` | 本文・図 | 変えない | 要求・応答の経路 |
| `platform/.../S3ObjectStorageClient.cs`（`GetTextAsync` / `GetBytesAsync` の実装） | — | 変えない | 共有の実装。分類は読み手（ingestion）の側で行う（他の呼び出し元の挙動を変えない） |

規則 10（この変更で新たに誤りになる自分の記述）: `DocumentUpdatedConsumer` の構築子へ `BodyMissingMetrics` を足したため、構築する箇所を
`git grep -n "new DocumentUpdatedConsumer(\|EmbeddingPendingMetrics>()"` で引き、5 箇所（試験 4・統合試験 1）と `Program.cs` を追随した。

## 受け入れ基準と試験の写像（`DocumentBodyMissingTests`・テスト仕様書 T-49〜T-51）

| 受け入れ基準 | 試験 |
| --- | --- |
| NoSuchKey（専用の型・汎用の型にエラーコード）を欠落へ写す | `NoSuchKeyは本文の欠落として写す`（2 例） |
| 404 `NoSuchBucket`・500・503・時間切れ・接続の失敗は写さない | `一時障害は本文の欠落へ写さずそのまま上げる`（5 例） |
| 本文があれば従来どおり | `本文があれば従来どおり返す`・`本文があれば従来どおり索引し計数は増えない` |
| ack（例外なし）・Warning 1 行（ID・キー・更新日時）・計数 1・点を書かない・完了を発行しない | `本文が無ければ記録だけして正常に終える`・`高機密の文書でも…` |
| 受け口でも一時障害は例外のまま | `一時障害は受け口でも例外のまま上げる` |
| 実配送で再試行なし・DLQ なし | `実配送で本文が無ければ一回の試行で終わりデッドレターへ行かない` |
| 実配送で 5xx は従来どおり再試行 | `実配送で5xxは従来どおり再試行される` |

## 変異試験（すべて赤になることを確かめた）

| 変異 | 赤になった試験 |
| --- | --- |
| M1 受け口の catch を外す | 記録だけして終える 2 件・実配送で DLQ に行かない |
| M2 判定を「状態コード 404」にする | `no-such-bucket-404`・記録だけ 2 件・実配送 |
| M3 判定を「S3 の例外すべて」にする | 500・503・404 NoSuchBucket・受け口の一時障害・実配送の 5xx 再試行 |
| M4 計数を増やさない | 記録だけして終える |
| M5 判定を型だけにする（エラーコードを見ない） | `error-code` の例 |

## 残るもの

- **#1931 項目 3（オーナーの裁定待ち）**: 本文の無い文書を題名だけで索引するか・アーカイブするか・削除するか。既存の点を残すか（削除を本文の取得の後へ動かすか）。
  題名だけで点を書く既存の経路（`IndexMetadataOnlyAsync`。ADR-0070 決定 4 のメタデータ点）はあるが、本件では使わない。
- wiki-service・graph-service の読み手も同じ NoSuchKey で再試行・DLQ になり得る（`DocumentUpdated` の fan-out）。項目 1 で切り替え後の分だけを流す限り当たらない。
- 作成日時は `DocumentUpdated` が運ばないため、Warning には更新日時を出す（契約を変えるのは射程外）。
