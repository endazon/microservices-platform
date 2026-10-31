---
title: 作業仕様書 — 同期の一時停止の状態と、停止中の 5 つの口のサーバの拒否・同期履歴の 1 行（#2378。#2127 の子 A。計画 ADR-0145 決定 1〜4）
type: spec
status: done
related_ids: [FR-20, UC-11, SC-20, ADR-0145, ADR-0037, ADR-0099, ADR-0114, IADR-0270, IADR-0444, IADR-0446, IADR-0474, IADR-0667]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#2378"
---

# 作業仕様書 — 同期の一時停止（サーバの側）（#2378）

> 本仕様書は実装着手前に作成した（着手 2026-10-31。develop `49dd98441`）。親は #2127（SC-20 主要素 7）。
> 計画 ADR-0145（Accepted。planning#856 の裁定）が振る舞いを定めた。画面（決定 5）は子 B（#2379）で、本 PR の後に置く
> （ADR-0145 フォローアップ 2）。

## 計画の要点（ADR-0145）

- 決定 1: 一時停止は**利用者単位**。トークン・端末・有効期限は変えない。失効・同期停止（ADR-0037 決定 4）とは別の操作。
- 決定 2: **サーバが拒む**。同期トークンで受ける 5 つの口（manifest・push・pull・delete・move）すべて。応答は **401 を使わず**、**機械可読な理由コード**を含む。
  状態コード・理由コード・状態の持ち方は実装が決めて IADR に書く。
- 決定 3: 停止中の拒否は同期履歴に**失敗（理由「停止中」）**として記録し、**同じ停止期間の拒否は 1 行**にまとめる。行は拒否回数と最初・最後の
  拒否の時刻を持つ。方向・内訳は空。
- 決定 4: 停止中に SC-19 で起きた変更は、再開後の通常の同期で追いつく。競合にしない。

## 現状（実測。develop `49dd98441`）

- `SyncSettings`（`OwnerId`・`TargetFolders`・`AiInputDefault`・`UpdatedAt`）に停止の状態は無い。
- 5 つの口は `ObsidianSyncEndpoints` の route group `/private-notes/sync` にあり、各端点の先頭で `ResolveDeviceAsync` を呼んで null なら 401。
- 同期履歴は `SyncAuditEntry`（1 操作 1 行）。`SyncAuditRecorder.FailureAsync` が失敗を記録する。manifest は記録しない。
- 試験は InMemory の DB。マイグレーションは EF のスナップショットと一致することを `HasPendingModelChanges` で確かめる試験がある。

## 母集合（規則 9）

`git grep -n -E "SyncSettingsDto|SyncHistoryEntryDto|SyncFailureReasons|ResolveDeviceAsync|sync-settings"` を全体へ掛けて引いた。

| 対象 | 扱い |
| --- | --- |
| `Domain/SyncSettings.cs`・`Domain/SyncAuditEntry.cs`・`Infrastructure/Persistence/DocumentDbContext.cs`・マイグレーション 1 本 | 状態と、停止期間ごとの 1 行の列を足す |
| `Features/ObsidianSync/ObsidianSyncEndpoints.cs`（＋新規 `SyncPauseGate.cs`） | **route group の端点フィルタ**で 5 つの口を一度に覆う |
| `Features/SyncSettings/**`（＋新規 `Pause/`・`Resume/`） | 一時停止・再開の口。設定の応答に状態を載せる |
| `Knowledge.Contracts/Dtos/PrivateNoteDto.cs`・`SyncHistoryDto.cs` | 追加だけ（既定値つきの末尾の引数） |
| `Knowledge.Bff.Endpoints/PrivateNoteBffEndpoints.cs` | 中継 2 本 |
| `docs/api/openapi.yaml`・生成物・`scripts/contract-schema-baseline.json` | 追随 |
| `docs/api/FR-20_obsidian-sync.md`・`docs/api/BFF_bff-surface.md`・`docs/functional/FR-20_obsidian-sync.md` | 追随 |
| `.claude/rules/traceability.repo.md` と別紙 | 計画 ADR のレンジを実測（`0146`）まで進める |

除外:
- `src/knowledge/frontend/**`・`docs/screens/SC-20*`・`docs/tests/SC-20*`（子 B）。
- Obsidian プラグイン（`src/obsidian-plugin`）。ADR-0145 決定 2 は「プラグインが状態を見て送らない作りは妨げない」で、統制はサーバが担う。プラグインの表示は子 B で扱う。
- `.ai-context/specs/` の過去の記録（凍結）。

## 方針（IADR-0667）

1. **状態の持ち方**: `SyncSettings` に `PausedAt`（null＝有効）と `PausePeriodId`（停止期間の識別子）を足す。一時停止で新しい期間を始める（停止中の一時停止は冪等で、期間を変えない）。再開で両方を消す。行が無ければ作る。
2. **門は route group の端点フィルタ**（`SyncPauseGate`）。5 つの口とこれから足す口を、端点が書き忘れても覆う。
   - フィルタは `ResolveDeviceAsync` で端末を解く。解けなければ（401 の系列）何もせず、端点にそのまま 401 を返させる。トークン・アカウントの拒否を 423 で上書きしない。
   - 解いた端末は `HttpContext.Items` に置き、端点の `ResolveDeviceAsync` はそれを使う（同じ要求で名簿を 2 度引かない）。
   - 所有者が停止中なら **423 Locked**、本文 `{"error":"sync_paused","pausedAt":"…"}`（既存の 409 の `error` と同じ鍵）で返す。409（競合）と 401（トークン）と取り違えない。
3. **理由コード**は `SyncFailureReasons.Paused = "sync_paused"`。応答の `error` と同期履歴の失敗理由で、同じ値を使う。
4. **同期履歴の 1 行**: `SyncAuditEntry` に `PausePeriodId`（null 可）・`RejectedCount`・`LastRejectedAt` を足す。
   - 停止期間の最初の拒否で行を作る。`OccurredAt` は最初の拒否の時刻で、方向は空・端末は空（停止期間の拒否は複数の端末から来る）・内訳は 0。
   - 以降の拒否は、回数と最後の時刻を更新する。
   - 一意索引は `(OwnerId, PausePeriodId)`（null を除く）。同時の作成と更新は、楽観ロック（`RejectedCount` を同時実行トークンにする）と再試行で 1 行に保つ。
   - 構造化ログ（`private-note.sync.paused`、denied）は拒否ごとに出す。
5. **応答の DTO**（追加だけ）:
   - `SyncSettingsDto` に `SyncPaused`・`SyncPausedAt` を足す。
   - `SyncHistoryEntryDto` に `RejectedCount`・`FirstRejectedAt`・`LastRejectedAt` を足す。いずれも null 可で、停止期間の行だけが持つ。
6. **口**: `POST /private-notes/sync-settings/pause`・`/resume`（JWT・本人。応答は `SyncSettingsDto`）。BFF は `POST /bff/private-notes/sync-settings/pause|resume`（`ForwardIfWritableAsync`）。

［2026-10-31 追記 / AI レビュー 🟡2・🟡3］同時の一時停止を 1 つの停止期間に収めるため、`PausePeriodId` を同時実行トークンにした。
主キー・トークンの衝突は読み直して当て直し、既に停止中なら冪等の 200 とする（フォルダの更新も同じく読み直す）。マイグレーションは列の変化が無いので、
同じ 1 本のまま作り直した。試験は `SyncPauseConcurrencyTests`（インターセプタで衝突を必ず起こす 3 件）。`SyncPausedResponse` は internal にした（🟢）。

## 受け入れ基準

- [x] 停止中は 5 つの口すべてが 423 と `error: sync_paused` を返す。別の端末のトークンでも同じ（否定形）。応答は 401 ではない
- [x] 再開すると、同じトークンのまま 5 つの口が通る（陽性対照）
- [x] 同じ停止期間の拒否は 1 行（回数・最初と最後の時刻）にまとまる。再開の後の再停止は別の行になる
- [x] 無効なトークン・アカウントの無効化は、停止中でも 401 のまま（停止中の拒否と見分けられる）
- [x] 停止中に SC-19 で削除した資料は、再開後の manifest に削除として出る。競合は作られない
- [x] 一時停止・再開は冪等で、トークンの有効期限を変えない
- [x] 契約は追加だけ（契約の基準線・openapi と DTO のずれの検査が緑）
- [x] 同時の一時停止（初回の作成・未停止の行）は 1 つの停止期間に収まり、後の 1 本は冪等の 200 で終わる

## 赤→緑

結果は PR 本文に載せる。
