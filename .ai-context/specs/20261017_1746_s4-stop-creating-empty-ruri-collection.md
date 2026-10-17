---
title: 取り込みが Ruri のコレクション（knowledge_chunks_ruri_v3）を既定で作るのをやめ、セルフホストの opt-in を有効にしたときだけ作る（#1746 段 S4 の片付け）
type: spec
status: done
related_ids: [FR-02, FR-03, ADR-0127, ADR-0017, ADR-0016, ADR-0092, ADR-0013, IADR-0085, IADR-0497, IADR-0467, IADR-0313, IADR-0025]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 1・5、§既存 IADR の扱い）
issue: "#1746"
---

# 仕様書: 取り込みが空の Ruri のコレクションを作るのをやめる（#1746 段 S4 の片付け）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。計画は project-planning の `main`（`55e464a`）の ADR-0127 を読んだ。
> 実装の基点は MSP `origin/develop` `01c3dcc2`。判断の記録は IADR-0085 の日付つき追記に置く（新しい IADR は起こさない。下の「判断」）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取り込み）・FR-03（横断検索）
- 関連 ADR: ADR-0127（決定 1: 高機密は埋め込まず語彙索引だけ／決定 5: ADR-0017 を置き換え、セルフホストを後付けできる構成は閉じない。配備物は opt-in のまま既定で無効）・ADR-0017（Superseded by ADR-0127）・ADR-0013（後付けできるハイブリッド構成）・ADR-0092 決定 1（束ねる）
- 関連 IADR: IADR-0497（§残るもの「取り込みは Ruri のコレクションを今も作る（空）。片付けは段 S4」）・IADR-0085（Ruri の opt-in 配備物）・IADR-0467（束ねる）・IADR-0313（決定的ローカル埋め込みの index 2）

## 目的・背景

取り込みサービスは `appsettings.json` の `Embedding:Collections` の全件を起動時に作る（`QdrantBootstrapHostedService`）。
既定の 2 件目が Ruri のコレクション（`knowledge_chunks_ruri_v3`・768 次元）であるため、**セルフホストを有効にしていない配備（稼働 PoC を含む）でも空の
コレクションが作られ、全文・2-gram・キーワードの索引が張られ、文書の削除のたびに 1 回ずつ Qdrant を呼んでいる**。

ADR-0127 決定 5 は、セルフホストの配備物（IADR-0085）を **opt-in のまま既定で無効**として残すと決めた。したがって配備物そのもの（Helm の
`embedding.*`・`templates/embedding.yaml`・compose の `embedding` profile・ゲートウェイの `selfhosted-ruri` エンドポイント〔既定 `Enabled: false`〕・検索の束ね）は
**撤去しない**。撤去するのは、opt-in を有効にしていないのに作られる Ruri のコレクションだけである。

## 対象範囲

- 対象:
  - `IngestionService/appsettings.json` の `Embedding:Collections` から Ruri の行を外す。
  - opt-in の配線: Helm は `embedding.enabled=true` のとき取り込みへ `Embedding__Collections__1__*` を描画する。compose は検索と同じ `.env` の `SEARCH_FUSED_COLLECTION` を取り込みにも与える。
  - 取り込みは名前が空のコレクション定義を捨てる（compose の `${…:-}` が空文字で届くため。検索側 `ResolveFusedCollectionNames` と同じ作法）。
  - 試験（C# の束ね・空名の除外、`scripts/k8s-local-up.test.js` の配線の静的検査）。
  - 文書の追随（運用仕様書・テスト仕様書・`deploy/` の注記・`scripts/republish-document-updated.js` の点検コマンド）と、稼働 PoC に残る空のコレクションの後始末手順（運用仕様書。**削除はしない**）。
  - IADR-0085・IADR-0497 への日付つき追記。
- 対象外:
  - セルフホストの配備物の撤去（ADR-0127 決定 5 が残すと決めた）。
  - ゲートウェイの `selfhosted-ruri` エンドポイント定義・越境判定（opt-in の配備物の一部。既定で無効）。
  - 稼働 PoC の既存の空コレクションの削除（手順だけ書く。実行は運用者の判断）。
  - 段 S3（計測）・段 S5（有効化）。

## 母集合（規則 9・10）

### 引き方

誤りの側の文字列で追跡下の全ファイルを走査した（基点 `01c3dcc2`）。

```console
git grep -l -i 'ruri' -- ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md'   # 62 ファイル（spec 自身を含まない）
git grep -n 'knowledge_chunks_ruri_v3\|ruri_v3' -- ':!.ai-context'                              # 値としてのコレクション名
git grep -n 'Embedding__Collections\|SEARCH_FUSED_COLLECTION' -- deploy scripts docs perf     # 配線
```

### 是正する

| # | 箇所 | 現状 | 是正 |
| --- | --- | --- | --- |
| 1 | `src/knowledge/backend/Services/IngestionService/appsettings.json:8` | 既定で Ruri のコレクションを宣言 | 行を外す |
| 2 | `IngestionService/Program.cs:51`・`QdrantBootstrapHostedService.cs:21`・`Domain/Ports/EmbeddingCollectionsOptions.cs:3-5,22,25` | 「voyage/1024・ruri/768 を作る」 | 既定は voyage だけ・Ruri は opt-in のときだけと書き直す。空名の除外を足す |
| 3 | `IngestionService/Tests/Domain/Ports/EmbeddingCollectionsOptionsBindingTests.cs` | 「appsettings.json は voyage / ruri の 2 コレクションを宣言している」 | 実ファイルを読み、Ruri が既定に無いことを固定。添字の欠け（0 と 2）と空名の除外を足す |
| 4 | `deploy/helm/microservices-platform/templates/deployment.yaml:382`（決定的ローカル） | 「index 2 は appsettings.json の 2 件（voyage=0 / ruri=1）の後ろ」 | index 1 は opt-in の Ruri の位置（取り込みへ描画）と書き直す。Ruri の取り込みの配線を足す |
| 5 | `deploy/helm/microservices-platform/values.yaml:1239-1244` | 「IngestionService の Embedding:Collections と一致していなければならない」 | 取り込みへも描画すると書き直し、`embedding.dimensions: 768` を足す |
| 6 | `deploy/docker-compose.yml:503-505・786-790` | 有効化の手順に取り込みが無い | 取り込みへ `Embedding__Collections__1__*` を足し、手順に書く |
| 7 | `scripts/k8s-local-up.test.js:3510-3518` | 取り込みの `appsettings.json` に Ruri があることを要求 | 既定に無いこと・Helm と compose が opt-in と同じ条件で描くことを固定する |
| 8 | `scripts/republish-document-updated.js:830`・`docs/operations/operations.md:1362` | 点検の for 文が `knowledge_chunks_ruri_v3` も引く | 外す（opt-in のときだけ足すと注記） |
| 9 | `docs/operations/operations.md:1063（表）・1169-1176（配備物）・1260（再索引手順 1）` | 取り込みが Ruri を作る前提 | 日付つきの追記で改まったことを書く。後始末手順の節を足す |
| 10 | `docs/tests/FR-03_hybrid-search.md:149`（T-88） | 「取り込みの Ruri コレクション名と一致」 | 新しい行で取り込みの配線を足す（既存行は日付つきで注記） |
| 11 | IADR-0085（追記の末尾「残りは段 S4」）・IADR-0497（§残るもの 190 行） | 段 S4 の片付けが未了 | 日付つき追記で本 PR を指す |

### 是正しない（除外理由）

| 箇所 | 除外理由 |
| --- | --- |
| `.ai-context/specs/`・`.ai-context/superpowers/`・IADR の本文 | 凍結記録（追記ブロックだけ） |
| `src/platform/backend/Services/LlmGateway/**`（`appsettings.json` の `selfhosted-ruri`・`EmbeddingRouter.cs` 等）・`EmbedDto.cs` | opt-in の配備物そのもの（既定 `Enabled: false`）。ADR-0127 決定 5 が残すと決めた |
| `IngestionService/Tests/Infrastructure/**`・`RetrievalService/Tests/**`・`LlmGateway/Tests/**` の Ruri | 複数コレクションの振る舞いの試験の材料（options を直接組む）。既定の構成に依らない |
| `RetrievalService/Domain/FusedCollections.cs`・`QdrantVectorStore.cs` | 束ねる仕組み（既定は空）。今も正しい |
| `templates/embedding.yaml`・`values.yaml` の `embedding.*`（`collection` 以外）・compose の `embedding` サービス | opt-in の配備物。残す |
| `perf/ndcg/README.md`・`scripts/measure-search-ndcg.js` の Ruri | opt-in の A/B の参考（前の PR で注記済み）。取り込みの作成には触れていない |
| `docs/functional/FR-03_hybrid-search.md`・`docs/security/security.md`・`docs/screens/SC-11_configuration-viewer.md` の Ruri | 束ね・越境・画面の記述。取り込みがコレクションを作ることに依っていない |
| `scripts/verify-tool-oidc-logins.sh`・`scripts/scripts.repo.test.js:12217` | `ruri` は redirect URI の変数名。無関係 |
| `scripts/scripts.repo.test.js:4244-4269` | nDCG の集計試験の名札。無関係 |
| `ConversionService`・`DocumentService` の `ruri`（大小無視の一致） | 別語（`PlaceholderUri`・`overUri` の部分一致）。無関係 |
| `CHANGELOG.md` | 生成物 |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 決定的ローカル埋め込みの「index 2 は 2 件の後ろ」→ 添字は欠けてよい（.NET の配列の束ねは子の鍵を順に並べる。試験で固定）。index 1 は opt-in の Ruri が使う。
- `IngestionTimeouts.From(..., embeddingCollections.Count + 1)` の数は既定で 1 減る（最悪の所要時間が短くなる向き）。空名を捨てた後の数で数える。
- `QdrantKeywordIndexDiscoveryHostedService` 等の「全コレクション」は既定で Ruri を含まなくなる（意図どおり）。

## 判断

- **新しい IADR は起こさない。** ADR-0127 決定 5 と IADR-0085 の 2026-10-05 追記が既に「配備物は opt-in のまま残す」と決めており、本 PR はその帰結（既定では作らない）を実装する。
  配線の形（取り込みも同じ opt-in の条件で描く）は IADR-0085 の日付つき追記に残す。
- **compose は検索と同じ変数 `SEARCH_FUSED_COLLECTION` を取り込みにも与える。** 片方だけ与えると「索引できない」（取り込み側が無い）か「索引されるが検索されない」（検索側が無い）になる。1 つの値で両方を動かす。
- **空名の除外は取り込みの束ねの直後に 1 か所で行う**（`EmbeddingCollectionsOptions.DropUnnamed`）。`Configure` の後の `PostConfigure` と、起動時に数える `Get` の両方で使う。

## 受け入れ基準

1. 既定の構成（opt-in 無効）で、取り込みの `Embedding:Collections` に `knowledge_chunks_ruri_v3` が無い（試験: C# の束ね・`k8s-local-up.test.js`）。
2. Helm で `embedding.enabled=true` のとき、取り込みへ `Embedding__Collections__1__Name=<embedding.collection>`・`VectorSize=<embedding.dimensions>` が描かれ、既定では描かれない（`helm template` と静的検査）。
3. compose の取り込みは `SEARCH_FUSED_COLLECTION` を検索と同じ値で受け、空なら Ruri のコレクションを作らない（空名の除外の試験・静的検査）。
4. 添字が欠けた構成（0 と 2。決定的ローカル埋め込みだけ有効）でも 2 件が束ねられる（試験）。
5. 運用仕様書に、稼働 PoC に残る空の Ruri のコレクションの点検と後始末の手順がある（削除はしない。消すかは運用者の判断）。

## 検証

- `dotnet build` / `dotnet test`（knowledge の slnx。IngestionService の試験）・`dotnet format --verify-no-changes`
- `node scripts/k8s-local-up.test.js`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系検査
- `helm template`（既定と `--set embedding.enabled=true`）
