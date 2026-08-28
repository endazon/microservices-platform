---
title: 作業仕様書 — 辺の型辞書の DB 層の防壁を「確認できる状態」へ戻す（#941 第 2 巡）
type: spec
status: done
related_ids:
  - FR-17
  - SC-09
  - ADR-0033
  - ADR-0027
  - IADR-0242
  - IADR-0260
  - IADR-0291
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0033 決定 9（参照が 1 件でもある辺の型は削除を拒否する）"
  - "ADR-0033 決定 5・6（アンカー欄の予約 / 同一関係の重複禁止）"
  - "ADR-0027（メッセージング基盤は Wolverine）"
  - "SC-09（辺の型辞書の契約。重複は 409）"
issue: "#941"
---

# 作業仕様書: 辺の型辞書の DB 層の防壁を「確認できる状態」へ戻す（#941 第 2 巡）

## 起点

- 実装 issue: `#941`。起点 ID: **FR-17 / SC-09 / ADR-0033（決定 5・6・9）/ IADR-0242（決定 7）**。
- 先行: `.ai-context/specs/20260823_issue-941_edge-type-db-guards.md` ＋ `IADR-0260`（2026-08-23）。

## 着手前の実測 —— issue 本文の前提はすでに古い

issue 本文は「`Knowledge.IntegrationTests` は GraphService を参照していない」「参照追加が要る」と書くが、
**着手時点（`b1da69e`）でその作業は既に入っている。** 鵜呑みにせず実測した（規則: issue 本文の
「反映先」は母集合ではない）。

```console
$ git rev-parse HEAD
b1da69e4dd08f5122a7ec5b1f3a3e0c7b5e4a231
$ git status --short          # 何も出力されない（作業ツリーは clean）

$ grep -c GraphService src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj
（ProjectReference 1 件。マーカー型の注記つき）

$ ls src/knowledge/backend/Tests/Knowledge.IntegrationTests/GraphService/
EdgeTypeDbGuardTests.cs   GraphServiceFactory.cs      # 6 件のテストが既に在る
```

→ **本巡の課題は「テストを足す」ことではない。**「先行巡が置いた 6 件が、**いま実走したら本当に
防壁まで到達するか**」を確かめることである。#941 の主題（在ることになっているが機能したことが
確認されていない）は、**テスト自身にもそのまま当てはまる** —— この 6 件は一度も実行されていない
（先行巡の仕様書が自ら「6 件とも skip」と記録している）。

## 母集合の取り方（`traceability.repo.md` 規則 9・10）

規則 10（**是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す**）を、先行巡の成果物へ
適用する。母集合は「**先行巡（2026-08-23）以降に GraphService へ入った変更のうち、6 件の
テストの前提を崩し得るもの全て**」とする。「テストが参照する対象」ではなく「テストの前提」の側から引く。

### 軸A — マイグレーションの増減（スキーマ側の前提）

```console
$ ls src/knowledge/backend/Services/GraphService/Infrastructure/Persistence/Migrations/*.cs | grep -v Designer | grep -v Snapshot
20260822003838_InitialCreate.cs
20260822074729_AddAiSuggestions.cs
20260822092002_AddEdgeTypeWeight.cs
20260822111334_FixEdgeTypeWeightDefault.cs
20260827222222_AddEdgeExtractedFrom.cs      # ← 先行巡より後
```

`AddEdgeExtractedFrom`（#912）は `edges` へ `ExtractedFrom uuid NULL` と非一意索引
`ix_edges_extracted_from` を足す。**外部キーではなく、NOT NULL でもない。**
→ カタログ突合（外部キーの完全一致・UNIQUE 索引の名前集合）にも、生 SQL の INSERT（列を省ける）にも
影響しない。**前提は崩れない。**

### 軸B — `Program.cs` が起動時に要求する構成（ホスト起動の前提）

テストは `GraphServiceFactory.CreateClient()` でホストを起こす。**起動に失敗すれば 6 件とも
防壁に触れずに落ちる。** Program.cs が読む構成を全部引いた。

| 構成キー / 依存 | 由来 | 器（`IntegrationTestFactoryBase`）が与えるか |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | 未設定なら throw（#1012） | ✅ 与える |
| `Otlp:Endpoint` | 可観測性 | ✅ 与える |
| `Auth:Authority` | `AddPlatformAuth` | ✅ 与える |
| `Pipeline:ConfigPath` | `AddPlatformPipelineConfig` | ✅ 与える（本番の正本を指す） |
| `Services:AuthorizationService` / `Services:LlmGateway` | `HttpClient` の基底 URI。遅延 | ✅ 既定で足りる |
| `ObjectStorage:*` | 未設定なら縮退クライアント（実測: `IsConfigured` が false なら `NullObjectStorageClient`） | ✅ 既定で足りる |
| **`RabbitMq:ConnectionString`** | **`builder.Host.UseWolverine` → `opts.UseRabbitMq(...).AutoProvision()`** | 🔴 **与えない** |

### 軸C — 誤りの側の文字列で全走査する（規則 9）

`GraphServiceFactory.cs` は「GraphService はメッセージングを一切構成しない（実測）」と**断言している**。
その断言が今も真かを、**断言の側の語**で引き直した。

```console
$ grep -rln "UseWolverine" --include=Program.cs src/ | grep -v obj/
src/knowledge/backend/Services/ConversionService/Worker/Program.cs
src/knowledge/backend/Services/RetrievalService/Program.cs
src/knowledge/backend/Services/DocumentService/Program.cs
src/knowledge/backend/Services/WikiService/Program.cs
src/knowledge/backend/Services/IngestionService/Worker/Program.cs
src/knowledge/backend/Services/GraphService/Program.cs      # ← 断言と矛盾する
src/knowledge/backend/Services/DataSourceService/Program.cs
```

**断言は偽になっていた。** `#1016`（graph-delete 段）と `#911`（graph-sync 段）が 2026-08-28 に
GraphService へ Wolverine ホストを入れた —— 先行巡（08-23）の**後**である。
`GraphService.csproj` も `WolverineFx.RuntimeCompilation` を足しており（「本プロジェクトは
Wolverine ホストを起こす」と明記）、**csproj とテスト器の注記が正面から食い違っている。**

対比（他の 6 サービスの器がブローカを渡しているか）:

```console
$ grep -n "base(pg" src/knowledge/backend/Tests/Knowledge.IntegrationTests/Fixtures/IntegrationTestFactory.cs
DocumentServiceFactory   ... : base(pg, rabbit)
DataSourceServiceFactory ... : base(pg, rabbit)
AuthorizationServiceFactory ... : base(pg, null)    # AuthorizationService は UseWolverine を呼ばない → 正しい
WikiServiceFactory       ... : base(pg, rabbit)
IngestionServiceFactory  ... : base(pg, rabbit)
ConversionServiceFactory ... : base(pg, rabbit)

$ grep -n "base(pg" src/knowledge/backend/Tests/Knowledge.IntegrationTests/GraphService/GraphServiceFactory.cs
    public GraphServiceFactory(PostgresFixture pg) : base(pg, null) { }   # 🔴 唯一の例外
```

→ **Wolverine ホストを起こすサービスの器でブローカを渡していないのは `GraphServiceFactory` だけである。**

### 軸D — 段宣言の突合（起動時 fail-fast の前提）

器は `Pipeline:ConfigPath` を**本番の正本**へ向ける。`AddPlatformWolverineStep` の規則 2・3
（宣言があるのに段が未宣言 / consumer 完全名の不一致 → **起動失敗**）が効くので、正本を確かめた。

```console
$ grep -n "graph-" deploy/helm/microservices-platform/files/pipeline.json
"name": "graph-delete" ... "consumer": "GraphService.Features.GraphDocuments.DocumentDeletedConsumer"
"name": "graph-sync"   ... "consumer": "GraphService.Features.GraphDocuments.GraphDocumentSyncConsumer"
```

Program.cs の `AddPlatformWolverineStep<DocumentDeletedConsumer>` / `<GraphDocumentSyncConsumer>` の
名前空間と一致する。**ここは崩れていない。**

### 母集合と対象／除外

| # | 先行巡以降の変更 | テストの前提を崩すか | 本巡の対象 | 理由 |
| --- | --- | --- | --- | --- |
| 1 | GraphService が Wolverine ホストを起こすようになった（#1016 / #911） | **崩す**（ホストが起動しない） | **対象** | 本巡の主題 |
| 2 | `ExtractedFrom` 列 ＋ 非一意索引の追加（#912） | 崩さない | 除外 | FK でも NOT NULL でもない。軸A で実測 |
| 3 | VSA 再編（IADR-0282）による名前空間移動 | 崩さない | 除外 | ビルドが通る＝参照は解決している（実測） |
| 4 | `AddPlatformWolverineBroker()` を readiness へ追加 | 崩さない | 除外 | 6 件は `/health/ready` を叩かない |
| 5 | `EdgeTypeFallbackMetrics` / カタログ口（#962）の追加 | 崩さない | 除外 | 起動時に外部依存を持たない |
| 6 | 段宣言（pipeline.json）への graph-delete / graph-sync 追加 | 崩さない | 除外 | 軸D で一致を実測 |

**除外は 5 件。うち「崩さない」と判定した根拠はいずれも本仕様書内に実測コマンドで残した。**

## 発見（本巡の中身）

🔴 **先行巡が置いた 6 件は、いま Docker のある環境で走らせると 1 件も防壁へ到達しない。**

`GraphServiceFactory` はブローカを渡さないので、`RabbitMq:ConnectionString` が未設定のまま
Program.cs の既定値 `amqp://guest:guest@rabbitmq:5672`（compose 前提のホスト名）へ繋ぎに行く。
`IntegrationTestFactory.cs` 自身が ADR-0027 / #441 E1 の実測としてこの失敗を記録している ——
**「Wolverine は接続先をホスト構築時に読む」「`BrokerInitializationException: Unable to initialize
the Broker rabbitmq in time` になる」。**

しかも失敗の起き方が悪い。`CreateClient()` は `InitializeAsync` の中にあり、
**`DockerRequired.SkipUnlessAvailable()` より先に走る。** つまり:

- **Docker が無い環境**（本作業環境・PR の CI）: `postgres.IsAvailable` が false なので
  `CreateClient()` は呼ばれず、6 件は skip される。**何も起きないので誰も気付かない。**
- **Docker がある環境**（`integration.yml`。**この 6 件が初めて実走する場所**）: ホスト起動で落ちる。

→ **「防壁が機能することを確認する」ための 6 件が、確認できないまま緑（skip）で通過し続ける。**
#941 が正そうとしている当の形（在ることになっているが機能したことがない）が、
**防壁からその検査器へ 1 段くり上がって再発していた。**

## 対象範囲

- **対象**: `Knowledge.IntegrationTests` の GraphService 用の器とテストクラスの配線のみ。
- **対象外**: 防壁そのもの・`Features/`・マイグレーション。**防壁は既に在り、宣言は正しい**
  （下の「マイグレーション走査」で実測）。本番コードは 1 行も変えない。
- **対象外**: `.ai-context/specs/20260823_...`（先行巡の記録）の本文書き換え。
  日付つき追記で前方参照だけを足す（`traceability.repo.md` の凍結の射程 ①）。

## 設計（→ IADR-0291）

1. **`GraphServiceFactory` は `RabbitMqFixture` を必須の引数として受け取る。**
   既定値も null 許容も置かない —— **ブローカ無しでは構築できない形**にして、同じ退行を
   型で止める。検査器は足さない（同型の事故はまだ 1 回目である。規約追加の条件を満たさない）。
2. **テストクラスは `IClassFixture<RabbitMqFixture>` を足し、`InitializeAsync` の門を
   `postgres.IsAvailable && rabbit.IsAvailable` にする。** 既存の `WikiSyncTests` と同じ形。
3. **`DockerRequired` のままにする（`BrokerRequired` へ寄せない）。** この 6 件は Postgres を
   必ず要るので、「ブローカだけ外から与える」経路は成立しない。判定を緩めると、Postgres が
   無い環境で skip されずに落ちる。
4. **本番の配線はそのまま使う**（器の既定方針）。Wolverine を剥がす選択は採らない —— 剥がすと
   「出荷される版の起動経路」を試験しなくなり、#941 が退けた「変異版だけを試験する」に戻る。

## 受け入れ基準

- [x] `GraphServiceFactory` はブローカ無しでは構築できない（コンパイルが通らない）。
- [x] 6 件のテストクラスが Postgres とブローカの双方の可用性を門にする。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
- [x] マイグレーションが `ON DELETE RESTRICT` / 2 つの UNIQUE 索引 / アンカー列の
      `NOT NULL DEFAULT ''` を**実際に出力している**ことを、DDL の生成物で確かめた。
- [x] 本番コード（`Features/` ・ `Migrations/`）を 1 行も変えていない。

## マイグレーション走査（Docker 無しで確かめられる上限）

**モデル宣言とマイグレーションの間に差が無いこと**を機械で確かめた。

```console
$ dotnet ef migrations has-pending-model-changes --project .../GraphService.csproj --context GraphDbContext --no-build
No changes have been made to the model since the last migration.
```

**マイグレーションが出力する DDL そのもの**を生成して走査した（DB は要らない）。

```console
$ dotnet ef migrations script --project .../GraphService.csproj --context GraphDbContext --no-build --idempotent -o graph.sql
$ grep -nE "FOREIGN KEY|ON DELETE|CREATE UNIQUE INDEX|Anchor" graph.sql
48:  "SourceAnchor" character varying(200) NOT NULL DEFAULT '',
49:  "TargetAnchor" character varying(200) NOT NULL DEFAULT '',
53:  CONSTRAINT "FK_edges_edge_types_EdgeTypeId" FOREIGN KEY ("EdgeTypeId") REFERENCES edge_types ("Id") ON DELETE RESTRICT
61:  CREATE UNIQUE INDEX ux_edge_types_name ON edge_types ("Name");
89:  CREATE UNIQUE INDEX ux_edges ON edges ("SourceDocumentId", "TargetDocumentId", "EdgeTypeId", "SourceAnchor", "TargetAnchor");
```

判ること／判らないことを分けて書く。

| 検証対象 | 静的に確かめたこと | **確かめられていないこと** |
| --- | --- | --- |
| `ON DELETE RESTRICT` | DDL が `ON DELETE RESTRICT` を出力する。FK は `edges` の 1 本だけ | **実際に削除を拒むか** |
| `ux_edge_types_name` | `CREATE UNIQUE INDEX` を出力する | **実際に 2 件目を拒むか** |
| `ux_edges` | 5 列を**この順で**並べた `CREATE UNIQUE INDEX` を出力する | **実際に 2 行目を拒むか** |
| アンカーの空文字既定 | `NOT NULL DEFAULT ''`。**NULL 可ではない**ので `ux_edges` の前提は成立する | 保存値が実際に `''` か |
| 409 変換 | 分岐が**コード上に在る**（`DbUpdateException` を捕まえる箇所が POST / PUT / DELETE に各 1） | **分岐が実際に通るか** |

🔴 **右の列は 1 つも埋まっていない。** 静的走査は「宣言が正しい」までしか言えず、
**#941 が問うている「発火した」は Docker のある環境でしか埋まらない。**

## 実走の確認手順（本巡で最も重要な出力）

**この 6 件は PR では走らない。** `ci.yml` は `--filter "Category!=Integration"` で除外し、
回収先は `integration.yml`（develop への push ＋ 日次 ＋ 手動）である（IADR-0232 決定 3）。
**マージされるまで、本巡の是正が効いたかどうかは判らない。**

マージ後、`integration.yml` の**生の出力**を次の順で読む。**上から順に、前が満たされない限り
次を読む意味は無い。**

1. **ホストが起動したか。** ログに `BrokerInitializationException` / `Unable to initialize the
   Broker rabbitmq` が **0 件**であること。1 件でも出ていれば本巡の是正は効いていない。
2. **6 件が `Passed` として現れるか。** 名前で確かめる（`EdgeTypeDbGuardTests` の 6 件）。
   **`Skipped` が 1 件でも残っていたら、その件は依然として何も測っていない。**
3. **`Knowledge.IntegrationTests` の `Passed` が 30 → 36 に増えているか**（本巡時点の実測 30）。
   `Total` は 70 のまま変わらない（件数は増やしていない）。
4. `check-coverage-floor` の出現レポート数が**据え置き**であること（増えていたら二重実行）。

🔴 **緑・0 件・skip はいずれも「測った証拠」にならない。** 手動で先に確かめたいときは
`workflow_dispatch` で `integration.yml` を回す（`force_failure` は false のまま）。

## 計画書との差異

- 差異: なし。**防壁の宣言は計画（ADR-0033 決定 5・6・9）どおりであり、本巡は器の欠落を直した
  だけである。** 計画への環流は不要。

## 残件・申し送り

1. 🔴 **6 件は本作業環境では 1 件も実走していない**（Docker daemon が無い）。是正の妥当性の根拠は
   (a) 他 6 サービスの器との対比、(b) `IntegrationTestFactory.cs` が記録する #441 E1 の実測、
   (c) `Program.cs` が構築時にブローカへ繋ぐことの読み取り、の 3 点であり、**実走ではない。**
   上の「実走の確認手順」で埋めること。
2. 先行巡の残件（`character varying(N)` の長さ制約は防壁として未検証）はそのまま残る。
3. **同型の退行を止める検査器は置いていない。** 「サービスが Wolverine ホストを起こすように
   なったのに、その統合テストの器がブローカを渡していない」を機械で検出するのは容易だが、
   本リポジトリの規約は**同型の事故が 2 回起きてから**検査器を足す（1 回目は記録に留める）。
   **本件はその 1 回目である。** 2 回目が起きたら、`Program.cs` の `UseWolverine` 出現集合と
   器の引数を突き合わせる検査を足すこと。
