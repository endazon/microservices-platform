---
title: 個人資料（台帳・容量・同期端末） データ仕様書
type: data-spec
status: completed
created: 2026-08-23
updated: 2026-08-28
author: Claude
---
<!-- trace:
ids: [FR-19, FR-20, FR-22, UC-11]
adrs: [ADR-0037, ADR-0054, ADR-0057]
iadrs: [IADR-0253, IADR-0270, IADR-0296]
specs: [20260823_issue-451_private-note-obsidian-sync-core]
issues: [#451]
-->

# データ仕様書: 個人資料（台帳・容量・同期端末）

文書サービスの DB（Database per Service）に置く 3 エンティティ。個人資料の実体は既存の
文書（`Documents`）＋版（`DocumentVersions`）であり、本書のエンティティはそれを補う台帳である。
文書本体へ列を足さないのは、文書を読む全消費面（イベント・DTO・射影）の契約へ波及させない
ためである（共有先テーブルと同じ分離）。

## PrivateNotes（個人資料の台帳。文書と 1:1）

| 属性 | 型 | 必須 | 制約 | 説明 |
| --- | --- | --- | --- | --- |
| DocumentId | uuid | ○ | PK・FK Documents（**カスケード削除**） | 文書 ID。文書の物理削除＝台帳行の消滅＝容量からの解放 |
| OwnerId | varchar(200) | ○ | index | 所有者（同期スコープ・容量集計のキー） |
| VaultPath | varchar(1024) | ○ | | Obsidian Vault 内の相対パス（同期の突き合わせキー。アクティブ行内で所有者ごとに一意をアプリ層で強制） |
| LatestBytes | bigint | ○ | | **最新版の本文 UTF-8 バイト数**。容量算入の単位。版履歴のバイト数を持つ場所は無い（＝版履歴は構造的に非算入） |
| ContentHash | varchar(64) | | | 最新版本文の SHA-256（hex）。プラグインの差分判定用 |
| IncludeInSearch / IncludeInGraph / IncludeInAi | boolean | ○ | 既定 false | 露出 3 トグル（独立・既定 OFF） |
| DeletedAt / PurgeAt | timestamptz | | | 論理削除時刻と自動物理削除期限（削除＋90 日）。null = アクティブ |
| PurgeImminentNotifiedAt | timestamptz | | | 完全削除 7 日前通知の発火記録（1 回だけ送る） |
| CreatedAt / UpdatedAt | timestamptz | ○ | | |

## PrivateNoteQuotas（利用者ごとの保存容量）

| 属性 | 型 | 必須 | 制約 | 説明 |
| --- | --- | --- | --- | --- |
| OwnerId | varchar(200) | ○ | PK | 利用者 |
| LimitBytes | bigint | ○ | 既定 1 GB・上限 1 TB（アプリ層で強制） | 管理者が変更する |
| Warned80 / Warned95 | boolean | ○ | | 容量警告の発火記録。**跨ぎで 1 回・閾値を下回ると解除**（再武装） |
| WeeklyDigestSentAt | timestamptz | | | 週次削除通知の送出記録（7 日間隔の下限） |
| UpdatedAt | timestamptz | ○ | | |

## SyncDevices（同期端末と同期トークン）

| 属性 | 型 | 必須 | 制約 | 説明 |
| --- | --- | --- | --- | --- |
| Id | uuid | ○ | PK | 端末 ID |
| OwnerId | varchar(200) | ○ | index | 所有者 |
| DeviceName | varchar(200) | ○ | | 表示名（連携設定画面用） |
| TokenHash | varchar(64) | ○ | **unique index** | 同期トークンの SHA-256（hex）。**平文は保存しない**。照合の入口 |
| IssuedAt / ExpiresAt | timestamptz | ○ | | 発行と期限（発行＋30 日） |
| RevokedAt | timestamptz | | | 失効（個別・一括）。null = 未失効 |
| LastSyncAt | timestamptz | | | 最終同期時刻（同期状態の表示用） |
| ExpiryNotifiedAt | timestamptz | | | 期限 7 日前通知の発火記録（再発行でリセット） |

## ライフサイクル

- 作成: 画面（本文なし）または同期 push（本文あり）。
- 論理削除: 台帳の `DeletedAt` / `PurgeAt` のみ（文書の状態は変えない）。
- 完全削除: **オブジェクトストレージの本文実体を先に削除** → 文書行の物理削除 →
  版・共有・台帳がカスケード削除 → 文書削除イベントで下流（索引・グラフ）を掃除。
  🔴 **順序は入れ替えない。** 台帳を先に消すと、実体を指す値がどこにも残らず不可視のまま残留する。
  実体を消せなければ行は残る（利用者には失敗が返る）。**個人資料は変換経路を通らないため
  図表資産を持たず**、消す対象は本文（および過去の版が指していた本文）だけである。
- マイグレーション: `20260822212832_AddPrivateNotes`（3 テーブル新設。既存テーブル変更なし）。

## 関連

- 機能仕様書: [FR-19_private-notes](../functional/FR-19_private-notes.md) /
  [FR-20_obsidian-sync](../functional/FR-20_obsidian-sync.md)
- データ仕様書: [document-and-version](document-and-version.md) / [document-share](document-share.md)
