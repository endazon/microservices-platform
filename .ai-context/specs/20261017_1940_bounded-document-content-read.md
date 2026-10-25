---
title: 文書の本文の口（DocumentRead.GetDocumentContent）を上限つきの読み取りにし、上限超過は全文を読む前に 413 にする（#1940）
type: spec
status: done
related_ids: [FR-06, UC-03, SC-03, NFR-16, ADR-0014, ADR-0106, IADR-0544, IADR-0549, IADR-0540]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md
issue: "#1940"
---

# 仕様書: 文書の本文の口を上限つきの読み取りにする（#1940）

## 起点となる計画書（トレーサビリティ）

- FR-06（文書管理）・UC-03・SC-03（文書詳細）・NFR-16。
- ADR-0014（本文はオブジェクトストレージ）・ADR-0106（SeaweedFS）。
- 親: #1905 / #1939 の残余（[IADR-0544](../adr/IADR-0544_document-content-via-document-service-grpc.md) 決定 4「測るのは読み込んだ後である」・§残余）。
- 実装判断の記録: **新しい IADR は起こさない。** IADR-0544 決定 4 と §残余へ日付つき追記で残す（決定は「上限 16 MiB・切り詰めない」のまま、
  測る時点だけが変わる。新しい決定を立てるほどの分岐が無い）。

## 背景（コードで確認）

- `DocumentContentUseCase.ReadAsync` は `IObjectStorageClient.GetTextAsync`（`S3ObjectStorageClient` は `StreamReader.ReadToEndAsync`）で
  **全文を文字列にしてから** `Encoding.UTF8.GetByteCount` で 16 MiB と比べる。数百 MB の本文はヒープ（document-service の `limits.memory`
  512Mi・GC ヒープ上限は約 60%）を溢れさせ得る。
- 共有のポートには大きさを問い合わせる口が無い（IADR-0544 §残余）。`ExistsAsync`（IADR-0549）は既定の実装が「対応していない」を投げる
  既定インターフェイスメソッドの前例である。

## 決めたこと

1. 共有のポート `IObjectStorageClient` に **`GetTextBoundedAsync(uri, maxBytes, ct)`** を足す。戻り値は `BoundedTextRead`
   （`Text`・`SizeBytes`・`Exceeded`）。既定の実装は `ExistsAsync` と同じく `NotSupportedException`（試験の偽物 18 個を書き換えない）。
2. `S3ObjectStorageClient` の実装:
   - `GetObject` の応答の `ContentLength` が上限を超えれば、**本文を 1 バイトも読まずに**応答を破棄し、`Exceeded`（`SizeBytes` = `ContentLength`）を返す。
   - `ContentLength` が無い・偽っている場合に備え、読み取りは **上限＋1 バイトで打ち切る**。超えたら `Exceeded`（`SizeBytes` = 上限＋1。
     **下限である**）。
   - 上限以内なら読んだバイト列を UTF-8 で復号する（BOM は従来の `StreamReader` と同じく落とす）。
   - 測るのは**格納されたバイト数**である（従来は復号後の文字列を UTF-8 へ戻した数。正しい UTF-8 では一致する。不正なバイト列のときだけ
     置換文字の分だけ違う —— 格納物の大きさで判定する方が「読む量」の上限として正しい）。
3. `NullObjectStorageClient` は `GetTextAsync` と同じく `NotSupportedException`（呼び出し側は `CanResolve` で先に止めている）。
4. `DocumentContentUseCase` は `GetTextBoundedAsync(uri, MaxBodyBytes)` を呼ぶ。`Exceeded` → `TooLarge(SizeBytes, MaxBodyBytes)`。NoSuchKey の判定・
   未構成の判定は変えない。proto の `size_bytes` の注記を「大きさ（分からないときは下限＝上限＋1）」へ改める（ワイヤ形は不変）。
5. 取り込み（ingestion）・wiki・graph の読み手は**変えない**（下の母集合）。

## 母集合（規則 9: `GetTextAsync` の呼び出し元を走査した。`grep -rn "GetTextAsync" --include=*.cs src`）

| 呼び出し元 | 扱い | 理由 |
| --- | --- | --- |
| DocumentService `DocumentContentUseCase` | **変える** | 本件。上限と 413 の契約を持つ唯一の口 |
| IngestionService `StorageDocumentContentReader` | 変えない | 上限の契約が無い（非同期の取り込み。超過時の扱い〔デッドレター・打ち切り〕は新しい決定が要る）。入口の変換・本文の直接投入（1 MB）側で大きさが決まる |
| WikiService `StorageMarkdownReader` | 変えない | 同上 |
| GraphService `StorageContentReader` | 変えない | 同上 |
| DocumentService `DocumentNormalizedConsumer` / `SyncConflicts/Get` / `ObsidianSync/Pull` | 変えない | 上限の契約が無い。射程外（必要なら別 issue） |
| ConversionService `StorageObjectStore` | 変えない | 同上 |

非同期の読み手に上限を入れるかは、超過時の扱いの決定が要るため本件では起こさない（残余として IADR-0544 追記に書く）。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC-1 | 上限を超える本文は、全文を読まずに 413 になる（メモリの割り当てを確かめる） | `S3ObjectStorageClientBoundedReadTests`: `ContentLength` が上限超なら本文ストリームから 0 バイトしか読まず、割り当ても本文の大きさに比例しない（`GC.GetAllocatedBytesForCurrentThread`）／`ContentLength` が無い（0 や偽り）でも読むのは上限＋1 バイトまで。`GrpcDocumentContentTests` の既存 TOO_LARGE 試験（BFF の 413 は `BffDocumentContentTests` が固定済み） |
| AC-2 | 上限以下は従来どおり 200 | 同 Bounded: ちょうど上限は本文を返す・BOM を落とす。既存 `GrpcDocumentContentTests`（ちょうど上限は AVAILABLE）・`BffDocumentContentTests` |
| — | 未構成は従来どおり投げる | `NullObjectStorageClientTests` |

## 変異（赤になることを確かめる）

1. `ContentLength` の事前判定を外す → AC-1（読まない）が赤。
2. 打ち切りを `maxBytes + 1` → 無制限に → 偽りの `ContentLength` の試験が赤。
3. 判定を `>` → `>=` → ちょうど上限の試験が赤。

## 検証

- `dotnet test` Platform.Shared.Infrastructure.Tests（Storage）・DocumentService.Tests（GetContent）。
- `dotnet format --verify-no-changes`（変更プロジェクト）。
- `check-proto-contracts`・`check-trace-blocks`・`gen-knowledge-graph --check`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。

## PoC での確認手順

1. document-service を配備する（BFF は変更なし。proto はワイヤ形が不変）。
2. 通常の文書: `curl -s -o /dev/null -w '%{http_code}\n' -b '<Cookie>' https://<host>/bff/documents/<id>/content` → `200`。
3. 16 MiB 超の本文がある場合: 同じ口が `413`（`sizeBytes` はオブジェクトの大きさ）。document-service の WARN 1 行
   `Document body of … over the limit …` が出て、Pod のメモリが本文の大きさだけ跳ねない（`kubectl top pod`）。
