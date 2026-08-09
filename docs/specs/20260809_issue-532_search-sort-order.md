---
title: SC-02 検索の並び順を 2 値（関連度〔既定〕／更新日時の新しい順）にする
type: spec
status: done
related_ids: [FR-03, UC-01, SC-02, IADR-0131, IADR-0149, IADR-0150]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0150_search-sort-after-retrieval.md"
  - "../adr/IADR-0149_search-result-updated-at-indexing.md"
  - "../adr/IADR-0131_openapi-as-bff-contract-source.md"
  - "../screens/SC-02_search-results.md"
  - "../functional/FR-03_hybrid-search.md"
  - "../tests/FR-03_hybrid-search.md"
  - ./20260809_issue-536_search-result-updated-at.md
---

# 仕様書: 検索の並び順を 2 値にする（#532）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-03**（ハイブリッド検索）
- ユースケース（UC）: **UC-01**（横断検索）
- 画面（SC）: **SC-02 検索結果一覧**
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md)
  §SC-02 §主要素「**並び順（関連度〔既定〕｜更新日時の新しい順）**」・§SC-02 裁定 **Q5**）
- 関連 ADR:
  **[IADR-0150](../adr/IADR-0150_search-sort-after-retrieval.md)（本作業の判断記録）**／
  [IADR-0149](../adr/IADR-0149_search-result-updated-at-indexing.md)（更新日時の索引表現。**本作業が使う側**）／
  [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5（値集合を `enum` にしない）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: **#532**（親 #454）。**先行 #536 が索引項目を載せた**／**先例 #531**（検索モード 3 値）

## 射程（#531 と同じ切り方）

**契約（`SearchRequest`）と `HybridSearchService` への配線までを射程とする。**
#531（検索モード）の issue 本文が「**無いのは呼び出し側から指定する経路（`SearchRequest` のパラメータと
`HybridSearchService` への配線）だけ**」と切ったのと同じ形である。

**切替 UI は射程外。** SC-02 のモックでは「並び: 関連度 ▾」と「キーワード｜意味 ⇄」が**同じツールバー行**に
並ぶため、**2 つまとめて 1 つの画面実装として扱うのが自然**である（[[IADR-0150]] フォローアップ）。
本作業は SC-02 画面仕様書の §実装しない要素 (a) を「契約に無い」から「**切替 UI が未実装**」へ書き換える。

> **［注記］この区別は直前に自分で誤った箇所である。** #536（PR #631）で SC-02 画面仕様書へ
> 「検索モード切替は実装済み」と書いたが、#531 が解消したのは契約だけで画面は未実装だった
> （`62f019e` で訂正済み）。**「契約が揃った」と「画面が実装された」を混同しない。**

## 目的・背景

計画は裁定 **Q5**（2026-08-05）で並び順を **2 値**に確定した。

> **並び順は「関連度（既定）／更新日時の新しい順」の 2 値に確定する**（Q5。従前の「ほか」を具体化した）。
> 更新日時順は**「規程が改訂されたはずだが最新版はどれか」という基本動作**に要る。
> タイトル順・作成者順は採らない（…選択肢が増えると利用者が迷う）。必要になった時点で足す。

**「更新日時の新しい順」は #536 が索引へ載せた項目をソートキーに使う。** したがって本作業は #536 の後続であり、
#536 の完了を待って着手した（#532 のコメントに実測つきで記録済み・`86004bb` で着地）。

## 母集合（[[IADR-0141]] 決定 1）

「**並び順の指定が通る経路**」と「**既存の値集合の作法**」の 2 軸で引いた。
**拡張子で絞らず、パスから引いた**（追跡下の全ファイル。`planning/` と `src/ai-stock-trading` は除く）。

```console
$ git grep -ln "SearchRequest" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
34                    # 軸 1: 検索要求を構築・受け取る側
$ git grep -ln "SearchModes" -- . ':!planning' ':!src/ai-stock-trading'
                      # 軸 2: #531 が確立した値集合の作法（本作業が揃える先）
```

### 軸 1: 並び順の指定が通る経路

| 層 | ファイル | 本作業での扱い |
| --- | --- | --- |
| 契約 | `Knowledge.Contracts/Dtos/SearchDto.cs`（`SearchRequest` / **`SearchSorts` を新設**） | **`SortBy` を既定値つきで追加** |
| 検索（配線） | `RetrievalService.../Foundation/Services/HybridSearchService.cs` | **取得後に並べ替える**（[[IADR-0150]] 決定 1・3・4） |
| BFF | `Platform.Bff`（`/bff/search`） | 型付き中継。**実装変更なし**・**透過をテストで固定する** |
| 契約書 | `docs/api/openapi.yaml` ＋ orval 生成物 | **追随**（`pnpm run codegen` を必ず再実行する） |
| 画面 | `sc02-results/` | **触らない**（§射程） |

### 軸 2: 値集合の作法（#531 の先例に揃える）

`SearchModes`（`SearchDto.cs`）が **`const` 文字列 ＋ `All` ＋ `IsValid` ＋ `Normalize`** の形を確立している。
**`SearchSorts` は同じ形にする** —— 同じ種類のものが 2 つの書き方で並ぶのを避ける。

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| **SC-02 の切替 UI** | §射程 のとおり。#531 と同じ切り方であり、モード切替と**まとめて**画面実装にするのが自然 |
| `updated_at` のペイロードインデックス | **[[IADR-0150]] 決定 5 で不要と判断した。**[[IADR-0149]] のフォローアップは「ストア側で並べる」前提だったが、決定 1 で取得後に並べるため使われない |
| `CitationDto` / SC-01（AI 回答） | 計画は**検索結果一覧**（SC-02）に並び順を求めており、AI 回答の出典には求めていない |
| `AiAnalysisService`（RAG の内部検索） | 同上。RAG が文脈へ入れる根拠の順序は利用者の表示順ではない |
| `src/ai-stock-trading` | 別プロジェクトの submodule |
| `planning/` | 本作業では pin を動かさない（#628 で `31a69c9` へ前進済み・レンジ引き直し済み） |

## 実装方針

### 1. 契約

```csharp
public record SearchRequest(
    …,
    string? Mode = null,
    // FR-03, SC-02（Q5 / #532）: 並び順。2 値（relevance〔既定〕/ updated）。
    string? SortBy = null);

public static class SearchSorts
{
    public const string Relevance = "relevance";
    public const string Updated = "updated";
    public static readonly string[] All = [Relevance, Updated];
    public static bool IsValid(string? sort) => …;
    public static string Normalize(string? sort) => IsValid(sort) ? sort!.ToLowerInvariant() : Relevance;
}
```

**`enum` にしない**（[[IADR-0131]] 決定 5）。**未知値・未指定は既定へ縮退する** ＝ 旧クライアントは従来どおり。

### 2. 検索（`HybridSearchService`）

- **`relevance`（既定）**: 現行の振る舞いを一切変えない。
- **`updated`**: 系統に依らず `candidateK`（＝ `max(topK * 4, topK)`）件を取得し、
  **更新日時の降順・`null` 末尾・同着は元の順序を保つ安定ソート**で並べ替えてから `topK` 件を返す。
  **単系統（`keyword` / `semantic`）でも候補を広げる**（[[IADR-0150]] 決定 3）。

### 3. 追随させる文書

- `docs/api/openapi.yaml`（`SearchRequest` スキーマ）＋ **orval 再生成**
- `docs/screens/SC-02_search-results.md`（§実装しない要素 (a) を「契約に無い」→「**切替 UI が未実装**」へ・
  §hi-fi 対応 表 7 行目・§未決事項 1）
- `docs/functional/FR-03_hybrid-search.md`・`docs/tests/FR-03_hybrid-search.md`
- `docs/adr/IADR-0150_*.md`（**新設**）＋ `docs/adr/README.md`
- **[[IADR-0149]] のフォローアップに追記**（ペイロードインデックスは不要と判断した旨。**前提が変わった**）

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 既定（未指定）は関連度順のまま | `HybridSearchServiceTests` |
| 2 | 未知値は既定へ縮退する | 同上（`SearchSorts.Normalize` の単体も） |
| 3 | `updated` は更新日時の降順になる | 同上 |
| 4 | **日時を持たないチャンクは末尾**（`MinValue` 扱いにしない） | 同上 |
| 5 | **同着は元の順序（関連度）を保つ**（安定ソート） | 同上 |
| 6 | `updated` のとき候補を `candidateK` まで広げる（単系統でも） | 同上（`RecordingVectorStore.LastVectorTopK` / `LastKeywordTopK` を見る） |
| 7 | `updated` でも返すのは `topK` 件 | 同上 |
| 8 | BFF が `sortBy` を後段へ渡す | `BffSearchEndpointTests` |

## 実装中に決めたこと（仕様書からの差分）

**BFF は並び順を正規化しない。** 縮退（未知値 → 既定）は `HybridSearchService` の 1 か所だけで行い、
BFF は利用者の指定をそのまま後段へ渡す。**2 か所で正規化すると規則が割れる**——BFF が先に既定へ倒すと、
後段の `Normalize` は永遠に既定値しか見なくなり、規則を変えたときにどちらが効くのか分からなくなる。
`BffSearchEndpointTests.PostSearch_ForwardsSortByToDownstream` が `unknown-sort` を含む 3 値で固定している。

## 検証記録（実測・すべて本作業の head で走らせた）

`node scripts/…` は**リポジトリのルートから実行する**。

| 対象 | 結果 |
| --- | --- |
| `dotnet test knowledge/backend/backend.slnx` | **450 passed / 0 failed**（18 skipped は統合テストの環境依存。**本作業で 10 件追加**） |
| `dotnet test platform/backend/backend.slnx` | **365 passed / 0 failed**（1 skipped。**本作業で 3 件追加**） |
| `dotnet format --verify-no-changes`（両ユニット） | OK |
| `pnpm typecheck` / `lint` / `format:check` | OK（lint は warning 9・error 0。既存の `react-refresh` 警告） |
| `pnpm test:coverage` | statements **96.39%** / branches **90.53%** / functions **91.68%** / lines **96.39%**（床 90 / 85 / 88 / 90。**割っていない**） |
| `pnpm build` ＋ `check-static-egress` | OK（24 ファイル・外部オリジン 0） |
| `check-chunk-budget` | **床は動かない**（578.15 kB のまま・遅延チャンク 6 本のまま）。**画面を触っていないので当然である** |
| `check-contract-schema` | **baseline を更新**（`SearchRequest.SortBy` の追加 ＋ `SearchSorts` 型の追加。**破壊的 0 件**） |
| `check-test-spec-coverage` | **床は動かない**（78 対のまま）。**既存のテストクラスへ足しただけ**だからである |
| その他 | `check-i18n-catalogs` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` / `check-test-traceability` / `check-bff-downstreams` / `check-unit-dependencies` / `check-backend-libraries` すべて OK |

**カバレッジ床は上げない**（#628・#536 と同じ判断）。**i18n カタログも動かない**——画面を触っていないので
新しい表示文言が無い。
