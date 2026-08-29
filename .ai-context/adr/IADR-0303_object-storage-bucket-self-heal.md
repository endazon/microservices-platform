---
title: IADR-0303 オブジェクトストレージのバケットを書き込み側で自己修復する
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-12, FR-21, NFR, ADR-0014, ADR-0015, IADR-0008]
author: Claude
created: 2026-08-29
updated: 2026-08-29
related_specs:
  - ../specs/20260829_issue-1033_object-storage-bucket-self-heal.md
---

# IADR-0303: オブジェクトストレージのバケットを書き込み側で自己修復する

## 文脈

バケット `knowledge-normalized` を作るのは **ConversionService の起動時 bootstrap
（`ObjectStorageBootstrapHostedService`）だけ**である。DocumentService は
「同じバケットを 2 か所から作りにいく理由が無い」として**意図的に登録していない**。

その bootstrap は **fail-open** である —— MinIO の起動待ちで例外が出ても警告を出して起動を続ける。
根拠として「保存時に再試行される（MassTransit リトライ）」と書かれていた。

🔴 **その根拠は成立していなかった。** 書き込み元は 4 サービス 6 箇所に散っており、
**DocumentService の `POST /documents` と DataSourceService の同期経路は
メッセージではなく同期 HTTP** である。**この経路に再試行は無い。**

### 実測（同じコード・逆の結果）

| run | conversion-service の bootstrap | 検索 seed |
| --- | --- | --- |
| 33200749231（`5e3b1e0`） | `Created object storage bucket knowledge-normalized` | 成功 |
| 33230268422（`3939e72`） | `DoesS3BucketExistV2Async` が例外 → 握り潰し | **500**（`The specified bucket does not exist`） |

**起動順序に依存する競合である。** 負けるとバケットは作られず、**そのスタックは以後ずっと
書き込みが 500 になる**。#1033 が「間欠的で原因不明」に見えていた理由がこれである。

## 決定

### 決定 1: 書き込みが `NoSuchBucket` を捕まえたら、バケットを作って 1 度だけ再試行する

`S3ObjectStorageClient` の `PutTextAsync` / `PutBytesAsync` を自己修復でくるむ。

**なぜクライアント側か** —— 書き込み元は 4 サービスに散っており、うち 2 サービス
（DocumentService / DataSourceService）はバケットを作らない。**クライアントに 1 箇所置けば
全経路が守られ、起動順序にも依存しない。**

**棄却案**: *bootstrap を全サービスへ登録する* —— fail-open のままなので競合を解かない
（全サービスが同時に負け得る）。*bootstrap を fail-closed にする* —— MinIO の起動が遅いだけで
全サービスが CrashLoop に入る。**回復の速さと引き換えに、正常な起動順序の揺らぎを事故に変える。**

### 決定 2: 捕まえるのは `NoSuchBucket` だけにする

権限不足（`AccessDenied`）・接続不能まで飲み込むと、**「バケットを作れば直る」わけではない失敗を
握り潰して原因を隠す**ことになる。射程をエラーコードで固定し、テストで固定する。

### 決定 3: 再試行は 1 度だけ

作ってもなお `NoSuchBucket` なら、それは別の問題である（名前の不一致・権限・別リージョン等）。
**無限に粘らず投げる。**

### 決定 4: 自己修復で作ったバケットにもバージョニングを有効化する

ADR-0014 / [[IADR-0008]] は版の保持を要求し、**完全削除（全版削除。ADR-0057 / [[IADR-0296]]）は
バージョニングが有効であることを前提にしている**。作成経路が 2 つに増えた以上、
**両方で有効化しなければ「版が残らないバケット」が静かに生まれる**。

### 決定 5: `EnsureBucketAsync` は自己修復から呼ばない

同メソッドの存在確認は静的な `AmazonS3Util.DoesS3BucketExistV2Async` であり、
**テストで差し替えられない**（＝検出力のある検査が書けない）。
**存在しないことは例外が既に教えている**ので、作成部分だけを私有メソッドへ切り出して呼ぶ。

### 決定 6: bootstrap の fail-open は維持する。ただし根拠を書き換える

fail-open のままでよいのは**決定 1 の自己修復があるから**である。
**自己修復を外すなら bootstrap も fail-closed へ変えなければならない**旨をコードコメントに残す。

## 検出力の証拠（変異試験・無変異ベースライン対照つき）

| # | 変異 | 結果 |
| --- | --- | --- |
| M0 | 無変異（対照） | **Passed 5 / Failed 0** |
| M1 | 自己修復を消す | **KILL**（Failed 3） |
| M2 | 作るだけで再試行しない | **KILL**（Failed 2） |
| M3 | `NoSuchBucket` 以外も飲み込む | **KILL**（Failed 1） |
| M4 | 自己修復経路でバージョニングを有効化しない | **KILL**（Failed 1） |

器は `AmazonS3Client` の派生（既存 `S3ObjectStorageClientDeleteTests` と同じ作法）。
`PutObjectAsync` / `PutBucketAsync` / `PutBucketVersioningAsync` が `public virtual` であることを
**SDK 4.0.100.2 の反射で実測**してから設計した。base を呼ばないためネットワーク I/O は無い。

## 🔴 実測できないこと

**本作業環境に Docker が無く、実 MinIO も k3d スタックも起こせない。**
**「実際に競合へ負けた配備で書き込みが通るようになる」ことは測っていない** ——
定めただけである。証拠は `develop` の Integration Stack でしか得られない。

## 影響

- 正常時の挙動は変わらない（例外が出ないので自己修復経路へ入らない。M0 が固定）。
- バケット作成の権限が無い配備では、従来どおり `AccessDenied` がそのまま上がる（決定 2）。
