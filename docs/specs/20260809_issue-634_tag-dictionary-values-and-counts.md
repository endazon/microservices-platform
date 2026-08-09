---
title: 作業仕様書 — タグ辞書の値集合の照会・追加と使用件数（#634）
type: work-spec
status: in-progress
related_ids:
  - FR-06
  - FR-09
  - SC-05
  - SC-09
  - UC-03
  - UC-05
  - ADR-0043
  - IADR-0152
  - IADR-0151
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md"
related_specs:
  - "../adr/IADR-0152_tag-dictionary-contract.md"
  - "../adr/IADR-0151_scoped-attribute-value-facets.md"
  - "../functional/FR-05_abac-access-control.md"
  - "../api/BFF_bff-surface.md"
---

# 作業仕様書 — タグ辞書の値集合の照会・追加と使用件数（#634）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-09** | タグ辞書の管理。**参照が 1 件でもあるタグは削除拒否・改名は既存文書へ追随・削除前に使用件数（N 件）を示す**（確定 2026-08-02） |
| 画面 | **SC-05** | 「既定タグ辞書に整合」。**辞書は管理系ロールが引ける照会口から取得する**（確定 2026-08-05・Q18） |
| 要求 | **FR-06** / **FR-09** | 文書管理／管理機能 |
| ユースケース | **UC-03** / **UC-05** | 文書登録・管理／管理者設定 |
| 計画 ADR | **ADR-0043**（`Accepted`） | 読み取り口は 1 系統・スコープだけロール別（決定 4）。辞書を丸ごと一般利用者へ返さない（決定 1） |

**契約の定義は [[IADR-0152]] が (a)(b)(c) をまとめて行う。** 本仕様書はそのうち **(a) 値集合の照会・追加**と
**(b) 使用件数**の実装を扱う。**(c) 改名の追随と保持方式の移行は #635** である。

## 射程と、issue を分割したこと

**#542 は 1 PR に収まらない。** [[IADR-0116]] 規約 4（PR ではなく issue を分割する）に従い、
#542 を親として **#634（本作業）** と **#635** の 2 つへ分けた。

分割の根拠は[[IADR-0139]] が定めた**「概ね 50 ファイル / +2500 行を超えるなら分ける」**である。
下記「母集合」の実測がこれを超える。

**計画の「(a)(b)(c) は分割できない」に反しない。** 計画の理由づけは依存関係であり
（「(c) は (a) を前提とする」「(b) が無ければ (c) の削除拒否を管理者が事前に判断できない」）、
**部分的な契約を出荷するな**という意味である。(a)(b) を先に着地させてから (c) を足す順序を禁じてはいない。
**契約そのものは [[IADR-0152]] が 3 つまとめて定める。**

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が引いた。走査基準: develop `cb2d611`。**

### 軸 1: 文書タグを運ぶ箇所（**#635 で触る。本作業では触らない**）

| 対象 | 実測 |
| --- | --- |
| バックエンドのファイル | **30 件**（`grep -rn "Tags"` から `WithTags`（OpenAPI のグループ名）・`hc.Tags`（ヘルスチェック）・生成物・`Migrations/` を除いた数） |
| タグ列を持つ EF DbContext | **3 つ**（DocumentService / ConversionService / WikiService）＝ **マイグレーション 3 本** |
| タグを運ぶイベント | **3 つ**（`RawDocumentFetched` / `DocumentNormalized` / `DocumentUpdated`） |
| タグを持つ DTO | **4 つ**（`DocumentDto` の 2 型 / `SearchResultDto` / `AttributeValueKeys`） |
| 外部システム | **Wiki.js**（`WikiPage.Tags` も表示名の複写。改名時は再 push が要る） |
| ベクトルストア | Qdrant ペイロードの `tags`（**全再索引**が要る） |
| フロントエンド | `DocumentForm` / `SearchResultsPage` / `sc09-admin-abac` の 3 箇所 |

**`LlmGateway` の `CompletionMetricsTests`（17 件）は計測メトリクスの `Tags` であり無関係なので除外した。**
**誤りの側から引いた**（[[IADR-0141]] 規則 1）——「文書タグ」で検索せず `Tags` で全部引いてから、
無関係なものを 1 つずつ確認して落とした。

### 軸 2: 辞書そのもの（**本作業で作る**）

| 対象 | 実測 |
| --- | --- |
| タグ辞書のエンティティ | **存在しない**（`class Tag` / `TagDefinition` / `DbSet<Tag` はいずれも 0 件） |
| 近い先例 | `AttributeDefinition`（platform の AuthorizationService）。**ABAC 属性の許可値であって辞書ではない**——計画も同じ切り分けをしている |
| 相乗りする口 | `/bff/attribute-values`（#540 で新設。[[IADR-0151]] 決定 4 が拡張点を用意している） |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `Document.Tags` などの**保持方式の変更** | **#635 の射程**。本作業は既存の `List<string>` に触らない |
| **タグの削除**（参照 1 件以上なら拒否） | **#635 の射程**。識別子参照でない状態で削除規則だけ入れると、改名と削除で保持方式の前提が食い違う |
| **辺の型（値集合）の辞書** | 知識グラフ（FR-17）は [[IADR-0142]] が着手条件を別に定めている。[[IADR-0152]] フォローアップに記録済み |
| `src/ai-stock-trading` | 別プロジェクトの submodule。変更しない |

## 実装方針

1. **辞書エンティティ**: DocumentService に `Tag`（識別子・表示名・作成日時）を新設する（[[IADR-0152]] 決定 1）。
   マイグレーション 1 本。
2. **契約**: `Knowledge.Contracts` に辞書の DTO を足す。
   **`AttributeValuesResponse` へ管理者スコープ専用のフィールドを足す**（既定 `null`。
   一般利用者の応答形は #540 から変えない。[[IADR-0151]] 決定 4 / [[IADR-0122]] 決定 2）。
3. **使用件数**: 現行版の `Document.Tags` を数える。**版履歴は数えない。アーカイブ済みは数える**
   （[[IADR-0152]] 決定 2）。**移行前なので表示名の一致で数える暫定である**ことをコメントとテストに残す。
4. **BFF**: `/bff/attribute-values` の 1 系統を保つ。読み取りは `ConfigViewer`、追加は `AdminOnly`
   （[[IADR-0152]] 決定 5）。**新しい読み取り口を作らない。**
5. **一般利用者の経路は変えない**——候補は Qdrant の facet のまま（[[IADR-0152]] 決定 4）。

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | 辞書の値集合を管理者・運用者が引ける |
| 2 | 一般利用者には管理者スコープのフィールドが出ない（**応答形が #540 から変わっていない**） |
| 3 | タグを追加できる（システム管理者のみ） |
| 4 | 使用件数が**現行版の文書の件数**である |
| 5 | **版履歴だけが参照するタグの件数が 0 である**（append-only なので数えたら削除できなくなる） |
| 6 | **アーカイブ済みの文書も数える** |
| 7 | 読み取り口が 1 系統のままである（`/bff/*` のルートグループが増えていない） |

## 追随させる文書

- `docs/api/openapi.yaml`（＋ orval 再生成）／`docs/api/BFF_bff-surface.md`
- `docs/functional/FR-09_*`（無ければ該当機能仕様書）／`docs/tests/`
- `docs/data/`（辞書エンティティ）

## 実装中に決めたこと（仕様書からの差分）

（着手後に追記する）

## 検証記録（実測）

（着手後に追記する）
