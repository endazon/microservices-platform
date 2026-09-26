---
title: 作業仕様書 — #1562 バケットの存在確認を HeadBucket へ替え、答えを「在る／無い／不明」の 3 値で扱う
type: spec
status: done
related_ids: [FR-06, FR-12, ADR-0014, ADR-0106, IADR-0303, IADR-0461]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0106_object-storage-seaweedfs.md
related_specs:
  - 20260829_issue-1033_object-storage-bucket-self-heal
  - 20260925_1499_object-storage-seaweedfs
issue: "1562"
---

# 作業仕様書 — #1562 バケットの存在確認が SeaweedFS で起動のたびに 503 になる

## 起点

- issue #1562。SeaweedFS 4.47 へ切り替えた稼働クラスタで、ConversionService の起動のたびに
  `Object storage bucket bootstrap failed; first write will create the bucket and retry (#1033)` が 1 回出る。
  スタックは `AmazonS3Util.DoesS3BucketExistV2Async`（`S3ObjectStorageClient.cs` 177 行）で `ServiceUnavailable`。
  バケットは在り、書き込み・読み出しは通っている。SeaweedFS が起動済みのまま ConversionService だけを再起動しても再現する。

## 編集前に確かめた事実

- `AmazonS3Util.DoesS3BucketExistV2Async` は内部で GetBucketAcl を撃つ（issue の観測。IADR-0461 決定 2 の
  「使う S3 機能」も `GetBucketAcl〔DoesS3BucketExistV2Async〕` と書いている）。
- `AWSSDK.S3` は `src/Directory.Packages.props` で `4.0.100.2`。`IAmazonS3` は `HeadBucketAsync(HeadBucketRequest, CancellationToken)`
  と `GetBucketLocationAsync` を持つ（`~/.nuget/packages/awssdk.s3/4.0.100.2/lib/net8.0/AWSSDK.S3.xml` の `M:Amazon.S3.IAmazonS3.*` で確認）。
  **HeadBucket を採る** —— 存在だけを問う専用の操作で、GetBucketLocation のように領域（ACL 同様の副次機能）の実装差に依存しない。
- 受け入れ試験 `ObjectStorageRoundTripTests`（Integration。SeaweedFS の実イメージを Testcontainers で起こす）は、
  各試験が**新しいコンテナ**に対して `EnsureBucketAsync` を 1 回呼ぶだけである。つまり**バケットが在る状態での
  存在確認を一度も踏んでいなかった**（無いときの GetBucketAcl は NoSuchBucket を返し、false として通っていた）。
  これが CI で見えなかった理由である。

## 変更

1. `S3ObjectStorageClient.EnsureBucketAsync` の存在確認を `HeadBucketAsync` へ替え、結果を 3 値にする。
   - 200 → 在る → 版管理を有効化（従来どおり）。
   - 404（状態コード）または ErrorCode `NoSuchBucket` / `NotFound` → 無い → 作成（従来どおり）。
   - それ以外（503・403・接続不能 等）→ **不明** → 警告を 1 行出し、作成も版の設定もせずに戻る。
     **不明は無いではない（原則 A）。** 無かった場合の回収は既存の書き込み時の自己修復（#1033・IADR-0303）が担う。
   - 取り消し（`ct` による `OperationCanceledException`）は握らずに投げる。
   - `AmazonS3Util` の using を外す。書き込み時の自己修復が `EnsureBucketAsync` を呼ばない理由のコメントのうち、
     「静的で差し替えられない」は事実でなくなるので日付つきで改める。
2. 単体試験 `S3ObjectStorageClientEnsureBucketTests`（`AmazonS3Client` 派生の偽物。既存試験と同じ作法・I/O なし）:
   200 / 404（NotFound）/ NoSuchBucket / 503 / 403 / 接続不能 / 取り消し / 版管理無効 を見る。
   旧実装へ戻すと 503 の試験が落ちる（`DoesS3BucketExistV2Async` は偽物の HeadBucket を呼ばず、実 I/O へ出る）。
3. 受け入れ試験（Integration）へ 1 件足す: **同じバケットへ `EnsureBucketAsync` を 2 回呼び、2 回目が「在る」の経路を通って
   警告を出さない**こと。これが #1562 を CI で捕まえる形である（日次 `integration.yml` と外部供給の口で走る。PR の `ci.yml` は本クラスを外す）。
4. Runbook（`docs/operations/object-storage-seaweedfs-cutover-runbook.md`）の確認項目「バケットの作成失敗が繰り返し出ていない」を、
   新しい挙動（起動時に bootstrap の失敗警告も存在不明の警告も出ない）に合わせて書き直す。
5. IADR-0461 に日付つきの決定 11 を足す（新しい IADR 番号は取らない）。IADR-0303 に 1 行の日付つき追記（「静的で差し替えられない」の失効）。

## 母集合（規則 9・10）

- 誤りの側の文字列で全追跡ファイルを走査した: `grep -rn "DoesS3BucketExist\|GetACL\|GetBucketAcl"`（`/obj/` `/bin/` `node_modules` `.git` を除く）。
  結果 7 行 / 5 ファイル:
  - `S3ObjectStorageClient.cs` 177・199 行 → 直す（変更 1）。
  - `IADR-0461` 97 行 → 決定 11 の追記で扱う（本文は書き換えない）。
  - `IADR-0303` 33・70 行 → 70 行の主張（静的で差し替えられない）へ日付つき追記。33 行は実測の記録であり直さない。
  - `.ai-context/specs/20260829_issue-1033_…` 38・110 行、`.ai-context/specs/20260925_1499_…` 66 行 → **確定済み仕様書は書き換えない**（除外）。
- Runbook の「繰り返し」: `grep -n "繰り返\|bootstrap failed"` を `docs/` 全体に掛け、対象は上記 Runbook 133 行の 1 箇所。
- 自分の変更で新たに誤りになる記述（規則 10）: `ObjectStorageBootstrapHostedServiceTests.cs` 18 行
  「EnsureBucketAsync の成功経路・例外握り潰し経路は実ストアが要るためスコープ外」→ 存在確認の分岐は新しい単体試験が覆うので、行き先を書き足す。

## 受け入れ基準

- [x] `dotnet build` / `dotnet test` （platform の `Platform.Shared.Infrastructure.Tests`）が通る。
- [x] 新しい単体試験がすべて通り、旧実装では 503 の試験が落ちる。
- [x] `dotnet format --verify-no-changes`（platform・knowledge）が通る。
- [ ] 受け入れ試験の追加分は日次 Integration で確かめる（本 PR では稼働クラスタに触れない）。
