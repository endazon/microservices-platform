---
title: Wiki 同期のスループット —— Wiki.js 2.5 の書き込みの内訳を実測し、Wiki.js の今の状態と同じ内容の pages.update を送らない（#1971）
type: spec
status: done
related_ids: [FR-13, UC-07, ADR-0011, ADR-0027, IADR-0021, IADR-0541, IADR-0554, IADR-0557]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧・同期）
issue: "#1971"
---

# 仕様書: Wiki 同期のスループット（#1971）

## 起点となる計画書（トレーサビリティ）

- FR-13（Wiki 閲覧・同期）・UC-07。
- ADR-0011（Wiki.js。閲覧・編集の実体）・ADR-0027（Wolverine。fan-out・再試行・DLQ）。
- 実装判断の記録: [IADR-0557](../adr/IADR-0557_wiki-sync-skip-unchanged-writes.md)。同期の形は [IADR-0021](../adr/IADR-0021_wiki-js-sync-graphql-push.md)（GraphQL push・正準パス `doc/<id>`）。
- 起点: #1971（PoC・2026-10-16。`pages.create` が 1 件約 8.4 秒・毎分約 7 件。`wiki-service.DocumentUpdated` に約 4,800 件）。

## 1 件の内訳（実測。Wiki.js 2.5.316 の固定 digest をローカルの docker で起動）

条件: `ghcr.io/requarks/wiki:2.5@sha256:fffff288…`（compose と Helm が固定する digest）＋ `postgres:16-alpine`。`docker run --cpus=<c>`。
GraphQL の形は `WikiJsGraphQlClient` と同じ（`singleByPath` → `create` / `update`）。測定スクリプトは PR 本文に要約を載せる（リポジトリには置かない）。

| 操作 | ページ数 | CPU | 1 件の秒数 | 毎分 |
| --- | --- | --- | --- | --- |
| `singleByPath { id }` | 5,000 | 1 | 0.02 | — |
| `singleByPath { id title content render isPublished tags }`（今回の比較用の問い合わせ） | 5,000 | 1 | 0.014（中央値）/ 0.020（p95） | — |
| `pages.create` | 0 | 1 | 3.63 | 16.4 |
| `pages.create` | 5,000 | 1 | 4.94 | 12.1 |
| `pages.update`（同じ内容） | 0 | 1 | 2.50 | 23.9 |
| `pages.update`（同じ内容） | 5,000 | 1 | 2.58 | 23.0 |
| `pages.create` | 5,000 | 0.5 | **9.00** | **6.7** |
| `pages.update`（同じ内容） | 5,000 | 0.5 | 5.12 | 11.6 |
| `pages.create` | 5,000 | 2 | 4.68 | 12.8 |
| `pages.update`（同じ内容） | 5,000 | 2 | 2.10 | 28.4 |

- **CPU 0.5 で PoC の値（8.4 秒・毎分 7 件）を再現した。** PoC の Wiki.js（requests 100m・limits 1000m）は実効で約 0.5 CPU しか得ていないと読める。
- **CPU を 1 から 2 にしても 1 件の時間はほぼ変わらない**（create 4.94 → 4.68）。1 件の処理は単一スレッドの Node の子プロセスで直列に走る。

### なぜ遅いか（Wiki.js 2.5 の `server/models/pages.js`・`server/core/scheduler.js`・`server/jobs/rebuild-tree.js` を読んだ）

- `createPage` は `renderPage`（ジョブ `render-page`）と `rebuildTree`（ジョブ `rebuild-tree`）を待つ。どちらも `worker: true` のジョブで、
  **1 回ごとに `child_process.fork` で Node のプロセスを起こし、DB へ接続し直し、設定を読み直す**。
- `rebuild-tree` は**全ページ**を読み、`_.find(tree, …)` の線形探索で木を組み（ページ数の 2 乗）、`pageTree` を `truncate` してから 100 行ずつ入れ直す。
- `updatePage` は `renderPage` の 1 回だけ（木の再構築はパスの移動のときだけ）。代わりに `pageHistory` へ版を 1 行足す。
- ログの時刻（CPU 1・5,000 ページ・create 1 件 4.8 秒）: render の子プロセス 約 2.0 秒（起動と DB 接続 約 0.5 秒＋描画）、rebuild の子プロセス 約 2.0 秒。

### 並列にすると（Wolverine のリスナーの並列度を上げる案 1 の評価）

| 操作 | 並列 | 毎分 | 失敗 |
| --- | --- | --- | --- |
| `pages.create` | 1 | 12.1 | 0/5 |
| `pages.create` | 2 | 13.9 | **2/8** |
| `pages.create` | 4 | 15.4 | **6/8** |
| `pages.update`（同じ内容） | 1 | 23.0 | 0/5 |
| `pages.update`（同じ内容） | 2 | 25.8 | 0/8 |
| `pages.update`（同じ内容） | 4 | 26.2 | 0/8 |

- 失敗はすべて `Error when running job rebuild-tree: insert into "pageTree" … duplicate key`。**`rebuild-tree` はトランザクションを張らずに truncate → insert するので、
  並んだ 2 本が互いの行と衝突する。** 並列にしても毎分は 1 割ほどしか増えず（CPU が律速）、作成は壊れる。

## 決めたこと（詳細は IADR-0557）

1. **案 2 を採る（変わっていない文書を書かない）。比較の相手は wiki_svc の DB ではなく Wiki.js の今の状態とする。**
   `UpsertPageAsync` の最初の `singleByPath` で `id title content render isPublished tags { tag }` を取り、
   表題・本文（序数比較）・タグ（Wiki.js と同じ正規化〔trim・小文字・重複除去〕の集合）・公開状態（`isPublished=true`）が送る内容と同じで、
   描画（`render`）が空でなければ `pages.update` を送らない。往復は 1 回のまま（取る列が増えるだけ）。
   - 同じなら `update` は Wiki.js の状態を変えない（本文・表題・タグ・公開状態が同じ値で上書きされるだけ）。**同期の意味論（冪等な upsert）は変わらない。**
   - `isPrivate` は比べない。Wiki.js 2.5 の `updatePage` は `isPrivate` を書かない（`patch` の列に無い。従前の実測とも一致）ので、比べても `update` で直せない。
2. **案 1（並列度を上げる）は採らない**（上の実測。作成が壊れ、毎分も増えない）。
3. **案 3（Wiki.js の CPU）は本件では変えない**。CPU 0.5 → 1 で 1 件の時間は約半分になる（create 9.0 → 4.9 秒）が、配備先のノードの余力の判断が要る。報告に判断材料として残す。
4. **案 4（一括操作の fan-out を選べるようにする）は実装しない**。ADR-0027 手順 3（fan-out の契約）に触れるので計画の判断が要る。報告に残す。
5. **案 5（駆動スクリプトが Wiki のキューも見る）は本件では入れない**。量の制御を Wiki の速さへ合わせるかは運用の判断（索引だけを直す再発行が Wiki の速さで律速される）。報告に残す。

## 効果の見積り

- 再発行（`republish-document-updated.js`）の大半は「Wiki.js に既にあり、内容が変わっていない文書」である（issue の記述）。
  その 1 件は `pages.update`（CPU 0.5 で 5.1 秒・CPU 1 で 2.6 秒）から `singleByPath` 1 回（0.014 秒）＋ 本文の取得・DB の更新（従来どおり）になる。
- **作成（Wiki.js にページが無い）は速くならない。** Wiki.js 側の子プロセス 2 本と木の再構築が残る。作成の多い一括操作は案 3 か案 4 が要る。

## 母集合（規則 9・10）

- `UpsertPageAsync` の実装と呼び出し: `git grep -n "UpsertPageAsync" -- src` → 実装 1（`WikiJsGraphQlClient`）・呼び出し 1（`DocumentSyncConsumer`）・試験の代役 9。
  **ポートの署名は変えない**（戻り値を増やすと代役 9 箇所に波及し、呼び出し側は結果を使わない）。
- `singleByPath` の問い合わせ: 同じクラスの `GetPageIdByPathAsync`（削除）・`GetRenderedContentAsync`（閲覧）・`ArchivePageAsync`（アーカイブ）。**upsert だけを変える**（他は対象外）。
- 「upsert は毎回 update する」と書いた記述: `git grep -n "あれば pages.update\|singleByPath で既存を引き" -- src docs .ai-context/adr` → `WikiJsGraphQlClient.cs` の冒頭の注記（直す）。
  `.ai-context/adr/IADR-0021` は凍結記録なので直さない。

## 受け入れ基準と試験

| # | 基準 | 試験（`WikiJsGraphQlClientTests`） |
| --- | --- | --- |
| A1 | Wiki.js のページが送る内容と同じなら `update` を送らない（往復は `singleByPath` の 1 回だけ） | `UpsertPageAsync_SkipsUpdate_WhenWikiJsAlreadyHoldsTheSameContent` |
| A2 | タグは Wiki.js の正規化（trim・小文字・重複除去・順不同）で比べる | 同上の Theory（大小文字・順序・空白・重複の違い） |
| A3 | 表題・本文・タグ・公開状態のどれかが違う、または描画が空なら `update` を送る | `UpsertPageAsync_Updates_WhenAnyComparedFieldDiffers`（Theory） |
| A4 | `isPrivate` だけが違うときは `update` を送らない（Wiki.js の update は isPrivate を書かない） | `UpsertPageAsync_SkipsUpdate_WhenOnlyIsPrivateDiffers` |
| A5 | 未存在（6003）は従来どおり `create` | 既存 `UpsertPageAsync_CreatesPage_WhenSingleByPathReturnsPageNotFoundError` |
| A6 | 既存の恒久・一時の失敗の写像（#1966）は変わらない | 既存 `UpsertPageAsync_MapsValueTooLongToInputRejected`・`KeepsOtherFailuresTransient` |

- 変異: 比較を常に false（従来の挙動）にすると A1・A2・A4 が赤、常に true にすると A3 が赤になることを確かめる。
- 実機: ローカルの Wiki.js 2.5.316 に対し、本クライアントで create → 同じ内容の upsert → 本文を変えた upsert を行い、
  2 回目で `pageHistory` が増えず `render-page` のジョブが起きないこと、3 回目で増えることを確かめる（一時の試験で行い、コミットしない）。
