---
title: 作業仕様書 — DashboardService.Api.Tests を xUnit1051 から剥がす（段階採用の第 1 プロジェクト）（#882）
type: spec
status: done
related_ids:
  - NFR
  - FR-10
  - UC-05
  - ADR-0030
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_xunit1051-staged-adoption-harness.md"
issue: "#882"
---

# 作業仕様書 — DashboardService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

[`IADR-0238`](../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md) が置いた器を使い、
**最初の実コード PR** として `DashboardService.Api.Tests`（実測 24 箇所）を移行する。

**1 PR = 1 プロジェクト。** 本 PR は `Refs #882`（`Closes` は最後の 1 本だけ）。

### 起点 ID の置き方（コミット件名に `FR-10` / `UC-05` を書かない理由）

frontmatter の `related_ids` には `FR-10` / `UC-05` を挙げているが、**コミット / PR 件名の
スコープは `NFR,IADR-0238` であり、意図的に計画 ID を含めていない。**

- `related_ids` の `FR-10` / `UC-05` は「**移行対象のテストが何を検証しているか**」という文脈である
  （`DashboardEndpointTests.cs` の先頭コメントが `FR-10, UC-05` を名乗っている）
- 一方、**本 PR が行ったのはテストの衛生（アナライザ規則の採用）であって、`FR-10` / `UC-05` の
  実装ではない。** 受け入れ基準も計画側ではなく [[IADR-0238]] 側にある
- `.claude/rules/traceability.md` は「**無理に近い番号を付けない。実在しない対応づけを作ると、
  監査が『その要求の実装』として数えてしまい、無採番より劣化する**」と定めている。
  規約整備・検査器・テスト衛生といった**メタ作業は無採番 `NFR` を使う**のが同ルールの指示である

**したがって件名の `NFR` は「番号を調べ損ねた」のではなく、規約どおりの選択である。**

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を `TestContext.Current.CancellationToken` へ直す
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true` にする
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ**同じ綴りで**追加する

どれか 1 つでも忘れると `check-xunit1051-ratchet.js` が落ちる
（`props-desync` / `migrated-nonzero`）。🔴 **`Contains` は大文字小文字を区別する。**

## 対象の母集合（走査で引いた。推定ではない）

`dotnet build src/knowledge/backend/backend.slnx -t:Rebuild -p:NoWarn= -m:1` の出力を
ファイル・行・列で一意化して引いた。**24 箇所・3 ファイル。**

| ファイル | 件数 | 行 |
| --- | ---: | --- |
| `DashboardEndpointTests.cs` | 21 | 18,28,38,49,50,51,55,70,71,73,88,89,90,92,107,108,109,111,134,155,172 |
| `IntrospectionEndpointTests.cs` | 2 | 21,24 |
| `HealthEndpointTests.cs` | 1 | 12 |

**除外したもの**: 同プロジェクトの `TestWebApplicationFactory.cs` / `TestAuthHandler.cs` /
`GlobalUsings.cs` は診断が 0 件（テストメソッドを持たない補助クラス）。走査結果に現れないので触らない。

現れた呼び出しは **5 種のみ**で、いずれも `CancellationToken` を受けるオーバーロードを持つ:
`PostAsJsonAsync` / `GetFromJsonAsync` / `GetAsync` / `SendAsync` / `ReadFromJsonAsync`。
**判断を要する箇所はゼロ**（引数を 1 つ足すだけで、オーバーロード解決もタイムアウト挙動も変わらない）。

`using` の追加は不要 —— 同プロジェクトの `GlobalUsings.cs` が `global using Xunit;` を持つ。

## 受け入れ基準

| 基準 | 確認方法 |
| --- | --- |
| 24 箇所すべてが `TestContext.Current.CancellationToken` を渡す | `-p:NoWarn=` 付きビルドで当該プロジェクトの xUnit1051 が **0 件** |
| **再発したらビルドが落ちる**（剥がしたら戻れない） | 変異試験（1 箇所を元へ戻す → `error xUnit1051` / exit 1） |
| **テスト件数が減らない** | 移行前後で `dotnet test` の合格件数が同じ |
| 他プロジェクトの残件が変わらない | baseline の他行と実測が一致（`check-xunit1051-ratchet.js`） |
| 器の 3 点が揃っている | `check-xunit1051-ratchet.js` が exit 0 |

## 結果

置換は **24 箇所・3 ファイル**。**引数を 1 つ足す以外の変更はしていない**
（属性・アサーション・テスト名・制御フローに手を触れていない）。

置換のたびに**パターンごとの出現件数を assert** してから置換した（同一テキストが複数箇所に
現れるため —— 例: `SendAsync(req)` は 3 箇所、`GetFromJsonAsync<List<SearchTrendDto>>(...)` は 2 箇所）。
合計が 21（主ファイル）＋ 3（他 2 ファイル）＝ **24** になることを最後に assert した。

### 受け入れ基準の結果

| 基準 | 結果 |
| --- | --- |
| 24 箇所すべてが `TestContext.Current.CancellationToken` を渡す | ✅ `-p:NoWarn=` 付き再測定で当該プロジェクトが**一覧から消えた**（0 件）。knowledge 合計 **526 → 502**（ちょうど −24） |
| **再発したらビルドが落ちる** | ✅ M-1（下表）で `error xUnit1051` / exit 1 |
| **テスト件数が減らない** | ✅ **16 → 16**（属性数も develop 版と一致: 16 / 1 / 1） |
| 他プロジェクトの残件が変わらない | ✅ 他 9 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | 移行済みの 1 箇所（`HealthEndpointTests.cs:12`）を元へ戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 24` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから当該プロジェクトを外す | `props-desync` | **exit 1・当該 1 件のみ** |

🔴 **M-1 の「変異が当たった」証跡は `git diff` が空になったことである** ——
その 1 箇所を戻すと当該ファイルは develop の内容と**バイト一致**に戻るため、差分が消える。
（他の変異のように「差分が出ること」ではなく「差分が消えること」が変異の証拠になる、珍しい形。）

復旧はいずれも `cmp` でバイト一致を確認し、復旧後に検査器が exit 0 へ戻ることを実測した。

### 検証

- `dotnet build src/knowledge/backend/backend.slnx`（Release）→ **0 エラー**、警告は既存の `CS0618` 2 件のみ
- `dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"` →
  **586 件・0 失敗・2 skip**
- `scripts.test.js`（`REQUIRE_REPO_TESTS=1`）→ **584 件 all passed**

## 🔴 推定と実測の乖離について（本 PR で記録する）

段階採用の順序は当初「`await` の出現数からの外挿」で決めていた。**その推定は実測と 2 倍以上ずれていた。**

| | 値 |
| --- | --- |
| 調査段階の推定合計 | **~1,930** |
| 実測合計 | **943** |

原因は外挿の**方法**ではなく、**校正に使った定数が二重計上だった**ことである。

- 推定は「`await` 数 × 1.32」で作られていた。この 1.32 は `1,886 ÷ 1,429`（測定時点 `6c1185c`）である
- **その 1,886 が 2 倍の重複計上だった**（MSBuild が 1 件をビルド中とサマリの 2 箇所へ出す）
- 真の比は `943 ÷ 1,429 = 0.66` であり、**1.32 はちょうどその 2 倍**

**正しい比 0.66 で外挿し直すと `1,462 × 0.66 = 965` となり、実測 943 との差は約 2%** である。
つまり **`await` からの外挿という方法自体は妥当**で、壊れていたのは校正定数だけだった。

さらに、当初「**この比 1.32 はリポジトリ固有**（AST は `await` 1,748 に対し診断 1,054 で比 0.60）。
他リポへ持ち出さないこと」と注意されていたが、**この「固有性」も二重計上の産物**である。
訂正後の MSP の比 **0.66** は AST の **0.60** に近く、両者を隔てていた差は消える。

> ⚠️ **AST 側の 1,054 が同じ二重計上でないことは未検証である。** もし AST も 2 倍で数えていれば
> 真の比は 0.30 となり、結論は逆になる。**確かめずに「比は共通」と言い切らないこと。**
> 確かめるには AST を `-p:NoWarn=` 付きでビルドし、ファイル・行・列で一意化して数える。

### 教訓（「推定は実測ではない」の実例）

- **推定値には必ず「推定である」と書く。** 本件は 3 つの文書が推定由来の 1,886 を断定形で載せていた
- **外挿の校正定数は、それ自体が実測であることを確かめる。** 方法が正しくても定数が壊れていれば
  結果は壊れる。しかも**倍率誤差は「それらしい値」に見えるので気付きにくい**
- **プロジェクト別の内訳は、合計以上に外れる。** `Knowledge.IntegrationTests` は推定 ~255 に対し
  実測 **4**（60 倍以上）。`await` の数は `CancellationToken` を受けるオーバーロードの有無と
  相関しないため、代理指標として使えない

## 移行順序の見直し（実測で引き直した）

推定順と実測順は**一致しない**。13 プロジェクト中**順位が同じなのは 5 件だけ**で、転倒数は 78 対中 15 である。

| プロジェクト | 推定 | 実測 | 推定順 | 実測順 |
| --- | ---: | ---: | ---: | ---: |
| `Knowledge.IntegrationTests` | ~255 | **4** | 12 | **1** |
| `DashboardService.Api.Tests` | ~32 | 24 | 1 | 2 |
| `AiAnalysisService.Api.Tests` | ~54 | 30 | 3 | 3 |
| `FeedbackService.Api.Tests` | ~41 | 30 | 2 | 4 |
| `IngestionService.Worker.Tests` | ~79 | 33 | 4 | 5 |
| `RetrievalService.Api.Tests` | ~108 | 47 | 5 | 6 |
| `WikiService.Api.Tests` | ~137 | 51 | 7 | 7 |
| `AuthorizationService.Api.Tests` | ~110 | 74 | 6 | 8 |
| `DataSourceService.Api.Tests` | ~183 | 75 | 9 | 9 |
| `DocumentService.Api.Tests` | ~202 | 94 | 10 | 10 |
| `LlmGateway.Api.Tests` | ~161 | 114 | 8 | 11 |
| `ConversionService.Worker.Tests` | ~239 | 138 | 11 | 12 |
| `Platform.Bff.Tests` | ~317 | 229 | 13 | 13 |

**最も大きく動いたのは `Knowledge.IntegrationTests`（12 位 → 1 位）**である。

### 差し替えた順序（実測の昇順。以降はこれに従う）

1. `SampleService.Tests`（雛形・**1**）
2. `Knowledge.IntegrationTests`（**4**）
3. **`DashboardService.Api.Tests`（24）← 本 PR**
4. `AiAnalysisService.Api.Tests`（30）／ `FeedbackService.Api.Tests`（30）
5. `IngestionService.Worker.Tests`（33）
6. `RetrievalService.Api.Tests`（47）
7. `WikiService.Api.Tests`（51）
8. `AuthorizationService.Api.Tests`（74）
9. `DataSourceService.Api.Tests`（75）
10. `DocumentService.Api.Tests`（94）
11. `LlmGateway.Api.Tests`（114）
12. `ConversionService.Worker.Tests`（138）
13. `Platform.Bff.Tests`（229）

別枠: `Platform.Shared.Infrastructure.Tests`（**0**）は U5 / Wolverine 移行チェーンの着地後。

🔴 **厳密な昇順では本 PR は 3 番目である**（1 と 2 のほうが小さい）。本 PR を先に出したのは
**24 箇所すべてを目視済みで判断を要する箇所がゼロ**という、器の実地検証に最も適した性質による。
順序を「小さい順」から外した唯一の箇所であり、以降は上の昇順に従う。
