---
title: 作業仕様書 — IngestToSearchQdrantTests の廃止予定の QdrantBuilder() を配備と同じ版の明示へ改め、CS0618 を 0 件にする（#1790）
type: spec
status: done
related_ids: [FR-02, FR-03, NFR, ADR-0009, ADR-0016, IADR-0315, IADR-0390]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1790"
---

# 作業仕様書 — 統合試験の Qdrant イメージを配備と同じ版へ固定する（#1790）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `2f2aa957`。
> 計画は project-planning `aa068ac`（隣接クローン・読み取り専用）の `02_requirements/01_requirements.md`（FR-02・FR-03）を読んだ。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-02**（取り込み・索引登録）・**FR-03**（横断検索）。対象の試験は両者の段間結合（[[IADR-0390]]）。
- 非機能要件: 無採番 `NFR`（試験の保守）。計画 ADR: ADR-0009（Qdrant）・ADR-0016。
- 版の追随の向き: [[IADR-0315]]（サーバ版はクライアント版 `Qdrant.Client` 1.18.1 へ揃える）。
- 先行: #1746（`LexicalIndexQdrantTests` の版の固定）・#1760（`KeywordIndexQdrantTests`）・#1772（台帳 C-5）。

## 問題

`IngestToSearchQdrantTests.cs:63` の `new QdrantBuilder().Build()` は Testcontainers 4.12.0 で廃止予定（`CS0618`）であり、
しかも `qdrant/qdrant:v1.13.4` を起こす。配備（`deploy/docker-compose.yml:136`・`deploy/local/infra/qdrant.yaml:18`）は
`qdrant/qdrant:v1.18.1` である。同じプロジェクトの 2 試験は版を `private const string QdrantImage` で**各自に**持っていた。

## 受け入れ基準

- AC1: knowledge の `backend.slnx` の `dotnet build` で `CS0618` が 0 件（警告 0 件）。platform の `backend.slnx` も警告 0 件のまま。
- AC2: 実 Qdrant を起こす 3 試験（IngestToSearch / LexicalIndex / KeywordIndex）が同じ定数 `QdrantTestImage.Reference`
  （`qdrant/qdrant:v1.18.1`）で起こす。
- AC3: 配備の 2 宣言と定数が食い違うと、コンテナを起こさない試験（PR の `ci.yml` で走る）が赤くなる（変異で確かめる）。
- AC4: Docker のある環境（`integration.yml`）で `IngestToSearchQdrantTests` が v1.18.1 で緑（本環境は Docker が無く未実施。PR で報告する）。

## 母集合（規則 9・10。`2f2aa957` 時点）

### 規則 9（誤りの側の文字列で走査）

走査: `grep -rnE "new (Qdrant|PostgreSql|Redis|RabbitMq|Keycloak|Container|MsSql|Azurite|Minio|Kafka|Nats|Mongo|Elasticsearch|Ollama)[A-Za-z]*Builder\(" src --include=*.cs`、
`using (Testcontainers|DotNet.Testcontainers)` を持つ全ファイル（knowledge 9 件）、`grep -rn "qdrant/qdrant"` の全追跡ファイル、
両 `backend.slnx` のビルド出力の `CS0618`。

| 箇所 | 形 | 扱い |
| --- | --- | --- |
| knowledge `Search/IngestToSearchQdrantTests.cs:63` | `new QdrantBuilder()`（引数なし・v1.13.4） | **対象**（AC1・AC2） |
| knowledge `Search/LexicalIndexQdrantTests.cs` / `KeywordIndexQdrantTests.cs` | `new QdrantBuilder(QdrantImage)`・各自の定数 | **対象**（AC2。定数を 1 箇所へ畳む） |
| knowledge `Fixtures/PostgresFixture.cs`・`DataSourceService/DataSourceSyncSingleWriterTests.cs` | `new PostgreSqlBuilder("postgres:16-alpine")` | 除外 E1（引数あり・廃止予定でない。配備と同じ参照） |
| knowledge `Fixtures/RabbitMqFixture.cs` | `new RabbitMqBuilder("rabbitmq:3.13-alpine")` | 除外 E1（配備は `3.13-management-alpine`。下記 E2） |
| knowledge `Fixtures/SeaweedFsContainer.cs` | `new ContainerBuilder(Image)`（digest 固定） | 除外 E1（定義試験が既にある） |
| platform `backend` | Testcontainers の使用なし | 除外（該当なし） |
| `src/ai-stock-trading/backend`（submodule） | `PostgreSqlBuilder("postgres:16")` 等、すべて引数あり | 除外 E3（別リポ・廃止予定の使用なし） |
| ビルド出力 `CS0618` | knowledge 1 件（上の 63 行目のみ）・platform 0 件 | 対象は 1 件 |
| `docs/how-to/run-integration-tests-without-docker.md:47` | `nerdctl run … qdrant/qdrant:latest`（外部 Qdrant を手で立てる手順） | 除外 E4 |

- **E1**: 引数ありの構築子は廃止予定ではない。本件の受け入れ基準（CS0618・Qdrant の版）の外。
- **E2**: RabbitMQ の試験（`3.13-alpine`）と配備（`3.13-management-alpine`）はタグが違う（版は同じ 3.13。管理 UI の有無）。
  警告は出ておらず、本件の範囲外。記録に留める。
- **E3**: submodule は別リポジトリであり、本 PR では触らない。
- **E4**: 手順書の `latest` は手で立てる外部 Qdrant の例であり、試験の定数ではない。本件の範囲外として記録に留める。

### 規則 10（この変更で新たに誤りになる自分の記述）

- `LexicalIndexQdrantTests` の注記「既定の `QdrantBuilder()` は v1.13.4 を起こす」は事実のまま残る（誤りにならない）。
  定数の置き場所が変わるので「置き場所は `QdrantTestImage`」を追記した。`KeywordIndexQdrantTests` の注記も同じ。
- 「`QdrantImage`」の語で全文を走査し、定数の削除で宙に浮く参照が無いことを確かめた（残るのは新しい試験の正規表現の名前だけ）。

## 設計

1. `Fixtures/QdrantTestImage.cs` に `public const string Reference = "qdrant/qdrant:v1.18.1";` を置く（**単一の置き場**）。
   3 試験はこれを `new QdrantBuilder(QdrantTestImage.Reference)` で使う。
2. `Search/QdrantTestImageDefinitionTests.cs`（Trait なし＝`ci.yml` の PR で走る）が、`deploy/docker-compose.yml` と
   `deploy/local/infra/qdrant.yaml` の `image:` 行の qdrant 参照を全部拾い、**1 件以上あること**と**全部が定数と等しいこと**を確かめる。
   - 前例: `SeaweedFsContainerDefinitionTests`（配備と同じイメージを試していることを、コンテナを起こさずに PR で止める）。
   - 試験と配備の Qdrant の版のずれは #1746（Lexical）と本件の **2 回目**であり、「同型 2 回で検査を足す」に当たる。
   - [[IADR-0315]] の「機械検査は置いていない」は**クライアント版とサーバ版の突合**についての記述で、本検査（試験と配備の突合）とは別である。
     ただし本検査で試験・compose・k8s の 3 箇所は一致が強制される（クライアント版との突合は引き続き無い）。
   - リポジトリの検査器（`scripts.repo.test.js`）ではなく xUnit に置くのは、定数が C# 側にあり、前例と同じ層で読めるため。
3. 新しい判断は「試験の版の置き場所」だけで、IADR-0315 の追随の向きを変えない —— **IADR は起こさない**。

## 実測

- ビルド（`dotnet build`、knowledge は変更後 `--no-incremental`）:

  | | 変更前 | 変更後 |
  | --- | --- | --- |
  | knowledge `backend.slnx` | 警告 1（`CS0618` 1） | **警告 0** |
  | platform `backend.slnx`（submodule 初期化済み） | 警告 0 | 警告 0 |

- 変異: 定数を `v1.13.4` に変えると `QdrantTestImageDefinitionTests` の 2 件（compose・k8s）が赤くなる。戻すと緑。
- 統合試験（AC4）: 本環境には Docker デーモンが無く実行していない。`integration.yml` を当ブランチで手動起動して確かめる。
