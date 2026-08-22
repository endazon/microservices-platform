---
title: 作業仕様書 — 辺の型辞書の DB 層の防壁が機能したことを確認する（#941）
type: spec
status: in-progress
related_ids:
  - FR-17
  - SC-09
  - ADR-0033
  - IADR-0242
  - IADR-0261
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0033 決定 9（参照が 1 件でもある辺の型は削除を拒否する）"
  - "ADR-0033 決定 5・6（アンカー欄の予約 / 最新版のみ保持＝同一関係の重複禁止）"
  - "SC-09（辺の型辞書の契約。重複は 409）"
issue: "#941"
---

# 作業仕様書: 辺の型辞書の DB 層の防壁が機能したことを確認する（#941）

## 起点

- 実装 issue: `#941`（関連: `#910` 辺の型辞書 API / `#450` 親）。
- 起点 ID: **FR-17 / SC-09 / ADR-0033（決定 9・5・6）/ IADR-0242（決定 7）**。
- **これは「DB 層のテストを足す」issue ではない。**「**在ることになっているが、機能したことが
  一度も確認されていない防壁**を確認する」issue である。`#910` の変異試験 G-1 で、アプリ層の
  事前カウントを外すと **500 ではなく 204 で参照中の型が黙って消えた**（単体は EF InMemory であり、
  InMemory は一意索引も外部キーも強制しない）ことが実測されている。

## 着手前の実測（母集合を引く前の前提確認）

### `Knowledge.IntegrationTests` は GraphService を参照していない

```console
$ grep -n "ProjectReference" src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj
46:  ...DocumentService.Api.csproj
47:  ...DataSourceService.Api.csproj
48:  ...AuthorizationService.Api.csproj
49:  ...WikiService.Api.csproj
51:  ...IngestionService.Worker.csproj
52:  ...ConversionService.Worker.csproj
53:  ...RetrievalService.Api.csproj
54:  ...AiAnalysisService.Api.csproj
55:  ...Platform.Shared.Contracts.csproj
（GraphService は 0 件）

$ grep -rln "GraphService" --include=*.csproj src/ | grep -v obj/
src/knowledge/backend/Services/GraphService/src/GraphService.Api/GraphService.Api.csproj
src/knowledge/backend/Services/GraphService/tests/GraphService.Api.Tests/GraphService.Api.Tests.csproj
```

→ **GraphService を実 PostgreSQL で起こすテストは 1 件も存在しない。** issue 本文の主張と一致。

### GraphService の既存テストは全て InMemory である

```console
$ grep -rn "UseInMemoryDatabase\|UseNpgsql" --include=*.cs \
    src/knowledge/backend/Services/GraphService/tests/ | grep -v obj/
EdgeTypeDictionaryTests.cs:16:  .UseInMemoryDatabase($"EdgeType_{Guid.NewGuid()}")
AuthorizedGraphViewTests.cs:96:  .UseInMemoryDatabase($"Store_{Guid.NewGuid()}").Options);
AuthorizedGraphViewTests.cs:113: .UseInMemoryDatabase($"Order_{Guid.NewGuid()}").Options);
AuthorizedGraphViewTests.cs:149: .UseInMemoryDatabase($"Store_{Guid.NewGuid()}").Options);
TestWebApplicationFactory.cs:80: services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(dbName));
（UseNpgsql は 0 件）
```

## 母集合の取り方（`traceability.repo.md` 規則 9・10）

母集合は「**GraphService のスキーマが持つ、書き込みを拒み得る DB 層の宣言（防壁）すべて**」とする。
issue 本文が挙げる 4 点を鵜呑みにせず（規則: 「issue 本文の『反映先』は母集合ではない」）、
**宣言側とマイグレーション出力側の 2 軸**で引き直した。さらに「検証されている」ではなく
**「検証されていない」側＝防壁名を参照するテストが在るか**で 3 軸目を引いた（規則 1）。

### 軸A — `GraphDbContext` の宣言

```console
$ grep -n "IsUnique\|OnDelete\|HasDatabaseName\|HasDefaultValue\|HasForeignKey" \
    src/knowledge/backend/Services/GraphService/src/GraphService.Api/Foundation/Persistence/GraphDbContext.cs
 54: e.Property(t => t.Weight).IsRequired().HasDefaultValue(EdgeType.DefaultWeight);
 57: e.HasIndex(t => t.Name).IsUnique().HasDatabaseName("ux_edge_types_name");
 76: e.Property(x => x.Rationale).HasMaxLength(2000).IsRequired().HasDefaultValue(string.Empty);
 80: e.Property(x => x.RejectedCount).IsRequired().HasDefaultValue(0);
 91: e.HasIndex(x => x.State).HasDatabaseName("ix_ai_suggestions_state");
 95:   .HasDatabaseName("ix_ai_suggestions_endpoints");
121: e.Property(x => x.SourceAnchor).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
122: e.Property(x => x.TargetAnchor).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
128:   .HasForeignKey(x => x.EdgeTypeId)
129:   .OnDelete(DeleteBehavior.Restrict);
139: }).IsUnique().HasDatabaseName("ux_edges");
141: e.HasIndex(x => x.SourceDocumentId).HasDatabaseName("ix_edges_source");
143: e.HasIndex(x => x.TargetDocumentId).HasDatabaseName("ix_edges_target");
145: e.HasIndex(x => x.EdgeTypeId).HasDatabaseName("ix_edges_type");
```

### 軸B — マイグレーション 4 本が出力する制約

```console
$ grep -n "unique: true\|ReferentialAction\|CreateIndex\|nullable: false, defaultValue" \
    src/.../Migrations/*.cs | grep -v Designer
InitialCreate.cs:55: SourceAnchor ... nullable: false, defaultValue: ""
InitialCreate.cs:56: TargetAnchor ... nullable: false, defaultValue: ""
InitialCreate.cs:68: onDelete: ReferentialAction.Restrict
InitialCreate.cs:71,75: CreateIndex ux_edge_types_name unique: true
InitialCreate.cs:77,82,87: CreateIndex ix_edges_source / ix_edges_target / ix_edges_type
InitialCreate.cs:92,96: CreateIndex ux_edges unique: true
AddAiSuggestions.cs:24,26: Rationale/RejectedCount の defaultValue
AddAiSuggestions.cs:39,44: CreateIndex ix_ai_suggestions_state / ix_ai_suggestions_endpoints
```

軸A と軸B は一致する（宣言だけがあってマイグレーションに落ちていないものは無い）。

### 軸C — 防壁名を参照するテストの有無（誤りの側から引く）

```console
$ grep -rn "ux_edges\|ux_edge_types_name\|DeleteBehavior.Restrict\|23503\|23505" \
    --include=*.cs src/ | grep -v "/obj/" | grep -v "/bin/"
→ 一致するのは GraphDbContext / Migrations / Edge.cs のコメント / Knowledge.Contracts のコメントのみ。
   **テストプロジェクト配下の一致は 0 件。**
```

### 母集合と対象／除外

| # | DB 層の宣言 | 防壁か | 本 PR の対象 | 理由 |
| --- | --- | --- | --- | --- |
| 1 | `edges` → `edge_types` の外部キー `ON DELETE RESTRICT` | ○ | **対象** | issue 検証対象 1。決定 9 の最後の防壁 |
| 2 | `ux_edge_types_name`（一意） | ○ | **対象** | issue 検証対象 2。SC-09「既存値と重複しない」 |
| 3 | `ux_edges`（5 列の一意） | ○ | **対象** | issue 検証対象 3。決定 6「同一関係を二重に持たない」 |
| 4 | `SourceAnchor` / `TargetAnchor` の `NOT NULL` ＋ 既定 `''` | ○ | **対象** | issue 検証対象 3 の後半。NULL 可だと #3 が壊れる |
| 5 | 一意制約違反 → 409 変換（アプリ層だが #1〜#3 が効かないと通らない分岐） | ○ | **対象** | issue 検証対象 4 |
| 6 | `ai_suggestions` → `edge_types` の外部キーを**張っていない**こと | ○（逆向き） | **対象** | 張ると決定 9 より厳しい規則を勝手に作る。**「無いこと」も防壁の設計である**ため同じ母集合に含めた |
| 7 | `edges` → `graph_documents` の外部キーを**張っていない**こと | ○（逆向き） | **対象** | イベント到着順への人工的依存を作らない（IADR-0242 決定 12-3） |
| 8 | 主キー（`edge_types.Id` / `edges.Id` / `graph_documents.DocumentId` / `ai_suggestions.Id`） | × | 除外 | いずれも `Guid.NewGuid()` 由来で衝突が起こり得ず、**防壁として設計されたものではない** |
| 9 | `character varying(N)` の長さ制約（20 / 100 / 200 / 1000 / 2000） | △ | 除外 | 入力長の検証は**アプリ層の契約**（400）であり、DB は最後の砦として設計されていない。#941 の射程外。**同型の未検証は残るため残件へ記載する** |
| 10 | 各列の `NOT NULL`（#4 以外） | × | 除外 | ドメイン型が非 null であり、アプリ層を経由しない書き込み経路が無い |
| 11 | 非一意索引 `ix_edges_source` / `ix_edges_target` / `ix_edges_type` / `ix_ai_suggestions_*` | × | 除外 | **性能のための索引であり書き込みを拒まない**。防壁ではない |
| 12 | 既定値 `Weight=0.5` / `RejectedCount=0` / `Rationale=''` | × | 除外 | 既定値であって拒否しない。`FixEdgeTypeWeightDefault` の修正の妥当性は別 issue の射程 |

**除外は 5 件（#8〜#12）。うち #9 だけは「防壁になり得るが射程外」であり、残件として記録する。**
他はいずれも「書き込みを拒む機構ではない」ため、本 issue の定義（機能したことが確認されていない
**防壁**）に当たらない。

## 対象範囲

- 追加: `src/knowledge/backend/Tests/Knowledge.IntegrationTests/` に GraphService の実 PostgreSQL
  結合テストを新設する。`Knowledge.IntegrationTests.csproj` へ `GraphService.Api` の
  `ProjectReference` を足す（`Program` 型衝突は `GraphServiceTestMarker` で避ける。IADR-0027 の作法）。
- 対象外: `GraphService.Api` 本体の変更（**防壁は既に在る。確認するのが本 issue である**）。
  `docs/tests/FR-17_knowledge-graph.md` の T-39 / T-40 の状態更新は**別担当のファイル領域と交差する
  ため本 PR では触らない**（残件へ記載）。

## 設計判断（→ IADR-0261）

1. **スキーマは `EnsureCreatedAsync` ではなくマイグレーションから作る。** 既存の
   `TagDictionaryUniquenessTests` は `EnsureCreatedAsync` を呼ぶが、それではモデルから直接
   スキーマが作られ、**「マイグレーションが RESTRICT を正しく出力しているか」を確かめられない**
   （issue 本文の指摘）。GraphService の `Program.cs` は起動時に `MigrateAsync` を実行するため、
   **ホストを起こすだけでマイグレーション出力が検証対象になる。**
2. **防壁は HTTP ではなく `GraphDbContext` を直接叩いて確かめる。** アプリ層のガードを外す変異を
   入れると「変異を入れた版」しか試験できず、出荷される版の防壁は依然として未発火のままになる。
3. **スキーマそのものを PostgreSQL のカタログ（`pg_constraint` / `pg_indexes` /
   `information_schema.columns`）で突合する。** 「張っていない外部キー」（母集合 #6・#7）は
   書き込みで反証できないため、**カタログを見る以外に固定する手段が無い**。
4. **削除時の RESTRICT → 409 変換は、未コミットのトランザクションで行ロックを掛けて競合を
   決定的に再現する。**

## テストケース（受け入れ基準への写像）

| # | 検証対象（issue） | テスト | 期待 |
| --- | --- | --- | --- |
| 1 | `ON DELETE RESTRICT` | 参照 1 件の型を `DbContext` から直接削除 | `DbUpdateException` / SqlState `23503` / 制約名 `FK_edges_edge_types_EdgeTypeId`・型の行は残る |
| 2 | `ux_edge_types_name` | 同名の型を 2 件、直接挿入 | `23505` / 制約名 `ux_edge_types_name` |
| 3 | `ux_edges` | 同一の 5 つ組の辺を 2 件、直接挿入 | `23505` / 制約名 `ux_edges`。1 件目のアンカーは `''`（NULL でない） |
| 4 | 409 変換（一意制約） | 同名を 4 並列で `POST /graph/edge-types` | Created は 1 件・残りは 409・**5xx は 0 件** |
| 4' | 409 変換（RESTRICT） | 未コミット挿入で競合を作り `DELETE` | 409 ＋ 使用件数 1・型は残る・**500 にしない** |
| 5 | マイグレーション出力 | カタログ突合 | FK は `edges` の 1 本だけで `confdeltype='r'`・`ux_*` は UNIQUE・アンカー列は `NOT NULL` ＋ 既定 `''` |

## 実行と検証

- `[Trait("Category", "Integration")]` を付ける（PR の `ci.yml` は `Category!=Integration` で除外し、
  `integration.yml`（develop への push ＋ 日次 ＋ 手動）が回収する。IADR-0232）。
- 🔴 **`[DockerFact]` 相当（`DockerRequired.SkipUnlessAvailable()`）は Docker が無いと skip する。
  「緑だった」は「走った」の証拠にならない。** 実走の確認は `dotnet test` の生の出力で
  skip 件数と `Passed` を読む（手順は IADR-0261）。
