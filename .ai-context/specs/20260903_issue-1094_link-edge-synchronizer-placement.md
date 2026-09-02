---
title: 作業仕様書 — LinkEdgeSynchronizer を GraphDocuments/Sync/ へ下ろす（#1094）
type: spec
status: done
related_ids:
  - NFR
  - FR-17
  - UC-10
  - ADR-0033
  - ADR-0065
  - ADR-0068
  - IADR-0261
  - IADR-0281
  - IADR-0282
  - IADR-0319
  - IADR-0334
  - IADR-0350
  - IADR-0351
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 2・5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・2・3
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md (Accepted) 決定 3・6
related_specs:
  - ./20260903_issue-1093_ai-suggestion-generator-placement.md
  - ./20260830_issue-1062_adr-0068-decision2-mcptool-contracts.md
  - ./20260828_issue-912_obsidian-link-extraction.md
---

# 作業仕様書: LinkEdgeSynchronizer の段（#1094）

起点: 実装 issue #1094（#1062 の着手時の母集合引き直しで新たに見つかった `ADR-0068` 決定 2 違反）。

**本 PR は #1093（`refactor/NFR-1093-ai-suggestion-generator-placement`）の上に積む** ——
同じサービス（GraphService）の同じ層を触るため衝突しやすい。基点は #1093 のコミット
`e7e77f8f`（その親が `origin/develop` `3d0a7048`）。`git rev-parse --is-shallow-repository` = **`false`**。

## 1. 母集合（着手時に自分で引き直した）

🔴 **#1094 本文の表（基点 `d3403107`）は転記しない。** 同じ走査を自分で回した。
**結論・内訳とも本文と一致した**が、一致したことも走査で確かめた。
分母の取り方は `IADR-0319` 決定 1（3 段目の操作フォルダからの実依存だけを数える）。

```console
$ grep -rn "LinkEdgeSynchronizer" src --include=*.cs
.../GraphService/Domain/ObsidianLinkParser.cs:8                                    … 散文コメント
.../GraphService/Features/GraphDocuments/LinkEdgeSynchronizer.cs:25,28             … 定義
.../GraphService/Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:38      … 散文コメント
.../GraphService/Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:49,84   … 実依存（注入・SyncResult）
.../GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs:19    … 散文コメント
.../GraphService/Program.cs:91                                                     … DI 登録
.../GraphService/Tests/Features/GraphDocuments/LinkEdgeSyncTests.cs:77             … テスト
.../GraphService/Tests/Features/GraphDocuments/Sync/GraphDocumentSyncConsumerTests.cs:35,36 … テスト
```

| 参照元 | 種別 | 操作として数えるか |
| --- | --- | --- |
| `Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:49,84` | 実依存（コンストラクタ注入・`SyncResult` の利用） | **数える（1 操作）** |
| `Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:38` | 同一ファイルの散文コメント | 数えない（重複） |
| `Program.cs:91`（`AddScoped<LinkEdgeSynchronizer>()`） | DI 登録 | 数えない |
| `Tests/Features/GraphDocuments/LinkEdgeSyncTests.cs` / `…/Sync/GraphDocumentSyncConsumerTests.cs` | テスト | 数えない |
| `Domain/ObsidianLinkParser.cs:8` / `Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs:19` | **散文コメント中の言及のみ**（依存していない） | 数えない |
| `Features/GraphDocuments/Delete/DocumentDeletedConsumer.cs` | **参照なし**（本文を読んで確認。`using` にも本文にも現れない） | — |

**使う操作は `GraphDocuments/Sync` の 1 つだけ → `ADR-0068` 決定 2 により 3 段目へ下ろす。**

### 🔴 陽性対照（走査が効いていることの証明）

同じサービスの `AiSuggestionEndpoints`（登録表）へ**同じ走査**を当てると **4 操作**が出る
（`Approve` / `Generate` / `List` / `Reject` の各 `Endpoint.cs`）。**同じサービス・同じ走査で
1 と 4 に割れる。**「参照が 1 つしか出ないのは走査が壊れているから」ではない。

### 層は動かさない（`IADR-0350` 決定 2・3）

`LinkEdgeSynchronizer` は `GraphDbContext` を受け取り `db.Edges` / `db.EdgeTypes` /
`db.Documents` を読み書きする。**`Domain/` へ置くと `Domain` → `Infrastructure` の using が
生まれ、`node scripts/check-unit-dependencies.js` 規則 3③ が止める**（`IADR-0282` 決定 2）。
`HttpClient` も持たないため `Infrastructure/ExternalServices/` の性質（ポート実装＋HTTP）も
持たない。**したがって層は `Features/` のままで、決定するのは段だけである。**

> 冒頭コメントが自称する「配置は合成ルート側（現 `Features/GraphDocuments/`）である」
> （`IADR-0281` 決定・段 2 待ち）の理由は**依存の向き**であり、**段の話ではない。**
> 段を下げても層は動かないので、この理由は本 PR で失効しない。**言い直しだけを追随させる。**

## 2. 作業（純粋な移送。`ADR-0068` 決定 5）

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `git mv Features/GraphDocuments/LinkEdgeSynchronizer.cs Features/GraphDocuments/Sync/` | rename として残す |
| 2 | 同ファイルの `namespace` | `GraphService.Features.GraphDocuments` → `…GraphDocuments.Sync`（`IADR-0261` の `<Svc>Service.*` 規約は維持） |
| 3 | 同ファイルの冒頭コメント | 現在地の言い直し（「現 `Features/GraphDocuments/`」）だけを追随。**依存の向きの説明は変えない** |
| 4 | `Program.cs` | `using GraphService.Features.GraphDocuments;` が他で要るかを確認したうえで整理。**DI 登録の内容（ライフタイム・登録型）は変えない** |
| 5 | `git mv Tests/Features/GraphDocuments/LinkEdgeSyncTests.cs Tests/Features/GraphDocuments/Sync/` | `IADR-0334` 決定 3（型を直接 `new` するテストは、その型が定義されたディレクトリへ） |
| 6 | テスト 2 件の `namespace` / `using` | `LinkEdgeSyncTests` は `namespace …Tests.Features.GraphDocuments.Sync;` へ。`GraphDocumentSyncConsumerTests` は `using GraphService.Features.GraphDocuments;` が不要になれば落とす（**足さない**。C# の外側名前空間探索で解決する。`IADR-0334` 決定 5） |

**触らないもの**: `Domain/ObsidianLinkParser.cs` と `Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs`
の散文（#1094 本文の指示どおり。パスを書いていない）、`IADR-0281` の凍結記録
（`.ai-context/adr/` の確定済み記録は書き換えない）、リンク辺の差分更新の規則そのもの、
GraphService の他クラスの段（射程外）。

## 3. 受け入れ基準

1. **`Features/GraphDocuments/` 直下に `.cs` が 0 件になる**（`Delete/` と `Sync/` の 2 フォルダだけが残る）
2. `dotnet build src/knowledge/backend/backend.slnx` が**新規警告なく**通る
3. **テスト件数が移送前後でプロジェクト単位（skip 込み）で完全に一致する** ——
   #1093 着地後の基点で `GraphService.Tests` = **失敗 0 / 合格 279 / スキップ 0 / 合計 279**
   （#1094 本文の 275 は基点 `d3403107` の値であり、develop が進んでいる）
4. `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が通る
5. `node scripts/check-unit-dependencies.js` / **`check-event-topology.js`**（`GraphDocumentSyncConsumer`
   は購読の宣言元。`scripts/event-topology-baseline.json` にパス結合が無いことを併せて確認する）/
   `check-test-traceability.js` / `check-doc-links.js` / `check-trace-blocks.js` が通る
6. 判断が `IADR-0351` に残る
