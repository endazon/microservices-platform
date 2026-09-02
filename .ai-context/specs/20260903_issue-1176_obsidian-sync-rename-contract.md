---
title: 作業仕様書 — Obsidian 同期契約にローカル → サーバのリネーム（vaultPath の更新）の口を足す（#1176）
type: spec
status: in-progress
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-19
  - SC-20
  - ADR-0037
  - IADR-0270
  - IADR-0338
  - IADR-0352
  - IADR-0353
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 仕様書: issue #1176 — 同期契約にリネーム（`vaultPath` の更新）の口を足す

> #1153 第 2 段（PR #1177・`IADR-0352`）の積み残し。第 2 段は**契約を変えない**という宣言ファイル領域の
> 制約の下にあり、その帰結として `IADR-0352` 決定 5 は「ローカル → サーバのリネームは紐付けだけ更新して
> **伝播しない**（契約に口が無い）」と決めた。本 issue はその制約を外し、**契約に口を足す**。
> `IADR-0352` の他の決定（1〜4・6）は覆さない。決定 5 の後半だけを `IADR-0353` が改定する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（双方向同期）。前提 FR-19
- ユースケース（UC）: UC-11
- 画面（SC）: SC-19（`vaultPath` は個人資料管理画面の表示項目）/ SC-20（本 PR は画面を触らない）
- 関連 ADR: `ADR-0037` 決定 2（双方向）・7（競合はサーバで自動解決しない）・9（監査は「誰が・いつ・何件」。
  タイトル・内容を記録しない）・14（KB が唯一の正）
- 実装 IADR: `IADR-0270`（サーバ側中核）/ `IADR-0338`（プラグイン第 1 段）/ `IADR-0352`（第 2 段）/
  `IADR-0353`（本作業。決定 5 後半の改定）

## 着手条件の確認（実測 2026-09-03・`feat/FR-20-obsidian-plugin-stage2` `f9d229af`）

1. `ADR-0037` は `Accepted`・「着手可否の注記」に留保は無い（#1153 と同じ）。**着手可**。
2. PR #1177 は **develop に未着地**。本ブランチは `origin/feat/FR-20-obsidian-plugin-stage2` から切る。
   PR は `develop` 宛てで、#1177 の後に着地させる。
3. `ADR-0037` はリネームを名指ししていない（`git grep` で ADR-0037 本文に「リネーム」0 件 /
   陽性対照「決定 14」6 件）。**計画は実装設計へ委ねている**ため、planning への裁定依頼は要らない。

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 9・10）

🔴 issue 本文の数え（#1153 の走査由来）を転記せず、`f9d229af` で引き直した。

| 走査 | コマンド | 結果 | 含意 |
| --- | --- | --- | --- |
| `Features/ObsidianSync/` のファイル | `find … -type f \| wc -l` | 8（Manifest 2・Pull 2・Push 2・Delete 1・合成点 1） | 口は 4 つ。ここへ 5 つ目（Move）を足す |
| リネームの口の不在（陰性） | `grep -rn "/move" Features/` | **0 件** | 口は無い |
| 同・陽性対照 | `grep -rn "notes/{id:guid}/delete" Features/` | 1 件 | 走査が機能している（「0 件」が走査の失敗ではない） |
| `PrivateNote` の `VaultPath` 変更手段 | `Domain/PrivateNote.cs` を通読 | `Create` で設定、以後 setter 無し。`RecordBody` はバイト数・ハッシュ・時刻だけ | ドメインに変更手段を足す（`MoveTo`） |
| パス一意性の実装 | `grep -rn "ActivePathExistsAsync\|PathConflictProblem"` | 定義 2・利用 2（`PrivateNotes/Create` と `ObsidianSync/Push` の新規経路） | **再利用する**（重複を作らない）。応答は 409 `vault_path_conflict` |
| `renamedLocally` の消費 | `grep -rn renamedLocally src/obsidian-plugin/src` | 6 件（型 1・初期化 1・追加 1・テスト 2・`main.ts` の通知 1） | 全件が「伝播しない」前提。追随する |
| 「伝播しない／口が無い」の記述（FR-20 文脈） | `grep -rn "伝播しない\|口が無い\|名前は変わりません"` を `docs/` `src/obsidian-plugin/` `.ai-context/` へ | `docs/api` 1・`docs/functional` 2・`docs/how-to` 1・`src/obsidian-plugin` 4（`pushSync.ts` 1・`pushSync.test.ts` 1・`pushPlanner.ts` 1・`main.ts` 1）・`.ai-context/adr` 5（`IADR-0352` 4・索引 1） | `docs/` と `src/` は追随。**`.ai-context/adr/IADR-0352` は確定済み記録であり書き換えない**（索引 1 行にだけ後継 ID を併記する。#580 の書式） |
| 同期の口を数え上げる記述 | `grep -rn "manifest / pull / push / delete\|manifest / push / pull / delete"` | `docs/api` 1・`docs/tests` 1・`syncClient.ts` 1 | 「4 つ」→「5 つ」へ追随 |
| `openapi.yaml` の `/private-notes/sync` | `grep -n "private-notes/sync" docs/api/openapi.yaml` | **1 件（コメントのみ。パス定義は 0）** | openapi は `/bff/*` だけを持ち、同期プロトコルは載っていない。**openapi.yaml は変えない**（生成対象外） |
| 契約スキーマ baseline | `grep -n "PushNoteRequest\|SyncManifestEntry" scripts/contract-schema-baseline.json` | 0 件 | `Features/ObsidianSync/*/Command.cs` は baseline の対象外。**baseline の更新は不要**（検査で最終確認する） |
| 監査アクション名の文書化 | `grep -rn "private-note.sync" docs/` | **0 件**（陽性対照: `src/` に 10 件） | 監査アクション名を追随させる文書は無い |

### 対象外（本 PR で触らない）

- **BFF に経路を足さない** —— 同期プロトコル群は資格情報が別系統で SPA は呼ばない
  （`docs/api/FR-20_obsidian-sync.md` の群表）。エッジ公開は #1154 の射程。
- **`Push` の既存の形は変えない** —— `PushNoteRequest.VaultPath` は新規作成でのみ使う、という
  現在の意味論を保つ（更新経路で `VaultPath` を書き換え始めると、旧クライアントが送る「古い
  `vaultPath`」でサーバ側リネームを巻き戻す事故が起きる。`IADR-0353` 決定 1）。
- 画面（SC-19 / SC-20）・通知サービス・自動同期。

## 決めること（詳細は `IADR-0353`）

1. 契約の形 —— 更新 push の拡張か、独立した口か
2. 楽観ロックの単位 —— リネームで版を進めるか
3. プラグイン側の伝播の位置 —— push の一巡のどこで送るか。失敗したときの状態
4. 監査 —— アクション名と、`vaultPath` を記録しないこと

## 実装範囲

### サーバ（`src/knowledge/backend/Services/DocumentService/`）

| 変更 | 内容 |
| --- | --- |
| `Domain/PrivateNote.cs` | `MoveTo(string vaultPath, DateTimeOffset now)` を足す（`VaultPath` と `UpdatedAt` だけを動かす） |
| `Features/ObsidianSync/Move/Command.cs` | `MoveNoteRequest(string VaultPath, int Version)` / `MoveNoteResponse(Guid NoteId, string VaultPath, int Version, DateTimeOffset UpdatedAt)` |
| `Features/ObsidianSync/Move/Endpoint.cs` | `POST /private-notes/sync/notes/{id:guid}/move` |
| `Features/ObsidianSync/ObsidianSyncEndpoints.cs` | 合成点へ 1 行足す |
| `Tests/Features/ObsidianSync/Move/ObsidianSyncMoveTests.cs` | 陽性・陰性を対で置く |

応答の規則（既存と同じ向きに揃える）:

| 状況 | 応答 |
| --- | --- |
| 成功 | 200 `MoveNoteResponse`。**版は進めない**（本文が変わっていないため） |
| 現在のパスと同じ | 200（冪等。何も書かない） |
| トークン欠落・不正・期限切れ・失効 | 401（区別しない） |
| 他人の資料・存在しない ID | **404**（存在秘匿。403 を返さない） |
| `vaultPath` が空 / `version` 欠落 | 400（`ValidationProblem`） |
| 版ずれ | 409 `{error:"version_conflict", serverVersion, serverUpdatedAt}`（push と同形） |
| 論理削除済み | 409 `{error:"deleted", purgeAt}`（push と同形） |
| 移動先が既存の有効な資料と重なる | 409 `{error:"vault_path_conflict", vaultPath}`（`PathConflictProblem` を再利用） |

### プラグイン（`src/obsidian-plugin/`）

| 変更 | 内容 |
| --- | --- |
| `protocol/types.ts` | `MoveNoteRequest` / `MoveNoteResponse` |
| `protocol/syncClient.ts` | `noteMovePath` / `move()`（409 の 3 形は既存の `parseConflict` を通る） |
| `protocol/pushPlanner.ts` | `rename-local` に `vaultPath`（新しいサーバパス）と `baseVersion` を載せる |
| `protocol/pushSync.ts` | `rename-local` で `move` を送る。成功なら状態の `localPath` と `vaultPath` を進め、409 は**再送せず**競合として報告する |
| `protocol/testFakes.ts` | `FakeServer` に move（楽観ロック・パス重複・削除済み）を足す |
| `obsidian/…` / `main.ts` | 通知文言を「伝播した」へ改める |
| `cli/pull.ts` | `move <from> <to>` 副コマンド（ローカルのリネーム記録 → push で伝播） |

### docs（表示テキストに計画 ID / IADR を書かず trace ブロックへ）

`docs/api/FR-20_obsidian-sync.md`・`docs/functional/FR-20_obsidian-sync.md`・
`docs/tests/FR-20_obsidian-sync.md`・`docs/how-to/obsidian-plugin-install.md`・
`docs/data/private-note.md`・`.ai-context/adr/README.md`（索引）。

## 受け入れ基準（Given-When-Then）

- [ ] A1 Given Obsidian 側でリネームしたファイル / When 同期する / Then manifest の `vaultPath` が
      新しい名前になり、**版履歴は保たれる**（版が増えも減りもしない）
- [ ] A2 Given 既存の有効な資料と同じ名前へのリネーム / When 送る / Then 409
      `vault_path_conflict` で**上書きしない**（相手の資料も自分の資料も動かない）
- [ ] A3 Given サーバ側が進んだ資料 / When 古い版でリネームを送る / Then 409 `version_conflict` で
      パスは変わらず、プラグインは**再送しない**
- [ ] A4 Given 他人の資料の ID / When リネームを送る / Then 404（存在秘匿）。陽性対照として
      本人の同じ操作が 200 であることを対で置く
- [ ] A5 Given 論理削除済みの資料 / When リネームを送る / Then 409 `deleted`
- [ ] A6 Given リネームの監査記録 / When 監査ログを読む / Then 「誰が・いつ・何件」だけがあり、
      **`vaultPath`（＝実質的な題名）を含まない**
- [ ] A7 Given プロトコル部 / When 単体テスト / Then Obsidian 実体なしで緑。
      **変異試験**: `pushSync` の move の版チェック（`baseVersion` の送出）を外すと落ちる

## 実測記録

### 1. 配備済みビルドに口が無いこと（2026-09-03・稼働 k3s・`kubectl port-forward svc/document-service 18093:8080`）

**陽性対照と対で取った** —— 「404 だった」を「口が無い」と読むには、同じ経路の既存の口が
別のコードを返すことを見せる必要がある（`curl -k` は使っていない。port-forward は http）。

```console
$ curl -s -o /dev/null -w "delete=%{http_code}\n" -X POST -H "Content-Type: application/json" -d "{}" \
    "http://127.0.0.1:18093/private-notes/sync/notes/00000000-0000-0000-0000-000000000000/delete"
delete=401
$ curl -s -o /dev/null -w "move=%{http_code}\n" -X POST -H "Content-Type: application/json" -d "{}" \
    "http://127.0.0.1:18093/private-notes/sync/notes/00000000-0000-0000-0000-000000000000/move"
move=404
```

`delete` は **401**（経路が在り、同期トークンの検証で落ちている）、`move` は **404**（経路そのものが無い）。
issue #1176 の主張（契約にリネームの口が無い）を、走査だけでなく稼働中のビルドで確かめた。

- 適用済み migration の突合（`kubectl -n platform-infra exec <postgres pod> -- psql -U kp -d document_svc`）:
  8 件で本ブランチの migration 一覧と一致。**本 PR は migration を足さない**（`VaultPath` 列は既存）。

### 2. 🔴 新しい口の実 HTTP 測定は**実施できていない**（権限の壁。未了）

配備済みの pod は当然この口を持たないため、新しい口を実 HTTP で測るには**本ブランチのビルドを
ローカルで起動**する必要がある。DocumentService は `ConnectionStrings__DefaultConnection` 未設定で
**fail-fast する**（`Program.cs:43`。in-memory への退避経路は無い）ため、Postgres／MinIO／RabbitMQ が要る。
次の 2 経路をいずれも試み、**どちらも実行許可の分類器に拒否された**。

| 試みたこと | 目的 | 結果 |
| --- | --- | --- |
| `kubectl get secret … -o jsonpath='{.data}'`（`postgres-app` / `minio-credentials` / `rabbitmq-app` / `keycloak-admin` の**キー名の列挙**） | port-forward した依存へローカル起動のサービスを繋ぐ | **拒否**（Blocked by classifier） |
| `nerdctl images`（使い捨ての Postgres / MinIO を自前の資格情報で起こす） | クラスタの秘密に触れずに依存を用意する | **拒否**（Blocked by classifier） |

**回避策を探して分類器をすり抜けることはしていない。** したがって本 PR が持つ新しい口の証跡は
**サーバ側 xUnit 6 件**（実 ASP.NET パイプライン・実ルーティング・実シリアライズ。ソケットではない）と
**プラグイン側 Vitest 3 件＋変異試験**であり、稼働クラスタでの往復は**未了**である。
必要な許可（k8s secret の読み取り、または使い捨てコンテナの起動）が与えられれば、次の 3 点を測る:

1. 陽性: push で作った資料を `move` → manifest の `vaultPath` が新名・`version` 据え置き
2. 陰性: 古い版で `move` → 409 `version_conflict`・名前は動かない
3. 陰性: 他人の資料 ID で `move` → 404（陽性対照として本人の同じ操作が 200）

### 3. 変異試験（2026-09-03 実測）

`pushSync.ts` の `rename-local` の 409 分岐を「`serverVersion` を積んで即再送」に書き換えると、
`pushSync.test.ts` の **`サーバが進んでいれば move は 409 になり、名前も中身も送り直さない` 1 件が落ちる**
（70 件中 1 件 fail / 69 件 pass）。戻して 70 件緑。楽観ロックを外す変異を検出できている。

## 完了の定義

- `dotnet build` / `dotnet test`（knowledge ユニット）/ `dotnet format --verify-no-changes` が緑
- `pnpm run typecheck` / `lint` / `format:check` / `vitest run` / プラグインの `build` が緑
- `node scripts/check-static-egress.js --require src/obsidian-plugin/dist` が緑
- `check-contract-schema.js` / `check-openapi-dto-drift.js` / `check-doc-links.js` /
  `check-trace-blocks.js` / `check-doc-updated.js` が緑
- 稼働 k3s の DocumentService に対する実 HTTP の証跡（陽性・陰性を対で）が本書に載っている
