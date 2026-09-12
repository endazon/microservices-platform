---
title: IADR-0444 個人資料の契約に公開範囲 3 状態・同期状態・タグを足し、同期対象範囲と競合をサーバの台帳で持つ（指定先と同期履歴は裁定待ちのまま契約に出さない）
type: impl-adr
status: Accepted
related_ids: [FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0036, ADR-0037, ADR-0046, ADR-0054, ADR-0063, IADR-0131, IADR-0139, IADR-0270, IADR-0352, IADR-0360]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/10_feedback/20260912_mock-elements-without-contract.md
related_specs:
  - ../specs/20260912_1441-1442_private-note-contract-gaps.md
---

# IADR-0444: 個人資料の契約に公開範囲 3 状態・同期状態・タグを足し、同期対象範囲と競合をサーバの台帳で持つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID:
  FR-19・FR-20・UC-11・SC-19（主要素 1・2・5・6）・SC-20（主要素 3・5）／
  ADR-0036（D-06 共有の単位は個人とグループ。§未確定事項 5 グループの粒度は**未決**）／
  ADR-0037（決定 3・4 同期対象の判定とフォルダ粒度、決定 7 競合は利用者が 3 択、決定 9 監査にタイトルを書かない、決定 14 KB が正）／
  ADR-0046（本文編集は Obsidian 経路のみ）／ADR-0054（`doc_scope`）／ADR-0063（タグ提案は個人資料も対象）／
  planning#614 の裁定記録（`10_feedback/20260912_mock-elements-without-contract.md`）
- 関連する実装 ADR:
  [IADR-0131](IADR-0131_openapi-as-bff-contract-source.md)（openapi 手書きが正本・状態文字列は enum にしない）／
  [IADR-0139](IADR-0139_domain-bundled-contract-prs.md)（同型の契約追加はドメイン単位で 1 PR）／
  [IADR-0270](IADR-0270_private-note-obsidian-sync-backend-core.md)（バックエンド中核。本決定はその台帳を広げる）／
  [IADR-0352](IADR-0352_obsidian-plugin-push-edit-granularity-and-conflict-resolution.md)（push は 409 を自動解決せずプラグインが 3 択を提示。**本決定はその 409 経路にサーバ側の記録を足す。プロトコルの応答は変えない**）／
  [IADR-0360](IADR-0360_obsidian-sync-rename-contract.md)（move の口）
- 関連する実装仕様書: [`20260912_1441-1442_private-note-contract-gaps.md`](../specs/20260912_1441-1442_private-note-contract-gaps.md)
- 起点 issue: #1441 / #1442（planning#614 の裁定。裁定待ち 2 件は planning#618）

## コンテキストと課題

SC-19 の公開範囲・同期状態・タグ、SC-20 の同期対象範囲・競合解決・同期履歴は、計画本文が定めているのに BFF 契約
（`PrivateNoteDto` / `SyncDeviceDto`）に項目が無く、UI/UX 改善（#1436）は「供給が無い値を 0 や空で描かない」規律に従って
列そのものを描かなかった。planning#614 の裁定はこれを「計画の不足ではなく契約の不足」とし、各要素の供給元を画面設計へ書いた。

後段を実測すると、供給元の実体は次のとおり分かれていた。

| 要素 | 後段の実体（着手前） |
| --- | --- |
| 公開範囲 | 共有台帳 `DocumentShare`（`user` / `group`）と管理 API `/documents/{id}/shares` は在るが、BFF / openapi に 1 本も出ていない |
| タグ | `Document.Tags`（辞書 ID の集合）は在る。`PrivateNote` にはタグ列が無い（1:1 の `Document` 側が持つ） |
| 同期状態 | 永続化されるのは `SyncDevice.LastSyncAt` と `PrivateNote.ContentHash` / `Version` だけ。**競合はその場の 409 応答として返るだけ**で記録が無い |
| 同期対象範囲 | サーバに無い（Obsidian 側のフォルダ指定はプラグインが持つ） |
| 同期履歴 | 無い（N と保持期間も計画に無い） |

課題は 3 つ。①契約にどの形で載せるか（状態の表現・指定先の扱い）、②「競合の一覧と解決」を画面から行うために
**サーバ側に何を持つか**（プラグインが既に 3 択を提示している）、③裁定待ちの 2 件をどう切り離すか。

## 検討した選択肢

### 論点 1: 公開範囲の表現

| 案 | 内容 | 判定 |
| --- | --- | --- |
| (1a) 3 状態の文字列 ＋ 件数（`visibility` / `sharedUserCount` / `sharedGroupCount`） | 指定先は載せない | **採用** |
| (1b) 共有先の一覧（識別子・種別）をそのまま載せる | 指定先の型（Keycloak グループか部門か）が ADR-0036 §未確定事項 5 で未決 | 棄却。未決の型を契約に焼き込むと裁定後に破壊的変更になる |
| (1c) 3 状態を enum で定義 | 後段が値を増やすと契約が壊れる | 棄却（IADR-0131 論点 C。`type: string` ＋ description に値集合） |

`groups` を「グループ共有が 1 つでもある」で優先するのは、グループ指定が個人指定より広い状態であり、
「一目で区別できる」（SC-19 主要素 2）ために**広いほうを表示する**からである。件数を併せて載せるので情報は落ちない。

### 論点 2: 同期状態の導出

| 案 | 内容 | 判定 |
| --- | --- | --- |
| (2a) `conflict` → `target` → `excluded` の優先順で導出。`target` は「有効端末あり かつ（フォルダ未設定 or 配下）」 | ADR-0037 決定 3（所有で判定）・決定 4（フォルダ粒度・外れたら同期停止）と整合 | **採用** |
| (2b) 端末の有無だけで `target` / `excluded` | フォルダを外した資料が「同期対象」に見える（決定 4 に反する） | 棄却 |
| (2c) 資料ごとに同期状態を永続化し push / pull が更新 | プロトコルの変更が要り、プラグインの改修を伴う | 棄却（導出で足りる） |

削除済みの資料は常に `excluded`（削除はサーバ側の論理削除であり同期の対象ではない。決定 5）。

### 論点 3: 競合をサーバで持つか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| (3a) push の 409 時に `SyncConflict` を記録し、端末が送った本文をオブジェクトストレージに保存。画面から 3 択で解決できる。正しい `baseVersion` の push が通れば `client` として閉じる | SC-20 主要素 5 を画面で満たせる。プロトコルの応答は不変。プラグイン側の 3 択（IADR-0352）はそのまま生きる | **採用** |
| (3b) 競合はプラグインだけが扱い、画面は描かない | 計画（主要素 5）を満たさない。裁定は「契約を足す」である | 棄却 |
| (3c) 409 の代わりにサーバが両版を保持して自動的に「両方を残す」 | 自動解決を既定にしない（決定 7）に反する | 棄却 |

同一資料・同一端末の未解決競合は**上書き**（行を増やさない）。端末が同じ土台で繰り返し push しても一覧が膨らまない。

### 論点 4: 同期対象範囲をどこが持つか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| (4a) サーバの `SyncSettings`（所有者ごと・フォルダの全量置き換え）。同期状態の導出だけに使い、プロトコル（manifest）は絞らない | 画面（主要素 3）を満たし、プラグインの挙動は変えない | **採用** |
| (4b) manifest をフォルダで絞る | プラグイン側のフォルダ指定と二重になり、決定 4「削除ではなく同期停止」の実体がプラグインとサーバで割れる | 棄却（将来の別段） |

### 論点 5: 裁定待ち 2 件の切り離し

指定先（ADR-0036 §未確定事項 5）と同期履歴（N と保持期間）は **契約に出さない**（planning#618 を起票）。
3 状態は指定先の型に依存しないため先に出す。同期履歴は口を作らない。

### 論点 6: 束ね

#1441 と #1442 は同じ資源 `private-notes` に対する同型の契約追加であり、IADR-0139 決定 1 の範囲（上限 4 件以内）で 1 PR に束ねる。

## 決定

1. **`PrivateNoteDto` に `visibility` / `sharedUserCount` / `sharedGroupCount` / `syncState` / `tags` を required で足す。** 状態は
   `type: string`（値集合は description と C# の定数 `PrivateNoteVisibilityValues` / `PrivateNoteSyncStates`）。
   🔴 **既定値付きで足す互換案は採らない** —— 後段が供給し忘れても `private` / `excluded` / 空配列が黙って返り、
   「供給が無い値を 0 や空で描かない」規律を型の側で破るため。`check-contract-schema` の破壊的変更として承認を記録した
   （`contract-breaking-allowlist.json` → baseline の `$acceptedBreakingChanges`）。
2. **公開範囲は共有台帳から導出する**: 0 行 → `private`、`group` 行あり → `groups`、それ以外 → `users`。**指定先は載せない**（決定 6）。
3. **同期状態は導出する**（論点 2 の (2a)）。永続化しない。
4. **競合はサーバの台帳 `SyncConflict` で持つ**（論点 3 の (3a)）。push の `version_conflict` 経路で記録し、端末の本文はオブジェクトストレージ
   （鍵 `private-notes/conflicts/{id}`）に置く。解決は `local` / `server` / `both` の 3 択のみ（`SyncConflictResolutions.Selectable`）。
   `both` の別名は `<元パスの拡張子前> (競合 yyyy-MM-dd HHmm).md`（タイトルも同形）で、既存の新規作成の経路（容量上限 507）を通す。
   解決時にローカル本文を消す。**409 応答の本文・ステータスは変えない。**
5. **同期対象範囲はサーバの `SyncSettings`**（所有者ごと `TargetFolders`・全量置き換え・正規化と上限は後段の検証）。
   同期状態の導出だけに使い、manifest は絞らない。対象から外しても資料は消えない（決定 4）。
6. **指定先と同期履歴は契約に出さない。** planning#618 の裁定後に別 PR で足す（指定先: `PrivateNoteDto` への追加と変更ダイアログの口。
   同期履歴: 一覧の口と保持の実装）。
7. **BFF は透過中継**（`RelayAsync`）で 5 口を足す。PUT / resolve は write ゲート。`x-roles: []`（認証のみ）。

## 結果

- 良い点: SC-19 の 3 列・絞り込み 3 軸、SC-20 の同期対象範囲と競合区画が、計画の供給元どおりの値で描ける。プラグインのプロトコルは不変で、
  既存のプラグイン側 3 択と画面側 3 択が併存する（どちらで解決しても台帳が閉じる）。
- 悪い点 / 残余リスク:
  - 未解決競合のローカル本文がオブジェクトストレージに残る。**保持期限は planning#618 の同期履歴の保持期間と揃える前提で未定**（仕様書 §未決事項）。
  - `groups` 優先の表示は「個人にもグループにも共有している」を件数でしか示さない。指定先が載れば解消する。
  - 同期状態は導出のため、フォルダ設定や端末の変化が即座に反映される一方、「最後に同期が成功したか」は表さない（それは同期履歴の関心。裁定待ち）。

## フォローアップ

1. planning#618 の裁定後: 指定先（型・一覧・変更ダイアログの口）と同期履歴（N・保持期間・口）を別 PR で足し、
   計画の SC-19 / SC-20「供給元と契約の現状」表を更新して環流する（本 PR では 4 要素ぶんの更新を環流する）。
2. 解決済み競合の本文削除と、未解決競合の本文の保持期限（同上）。
3. 同期の一時停止／再開（SC-20 主要素 7）は本決定の範囲外（`SyncSettings` に `Paused` を足す形が自然だが、裁定と画面の要求を先に読む）。
