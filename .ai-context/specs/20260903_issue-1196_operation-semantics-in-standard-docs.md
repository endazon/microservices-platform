---
title: 「操作」の語義（契機の形では決めない）を live な標準文書と雛形へ書く（issue #1196）
type: spec
status: draft
created: 2026-09-03
updated: 2026-09-03
author: claude
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30)
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
related_ids:
  - NFR
  - ADR-0065
  - ADR-0068
  - ADR-0077
  - IADR-0282
  - IADR-0319
  - IADR-0334
  - IADR-0349
  - IADR-0350
---

# 仕様書: 「操作」の語義を基盤の標準文書へ書く

## 起点となる計画書（トレーサビリティ）

- 非機能要求（NFR）: 文書統制（規範として掲げる語の定義が live な標準文書にあること）
- 計画 ADR: `ADR-0077`「『操作』は契機の形で決めない。分界は入口の配線と操作の処理である」（Accepted 2026-09-03。
  環流 planning#527 ／ 反映 planning#532）、`ADR-0065` 決定 2、`ADR-0068` 決定 1・2
- 実装 ADR（先行）: `IADR-0319`（段は使う操作を数えた結果で決める）、`IADR-0349` / `IADR-0350`（個別ファイルの段の裁定）

## 背景と課題

`ADR-0077` §結果 は **「基盤（MSP）側の作業は生じない —— 基盤は既に本決定の形である」** と明言している。
**本作業はコードの是正ではない。文書だけである。**

穴は語義の側にある。`src/README.md`「サービス直下の標準構成」と `docs/tech/tech-requirements.md`
「プロジェクト構成（サービス単位）」は `Features/<集約>/<操作>/` を規範として掲げ、
**段の決め方**（`IADR-0319`）も**集約直下に残るもの**の列挙も書いているが、
**「操作とは何か」を 1 行も定義していない。** planning#527 が報告したのはこの穴であり、
AST 側は「操作＝登録表に登録された HTTP 端点」という暫定解釈（`ADR-0077` が退けた案 B）へ倒れた。

**実装 ADR は起こさない。** 計画側の決定をそのまま文書へ写す作業であり、実装判断が無い。

## 母集合の引き直し（規則 9・10。自分で走査した）

**記憶で挙げず、誤りの側の文字列で走査した。** 基点 `origin/develop` = `45853885`。
`git rev-parse --is-shallow-repository` = **`false`**（出典に `git log` を引ける）。

### 走査 A —— 「操作」を含む live 文書・雛形

```console
$ git grep -l "操作" -- src/README.md docs/ templates/ .claude/ | wc -l
83
```

**83 件は広すぎる。** 大半は画面仕様書の「利用者が操作する」という日常語であり、
`Features/<集約>/<操作>/` のアーキテクチャ用語ではない。**日常語の側は本件の母集合ではない。**

### 走査 B —— アーキテクチャ用語としての「操作」に絞る

```console
$ git grep -n -E "<操作>|操作フォルダ|操作単位|操作の数|使う操作|1 操作|操作をまたぐ" \
    -- src/README.md docs/ templates/ .claude/
```

**12 ファイル・26 行**にヒットした（`.claude/` は 0 件）。内訳と扱いは次のとおり。

| # | ファイル | 行 | 扱い | 理由 |
| --- | --- | --- | --- | --- |
| 1 | `src/README.md` | 80, 99–105, 119 | **是正する** | 規範を掲げているのに語義が無い。★ 追記先 |
| 2 | `docs/tech/tech-requirements.md` | 119, 142–147 | **是正する** | 同上。1 と同じ内容を持つので片方だけ直すと割れる。★ 追記先 |
| 3 | `templates/unit-template/README.md` | 22, 29, 116 | **是正する** | 複製元。例が HTTP 由来だけで案 B への導線が残る。★ 追記先 |
| 4 | `docs/how-to/adding-a-unit-submodule.md` | 31, 36 | 除外 | **既に `*Consumer` を操作フォルダの要素として挙げており**、`ADR-0077` 決定 1 の側にある（追随不要） |
| 5 | `docs/tech/composable-component-guide.md` | 71 | 除外 | 同上。`配置: <Service>/Features/<集約>/<操作>/（*Consumer.cs）` と既に書いている |
| 6 | `docs/functional/FR-14_composability.md` | 40 | 除外 | 固定/可変の**置き場**を挙げるだけで、契機の形に触れていない。語義の穴が無い |
| 7 | `docs/tech/composability-classification.md` | 89 | 除外 | 同上（分類表の置き場欄） |
| 8 | `docs/tests/TEST_STRATEGY.md` | 309 | 除外 | テスト側の鏡写しの記述。段は「叩く操作の数」で決めるとあり契機の形に触れていない |
| 9 | `templates/unit-template/backend/Services/SampleService/README.md` | 9, 15 | **除外（要注意）** | 同型の HTTP 由来の記述を持つが、**issue #1196 の宣言ファイル領域が `templates/unit-template/README.md` の 1 枚に限っている**。#1195 が `templates/unit-template/**` を後から触るため、領域を広げると並列判定が崩れる。**穴が残ることは認識しており、報告する** |
| 10 | `templates/unit-template/.../SampleService/Program.cs` | 32 | 除外 | 「入口は `Endpoint.cs` が持つ」というサンプル固有のコメント。コードには触れない（issue の射程外宣言） |
| 11 | `templates/unit-template/.../Features/Samples/Create/Command.cs` | 3 | 除外 | 同上（サンプルコード） |
| 12 | `templates/unit-template/.../Tests/**`（3 件） | — | 除外 | テスト側の段の記述。契機の形に触れていない |

**規則 10 の引き直し**: 本変更で新たに誤りになる自分の記述は無い。追記は既存の列挙に条件と実例を
足すだけで、**既存の記述を否定しない**（`ADR-0077` 自身が「決定を改めない（補完である）」と述べている）。
**導出値（件数）は走査ではなく数え直した** —— 下の実測を参照。

## 実測（実行したコマンドと出力。陽性対照つき）

`ADR-0077` の実測 1 を追試し、issue #1196 が数え直した 2 群も自分で引き直した。

### 実測 1 —— HTTP 端点を持たない操作フォルダは実在する

```console
$ git ls-files "src/knowledge/backend/Services/GraphService/Features/KnowledgeHealth/*"
.../GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs
.../GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthHostedService.cs
.../GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthOptions.cs
```

**3 件。`Endpoint.cs` は無い。** 唯一の契機がスケジュール実行である操作フォルダである。

**陽性対照**（走査が機能していることの証明）:

```console
$ git ls-files | grep -c "Features/.*/Endpoint\.cs$"
111
```

同じ引き方で 111 件出るので、上の「`Endpoint.cs` は無い」は**引っかからなかったのではなく、無い**。

🔴 **本作業で新たに見つけた補強**: `ADR-0077` と issue #1196 はどちらも `grep -i "KnowledgeHealth"` で
引いているが、その出力には **`DashboardService/Features/KnowledgeHealth/Report/Endpoint.cs`** が混ざる。
**同名の集約・操作（`KnowledgeHealth/Report`）が 2 サービスに在り、DashboardService 側は HTTP 契機、
GraphService 側はスケジュール契機である。** 結論は変わらない（GraphService 側に `Endpoint.cs` は無い）が、
**同じ操作名が契機の違いで別々の形を採っている**ことは決定 1 の実例としてむしろ強い。

### 実測 2 —— 契機が 2 つある操作は 1 つの操作である

```console
$ git ls-files "src/knowledge/backend/Services/DataSourceService/Features/DataSources/*"
（15 件。うち Sync/ は 4 件）
.../Features/DataSources/Sync/DataSourceSyncHostedService.cs
.../Features/DataSources/Sync/DataSourceSyncOptions.cs
.../Features/DataSources/Sync/DataSourceSyncService.cs
.../Features/DataSources/Sync/Endpoint.cs
```

**HTTP 端点と常駐ジョブが同じ操作フォルダに同居している。**

### 実測 3 —— 購読ハンドラは 8 件・全件が 3 段目

```console
$ git ls-files | grep -E "Consumer\.cs$" | grep -v Tests
（8 件。ConversionJobs/Normalize・Documents/Catalog・GraphDocuments/Delete・GraphDocuments/Sync・
  Ingestion/Ingest・Search/RemoveDeleted・Wiki/RemoveDeleted・Wiki/SyncDocument）
```

**8 件とも 3 段目の操作フォルダにある** —— イベント購読で駆動されるユースケースが操作として
扱われている。

### 実測 4 —— 常駐ジョブは 7 件。段は契機ではなく「使う操作の数」で分かれている

```console
$ git ls-files | grep -E "HostedService\.cs$"
（7 件）
```

| 置き場 | 件数 | 内訳 |
| --- | ---: | --- |
| 3 段目（操作フォルダ） | **2** | `DataSources/Sync/`・`KnowledgeHealth/Report/` |
| 2 段目（集約直下） | **1** | `NotificationService/Features/Notifications/NotificationMaintenanceHostedService.cs` |
| `Features/` の外 | 4 | `IngestionService/Infrastructure/ExternalServices/` 2 件、`Platform.Shared.Infrastructure/` 2 件 |

**2 段目の 1 件は `ADR-0068` 決定 2 に適合している。** 中身を読むと 1 巡で 2 操作を駆動している。

```console
$ grep -nE "DispatchPendingAsync|PurgeExpiredAsync|^using NotificationService" \
    src/platform/backend/Services/NotificationService/Features/Notifications/NotificationMaintenanceHostedService.cs
 6:using NotificationService.Features.Notifications.DispatchEmails;
 7:using NotificationService.Features.Notifications.PurgeExpired;
44:                await dispatcher.DispatchPendingAsync(now, stoppingToken);
47:                await retention.PurgeExpiredAsync(now, stoppingToken);
```

**操作の処理はそれぞれ 3 段目にあり**（`DispatchEmails/EmailOutboxDispatcher.cs`・
`PurgeExpired/NotificationRetention.cs`）、**2 段目に居るのは入口の配線だけ**である。
🔴 **常駐ジョブが 2 段目に居るのは「常駐ジョブだから」ではなく「2 操作が使うから」である** ——
これが本作業で可視にする条件そのものである。

## やること

1. **`src/README.md`「サービス直下の標準構成」へ「操作」の定義を足す**（契機の形で決めない／
   契機が 2 つある操作は 1 つ／分界は入口の配線と操作の処理）。
2. **同じ定義を `docs/tech/tech-requirements.md`「プロジェクト構成（サービス単位）」へ反映する。**
   **`docs/` 配下なので計画 ID・IADR・仕様書名を表示テキストへ書かず trace ブロックへ入れる**
   （`adrs:` へ `ADR-0077` を足す）。
3. **集約直下の列挙から「契機の形で決まる」と読める余地を消す。** 「ホステッドサービス」を無条件の
   項目として並べず、**「複数の操作が使うものだけ」という条件と実例**（実測 4）を可視にする。
4. **`templates/unit-template/README.md` の `Features/<集約>/<操作>/` の行へ、契機が HTTP に
   限らないことを添える**（`*Consumer.cs` / 常駐ジョブも同じ段に来る）。
5. `updated:` を前進させる。

## やらないこと（射程外）

- **AST 側の移送（5 サービス・75 ファイル）。** `ADR-0077` 決定 3 が置き換えるのは **AST リポジトリの**
  `IADR-0289`（`ai-stock-trading` の 3 段化移送規則）§追記 1 であり、**本リポジトリの `IADR-0289` は
  別件**（統合テストの器の起動時依存）である。**本リポジトリの `IADR-0289` には手を付けない。**
- **`Hosted/` の置き場。** 本リポジトリにトップレベルの `Hosted/` は無く、AST 固有の不整合であり
  計画側で別に裁定が要る。
- **コード。** `src/**/Services/**` は本作業の領域に入れない。
- **実装 ADR（IADR）の起草。** 実装判断が無い（上記「背景と課題」）。

## 受け入れ基準

- [ ] `src/README.md` に「契機の形では決めない」旨の定義が本文にある
- [ ] `docs/tech/tech-requirements.md` に同じ定義が入り、2 文書の記述が食い違わない
- [ ] `node scripts/check-trace-blocks.js` が成功する（追記した表示テキストに計画 ID・IADR・仕様書名が無い）
- [ ] 2 文書の「集約直下に残るもの」の列挙で「ホステッドサービス」が無条件の項目として並んでおらず、
      「複数の操作が使うものだけ」という条件と実例が読み取れる
- [ ] `templates/unit-template/README.md` の `Features/<集約>/<操作>/` の行に契機が HTTP に限らないと書かれている
- [ ] `check-doc-links.js` / `gen-knowledge-graph.js --check` / `check-doc-updated.js` /
      `check-doc-type-vocabulary.js` / `check-reading-budget.js` がすべて成功する
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が成功する
