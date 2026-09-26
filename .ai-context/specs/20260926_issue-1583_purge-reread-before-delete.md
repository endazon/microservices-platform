---
title: 退職者の個人資料の削除で、削除の直前に所有者の状態を読み直し、判定の後に再有効化された利用者を消さない（#1583）
type: spec
status: done
related_ids: [FR-19, FR-20, UC-11, SC-19, NFR-09, NFR-14, ADR-0096, ADR-0057, ADR-0114, ADR-0036, IADR-0431, IADR-0428, IADR-0474]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md
  - planning:projects/microservices-platform/07_adr/ADR-0114_sync-token-rejected-after-account-disable.md
related_specs:
  - 20260911_issue-1409_private-note-disposal-after-window.md
  - 20260926_issue-1532_sync-token-rejected-after-disable.md
issue: "#1583"
---

# 仕様書: 退職者の個人資料の削除で、削除の直前に状態を読み直す（#1583）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-19（個人資料のライフサイクル）、FR-20（同期の経路のログ文言の固定）
- ユースケース／画面: UC-11、SC-19
- 関連 ADR: **ADR-0096 決定 1・2**（退職から 30 日の窓が閉じた所有者の個人資料を完全削除する。述語は 1 つ）、
  ADR-0057 決定 2（残余を置かない＝誤削除は取り返せない）、ADR-0036 D-09、ADR-0114（同期の経路）
- 関連 IADR: IADR-0431（退職の窓の口）、**IADR-0474 決定 6**（#1579 の監査。判定を先に全員分済ませる形を入れた）

## 目的・背景

#1579（IADR-0474 決定 6）で `PrivateNoteMaintenanceService.PurgeDepartedOwnersAsync` は「全員を判定 → 件数を 1 行ログ → まとめて削除」の形になった。
その結果、ある所有者の判定から削除までの隙間が**判定の 1 巡分**に広がった（最悪で所有者数 × 5 秒）。
その隙間で再有効化された利用者でも、判定時に `IsPurgeable` だったので資料が消える。削除は取り返せない。
所有者（利用者）は本削除が不可逆であり配備で有効であることを受け入れている（IADR-0474 決定 5）ので、**隙間そのものを縮める**。

## 受け入れ基準（#1583 と依頼の具体化）

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 各所有者の資料を消す**直前**に状態をもう一度読み、`IsPurgeable` が引き続き真のときだけ消す | `PrivateNoteDepartedOwnerPurgeTests`「判定の後に再有効化された所有者の資料は消えない」 |
| AC-2 | 読み直しが `null`（引けなかった・時間切れ）なら消さない | 同「削除の直前の読み直しで名簿を引けなければ消さない」 |
| AC-3 | 読み直しが例外で失敗しても消さない（取り消し以外）。他の所有者の処理と後段の定期処理は止めない | 同「削除の直前の読み直しが失敗したら消さず他の所有者は続ける」 |
| AC-4 | 対象の所有者は従来どおり消える（陽性対照）。読み直しは対象と判定した所有者にだけ行う（判定で落ちた所有者を二度引かない） | 同「窓が閉じたままの所有者は読み直しの後に消える」＋既存の陽性 4 件 |
| AC-5 | 変異 M8（`GrpcOwnerRetentionDirectory.cs:53-55` の `ct.ThrowIfCancellationRequested()` を外す）を試験で殺す | `GrpcOwnerRetentionDirectoryTests`「定期処理そのものが取り消されたら取り消しをそのまま伝える」 |
| AC-6 | 同期の経路の s2s 失敗の枝（`UserDirectoryGrpcClient.cs:181-188`）のログ文言を固定する（退職の窓の枝と対で） | `UserDirectoryGrpcClientStatusTests` の s2s の 2 件 |
| AC-7 | `GrpcOwnerAccountDirectory` が `GetAccountStatusAsync` を使う（退職の窓の読み口を使わない）ことを固定する。退職の窓の実装が `GetRetentionStatusAsync` を使うことも対で固定する | `GrpcOwnerAccountDirectoryTests` / `GrpcOwnerRetentionDirectoryTests` の記録ロガー試験 |

## 設計

- `PurgeDepartedOwnersAsync` の 2 巡目（削除のループ）で、`PurgeAllOwnedAsync` を呼ぶ前に `ownerRetention.GetAsync(owner, ct)` を読み直す。
  - `null` → 見送る。`IsPurgeable == false` → 見送る。例外（呼び出し元の取り消しを除く）→ 見送る（警告ログ。所有者 ID は出さない）。
  - 呼び出し元（定期処理の停止）の取り消しは握りつぶさず伝える。
  - 見送った人数を 1 行ログへ残す（所有者 ID は出さない。IADR-0474 決定 6 と同じ粒度）。
- 残る隙間は「読み直し → `PurgeAllOwnedAsync`（本文の実体の削除 → 行の削除）」の 1 人分であり、判定の 1 巡分ではない。これより詰めるには
  名簿と DB を跨ぐ排他が要り、ADR-0096 決定 2（述語を 1 つ足す形）の射程を越える —— 受容する（IADR-0474 への追記に書く）。
- 往復: 対象と判定した所有者は 2 往復になる（判定で落ちた所有者は 1 往復のまま）。日次粒度であり受容する。
- 新しい IADR は起こさない。IADR-0474 決定 6 へ日付つき追記ブロックを足す（依頼の指定・番号の欠番を作らない）。

## テスト

- 結合（`PrivateNoteDepartedOwnerPurgeTests`）: スタブ `StubOwnerRetentionDirectory` に「呼ばれた回ごとに答えを変える」宣言を足し、
  1 回目は窓が閉じた無効化済み、2 回目は 在籍中／`null`／例外 を返す。
- 単体: M8 は `FakeUserDirectoryClient.Hanging(asRpcException: true)` と呼び出し元の取り消しで測る（本番のチャネルは取り消しを
  `RpcException(Cancelled)` で投げ、共有クライアントが `null` に畳む。その形でしか M8 は見えない）。
- ログ文言は記録ロガーで測る。読み口の取り違え（`GetAccountStatusAsync` ↔ `GetRetentionStatusAsync`）は応答の写し方が同じなので
  ログ文言でしか区別できない —— 失敗させた偽の名簿で、どちらの文言が出たかを見る。
- 変異を 1 か所ずつ入れて赤を実測する（下の「検証」）。

## 母集合（規則 1〜6・9・10）

- 軸 1（誤りの側 —— 判定を先に全員分済ませる形を語る記述）: `git grep -n "判定を先に\|全員分"`（`CHANGELOG.md` 除外）→
  `PrivateNoteMaintenanceService.cs:123`（**反映**：コメントを書き換える）、`IADR-0277:61`（別件の「読み取り判定を先に行う」。**除外**）。
- 軸 2（往復の数を語る記述）: `PrivateNoteMaintenanceService.cs` の「所有者 1 人につき 1 往復」（**反映**）。
- 軸 3（削除の流れ・試験の件数を語る文書）: `git grep -n "PrivateNoteDepartedOwnerPurgeTests\|GrpcOwnerRetentionDirectoryTests\|GrpcOwnerAccountDirectoryTests\|UserDirectoryGrpcClientStatusTests"`（`src/` 除外）→
  `docs/tests/FR-19_private-notes-lifecycle.md` 行 18・19（**反映**：読み直しの基準を足し、導出値「12 メソッド・17 件」を計算し直す）、
  `docs/tests/FR-20_obsidian-sync.md` 行 32（**反映**：導出値「8 メソッド・13 件」を計算し直し、読み口の固定を足す）、
  `IADR-0474:140`（**反映**：追記ブロック）、`.ai-context/specs/20260911_issue-1409…`・`20260926_issue-1532…`（確定済み仕様書。**書き換えない**）、
  `scripts/test-spec-coverage-baseline.json`（クラス名の一覧。新しいクラスを足さないので**変更なし**）。
- 軸 4（失敗時の倒し方を語る文書）: `docs/api/east-west-grpc.md:299`「引けなかった → 削除しない」—— 読み直しでも同じ向き。**変更なし**。
  `deploy/docker-compose.yml:320`・`deploy/helm/.../values.yaml:273` は口の配線の注記で、流れを語らない。**変更なし**。

## 検証

- `dotnet build` / `dotnet test`（DocumentService.Tests・Platform.Shared.Infrastructure.Tests）/ `dotnet format --verify-no-changes`（両ユニット）。
- 変異（1 か所ずつ書き換えて試験を実行し、赤の件数を IADR-0474 の追記に記録する）。
