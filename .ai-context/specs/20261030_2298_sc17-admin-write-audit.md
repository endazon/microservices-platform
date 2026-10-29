---
title: 作業仕様書 — SC-17 のユーザー管理の書き込み（属性・部門・ロール・無効化・有効化）を BFF で監査ログに 1 操作 1 行で残す（#2298）
type: spec
status: done
related_ids: [FR-09, FR-05, SC-17, UC-05, NFR-28, ADR-0004, ADR-0116, IADR-0647, IADR-0301]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2298"
---

# 作業仕様書 — SC-17 の書き込みの監査（#2298）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7c71742d9`）。

## 計画

- 計画 05_screens §SC-17 アクション「操作は監査ログに記録」。
- 計画 ADR-0116 決定 1（部門の変更）「操作は監査ログに記録する」。

## 現状（develop で確かめた）

- `UserAdminBffEndpoints` の書き込み 5 口（`PUT /{userId}/attributes`・`PUT /{userId}/department`・`PUT /{userId}/roles`・`POST /{userId}/disable`・`POST /{userId}/enable`）は、透過中継するだけで監査を呼ばない。
- 後段（AuthorizationService）にも `IAuditLogger` の呼び出しは 0 件である（`git grep -n "IAuditLogger\|audit.Record" src/platform/backend/Services/AuthorizationService`）。二重記録の心配は無い。

## 方針

- SC-09（#2124・IADR-0647）と同じ形にする。BFF の中継で、後段の応答の状態コードから outcome を決め、`AdminWriteAudit.Record` で 1 操作 1 行を残す。
- action:
  - `user.attributes.replace`
  - `user.department.replace`
  - `user.roles.replace`
  - `user.disable`
  - `user.enable`
- detail は `target=<userId> status=<code>` だけにする。**入力値（属性値・部門・ロール名）は載せない**（NFR-28）。
- 後段不達（502 へ縮退）も `failed` で残す。読み取り（一覧・選択肢・部門の読み取り）は残さない。
- `Proxy` は共有先の利用者検索（`UserLookupBffEndpoints`）・グループ検索（`GroupLookupBffEndpoints`）と共用である。そのため、監査は省略可能な引数として足す。検索は監査しない（既定の null）。
- 新しい IADR は作らない。判断は IADR-0647 の決定（BFF で 1 操作 1 行。結果は後段の状態コードから決め、入力値は載せない）をそのまま SC-17 へ当てるもので、新しい選択を含まない。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "MapPut\|MapPost\|MapPatch\|MapDelete" src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/UserAdminBffEndpoints.cs` で数えた。書き込みは 5 口ですべて対象であり、作成の口は無い（計画どおり）。
  - SC-17 の他の書き込み経路として、容量の上限（`private-note.quota.set`）は後段が既に記録している。これは別画面の口なので対象外とした。
- **規則 10**: テスト仕様書 SC-17 の「対象外」表にある「監査記録の保持・閲覧 | 記録は認可基盤の管理イベント…」は、保持・閲覧の話であり記録の有無ではない。そのまま正しいので、書き換えなかった。T-68 を足した。

## 受け入れ基準 → 試験（`BffUserAdminAuditTests`）

| 基準 | 試験 |
| --- | --- |
| 5 口それぞれで監査が 1 件（action・利用者名・granted・`target=<userId> status=<code>`） | `書き込みは監査ログに1件残る`（Theory 5 件） |
| 入力値が detail に出ない | 同上（本文に入れた属性値・部門・ロール名が detail に無い） |
| 拒否・不達も残る | `後段の拒否はdeniedで残る`（400）、`後段不達はfailedで残る`（502） |
| 読み取りは残さない | `読み取りは監査しない`（陽性対照は上の書き込み） |
