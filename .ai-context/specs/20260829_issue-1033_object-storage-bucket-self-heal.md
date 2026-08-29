---
title: 作業仕様書 — バケット未作成で書き込みが 500 になる競合を塞ぐ（#1033）
type: spec
status: done
related_ids:
  - FR-06
  - FR-12
  - FR-21
  - NFR-05
  - NFR-21
  - ADR-0014
  - ADR-0015
author: implementation-agent
created: 2026-08-29
updated: 2026-08-29
---

# 作業仕様書 — バケット未作成で書き込みが 500 になる競合を塞ぐ（#1033）

## 1. 事実（実測）

`develop` `3939e72` の **Integration Stack** run 33230268422 が段 12（検索 seed）で落ちた。

```
[seed-search-documents] POST http://localhost:18092/documents が失敗しました（500）
```

document-service のログ:

```
fail: Microsoft.AspNetCore.Server.Kestrel[13]
      Amazon.S3.AmazonS3Exception: The specified bucket does not exist
```

conversion-service のログ（同 run）:

```
   at Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(...)
   at S3ObjectStorageClient.EnsureBucketAsync(...) S3ObjectStorageClient.cs:line 156
   at ObjectStorageBootstrapHostedService.StartAsync(...) ObjectStorageBootstrapHostedService.cs:line 26
```

**スタックが `StartAsync` で止まっている＝ `catch` が握り潰した。** バケットは作られなかった。

### 競合であることの実測（同じコード・逆の結果）

| run | conversion-service の bootstrap | 段 12 |
| --- | --- | --- |
| 33200749231（`5e3b1e0`） | ✅ `Created object storage bucket knowledge-normalized` | ✅ 成功 |
| 33230268422（`3939e72`） | ❌ 例外 → 握り潰し | ❌ **500** |

**ConversionService の起動が MinIO の readiness と競合している。** 勝てば緑、負ければ
そのスタックは以後ずっと赤い。#1033 が「間欠的で原因不明」に見えていた理由である。

## 2. 🔴 fail-open の根拠が、この経路では成立していない

```csharp
catch (Exception ex)
{
    // 起動を止めない。MinIO 起動待ち等の一時失敗は保存時に再試行される（MassTransit リトライ）。
    logger.LogWarning(ex, "Object storage bucket bootstrap failed; will rely on retry on first write");
}
```

**DocumentService の `POST /documents` は同期 HTTP の書き込みであり、MassTransit の
メッセージではない。この経路に再試行は無い。** 「統制を定めた（後で再試行される）」と
「統制が働いている（この経路には再試行が無い）」の食い違いである。

## 3. 母集合（自分で引いた。除外理由つき）

### 軸 1 —— 書き込み面の呼び出し元（`PutTextAsync` / `PutBytesAsync` の全走査）

本番コードは **4 サービス 6 箇所**:

| サービス | 箇所 | バケットを作るか |
| --- | --- | --- |
| ConversionService | `StorageObjectStore.cs:13,17` | ✅ 作る（唯一） |
| DataSourceService | `DataSourceSyncService.cs:110` | ❌ 作らない |
| DocumentService | `DocumentEndpoints.cs:119,301` / `ObsidianSyncEndpoints.cs:109,257` | ❌ 作らない |

→ **バケットを作らないサービスが 2 つあり、そのどちらも書き込む。**

### 軸 2 —— `AddPlatformObjectStorageBootstrap`（作る側）の全走査

🔴 **`git grep -ln`（ファイル一覧）では 2 サービスに見える。しかし行で見ると 1 つは注釈である。**

```
ConversionService/Worker/Program.cs:45: builder.Services.AddPlatformObjectStorageBootstrap();   ← 実行文
DocumentService/Program.cs:64:          // …ConversionService が担っており（`AddPlatformObjectStorageBootstrap`）… ← 注釈
```

**実際に作るのは ConversionService だけである。** DocumentService は「同じバケットを
2 か所から作りにいく理由が無い」と**意図的に**書いていない。
（母集合規則: `grep -l` を母集合にしない。行で見る。）

その判断は**定常状態では正しい**が、**唯一の作成者が競合に負けたときの回復手段が無い**。

### 軸 3 —— `IObjectStorageClient` の実装（追随が要るか）

`S3ObjectStorageClient`（本件の対象）/ `NullObjectStorageClient`（S3 を触らない。対象外）/
テストダブル（実 I/O 無し。対象外）。**変更は 1 実装に閉じる。**

## 4. 直し方

### (a) 書き込みの自己修復（`S3ObjectStorageClient`）

`PutObjectAsync` が `NoSuchBucket` で落ちたら、**バケットを作って 1 度だけ再試行する**。

🔴 **`EnsureBucketAsync` は呼ばない。** 同メソッドは存在確認に
`AmazonS3Util.DoesS3BucketExistV2Async`（**静的メソッド**）を使っており、テストで差し替えられない。
作成部分を私有メソッドへ切り出し、**自己修復はそちらだけを呼ぶ**（存在しないことは
例外が既に教えている。改めて問い合わせる必要が無い）。

**なぜクライアント側か**: 軸 1 のとおり書き込み元は 4 サービスに散っている。
**1 箇所直せば全経路が守られる**。起動順序にも依存しない。
（代案「bootstrap を全サービスへ登録」は fail-open のままなので競合を解かない。）

### (b) 偽の根拠を直す（`ObjectStorageBootstrapHostedService`）

「MassTransit リトライで再試行される」を、**実際に何が保証されるか**へ書き換える。

## 5. 検出力の証拠

🔴 **この環境に Docker が無く、実 MinIO も k3d も起こせない。** そこで**実 I/O の要らない層**で測る。

`AmazonS3Client` を派生した偽物で `PutObjectAsync` / `PutBucketAsync` /
`PutBucketVersioningAsync` を差し替える（既存 `S3ObjectStorageClientDeleteTests` と同じ作法。
**3 つとも `public virtual` であることを SDK 4.0.100.2 の反射で実測済み**）。

変異は最低 3 種: ①自己修復を消す ②再試行せず作るだけ ③`NoSuchBucket` 以外も飲み込む。

## 6. 触るファイル

- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Composable/Adapters/Storage/S3ObjectStorageClient.cs`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Composable/Adapters/Storage/ObjectStorageBootstrapHostedService.cs`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Composable/Adapters/Storage/`（新規テスト）
- `.ai-context/adr/IADR-0303_*.md`（新規）
- `docs/how-to/session-handoff.md`（型 3 の新しい実例）

## ［2026-08-29 追記 / #1033］AI レビューの指摘 2 件に対応した

1. **並行作成のレース**（🟡）—— 指摘どおり**実在する欠陥**だった。自己修復はリクエストごとに
   走るため、未作成の窓へ同時到達した書き込みが並行して作成を撃ち、**負けた側は
   `BucketAlreadyOwnedByYou` で失敗**する（SDK に専用例外型が実在することを実測）。
   レビューは「以前より悪化はしない」としたが、**競合の窓はまさに本件が起きる場面**であり、
   いちばん必要なときに失敗する。既存エラーを吸収する形へ直し、変異 M5 / M6 を足した。
2. **無採番 `NFR`**（🟡）—— 計画の非機能要件表を実際に見て **NFR-05（可用性）**・
   **NFR-21（障害検出〜復旧）** へ採番し直した。当たる番号があった。
