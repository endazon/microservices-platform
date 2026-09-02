---
title: FR-20 個人資料・Obsidian 同期 API 通信仕様書
type: api-spec
status: completed
created: 2026-08-23
updated: 2026-09-03
author: Claude
---
<!-- trace:
ids: [FR-19, FR-20, FR-22, UC-11, SC-19, SC-20]
adrs: [ADR-0037, ADR-0054]
iadrs: [IADR-0270, IADR-0338, IADR-0352]
specs: [20260823_issue-451_private-note-obsidian-sync-core, 20260828_issue-451a_private-notes-bff, 20260902_issue-1098_obsidian-plugin-pull-stage1, 20260903_issue-1153_obsidian-plugin-push-delete-conflict-stage2]
issues: [#451, #1098, #1153]
-->

# 通信仕様書: 個人資料・Obsidian 同期 API

文書サービスが提供する REST API。リソース名は `private-notes`（計画が確定させた綴り）。
3 群に分かれ、認証がそれぞれ違う。

**［2026-08-28 追記］BFF 端点（`/bff/private-notes*`）は実装済みである**（従前の「未実装の残件」は
本追記で置き換わる）。画面が呼ぶのは BFF であり、本書の口を直接は呼ばない。**同期プロトコル群と
上限管理は BFF に載せていない**（前者は資格情報が別系統でプラグインが呼ぶ、後者は載せる画面が無い）。
契約の正は API 定義（`docs/api/openapi.yaml` の `/bff/private-notes*`）である。

| 群 | パス | 認証 | 利用者 | BFF |
| --- | --- | --- | --- | --- |
| ライフサイクル | `/private-notes*` | JWT（認証必須・ロール不要） | 画面（個人資料管理） | あり |
| 端末・トークン | `/private-notes/devices*` | JWT（認証必須・ロール不要） | 画面（連携設定） | あり |
| 同期プロトコル | `/private-notes/sync*` | **Bearer 同期トークン**（JWT 不要） | Obsidian プラグイン | **なし** |
| 上限管理 | `/private-notes/quotas/{ownerId}` | JWT ＋ 管理者ロール | 管理画面 | **なし** |

## エンドポイント一覧

| メソッド | パス | 概要 | 主な応答 |
| --- | --- | --- | --- |
| GET | `/private-notes` | 本人の資料一覧（削除済み含む）＋容量 | 200 |
| POST | `/private-notes` | 作成（タイトルのみ・本文なし） | 201 / 507（容量） / 409（パス重複） |
| DELETE | `/private-notes/{id}` | 論理削除（`capacityFreed=false` を返す） | 200 / 404 |
| POST | `/private-notes/{id}/restore` | 復元（90 日以内） | 200 / 404 / 409 |
| POST | `/private-notes/purge` | 完全削除（ids 配列。単票／一括共用。解放容量を返す） | 200 / 404 / 409 |
| PUT | `/private-notes/{id}/exposure` | 露出 3 トグル | 200 / 404 |
| GET | `/private-notes/devices` | 端末一覧（トークンは載らない） | 200 |
| POST | `/private-notes/devices` | トークン発行（**平文はこの応答のみ**・期限 30 日） | 201 |
| POST | `/private-notes/devices/{id}/reissue` | 手動再発行（旧トークン即時無効） | 200 / 404 |
| DELETE | `/private-notes/devices/{id}` | 個別失効 | 204 / 404 |
| POST | `/private-notes/devices/revoke-all` | 全端末一括失効 | 200 |
| GET | `/private-notes/sync/manifest` | 同期対象の一覧（削除フラグ付き） | 200 / 401 |
| POST | `/private-notes/sync/notes` | push（noteId 無し=新規・有り=更新。edits 1 件 = 1 版） | 201・200 / 401 / 404 / 409 / 413 / 507 |
| GET | `/private-notes/sync/notes/{id}` | pull（本文取得） | 200 / 401 / 404 |
| POST | `/private-notes/sync/notes/{id}/delete` | 論理削除（Obsidian 側削除の伝播） | 200 / 401 / 404 |

## 認証・認可の規則

- 同期トークンは `Authorization: Bearer <token>`。検証失敗（欠落・不正・期限切れ・失効）は
  **区別せず 401**。
- 所有者スコープ外の資料 ID は **404**（存在秘匿。403 を返さない）。
- ライフサイクル群の主体は JWT の主体のみから決める（クエリ・本文に主体の口を作らない）。

## 競合の契約（409 応答）

```json
{ "error": "version_conflict", "serverVersion": 7, "serverUpdatedAt": "…" }
```

push の `baseVersion`（クライアントが最後に見た版）と現在版の不一致で返す。
自動解決しない。クライアントは pull で現在版を取得し、利用者の選択
（ローカル採用＝再 push／サーバ採用＝上書き／両方残す＝別パスで新規 push）に従う。

push の 409 は他に 2 形ある。クライアントは `error` で区別する。

| `error` | 場面 | 本文 |
| --- | --- | --- |
| `version_conflict` | 更新の `baseVersion` が現在版と不一致 | `serverVersion` / `serverUpdatedAt` |
| `deleted` | 更新先がサーバ側で論理削除済み | `purgeAt` |
| `vault_path_conflict` | 新規作成の `vaultPath` が既存の有効な資料と重なる | `vaultPath` |

## 同期プロトコルの呼び手（Obsidian プラグイン）

- **呼ぶのは自作プラグイン**（`src/obsidian-plugin/`）だけである。`manifest` / `pull` / `push` / `delete` の
  4 つを呼ぶ。BFF は経由せず、資格情報は Bearer 同期トークン。
- 応答の形は client 側の型ガードで確かめ、契約と違えば失敗にする（黙って空扱いにしない）。
  **契約を変えるときはサーバ側テストと `src/obsidian-plugin/src/protocol/types.ts` の両方を動かす。**
- 接続先は基底 URL（https）で、パスは本書の `/private-notes/sync/*` をそのまま連結する。
- push の `edits[]` はプラグインが**保存の間隔 30 秒で刻んだ編集列**（未送信分をまとめて送る。1 要素 = 1 版）。
  更新の `baseVersion` は最終同期時の版（楽観ロック）。409 `version_conflict` を受けても**再送しない**——
  利用者の選択を待つ。
- 🔴 **更新の push は `vaultPath` を書き換えない**（サーバは本文・タイトルだけを更新する）。プラグインは
  ローカルのリネームを伝播できず、契約にリネームの口を足すのは別 issue。
- 🔴 **配備済みクラスタのエッジは `/private-notes/sync/*` を外へ出していない**（`/bff/*` と SPA のみ）。
  実測: `https://localhost/bff/private-notes/sync/manifest` → 404。公開経路は配備側の別 issue。
  ローカル検証は `kubectl port-forward svc/document-service` を接続先にする。

## 関連

- 機能仕様書: [FR-19_private-notes](../functional/FR-19_private-notes.md) /
  [FR-20_obsidian-sync](../functional/FR-20_obsidian-sync.md)
- テスト仕様書: [FR-20_obsidian-sync](../tests/FR-20_obsidian-sync.md)
