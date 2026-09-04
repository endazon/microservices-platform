---
title: IADR-0370 単体 / 結合の区分は Category とは別の TestKind トレイトで表し、CI の振り分けには使わない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0065
  - IADR-0161
  - IADR-0232
  - IADR-0237
  - IADR-0289
  - IADR-0334
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 3
---

# IADR-0370: 単体 / 結合の区分の表し方（#1145）

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0065` 決定 3（3 行目）／`NFR`（無採番。テスト基盤の整備であり、計画側の
  非機能要件表に当たる番号が無い。`.claude/rules/traceability.md`「起点 ID の種別」の 2 に当たる）
- 関連する実装仕様書: `.ai-context/specs/20260904_issue-1145_unit-integration-trait.md`
- issue: #1145（#1063 の申し送り）

## コンテキストと課題

計画 `ADR-0065` 決定 3 は `Tests/` を本体の鏡写しへ改めた際、**「単体か結合かはフォルダではなく
テストの書き方（トレイト・命名）で表す。区分そのものを捨てるのではない」**と条件を付けた。
`IADR-0334`（移送本体）は「テストの内容を書き換えない」制約を持っていたため区分の表現へ触れず、
#1145 へ申し送った。**移送で失われた区分を、別の軸で置き直すのが本 IADR である。**

決めるのは 3 点である。

1. **トレイトか命名規約か。**
2. **トレイトなら、どの名前を使うか。**
3. **CI で種別ごとの選択実行を行うか。**

🔴 **2 が本 IADR の要である。** 本リポジトリには既に `[Trait("Category", ...)]` が実在し、
**CI の振り分けに load-bearing である** —— `ci.yml` の `backend-build` は
`--filter "Category!=Integration"` で PR から除外し、`integration.yml` が `--filter` 無しの全量で
回収する（`IADR-0232` 決定 3・改定 3）。

**そこで言う `Category=Integration` の外延は「Testcontainers で実コンテナ（実 Postgres / 実 RabbitMQ /
実 MinIO）を起こす」である。** 同じ `Knowledge.IntegrationTests` に居ながら `Category=Deployment`
（helm マニフェスト検証）と `Category=EndpointRouting`（インプロセス）は Docker を使わないので
**PR で走り続ける** —— `ci.yml` のコメントが明示している。

**一方、本作業が表そうとしている「結合」の外延は違う。** 14 サービスの `Tests/` で結合に当たる
101 クラスのうち **96 クラスは `TestWebApplicationFactory`** を通すものであり、これらは
`IADR-0161` により **InMemory DB（クラスごとに一意な名前）** を使い、ブローカも
`TestRabbitMqConfiguration` で差し替えている。**Docker を 1 つも要さない。**
実測でも per-service `Tests/` に Testcontainers の本文一致は **0 件**である
（陽性対照: 同じ正規表現が `Knowledge.IntegrationTests` では 29 ファイルを拾う）。
これは `IADR-0289` が別の経路で確かめた「per-service の器はブローカを持たない」とも整合する。

**したがって `Category=Integration` を per-service の結合テストへ流用すると、
Docker を要さない 758 個の `[Fact]`/`[Theory]` 宣言（実行 875 件）が PR の CI から静かに消える。**
`IADR-0232` が回収先を用意して意識的に外したものとは性質が違う ——
**こちらには回収先が無い**（`integration.yml` は `--filter` 無しで全量を回すので回収はされるが、
PR の待ち時間は縮まらないのに PR の精度だけが落ちる。交換になっていない）。

## 検討した選択肢

| 案 | 機械選択 | `Category` との衝突 | 評価 |
| --- | --- | --- | --- |
| **A. 命名規約**（`*IntegrationTests` / `*Tests`） | `--filter "FullyQualifiedName~IntegrationTests"` で一応可能 | 無し | **却下**。①既存 217 クラス中 `*EndpointTests` 等の綴りが結合と一致せず、**内容の書き換え（リネーム）が要る**（#1063 と同じ制約に抵触）。②綴りは**強制されない**ので、次のクラスが違う綴りを採れば静かに外れる。③`IADR-0334` が「ファイル名は所属の問いを内容の問いへ読み替えさせる」と警告した罠に、種別の軸でも入る |
| **B. `Category` トレイトを流用** | 可 | 🔴 **衝突する** | **却下**。上記のとおり **PR の CI から 875 件が消える**。`ci.yml` を同時に直せば避けられるが、必須 check の走る中身を本 PR の射程外の理由で変えることになる |
| **C. 新しいトレイト名 `TestKind`（採用）** | 可 | 無し | **採用**。既存の軸を 1 バイトも動かさずに、`ADR-0065` 決定 3 が求める区分を独立した軸として持てる |

## 決定

### 決定 1: トレイトで表す。名前は `TestKind`、値は `Unit` / `Integration`

```csharp
[Trait("TestKind", "Unit")]
public class SampleAggregateTests
```

**2 つのトレイトは別の軸である。混ぜない。**

| トレイト | 問い | 使い手 |
| --- | --- | --- |
| `Category`（既存・本 IADR は触らない） | **実コンテナ（Docker）を起こすか** | `ci.yml` / `integration.yml` の振り分け（`IADR-0232`） |
| `TestKind`（本 IADR） | **単体か結合か**（`ADR-0065` 決定 3 の区分） | 開発時の選択実行・区分の宣言 |

**`Category` を「実行環境の要求」と読み替えて流用しない。** 同じ語（`Integration`）を外延の違う
2 つの意味で使うと、**次に読む者はどちらの意味で書かれた `Integration` かをコードから判定できない。**

### 決定 2: 「結合」の判定は signal で決め、印象で決めない

`docs/tests/TEST_STRATEGY.md`「テスト種別と責務」の定義を機械で引ける形へ落とす。
**次のいずれかに当たれば結合、どれにも当たらなければ単体である。**

| signal | 内容 | 実測 |
| --- | --- | --- |
| A | `WebApplicationFactory` 派生を通す（`.CreateClient()` / `CreateDefaultClient` を含む） | 96 |
| B | `HostApplicationBuilder` / `Host.CreateApplicationBuilder` / `new HostBuilder` / `WebApplication.CreateBuilder` を自前で組む | 3 |
| C | プロセス外の実資源への到達を `Assert.SkipUnless` / `Assert.SkipWhen` で門にしている（実 `pandoc` / 実 `pdftotext`） | 2 |
| D | Testcontainers / `DockerRequired` / `PostgresFixture` / `RabbitMqFixture` / `MinioFixture` / `BrokerRequired` / Respawn | **0** |

**共通するのは「検証対象の外側にある合成、または実資源を実際に通すか」である。**
`TEST_STRATEGY` が結合の道具に `Mvc.Testing` を挙げているのは A の意味であり、
単体を「ドメイン規則・ハンドラの分岐」と書いているのは「型を直接呼ぶ」の意味である。

🔴 **走査はコメントを剥がしてから当てる。** 剥がさないと **6 件が偽陽性になる** ——
`DatabaseConnectorTests` / `QdrantFullTextIndexBootstrapTests` ほかは、本文ではなくコメントで
`DockerRequired` / `Testcontainers` に言及しているだけである（「実 SQL は follow-up の統合テストで
確認する」といった申し送り）。**この 6 件はすべて単体である。**

> **`UseInMemoryDatabase` で `DbContext` を組み、ホストを立てずにハンドラを直接呼ぶ 20 クラスは単体である。**
> InMemory プロバイダは**実 DB の代役**であって実依存ではない。`TEST_STRATEGY` の結合は
> 「実依存を伴う往復」であり、通しているのはテストダブルである。

### 決定 3: 付与の粒度はクラスとし、メソッド単位の上書きを置かない

217 クラスすべてが `TestKind` を 1 つだけ持つ。**メソッド単位の例外を置かない。**

理由は、置くと**両方のバケツの合計がフィルタ無しの件数と一致しなくなる**か、
一致を保つために「クラスの値をメソッドが上書きする」規則を人が覚える必要が生じるためである。
**合計の一致は本区分が壊れていないことを測る唯一の機械的な性質であり、それを守る。**

代償を 1 つ受け入れる。`PandocConversionServiceTests` / `PdfTextLayerConverterTests` は
**1 クラスの中に実バイナリを要する `[Fact]` と要さない `[Fact]` が混在**するが、
**クラス全体を結合とする** —— この 2 クラスは外部プロセスへのアダプタの試験であり、
主題は実バイナリとの往復にある。

### 決定 4: 置き場所の軸（`IADR-0334`）と種別の軸（本 IADR）は独立である

`Tests/` 直下に残る `HealthEndpointTests` / `IntrospectionEndpointTests` は
**置き場所が `Tests/` 直下であることと無関係に結合である**（signal A）。
逆に `Tests/Features/<集約>/<操作>/` に居ても単体のものがある。
**フォルダから種別を読まない。** `ADR-0065` 決定 3 が「種別でフォルダを割らない」と決めた以上、
逆向きの推論（フォルダから種別を導く）も成り立たない。

### 決定 5: 🔴 CI に新しい `--filter` を足さない。`ci.yml` / `integration.yml` を 1 バイトも触らない

issue #1145 は「CI で種別ごとの選択実行が要るかを判断する」と書いている。**要らない。**

1. **PR を速くしない。** per-service の結合 101 クラスは Docker を要さず、実測で
   `--filter "TestKind=Integration"` の 14 プロジェクト合計は 875 件・最長の脚（GraphService）で
   2 分 4 秒である。外しても PR のクリティカルパスは `Platform.Bff.Tests`（2 分）に張り付いたままで、
   **落ちるのは精度だけになる**（`IADR-0232` 決定 1 の判断基準「速くなるが精度が落ちる手段は、
   回収先を書けるなら採る」に対し、**そもそも速くならない**）。
2. **`integration.yml` へ足すと `IADR-0232` 決定 3 の 3 つの利点が同時に壊れる。**
   カバレッジ床の置き直し・二重集計・fail-closed の門。同 IADR は
   **「ここへ `--filter` を戻すなら門も必ず戻すこと」**と明記している。
3. **`TestKind` は母集合が 14 サービスに閉じている。** `Platform.Bff.Tests`（487 件）・
   `Platform.Shared.*.Tests`（293 件）・`Knowledge.Contracts.Tests`（47 件）・
   `Knowledge.IntegrationTests`（77 件）は持たない。**slnx 全体へこのフィルタを掛けると、
   これら 904 件が両方のバケツから落ちる。** CI をこのフィルタに依存させてはならない。

**したがって `TestKind` の使い手は開発時の手元実行である**（`dotnet test <slnx> --filter "TestKind=Unit"`）。
`ADR-0065` 決定 3 の受け入れ基準は「機械的に**選択できる**」であって「CI が選択して**いる**」ではない。

### 決定 6: 検査器は足さない。本 IADR を 0 回目の記録とする

「`TestKind` を持たないテストクラスが無いこと」は**構文だけで機械化できる**（`IADR-0334` の
「テストの主題」と違い、シンボル解決を要さない）。それでも足さない。
`CLAUDE.md` の規約は**同型の事故が 2 回起きてから検査器を足す**（1 回目は記録に留める）であり、
**本件は 0 回目である** —— 走査したが、トレイト付け忘れの**事故**の記録は本リポジトリに存在しない
（`IADR-0232` が挙げるのは *リスク* であって事故ではない）。

**代わりに 2 つの緩衝がある。**

- **付け忘れは fail-safe に倒れる。** 決定 5 により CI はこのフィルタに依存しないため、
  付け忘れたテストは **CI で走り続ける**（検証は 1 件も失われない）。
- **雛形が形を示す。** `templates/unit-template` の 4 クラスが単体 2・結合 2 を持ち、
  新しいユニットは付いた状態から始まる。

🔴 **1 回目が起きたら「個別の付け忘れ」として処理しないこと**（`IADR-0166` が同型の劣化を記録している）。
**2 回目が起きたら、`scripts/` へ「`Tests/` のトップレベル・テストクラスが `[Trait("TestKind", ...)]` を
1 つ持つこと」の検査を足す。** 判定に要るのは決定 2 の signal ではなく、属性の有無だけである。

## 理由

**トレイトを採ったのは、区分を「守られる形」で持つためである。** 命名規約は綴りに依存し、
綴りは強制されない。トレイトは `--filter` の対象であり、**合計の一致という測れる性質**を持つ。

**`Category` を避けたのは、外延の違う 2 つの概念に同じ語を当てないためである。**
本リポジトリでは「Integration」という語が既に 2 つの意味で使われている ——
`Knowledge.IntegrationTests`（層の名前）と `Category=Integration`（Docker が要る）。
**3 つ目を同じ名前空間へ足すと、`--filter` に書かれた `Integration` がどれを指すかを
コードから判定できなくなる。** 名前を分けるのは、読み手の推測を減らすためである。

**決定 5 が「足さない」を選べるのは、`IADR-0232` が既に軸を 1 本持っているからである。**
新しい軸を作ったからといって、CI の門を増やす理由にはならない。**門は 1 つで足りる。**

## 結果

- **良い影響**
  - `ADR-0065` 決定 3 の受け入れ基準 4 が満たされる。`dotnet test --filter "TestKind=Unit"` /
    `"TestKind=Integration"` で 14 サービスすべてを機械的に分けられる。
  - **合計の一致が実測で取れている**（14 プロジェクトすべてで
    `Unit + Integration = フィルタ無し`。1059 + 875 = 1934）。**両方が 0 でない**ことが
    フィルタの効いている陽性対照になっている。
  - 既存の `Category` 軸と `ci.yml` / `integration.yml` に一切影響しない。テスト件数も不変である。
  - 種別が**コードに宣言として現れる** —— 従前は「命名と `Assert.SkipUnless` から読む」しかなく、
    96 クラスの `TestWebApplicationFactory` 利用は名前からは読めなかった。
- **悪い影響 / トレードオフ**
  - 🔴 **トレイトが 2 つになる。** `Category` と `TestKind` を取り違えると、
    「Docker が要る」と「結合である」を混同する。**本 IADR の表がその区別の正本である。**
  - **母集合が 14 サービスに閉じている。** 射程外 5 プロジェクト（904 件）は `TestKind` を持たない。
    slnx 全体へフィルタを掛けると両バケツから落ちる（決定 5 の 3）。
  - **判定は時点に依存する。** 単体だったクラスが `TestWebApplicationFactory` を使い始めたら
    結合へ移す必要がある。`IADR-0334` 決定 2 が受け入れたのと同じ依存であり、同じ理由で受け入れる。
  - **機械検査が無い**（決定 6）。付け忘れは fail-safe に倒れるが、合計の一致は静かに崩れ得る。

## 関連

- 計画 ADR: `ADR-0065` 決定 3
- 実装 IADR: `IADR-0334`（置き場所の軸）、`IADR-0232`（`Category` 軸と CI の振り分け）、
  `IADR-0161`（器の InMemory DB 分離）、`IADR-0237`（陽性対照を対で置く）、
  `IADR-0289`（per-service の器は起動時依存を持つ）
- 作業仕様書: `.ai-context/specs/20260904_issue-1145_unit-integration-trait.md`
- issue: #1145（#1063 の申し送り）
