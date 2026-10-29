---
title: 作業仕様書 — 同期競合でローカル版の本文の参照が無いまま local / both を確定しない（409 local_content_missing。SC-20 は択を押せなくする）（#2307）
type: spec
status: done
related_ids: [FR-20, UC-11, SC-20, ADR-0037, IADR-0352]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2307"
---

# 作業仕様書 — 参照の無いローカル版で確定しない（#2307）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7c71742d9`）。

## 現状（develop で確かめた）

- `Resolve/Endpoint.cs` は、ローカル版の本文を `GetSyncConflictEndpoint.ReadAsync(storage, conflict.LocalContentUri, …)` で読む。
- `LocalContentUri` が空なら、`ReadAsync` は空文字と `Unavailable=false` を返す（取り出しに失敗したのではないため）。
- この状態で選ぶと、次のことが起きる。
  - `local`: **空の本文を新しい版として書く。**
  - `both`: 空の別名資料を作る。
- #2305（#2141）で塞いだのは、取り出しの失敗（503 `local_content_unavailable`）だけである。
- 空になるのは次の 2 通りである。
  - 記録時に退避できなかった場合: ストレージが縮退して URI を返さない。
  - 古い行。

## 方針

- **後段**: `local` / `both` で `LocalContentUri` が空なら、本文を読まず、何も確定せずに **409 `{ error: "local_content_missing" }`** を返す。
  - 503 にしないのは、再試行しても直らない恒久の状態だからである。`local_content_unavailable`（503）は一時的な障害を表し、選び直しや再試行で直り得る。
  - 競合は未解決のまま残り、`server` は選べる。
- **詳細**: `SyncConflictDetailDto` に `localContentMissing`（bool。既定 false）を足す。
  - 取り出しの失敗（`localContentUnavailable`）とは別の印にする。画面の文言が違うためである。前者は「ストレージの障害」、こちらは「端末の本文が記録されていない」。
- **SC-20**: `localContentMissing` のとき、ローカル版の欄に「端末の本文が記録されていません。」と出し、「ローカルを採用」と「両方を残す」を押せなくする。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "LocalContentUri"` で数えた。読む箇所は詳細（Get）と解決（Resolve）の 2 つで、両方を直した。
  - 消す箇所（Resolve の確定後・`SyncConflictRecorder.CloseAsync`）は、既に空のときを飛ばしている。
- **規則 10**: 次の 2 つを、この変更に合わせて書いた。
  - openapi の resolve の 409 の説明と、詳細のスキーマ
  - テスト仕様書 SC-20

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 参照が空の競合で `local` / `both` を選ぶと確定せず、本文が変わらない | `SyncConflictEndpointTests` の新しい Theory（409・未解決のまま・本文と版が不変・別名資料が増えない） | 赤（200 で空の本文を書く） |
| `server` は従来どおり解決できる（陽性対照） | 同ファイルの新しい Fact | 緑 |
| 詳細が `localContentMissing` を返す | 同ファイル | 赤 |
| SC-20 は択を押せなくする | `ObsidianSettingsPage.test.tsx` | 赤 |
