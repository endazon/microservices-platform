---
title: IADR-0540 取り込みは本文の NoSuchKey を恒久的な欠落として扱い、再試行・DLQ に回さず Warning と計数 ingestion.body_missing を残して ack する。索引への扱いは未決（#1931 項目 3）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-05, UC-04, NFR-06, ADR-0016, ADR-0027, ADR-0070, ADR-0106, IADR-0358, IADR-0536]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-02 取り込み / NFR-06 障害時の縮退運転）
related_specs:
  - ../specs/20261015_1931_ingestion-body-missing.md
---

# IADR-0540: 取り込みは本文の NoSuchKey を恒久的な欠落として扱う（#1931 項目 2）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（オーナーの依頼 #1931 項目 2。索引への扱いは項目 3 としてオーナーの裁定待ち）

## 起点・関連

- 起点 issue: #1931（PoC の再索引のカナリアで DLQ が 36 → 48。本文の `NoSuchKeyException` が 20 分で 43 回）。
- 関連する計画書 ID: FR-02・UC-04・NFR-06。FR-05 / ADR-0016 は冒頭の全コレクション削除を動かさない理由として引く。
- 関連する計画 ADR: ADR-0027（Wolverine の再試行・DLQ）・ADR-0106（SeaweedFS）・ADR-0070 決定 4（本文なしのメタデータ点）。
- 関連する実装 ADR: [IADR-0358](./IADR-0358_bodyless-document-metadata-point.md)（本文なし＝チャンク 0 件のメタデータ点）・
  [IADR-0536](./IADR-0536_embedding-pending-lexical-points.md)（埋め込み待ちの点。経路は変えない）。
- 関連する実装仕様書: `.ai-context/specs/20261015_1931_ingestion-body-missing.md`（母集合・変異試験）。
- 採番: `origin/develop` `8b06c8b7` の最大は IADR-0539。開いている PR に IADR を足すものは無い（push の直前に確かめた）。本件は **0540**。

## コンテキストと課題

- 9/26 の SeaweedFS 切り替えより前の文書（dry-run で 5,803 件）は、旧ストレージの中身を破棄したため本文のオブジェクトが無い。
- 取り込みは `NoSuchKeyException` を例外のまま上げ、共通既定（4 試行・2/10/30 秒）で再試行した後 DLQ へ送っていた。
  欠落は恒久的なので再試行は無駄で、DLQ を汚し、カナリアの「キューが 5 分以内に空になる」を破る。
- 決めること: (1) 何を恒久的な欠落とみなすか、(2) 取り込みは何をするか、(3) 観測、(4) 索引の既存の点と新しい点。

## 検討した選択肢

1. 共有の `S3ObjectStorageClient` で NoSuchKey を共通の例外へ写す —— 他の 7 つの呼び出し元（wiki・graph・document・BFF 等）の挙動が同時に変わる。期限のある修正の射程を越える。
2. **取り込みの読み手（`StorageDocumentContentReader`）で写し、受け口で記録して終える（採用）** —— 影響は取り込みだけ。
3. Wolverine の失敗規則で NoSuchKey を「破棄」にする —— 規則は DLQ・再試行の制御はできるが、Warning の中身（文書 ID・キー）と計数を受け口の文脈で残しにくい。
   SDK の型が受け口の外の規則へ漏れる。

## 決定

1. **恒久的な欠落とみなすのは NoSuchKey だけである。** 判定は SDK の型 `NoSuchKeyException` か、`AmazonS3Exception.ErrorCode == "NoSuchKey"`（S3 互換の実装が汎用の型で返しても拾う）。
   **状態コード 404 だけでは判定しない**（`NoSuchBucket` も 404 で、構成の不調＝再試行の対象）。**メッセージ文字列では判定しない。**
   読み手はこれをドメインの例外 `DocumentBodyMissingException`（`Domain/Ports`）へ写す。SDK の型は Infrastructure の外へ出さない。
2. **受け口は欠落を捕まえて正常に終える（ack）。** 再試行も DLQ もない。完了イベント（`IngestionCompleted`）は発行しない（完了していない。購読者も無い —— #1771）。
3. **観測**: Warning を 1 行（文書 ID・本文の所在 `storage://<bucket>/<key>`・更新日時。秘密情報は含まない）。計数 `ingestion.body_missing`（Meter はサービス名
   `microservices-platform.ingestion-service`。既存の `AddMeter` で収集される。タグは持たない —— 基数が無界になるため）。
   作成日時は `DocumentUpdated` が運ばないので更新日時で代える（契約を変えるのは射程外）。
4. **索引への扱いは未決（#1931 項目 3）である。本件は何も書かず、何も消し足さない。**
   - 題名だけのメタデータ点（`IndexMetadataOnlyAsync`。ADR-0070 決定 4・IADR-0358 の既存の経路）は**使わない**。
   - 冒頭の全コレクション削除（FR-05 / ADR-0016 の fail-closed）は**従来どおりの位置（本文の取得より前）に残す**。そのため、本文の無い文書の既存の点は
     この削除で消える —— **これは本件が足した削除ではなく従来の挙動である**。「既存の点を残す」には削除を本文の取得の後へ動かす必要があるが、
     そうすると機密区分が上がった文書で旧い区分の点が残る（ABAC の fail-open）。高機密の扱いを変えないために、本件では動かさない。
     点を残すか・題名だけで書くか・アーカイブするか・削除するかは項目 3 の裁定で決める。
5. 時間切れ・5xx・接続の失敗・バケットが無いは**従来どおり例外のまま上げる**（再試行 → DLQ）。高機密の分岐・総枠のデッドレター（`EmbeddingBudgetDeadLetterPolicy`）・
   IADR-0536 の待ちの点の経路は変えない。

## 理由

- 欠落は再試行しても現れない。DLQ は「人が見て再投入を判断するもの」であり、恒久的な欠落の写しを溜めると本物の一時障害が埋もれる。
- 判定を型とエラーコードに限るのは、404 の広い判定が `NoSuchBucket`（自己修復・再試行の対象）を飲み込み、文字列の判定が SDK や S3 互換実装の文言の違いで静かに外れるため。
- 索引への書き込み・削除を決めないのは、オーナーの裁定（項目 3）を先取りしないため。「記録だけ」は最小で、後から書き込みを足せる。

## 結果

- PoC: 本文の無い文書は 1 回の試行で終わり、DLQ は増えない。件数は `ingestion_body_missing_total`（Prometheus）とログの `body object missing (NoSuchKey)` で読める。
- 残余: wiki-service・graph-service の読み手も同じ NoSuchKey で再試行・DLQ になり得る（`DocumentUpdated` の fan-out。射程外）。
  項目 1（`--created-after`）で切り替え後の分だけを流す限り当たらない。
- 試験: `DocumentBodyMissingTests`（T-49〜T-51）。変異 5 種で赤になることを確かめた（仕様書）。

## 関連

- #1931（項目 1: 駆動スクリプトの `--created-after`・項目 3: 本文の無い文書の扱い）
