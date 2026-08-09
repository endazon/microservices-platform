---
title: SC-01 / SC-08 の権限内属性値の照会 API を新設する（ADR-0043 の制限を守る）
type: spec
status: done
related_ids: [FR-04, FR-05, UC-02, SC-01, SC-08, ADR-0043, IADR-0014, IADR-0139, IADR-0151]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0151_scoped-attribute-value-facets.md"
  - "../adr/IADR-0139_domain-bundled-contract-prs.md"
  - "../adr/IADR-0014_qdrant-attribute-payload-key.md"
  - "../api/BFF_bff-surface.md"
  - "../functional/FR-03_hybrid-search.md"
---

# 仕様書: 権限内属性値の照会 API を新設する（#540）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（対象範囲の指定）／**FR-05**（ABAC）
- ユースケース（UC）: **UC-02**（AI 分析を依頼する）
- 画面（SC）: **SC-01**（検索／チャット）・**SC-08**（AI 分析）の**対象範囲フィルタ**
  「**権限内のタグ／部門／プロジェクトのみ選択可**」
- **前提となる計画 ADR: [ADR-0043](../../planning/projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md)（`Accepted`・裁定 Q2）**
- 関連 ADR: **[IADR-0151](../adr/IADR-0151_scoped-attribute-value-facets.md)（本作業の判断記録）**／
  [IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md)（ペイロードの表現）／
  [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)（束の判定。**本作業で覆した**）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: **#540**（親 #454）。後続 **#542**（同じ口へ管理者スコープ）／**#539**（この口を使う画面側）

## 射程と、束ねの判定を覆したこと

**#540 を単独で実装する。** [[IADR-0139]] 決定 5 の表は #540 と #542 を「**束ねる**」と事前判定していたが、
**着手時の実測で条件 F（契約の追加に閉じる）を満たさないことが分かった。**

| 条件 | 判定 | 実測 |
| --- | --- | --- |
| **A. 同一資源** | ✅ **満たす** | ADR-0043 決定 4 が「読み取り口を 3 種類作らない。1 系統とし、スコープだけロールで変える」と定める。#540（一般利用者スコープ・件数なし）と #542（システム管理者スコープ・件数あり）は**同じ口の別スコープ**である。**事前判定は正しかった** |
| **F. 契約の追加に閉じる** | ❌ **満たさない**（#542 側） | #542 の (c)「改名時に既存文書を追随させる」は issue 本文自身が「**文書がタグの識別子を参照し表示名を複写しない**保持方式を前提とする」と書くが、**その前提が成り立っていない**（下記） |

```console
$ grep -n "Tags" src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Domain/Document.cs
16:    public List<string> Tags { get; private set; } = [];      # 表示名の文字列を複写して持つ
$ grep -n "Tags" .../Foundation/Domain/DocumentVersion.cs
17:    public List<string> Tags { get; private set; } = [];      # 版にもコピー（履歴）
$ grep -n "class" src/platform/backend/.../Foundation/Domain/AbacEntities.cs
6:public class AttributeDefinition                               # タグ辞書エンティティは存在しない
```

したがって #542 の (c) は「**保持方式を識別子参照へ移す**（既存データの移行）」か
「**改名のたびに全文書・全版・索引ペイロードを書き換える**」かのどちらかを要求する。**#536 と同じ型**である。

**#542 は次のユニットとして単独で扱う。** 着手時に (c) の保持方式を判断し、
**`DocumentVersion`（履歴）の過去版タグを改名で書き換えるのか**という論点は実装だけで決められないため、
必要なら計画へ裁定を仰ぐ。

**束ねないが、決定 4 の「1 系統」は守る** —— #540 は**口をスコープで拡張できる形**に作る（[[IADR-0151]] 決定 4）。

## 目的・背景

計画は SC-01 / SC-08 に「**権限内のタグ／部門／プロジェクトのみ選択可**」な対象範囲フィルタを定めているが、
**一般利用者が呼べる候補一覧 API が無い**（辞書は `/bff/admin/authz` の**管理者限定**にしかない）。

ADR-0043 は返す範囲に 4 つの制限を置いた。**外すと存在秘匿（ADR-0004 の 404 原則）が実質的に破れる。**

1. **決定 1**: 到達できる文書に**実際に付与されている値**だけを返す（**辞書を丸ごと返さない**）。
2. **決定 2**: **値ごとの件数は返さない**（「12 件だが自分の検索では 8 件」＝**見えない文書が 4 件ある**）。
3. **決定 3**: 自由入力による代替は採らない。
4. **決定 4**: **読み取り口は 1 系統**とし、スコープだけロールで変える。

## 母集合（[[IADR-0141]] 決定 1）

「**ABAC スコープが通る経路**」と「**属性値がどこに在るか**」の 2 軸で引いた。
**拡張子で絞らず、パスから引いた**（追跡下の全ファイル。`planning/` と `src/ai-stock-trading` は除く）。

```console
$ git grep -ln "AccessScope" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
（軸 1: スコープ解決と評価の経路）
$ git grep -ln "BuildAttributeFilter\|attributes\[" -- src ':!src/ai-stock-trading'
（軸 2: Qdrant ペイロードの属性を読む／フィルタする側）
```

### 軸 1: ABAC スコープが通る経路（**再利用する**）

| 層 | 実体 | 本作業での扱い |
| --- | --- | --- |
| スコープ解決 | `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.ResolveAsync` | **そのまま使う**（クライアント指定の Scope を信頼しない・deny-by-default で null 縮退） |
| スコープ → Qdrant フィルタ | `RetrievalService.../QdrantVectorStore.BuildAttributeFilter` | **同じ組み立てを使う**（検索段と定義を一致させる。[[IADR-0151]] 決定 1） |
| 契約 | `Knowledge.Contracts/Dtos/`（**新設**） | 照会の要求／応答 DTO |
| 検索サービス | `RetrievalService`（**新設エンドポイント**） | facet を呼び、**件数を捨てて**値集合を返す |
| BFF | `Platform.Bff`（**新設エンドポイント**） | スコープをサーバ側で解決して後段へ渡す |
| 契約書 | `docs/api/openapi.yaml` ＋ orval 生成物 | **追随**（`pnpm run codegen` を必ず再実行する） |

### 軸 2: 属性値がどこに在るか（**実測**）

| 置き場所 | 形 | 候補の出所として使えるか |
| --- | --- | --- |
| Qdrant ペイロード `attributes` | **ネスト構造体** `attributes -> {k: v}`（[[IADR-0014]]） | **使う**（検索段と同じ集合になる） |
| Qdrant ペイロード `tags` | 文字列リスト | **使う** |
| `AttributeDefinition.AllowedValues`（AuthorizationService） | 辞書の**全値** | **使わない**（ADR-0043 決定 1 が禁じる） |
| `Document.Tags` / `DocumentVersion.Tags`（DocumentService） | 文字列リスト | **使わない**（ABAC 判定が 2 経路に分かれる） |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| **#542（タグ辞書の管理者スコープ）** | §射程 のとおり**条件 F を満たさない**。次のユニットとして単独で扱う |
| **#539（SC-01 / SC-08 の画面側フィルタ）** | 本作業が作る口を**使う側**である。[[IADR-0139]] 決定 5 が「別資源・順序依存は束ねる理由にならない」と判定済み |
| `AttributeDefinition` の CRUD（`/authz/attributes`） | 既存の管理者限定 API。本作業は**読み取りの新しい口**を足すだけで、既存口は触らない |
| キャッシュ層 | **[[IADR-0151]] 決定 3 で入れないと判断した**（主体ごとに変わり共有できず、キー忘れで他人の候補が漏れる面を作る） |
| `src/ai-stock-trading` | 別プロジェクトの submodule |
| `planning/` | 本作業では pin を動かさない |

## 実装方針

1. **契約**: 照会の要求（属性キー）と応答（**値の配列だけ**）を `Knowledge.Contracts` へ足す。
   **件数のフィールドを作らない**——[[IADR-0151]] 決定 2・4（#542 が管理者スコープ専用に足せる形に保つ）。
2. **RetrievalService**: ABAC フィルタ付きで Qdrant の facet を呼び、**件数を捨てて**値集合を返す。
   `tags`（リスト）と `attributes.<key>`（ネスト）の両方を引けるようにする。
3. **BFF**: `BffScopeResolver` でスコープをサーバ側解決し、後段へ渡す。
   **スコープが解決できないときは空配列**（404 にも 403 にもしない。[[IADR-0151]] 決定 5）。
4. **画面は触らない**（#539 の射程）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | 対象 |
| --- | --- | --- |
| 1 | 到達できる文書に付いた値だけが返る（辞書の全値ではない） | RetrievalService |
| 2 | **応答に件数が含まれない**（ADR-0043 決定 2） | 契約 ＋ RetrievalService |
| 3 | ABAC フィルタが検索段と同じ集合を切る | RetrievalService |
| 4 | スコープ未解決（deny-by-default）は**空配列**（404 / 403 にしない） | BFF |
| 5 | クライアント指定の Scope を信頼しない（権限昇格の防止） | BFF |
| 6 | `tags` と `attributes.<key>` の両方を引ける | RetrievalService |

## 追随させる文書

- `docs/api/openapi.yaml`（新しい口）＋ **orval 再生成**
- `docs/api/BFF_bff-surface.md`（一覧表へ 1 行）
- `docs/functional/FR-03_hybrid-search.md` または新設の機能仕様書（**着手時に判断する**）
- `docs/tests/`（受け入れ基準の写像先。**着手時に判断する**）
- `docs/adr/IADR-0151_*.md`（**新設**）＋ `docs/adr/README.md`
- **[[IADR-0139]] 決定 5 の表へ追記**（束ねの判定を覆したこと。**本作業で実施済み**）

## 実装中に決めたこと（仕様書からの差分）

### facet の畳み込みを LINQ にせず、明示的なループにした

`ADR-0043` 決定 2（件数を返さない）が守られていることを、**コードを読んで確認できる形**にした。
LINQ の連鎖だと `Count` に触れていないことが読み取りにくい——**明示的な `foreach` で
`hit.Value` しか参照しない**ようにし、その理由をコメントに書いた。
`InMemoryVectorStore` 側も同じ意味論（値集合だけを返す）で実装した。

### 応答の「件数が無いこと」を **生の JSON 本文**で見る

`AttributeValuesResponse` にフィールドが無いことは型で分かるが、**実装が余計なものを載せていない**
ことまでは型では分からない。`AttributeValues_ResponseCarriesNoCounts` は**応答本文の文字列**に
`count` / `Count` / `件数` が現れないことを見る。**同じタグが 2 件ある状態を作ってから**確かめており、
多重度が漏れないことも同時に固定している。

### `BffEndpointCompositionTests` が新しいルートを止めた（想定どおりの働き）

`/bff/attribute-values` を足したところ、**合成点のルート一覧を固定するテストが落ちた**
（「期待外の `/bff/*` ルートグループが登録されている」）。**期待リストへ明示的に足して通した**——
BFF の口が黙って増えないようにする既存の守りであり、**落ちたのは正しい**。

### `BffTestFactory` の観測プロパティはテスト間で共有される

`IClassFixture` なので `LastAttributeValuesBody` はテストを跨いで残る。**観測する側が呼ぶ前に
`null` へ戻す**ようにし、その理由をプロパティのコメントとテストに書いた
（最初に書いたときは前のテストの値を拾って落ちた）。

## 検証記録（実測・すべて本作業の head で走らせた）

`node scripts/…` は**リポジトリのルートから実行する**。

| 対象 | 結果 |
| --- | --- |
| `dotnet test knowledge/backend/backend.slnx` | **459 passed / 0 failed**（18 skipped は統合テストの環境依存。**本作業で 9 件追加**） |
| `dotnet test platform/backend/backend.slnx` | **370 passed / 0 failed**（1 skipped。**本作業で 5 件追加**） |
| `dotnet format --verify-no-changes`（両ユニット） | OK |
| `pnpm typecheck` / `lint` / `format:check` | OK（lint は warning 9・error 0。既存の `react-refresh` 警告） |
| `pnpm test:coverage` | statements **96.39%** / branches **90.53%** / functions **91.68%** / lines **96.39%**（床 90 / 85 / 88 / 90。**割っていない**） |
| `pnpm build` ＋ `check-static-egress` | OK（24 ファイル・外部オリジン 0） |
| `check-chunk-budget` | **床は動かない**（578.15 kB・遅延チャンク 6 本のまま）。**画面を触っていないので当然である** |
| `check-contract-schema` | **baseline を更新**（`AttributeValuesRequest` / `AttributeValuesResponse` / `AttributeValueKeys` の**型追加 3 件**。**破壊的 0 件**） |
| `check-test-spec-coverage` | **床は動かない**（78 対のまま）。**既存のテストクラスへ足しただけ**だからである |
| その他 | `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` / `check-i18n-catalogs` / `check-test-traceability` / `check-bff-downstreams` / `check-unit-dependencies` / `check-backend-libraries` / `check-landed-subjects` すべて OK |

**カバレッジ床は上げない**（#628・#536・#532 と同じ判断）。**i18n カタログも動かない**——画面を触っていない。
