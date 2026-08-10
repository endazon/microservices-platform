---
title: 作業仕様書 — タグ辞書の値集合の照会・追加と使用件数（#634）
type: spec
status: done
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

### `Tag` に `Rename` と `UpdatedAt` を置かなかった

改名は #635 の射程である。**先に置くと、何も書き込まない `UpdatedAt` 列と呼ぶ側の無いメソッドが残る**——
`UpdatedAt` が常に `CreatedAt` と等しい列は読み手を誤らせる。`CLAUDE.md` の
「過剰な抽象化・起こり得ないケースへの防御的実装」を避けた。**#635 が自分のマイグレーションで足す。**

**`Id` は #634 の時点でも要る。** 主キーであり、[[IADR-0152]] 決定 6 の
「識別子は改名で変わらない」という土台そのものだからである。

### 使用件数を SQL で数えず、現行版の文書のタグを読んで数えた

`Document.Tags` は jsonb へ変換した `List<string>` であり、**SQL 側で展開して数えられない**。
辞書は管理画面で人が管理する値集合なので、現行版の文書のタグだけを読んで数える。
**版履歴は読んでいない**（[[IADR-0152]] 決定 2）。

### 同じ文書に同じタグが 2 度あっても 1 件と数える

数えるのは「このタグを**使っている文書の数**」である（SC-09 の「このタグは N 件で使われている」）。
`Distinct` を通す。

### BFF で辞書を引くのは `key=tags` かつ管理者・運用者のときだけにした

`IsTagKey` と `IsDictionaryReader` の 2 条件で門を作った。**一般利用者では後段を呼びもしない**
（呼べば DocumentService が 403 で止めるが、呼ぶこと自体が無駄である）。
**実効境界は DocumentService 側**であり（[[IADR-0044]] 多層防御 / [[IADR-0039]] 決定 2）、
BFF の判定は早期の門にすぎない。**BFF を迂回されても `/tags` が同じロールを要求する。**

### 辞書が引けなくても候補一覧を落とさない

`FetchTagDictionaryAsync` は**後段へ到達できないとき**は辞書を添えずに続ける
（候補 `Values` は辞書に依存しないため）。**後段が返した非 2xx は透過する**——
辞書を引けるのは管理者・運用者だけなので、一般利用者には影響しない
（`BFF_bff-surface.md` の縮退表の注記と同じ扱い）。

### 読み取りに `ConfigViewer` ポリシーを使わず、`RequireRole` を直に書いた

仕様書では `ConfigViewer`（管理者 OR 運用者）と書いたが、**採らなかった**。
`ConfigViewer` は「**FR-15, SC-11, IADR-0030: 構成情報の閲覧**は管理者・運用者ロールに限定する」と
**用途を名前で宣言しているポリシー**であり、タグ辞書の読み取りへ流用すると意味が二重になる——
以後どちらかの要件が動いたときに、もう一方が黙って巻き添えになる。
**`DocumentEndpoints` の書き込みグループと同じく `p.RequireRole(AdminRole, OperatorRole)` を直に書いた**
（ロール定数は共有しているので、綴りの単一情報源は保たれている）。

### 名前の重複は正規化後に見た

前後の空白だけが違う 2 つを別物として登録できると、**辞書が実質的に重複を許す**ことになる。
`Tag.Normalize`（`Trim`）を通し、DB にも一意インデックスを張った。

## 検証記録（実測・すべて本作業の head で走らせた）

`node scripts/…` は**リポジトリのルートから実行する**。

| 対象 | 結果 |
| --- | --- |
| `dotnet test knowledge/backend/backend.slnx` | **473 passed / 0 failed**（18 skipped は統合テストの環境依存。**本作業で 14 件追加**。459 → 473） |
| `dotnet test platform/backend/backend.slnx` | **376 passed / 0 failed**（1 skipped。**本作業で 4 件追加**。372 → 376） |
| `dotnet format --verify-no-changes`（両ユニット） | OK |
| `pnpm typecheck` / `lint` / `format:check` | OK（lint は warning 9・error 0。既存の `react-refresh` 警告） |
| `pnpm test:coverage` | statements **96.39%** / branches **90.53%** / functions **91.68%** / lines **96.39%**（床 90 / 85 / 88 / 90。**割っていない**） |
| `pnpm build` ＋ `check-static-egress` | OK（24 ファイル・外部オリジン 0） |
| `check-chunk-budget` | **床は動かない**（578.15 kB のまま）。**画面を触っていないので当然である** |
| `check-contract-schema` | **baseline を更新**（`TagDto` / `CreateTagRequest` / `TagDictionaryResponse` の型追加 ＋ `AttributeValuesResponse.Dictionary` の**既定値ありメンバー追加**。**破壊的変更 0 件**。[[IADR-0122]] 決定 2） |
| `check-test-spec-coverage` | 床は動かない（`TagDictionaryTests` は FR-09 のテスト仕様書へ記載済み） |
| EF マイグレーション | `AddTagDictionary`（**新規テーブル 1 本のみ。既存テーブルへの変更なし**） |
| その他 | `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` / `check-i18n-catalogs` / `check-test-traceability` / `check-bff-downstreams` / `check-unit-dependencies` / `check-backend-libraries` / `check-landed-subjects` / `scripts.repo.test` すべて OK |

**カバレッジ床は上げない**（#628・#536・#532・#540 と同じ判断）。**i18n カタログも動かない**——画面を触っていない。

**#634 の変更規模は 18 ファイル**である。#542 を分けなければ、これに #635 の
30 ファイル・マイグレーション 3 本・Qdrant 全再索引が乗っていた。

## レビュー指摘への対応（PR #636・AI レビュー）

| 指摘 | 対応 |
| --- | --- |
| 🔴 **[[IADR-0152]] 決定 5 と実装が食い違っている**（決定 5 は `ConfigViewer` と書いているのに、実装は `RequireRole(AdminRole, OperatorRole)` を直書き）。**決定の正本が古いまま `Accepted` で残る**ため、将来 `adr-guardian` / `traceability-auditor` が今回の実装を「ADR 違反」と誤検出する | **指摘のとおりで、私の落ち度である。** 逸脱の理由を**作業仕様書にだけ書いて、決定の正本を直さなかった**。[[IADR-0152]] の決定 5 を実装に合わせて書き直し、**選択肢の節（2b）に `ConfigViewer` を採らなかった理由**を残した。**日付つき追記ではなく本文を直した**——本 IADR は**この PR で新設したもの**であり、着地した版が無いので「後から差し込んだ注記」に当たらない（`.claude/rules/traceability.md` の注記規約は live な文書への事後注記が対象である） |
| 🟡 **タグ追加の重複チェックに TOCTOU の隙**。事前検証（`AnyAsync`）と保存の間に別の要求が同名を入れると、DB の一意インデックス違反が未処理の `DbUpdateException` になり、契約「重複は 409」に反して**素の 500** になり得る | **受け入れた。** `SaveChangesAsync` を `try/catch (DbUpdateException)` で包み 409 へ写した。**`AuthzEndpoints` の `AttributeDefinition` が同型の先例**であり、そこを見ていなかった取りこぼしである |

### レースのテストは統合テスト側へ置いた（実測にもとづく判断）

**単体テストでは踏めない。** `DocumentService.Api.Tests` は **EF InMemory** を使っており
（`TestWebApplicationFactory` の `UseInMemoryDatabase`）、**InMemory プロバイダは一意インデックスを強制しない**。
単体側に並行作成のテストを書いても、**2 件とも 201 になって通ってしまい、ガードを検証したことにならない**。

そこで **`Knowledge.IntegrationTests`（実 PostgreSQL）** へ 2 件足した:

- `CreateTag_Duplicate_Returns409`（逐次の重複 → 事前検証が 409）
- `CreateTag_Concurrently_ExactlyOneSucceeds_AndNoServerError`（**同時 4 本 → 201 が 1 件・残りは 409・500 は 0 件**）

**本作業環境では Docker が無く skip される**（`knowledge` の skipped が 18 → 20 になったのはこの 2 件である）。
**CI と、Docker のあるレビュー環境では実走する。**

**なお先例（`AuthzEndpoints` の `catch (DbUpdateException)`）にはテストが無い**（実測）。
同じ理由（InMemory では踏めない）と思われるが、**そちらへテストを足すのは本 issue の射程外**なので触らない。
