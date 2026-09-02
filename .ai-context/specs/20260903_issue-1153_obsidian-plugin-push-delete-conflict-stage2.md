---
title: 作業仕様書 — Obsidian プラグイン第 2 段: push / delete / 競合解決（3 択）/ サーバ側削除・リネームの伝播 /「1 編集」の刻み（#1153）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-20
  - ADR-0037
  - ADR-0046
  - IADR-0270
  - IADR-0338
  - IADR-0352
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 仕様書: issue #1153 — Obsidian プラグイン第 2 段（push / delete / 競合 3 択 / サーバ側削除・リネームの伝播）

> #1098 第 1 段（PR #1156・`IADR-0338`）の残射程。第 1 段の判断（配置・ビルド・接続先・トークン保管・
> pull の差分規則）は**覆さない**。本段で新たに決める実装判断（「1 編集」の刻み・push の計画規則・
> 409 の 3 択の実現・サーバ側削除／リネームの伝播・ローカルのリネーム）は `IADR-0352` に置き、
> 本書は範囲・母集合・受け入れ基準・実測を持つ。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（双方向同期）。前提 FR-19
- ユースケース（UC）: UC-11
- 画面（SC）: SC-20（本 PR は画面を触らない。競合の解決はプラグイン側の Modal で行う）
- 関連 ADR: `ADR-0037` 決定 2（双方向）・4（フォルダ粒度。外れたら同期停止であって削除ではない）・
  5（Obsidian 側の削除は論理削除・90 日）・7（競合は 3 択を利用者へ提示。自動解決を既定にしない）・
  8（1 編集 = 1 版）・14（KB が唯一の正）／フォローアップ 5（「1 編集」の定義）・11（完全削除の
  Obsidian 側への伝播）／`ADR-0046`
- 実装 IADR: `IADR-0270`（サーバ側中核。決定 3・7）/ `IADR-0338`（第 1 段）/ `IADR-0352`（本作業）

## 着手条件の確認（実測 2026-09-03・`develop` `3d0a7048`）

1. `ADR-0037` は `Accepted`・「着手可否の注記」は留保を外している（第 1 段と同じ）。**着手可**。
2. サーバ側の口は #451 で実装済み（§母集合）。**契約は変えない**（#1153 宣言ファイル領域）。
3. 第 1 段の PR #1156 は着地済み（`3d0a7048`）。本段はその上に積む。

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 9・10）

🔴 issue 本文の数え（2026-09-02・第 1 段のブランチ）を転記せず、`3d0a7048` で引き直した。

| 走査 | 結果 | 含意 |
| --- | --- | --- |
| `Features/ObsidianSync/*` のファイル | 8（Manifest 2・Pull 2・Push 2・Delete 1・合成点 1） | 口は 4 つ揃っている。**リネームの口は無い** —— `Push/Endpoint.cs` の更新経路は `note.RecordBody(...)` だけで **`VaultPath` を書き換えない**（`PrivateNote` に `VaultPath` の setter も無い）。ローカル → サーバのリネームは契約上伝播できない（→ `IADR-0352` 決定 5。契約変更は別 issue） |
| Push の契約（`Push/Command.cs`） | `PushNoteRequest(NoteId?, VaultPath, Title, BaseVersion?, Edits[{Content, EditedAt?, ChangeNote?}])` → 201/200 `PushNoteResponse(NoteId, Version, ContentHash, Bytes)` | 新規は `NoteId=null`。更新は `BaseVersion` 必須（無ければ 400）・不一致で 409 `{error:"version_conflict", serverVersion, serverUpdatedAt}`・削除済みへは 409 `{error:"deleted", purgeAt}`。新規のパス重複は 409 `{error:"vault_path_conflict", vaultPath}`（`PrivateNoteEndpoints.PathConflictProblem`）。1 MB 超は 413。容量 100% の新規は 507 |
| Delete の契約（`Delete/Endpoint.cs`） | `POST /notes/{id}/delete` → 200 `{deletedAt, purgeAt}`。冪等 | 論理削除（90 日）。復元は画面（SC-19） |
| `syncClient.ts` が呼ぶパス | `GET manifest` / `GET notes/{id}` の 2 つ（`POST` は 0 件。陽性対照: `MANIFEST_PATH` 1 件） | 第 2 段で `POST notes` / `POST notes/{id}/delete` を足す |
| `previousPath` の消費箇所（`grep -rn previousPath src/obsidian-plugin/src`） | 型・計算・テストの表明のみ。`pullSync.ts` は読まない（PR #1156 の AI レビュー指摘と一致） | サーバ側リネームは検知だけで旧ファイルが残る。本段で消費する |
| `第 1 段` / `取り込みのみ` / `pull のみ` / `第 2 段` の記述（`docs/` `src/obsidian-plugin/` `.github/`） | `docs/api` 1 箇所・`docs/functional` 5・`docs/how-to` 1・`docs/screens` 2・`docs/tests` 3・`src/obsidian-plugin` 7（main / pullPlanner / syncClient / settingsTab / manifest.json / package.json / テスト 2） | すべて本 PR で追随（規則 10: 是正後に「第 2 段で対応」の語で引き直す） |
| 既存 issue の重複検索（`Obsidian` `push` `競合`） | #1153（本件）・#1154（エッジ経路）のみ | 重複なし |

**除外**: `src/ai-stock-trading`（submodule）・`node_modules`・`CHANGELOG.md`（自動生成）・
`docs/how-to/plan-id-*` `IADR-0121`（「第 2 段」は別文脈の語）。

## 対象範囲

### 対象（本 PR ＝ 第 2 段）

1. **「1 編集」の刻み**（`protocol/editJournal.ts`）: Obsidian の保存イベント（`vault.on('modify')`）を
   **静穏窓 30 秒**で畳み込んだ単位を 1 編集とし、`data.json` の `journal` に本文つきで積む
   （1 ファイル最大 50 件。超えたら古いものから落とす）。オフラインで 10 回保存（各 30 秒以上空く）
   すれば push の `edits[]` は 10 要素 → サーバに 10 版（決定 8）。
2. **push**（`protocol/pushPlanner.ts` / `pushSync.ts`）: 同期フォルダ配下の `.md` を走査し、
   未追跡 → 新規（`noteId` 無し）／追跡済みで内容が変わった → 更新（`baseVersion` = 同期状態の版。
   楽観ロック）。**409 を受けたら上書きしない**（journal は残す。競合として報告）。
3. **delete**: Obsidian 側の削除イベント（`vault.on('delete')`）を journal に記録し、push で
   `POST …/notes/{id}/delete`（論理削除）。**「対象フォルダから外す」（`rename` で外へ出た・
   同期フォルダ設定の変更）は削除を送らず追跡を外すだけ**（決定 4）。
4. **競合の 3 択 UI**（`obsidian/conflictModal.ts`。`ADR-0037` 決定 7）: 409 `version_conflict` の資料ごとに
   Modal で「ローカルを採用」「サーバを採用」「両方残す」「保留」を出す。解決の実体は
   `protocol/conflictResolver.ts`（Obsidian 非依存）。CLI では `resolve <path> local|server|both` で非対話。
5. **サーバ側削除の伝播**: manifest `deleted=true` で追跡済みなら **state に `serverDeleted` を残し、
   ローカルは消さない**。push 時に「サーバ側で削除済み」の競合として提示し、「ローカルを採用（新規として
   再作成）」「サーバを採用（ローカルをゴミ箱へ）」から選ぶ（`ADR-0037` フォローアップ 11 ②）。
6. **サーバ側リネームの伝播**: 同期状態に `vaultPath`（サーバ値）を持ち、manifest の `vaultPath` が
   変わったら**ローカルを移動**する（ローカルが最終同期時のままなら旧パスを消す。編集されていれば旧
   ファイルを残して報告）。
7. **ローカルのリネーム**（フォルダ内）: journal に記録し、追跡の紐付け（noteId → 新パス）だけ更新
   する。**サーバへは伝播できない**（契約に口が無い。§母集合）。push の `vaultPath` には新パスを載せる
   （サーバが将来受けるようになれば追随する）。利用者には「リネームは未伝播」を通知する。
8. 第 1 段の `conflict(local-modified)`（pull 側）は「push で送るローカル編集」へ意味が変わる。
   本当の競合は push 側の 409 で現れる。
9. Node ハーネス `dist/cli.mjs` を副コマンド化（`pull` 既定 / `push` / `sync` / `record` / `delete` /
   `rename` / `resolve`）。実 HTTP の証跡を push / delete / 競合の 3 経路で取る。
10. 単体テスト（`protocol/`）: journal の畳み込みと 50 件上限・push 計画の各分岐・409 で上書きしない・
    3 択それぞれ・削除／同期停止の区別・サーバ側削除／リネームの伝播。**変異試験 1 本**（楽観ロックを
    外す＝409 を自動で再送する変異でテストが落ちることを記録）。

### 対象外（理由と送り先）

| 項目 | 理由 | 送り先 |
| --- | --- | --- |
| ローカル → サーバのリネーム伝播 | 契約に `vaultPath` を書き換える口が無い（`Push` は更新時に `VaultPath` を無視）。契約変更は #1153 の宣言領域外 | 別 issue（サーバ契約。本 PR で起票） |
| 保存のたびの自動 push（バックグラウンド同期） | 計画は同期の契機を定めていない。本段は「同期」コマンド／設定タブのボタンで手動。journal は常時積む | 後続（必要になったら） |
| 競合の 2 ペイン差分表示 | 計画 SC-20 の記述は画面側の話で、プラグインの Modal は 3 択と要約（版・編集数）で足りる。差分表示は UI の肥大 | 後続（利用者の要望があれば） |
| `/private-notes/sync/*` のエッジ公開 | 別 issue | #1154 |
| 配布のリリース資産化・en ロケール・カバレッジ算入 | 第 1 段と同じ | `IADR-0338` フォローアップ 3〜5 |
| SC-20 画面の変更 | 口が無い（第 1 段と同じ）。本 PR は画面を触らない | #451 残 / 別 issue |
| サーバ側の**完全削除**（purge）の検知 | manifest から行が消えるだけで `deleted=true` とは区別できない。追跡済みで manifest に無い資料は「サーバに無い」として state から外し、ローカルは消さない（消す向きの操作をしない） | 本 PR で「触らない」と決めるところまで（`IADR-0352` 決定 4） |

## 設計（要点。判断の記録は `IADR-0352`）

```text
src/obsidian-plugin/src/
  protocol/
    types.ts            PushNoteRequest / PushNoteResponse / DeleteNoteResponse / SyncConflictError（409 の 3 形）
                        / SyncTooLargeError（413）/ SyncQuotaError（507）
    transport.ts        method に POST を足し、body を運ぶ
    syncClient.ts       push() / delete() を足す（GET 2 つは変えない）
    editJournal.ts      「1 編集」の刻み（静穏窓・上限 50）・削除／リネーム／フォルダ外への移動の記録（純粋関数）
    pushPlanner.ts      push の計画（create / update / delete / untrack / rename-local / server-deleted / missing-local / unchanged）
    pushSync.ts         push の一巡（409 → 競合として報告・上書きしない）
    conflictResolver.ts 3 択の実体（local: pull で版を積み直して再 push / server: pull で上書き / both: 別パスで新規 push）
    pullPlanner.ts      state に vaultPath / title / serverDeleted を持つ。サーバ側リネームとローカルのリネームを区別する
    pullSync.ts         previousPath を消費（移動）・server-deleted を state に残す
  obsidian/
    vaultFileStore.ts   list / remove（trashLocal）/ rename を足す
    conflictModal.ts    3 択 Modal（Obsidian 依存はここだけ）
  main.ts               vault イベント → journal。コマンド: pull / push / 同期（pull → push）。競合は Modal へ
  cli/pull.ts           副コマンド化（pull / push / sync / record / delete / rename / resolve）
```

- **同期状態**（`data.json` `syncState`。第 1 段の 5 項目に加えて）: `vaultPath`（サーバ値）・`title`・
  `serverDeleted?`。第 1 段の state（`vaultPath` 無し）はそのまま読める（無ければサーバ値を正として
  第 1 段と同じ挙動）。
- **journal**（`data.json` `journal`）: `edits[localPath][] = {at, content}` / `deleted[localPath]` /
  `movedOut[localPath]` / `renamed[newPath] = oldPath`。トークンは持たない（変えない）。
- **push の計画規則**（詳細はテストが固定）:

| ローカル（同期フォルダ配下の `.md`） | 追跡状態 | journal | 判定 |
| --- | --- | --- | --- |
| 在る | 未追跡 | 任意 | `create`（edits = journal の編集列 ＋ 最後と違えば現在の内容） |
| 在る | 追跡済み・内容が最終同期時と同じ | 編集なし | `unchanged` |
| 在る | 追跡済み | 編集あり or 内容が変わった | `update`（`baseVersion` = state.version） |
| 無い | 追跡済み | `deleted` | `delete`（論理削除を送る） |
| 無い | 追跡済み | `movedOut` | `untrack`（同期停止。削除は送らない） |
| 無い | 追跡済み | 記録なし | `missing-local`（報告のみ。削除は送らない） |
| 在る（新パス） | 追跡済み（旧パス） | `renamed[new]=old` | `rename-local`（紐付けだけ更新。サーバへ未伝播を報告）→ 続けて内容で判定 |
| 任意 | `serverDeleted` | 任意 | `server-deleted`（競合として提示。ローカルが無ければ state から外すだけ） |
| — | 追跡済みだが同期フォルダの外 | — | `untrack`（設定変更で外れた） |

- **409 の 3 択**（`conflictResolver.ts`）: `local` = `pull(noteId)` でサーバ版を読み `baseVersion` に積んで
  再 push（ローカルの編集列を送る）／`server` = `pull` の本文でローカルを上書きし journal を捨てる／
  `both` = ローカルの内容を `<名前> (ローカル YYYYMMDD-HHmm).md` に書いて新規 push し、元のパスはサーバの
  本文で上書き。**利用者が選ぶまでどれも実行しない**（保留可）。

## 受け入れ基準（issue の写像）

- [ ] Given オフラインで 10 回保存した資料 / When 同期する / Then サーバに 10 版が刻まれる（単体: `editJournal.test.ts` の畳み込み ＋ `pushSync.test.ts` の `edits.length === 10`。実測: `record` × 10 → `push` → manifest の `version` = 10）
- [ ] Given サーバ側が進んだ資料をローカルでも編集した / When push する / Then 409 を受けて 3 択を提示し、選ぶまで上書きしない（単体: `pushSync.test.ts` 409 → サーバ内容不変・journal 残存。変異試験: 自動再送に変えると落ちる。実測: 版ずれ push → 409 → `resolve local` → 200）
- [ ] Given 同期フォルダから外したファイル / When 同期する / Then サーバへ削除を送らない（単体: `pushPlanner.test.ts` `movedOut` → `untrack`。`deleted` → `delete` と対）
- [ ] Given Obsidian 側で削除したファイル / When 同期する / Then サーバ側は論理削除（90 日保管）になる（単体: `pushSync.test.ts`。実測: `delete` → 200 `{deletedAt, purgeAt}` → manifest `deleted=true`）
- [ ] Given サーバ側で削除・リネームされた資料 / When pull する / Then 削除はローカルを消さず state に残し、リネームはローカルを移動する（単体: `pullSync.test.ts`）
- [ ] Given プロトコル部 / When 単体テスト / Then Obsidian 実体なしで緑
- [ ] Given `src/` / When typecheck / lint / test / build / format:check / Then 成功。成果物の egress 走査 0 件
- [ ] Given `docs/` の第 1 段の記述 / When 本 PR / Then 追随している（§母集合の行）

## テスト方針

- 単体は `protocol/` に閉じ、Obsidian API に触れる層（`main.ts` / `conflictModal.ts` / `vaultFileStore.ts`）は薄く保つ。
- 🔴 否定形は陽性対照と対で置く: 「`movedOut` は削除を送らない」は「`deleted` は送る」と同じテストで。
  「409 で上書きしない」は「版が合えば 200 で進む」と対。
- 変異試験: `pushSync.ts` の 409 分岐を「`serverVersion` を `baseVersion` に積んで即再送」に書き換え、
  テストが落ちることを記録して戻す（§実測記録）。

## 計画書との差異

- 「1 編集」の定義（`ADR-0037` フォローアップ 5）は計画が実装設計へ委ねた論点であり、`IADR-0352` 決定 1 で
  **保存イベントの静穏窓（30 秒）**と決めた。裁定は要しない（計画が委ねている）。
- サーバ側削除のローカルへの伝播（フォローアップ 11 ②）は「**消さない・提示する**」と決めた
  （`IADR-0352` 決定 4）。決定 5（論理削除は復元できる）と矛盾しない向き。
- ローカル → サーバのリネームは契約が受けない。契約変更は別 issue で起票する（計画側の裁定ではなく
  実装契約の追加）。

## 未決事項（残件として報告書へ）

1. ローカル → サーバのリネーム伝播（サーバ契約に `vaultPath` の更新を足す）—— **#1176 に起票**
2. 自動同期（保存のたび／一定間隔）—— 後続
3. `IADR-0338` フォローアップ 1・3〜5 はそのまま

## 実測記録（2026-09-03）

### 単体・ゲート（`src/`。`pnpm --config.manage-package-manager-versions=false …`）

| 検査 | 結果 |
| --- | --- |
| `pnpm --filter @platform/obsidian-plugin run typecheck` | exit 0 |
| `pnpm exec eslint obsidian-plugin` | 0 errors |
| `pnpm exec vitest run obsidian-plugin` | **11 files / 67 tests passed**（第 1 段 32 → 67） |
| `pnpm exec prettier --check obsidian-plugin` | OK（`--write` 後） |
| `pnpm --filter @platform/obsidian-plugin run build` | `dist/main.js 64.3kb` / `dist/cli.mjs 44.1kb` / `manifest.json` |
| `node scripts/check-static-egress.js --require src/obsidian-plugin/dist` | OK（3 ファイル・外部オリジンからの取得なし） |
| `node scripts/check-{trace-blocks,doc-links,doc-type-vocabulary,plan-id-qualification,test-traceability,test-spec-coverage}.js` / `gen-knowledge-graph --check` | すべて OK（`git add -A` 後） |
| `node scripts/check-adr-numbering.js` | **欠番 IADR-0339〜0351 で fail**（先行 PR が採番済みで未着地。想定内。本 IADR は当初 0349 で起草し、オーケストレータの指示で **0352 へ改番**した） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | `check-nul-bytes` を含む前段はすべて ok。**`check-adr-numbering` の実ツリー検査（上の欠番）で停止** —— 同じ原因。着地時に develop の採番で解消する |
| **変異試験**: `pushSync.ts` の 409 `version_conflict` 分岐を「`serverVersion` を `baseVersion` に積んで即再送」に書き換え | `pushSync.test.ts` 1 件（409 で上書きしない）＋ `conflictResolver.test.ts` 4 件（競合が起きない前提が崩れる）＝ **5 件 fail**（`expected [] to have a length of 1`）。戻して 67 件緑 |

### 実 HTTP（稼働 k3s・Obsidian 本体なし・`dist/cli.mjs`。2026-09-03）

接続先は既存の `port-forward svc/document-service 18093:8080`（別セッションが張っていた同じ実体。Pod は
再起動していない）。同期トークンの発行は PR #1156 のオーケストレータの手順を踏襲: Keycloak Admin REST API
（`port-forward svc/keycloak 18080:8080`）で一時ユーザー `msp1153-probe`（`requiredActions` 空）と一時
direct-grant クライアント（`profile` scope）を作り、password grant の JWT で `POST /private-notes/devices`。
**Keycloak pod で `kcadm.sh` は exec していない。** 終了時に端末・資料（論理削除 → purge）・ユーザー・クライアントを
削除（残 0 を確認）。秘匿値は標準出力に出していない。`curl -k` は使っていない（http の loopback のみ）。

| # | 手順 | 結果 |
| --- | --- | --- |
| N1 | トークン未設定で `cli.mjs push` | exit **3**（HTTP を出さない） |
| N2 | `MSP_SYNC_TOKEN=not-a-real-token` で `pull` / `push`（未追跡ファイル 1 件あり） | いずれも **401 → exit 2**「Vault のファイルは変更していません」。状態ファイルは作られない（陰性） |
| P1 | `record` × 10（`MSP_EDIT_QUIET_MS=0`）→ `push` | `created: [個人資料/msp-1153/positive.md]` `versionsPushed: 10` → manifest **`version=10`**（決定 8） |
| P2 | 別端末相当の push（`baseVersion=10`）でサーバを v11 へ → ローカルを編集して `record` → `push` | **409** → `conflicts: [{cause: version, baseVersion: 10, serverVersion: 11, pendingEdits: 1}]`・exit 4。サーバ本文は **v11 のまま**（上書きしていない） |
| P3 | `resolve <path> local` | `pushed` version **12**・サーバ本文がローカルの内容に（決定 7 の「ローカル採用」） |
| P4 | `del.md` / `out.md` を新規 push → `delete del.md` → `rename out.md archive/out.md` → `push` | `deleted: [del.md]`・`untracked: [{out.md, moved-out}]`。manifest: **`del.md deleted=true`**（論理削除）／**`out.md deleted=false`**（削除を送っていない。決定 4・5） |
| P5 | 2 回目の run（前回の資料が残った owner）で `push` | `path-taken` の競合として報告し上書きしない（サーバに同パスの有効な資料が在る）。cleanup 後の 3 回目は P1〜P4 どおり |

### 受け入れ基準の充足

- [x] オフライン 10 回保存 → 10 版（単体 P13・P14。実 HTTP P1）
- [x] 409 → 3 択を提示し、選ぶまで上書きしない（単体 P15・P19〜P22。変異試験。実 HTTP P2・P3）
- [x] 同期フォルダから外したファイルは削除を送らない（単体 P16。実 HTTP P4 `out.md deleted=false`）
- [x] Obsidian 側の削除は論理削除（単体 P16。実 HTTP P4 `del.md deleted=true`）
- [x] サーバ側削除はローカルを消さず、リネームは移動（単体 P23・P24）
- [x] プロトコル部が Obsidian なしで緑（67 件）
- [x] typecheck / lint / test / build / format / egress 走査
- [x] `docs/` の第 1 段の記述を追随（§母集合の 12 箇所。`src/obsidian-plugin` の 7 箇所も）
