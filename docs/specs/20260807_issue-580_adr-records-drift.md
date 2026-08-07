---
title: 作業仕様書 ADR の索引・引用が実態とずれている 5 件を是正する（#580）
type: spec
status: done
related_ids: [NFR, IADR-0048, IADR-0061, IADR-0138, ADR-0003, ADR-0027, ADR-0030]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0003_messaging-masstransit-rabbitmq.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
---

# 仕様書: ADR の索引・引用が実態とずれている 5 件を是正する（#580）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。保守性——記録（索引・引用）と実態の一致）
- ユースケース（UC）: なし／画面（SC）: なし
- 関連 ADR:
  - [IADR-0048](../adr/IADR-0048_dotnet10-target-framework.md) 決定 1（MSBuild 設定の
    単一情報源は [`src/Directory.Build.props`](../../src/Directory.Build.props)）— Y-4 の根拠
  - [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)（`Accepted`・移行なし）— R-1 の正
  - [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md)（検査の「検出しないこと」を
    明示する作法）— Y-3 の作法の先例
  - 計画 [ADR-0003](../../planning/projects/microservices-platform/07_adr/ADR-0003_messaging-masstransit-rabbitmq.md)
    （`Superseded by ADR-0027`）／
    [ADR-0027](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md)
    （Wolverine・`Accepted`）／ADR-0030（バックエンドアプリケーションライブラリ）— Y-2 / Y-3 の対象
- 関連 issue: [#580](https://github.com/endazon/microservices-platform/issues/580)。親 [#454](https://github.com/endazon/microservices-platform/issues/454)。
  発見は定期監査（adr-guardian・2026-08-07・`ef588ce`）。

## 新 ADR（IADR）の要否

**不要**と判断する。5 件はいずれも**既に確定している決定へ記録を合わせる**方向の是正であり、
新たな決定を含まない。

- R-1 は IADR-0061 本体（`Accepted`）が正で、索引がそれに追随していないだけ。
- Y-2 は計画 ADR-0003 → ADR-0027 の Supersede が**計画側で既に決まっている**。本作業は引用の
  表記をその事実に合わせるだけで、どのメッセージング基盤を使うかは何も変えない。
- Y-3 は既存検査の**測定範囲を書き足す**だけで、検査の判定を変えない。
- Y-4 は IADR-0048 決定 1 の違反 1 件の解消。
- G-1 / G-2 は索引の体裁。

ただし Y-2 の「Superseded な計画 ADR をどう引用するか」は本作業限りの判断ではなく再発する型なので、
規約として [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) へ 1 項追記する
（決定ではなく**書式規約**なので ADR ではなくルール文書に置く）。

## 母集合の実測（着手前に数えたもの）

**#580 本文の実測値をそのまま信用せず、拡張子を限定しない走査で数え直した。** 本セッションが直近で
3 回踏んだ型（`--include` で `.sh` / `.js` を母集合から外す・正しい表記だけを検索語にする・issue 本文を
読まず記憶で束を作る）を避けるため、**検索式と母集合をここに残す**。

### 検索式（すべて `git grep`＝追跡ファイル全件・拡張子フィルタなし）

```console
# 走査除外は submodule のみ（本 issue の対象外）
$ git grep -n -I 'ADR-0003' -- . ':!planning' ':!src/ai-stock-trading' | wc -l
90
# 上には IADR-0003（EFCore.Relational のピン＝別 ADR）が混ざる。裸の ADR-0003 だけを取る
$ git grep -n -I -P '(?<!I)(?<![A-Za-z0-9])ADR-0003(?![0-9])' -- . ':!planning' ':!src/ai-stock-trading' | wc -l
79
$ git grep -n -I -P 'IADR-0003(?![0-9])' -- . ':!planning' ':!src/ai-stock-trading' | wc -l
11   # ← 対象外（誤爆源）

# 誤った側からも引く（表記ゆれの取りこぼし検査）
$ for pat in 'ADR[ _]0003' 'ADR-3\b' 'ADR-03\b' 'ＡＤＲ' 'masstransit-rabbitmq'; do ... done
ADR[ _]0003 : 0 / ADR-3 : 0 / ADR-03 : 0 全角 : 0 / masstransit-rabbitmq : 3（すべて plan_refs のパス）
```

### 数えた母集合と、#580（監査）の数えとのずれ

| 対象 | 本作業の実測 | #580 の記載 | ずれ |
| --- | --- | --- | --- |
| 裸 `ADR-0003` の総出現 | **79 行 / 45 ファイル** | （総数の記載なし） | — |
| うち `.cs` | **14 行 / 8 ファイル** | 「コード 15 行 / 10 ファイル」 | **ずれる**（下記） |
| うち `.csproj` | 1 行 / 1 ファイル | 同上に含むと思われる | — |
| うち `deploy/*.yml` `*.yaml` | **2 行 / 2 ファイル** | **記載なし** | **監査の取りこぼし** |
| うち実装 ADR（`docs/adr/`） | **11 行 / 5 ファイル** | 「実装 ADR 5 本」 | 一致（本数） |
| 索引の行数 | **139 行**（うち **1 行は表として壊れている**・後述） | 「全 139 件で機械照合」 | **監査の取りこぼし** |
| 索引 vs 本体の状態不一致 | **5 件**（R-1 の 1 件 ＋ 表記ゆれ 4 件） | 同じ 5 件 | 一致 |
| `<TargetFramework>` 上書き | **1 ファイル / 3 行** | 1 ファイル | 一致 |

**監査が取りこぼしていたもの（本作業で追加検出）:**

1. **`deploy/docker-compose.yml:1` と `deploy/local/infra/rabbitmq.yaml:1` の `ADR-0003` 引用**。
   監査の「コード 15 行 / 10 ファイル」は `.cs` ＋ `.csproj` の範囲であり、`deploy/` 配下の
   YAML コメントに残る 2 件が母集合から落ちていた。**#570 と同型（拡張子で母集合を切った）**である。
2. **索引 `docs/adr/README.md:108`（IADR-0082 の行）に閉じの `|` が無い**。
   GFM は行末パイプ省略を許すため描画は壊れないが、**表として厳密に解析すると 1 行足りない**
   （厳密パーサでの行数は 138、緩いパーサで 139）。監査が「全 139 件を機械照合」できたのは
   緩い突合だったためで、**#581（IADR 採番の機械検査）が厳密パーサを使うと、この 1 行だけが
   静かに検査対象から落ちる**。本作業で閉じパイプを補う。
3. `.cs` の行数が 15 ではなく **14**（ファイル数は 8）。監査の 15/10 は再現しなかった。

**索引の構造は他に破れていない**（実測: ID 重複 0・欠番 0（`IADR-0000`〜`IADR-0138` 連続）・昇順・
本体ファイル 139 件と 1:1）。

## 対象範囲

### R-1（🔴）索引の IADR-0061 行を本体に合わせる

- 索引 `docs/adr/README.md:87` は **`Proposed`** かつ題「改名は **Blue/Green 移行で行う**（起草・実行は
  stg 検証後）」＝ **2026-07-12 に棄却された初版の案**を掲げている。
- 本体 `IADR-0061` は frontmatter `status: Accepted`・本文 `- 状態: Accepted（2026-07-12。新名称
  microservices-platform を確定し、改名を実施）`・題「stg 未構築のため移行なし・初回構築を新名称で行う」。
- **本体が正**なので本体は触らず、索引行の題と状態を本体の題・状態へ置き直す。

### Y-2（🟡）Superseded な計画 ADR-0003 の引用

#### 方針の決定: 「注記」を採り、「ADR-0027 への付け替え」は採らない

**理由（この 3 点で決めた）:**

1. **付け替えは事実に反する。** ADR-0027（Wolverine）は `Accepted` だが**実装は移行していない**。
   コードは今も MassTransit で動いており、`// ADR-0027:` と書けば「この実装は Wolverine の決定に
   従っている」という**偽の主張**になる。CLAUDE.md も「MassTransit は不採用で、既存参照は
   `scripts/backend-library-baseline.json` の ratchet 管理下にある」＝**未移行**と明記している。
2. **付け替えは「当時なぜそう実装したか」を消す。** トレーサビリティ規約のコードコメント ID は
   実装の**由来**の記録であり、由来は ADR-0003 のままである。付け替えると、移行前後の区別が
   コード上から消え、移行 PR が「どこを直せば移行完了か」を機械的に引けなくなる。
3. **注記は移行時に一括で置き換えられる。** `ADR-0003（Superseded by ADR-0027）` という決まった
   文字列にしておけば、Wolverine 移行時に `ADR-0027` へ一括置換できる。逆（先に付け替える）は、
   移行の実施と記録の一致が永久に検証できなくなる。

#### 引用の形は「置き場所」で 2 通りに分ける

**機械可読な ID リストに散文を混ぜてはならない**（`related_ids: - ADR-0003（Superseded by ADR-0027）`
と書くと ID として解析できなくなり、監査・`trace-check` の突合を壊す）。よって:

| 置き場所 | 形 |
| --- | --- |
| frontmatter の ID リスト（`related_ids` / `related_adrs`）・`plan_refs` | **ID は壊さず `ADR-0027` を項目として併記**する（`ADR-0003` は残す＝由来の記録） |
| 散文・コード / 設定ファイルのコメント | ID に隣接する括弧内へ **`Superseded by ADR-0027`** を書く。括弧が無ければ `ADR-0003（Superseded by ADR-0027）`、既に説明の括弧があれば `ADR-0003（MassTransit + RabbitMQ。Superseded by ADR-0027）` のように末尾へ足す（既存の説明を消さない） |

この形にしておけば、Wolverine 移行時に `Superseded by ADR-0027` を目印に一括で置き換えられる。

#### 是正する母集合と、しない母集合

**是正する = live な権威文書とコード（41 行）**——読者がその ID を辿って**現在の根拠**を得ようとする箇所。

| 区分 | 行数 / ファイル数 |
| --- | --- |
| `.cs`（コードコメント） | 14 / 8 |
| `.csproj`（参照理由コメント） | 1 / 1 |
| `deploy/*.yml` `*.yaml`（配備資産のコメント） | 2 / 2 |
| `docs/adr/`（実装 ADR 5 本） | 11 / 5 |
| `docs/functional/` `docs/data/` `docs/tech/` `docs/tests/`（live な仕様書） | 13 / 6 |
| **計** | **41 / 22** |

**是正しない = 日付付きの一時点記録（38 行 / 18 ファイル）**

- `docs/specs/`（30 行 / 13 ファイル）: 作業仕様書は **作業 / PR 単位の一時点記録**（CLAUDE.md の
  仕様書表）であり、書いた時点の判断を残すことが目的である。最古は 2026-06-26 で、当時 ADR-0027 は
  存在しない。**後から注記を足すのは記録の改竄**にあたる。
- `feedback/`（5 行 / 3 ファイル）: 計画リポへ実際に送った内容の写しであり、送った後で書き換えられない。
- `docs/superpowers/`（3 行 / 2 ファイル）: 保管された旧計画。

> **#580 の受け入れ基準「`ADR-0003` の引用が全件」を、本作業は上記 41 行（live 母集合）について
> 満たし、38 行（一時点記録）については意図的に満たさない。** 「全件」を字義どおり取ると記録の
> 改竄になるため、母集合の切り方をここで確定し、規約として
> [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) へ残す（同型の再発防止）。

#### 機械検査を作らない理由（測定範囲の明示）

「Superseded な計画 ADR を無注記で引用していないか」を機械検査するには、計画 ADR の `status` を
読む必要がある。しかし [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) が実測で
確定しているとおり、**PR の CI はどのジョブも planning submodule を populate しない**ため、
その検査は CI で常に skip され、**緑のまま素通りする**（＝ Y-3 で問題にしているのと同じ
「緑を担保と読む」構図を新たに作る）。よって本作業では検査を作らず、規約文書に留める。

### Y-3（🟡）MassTransit ratchet の測定範囲を明記する

`check-backend-libraries.js` が見るのは **`.csproj` / `.props` / `.targets` の `PackageReference` と
`.cs` の `using` 宣言**だけで、**既に baseline 済みのプロジェクト内で結合が深まっても検出しない**。

実例（本作業で追試）: `bc7bc8e`（#568）が
`src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Composable/Steps/RawDocumentFetchedConsumer.cs:81`
へ `context.GetRetryAttempt() + 1 >= MassTransitExtensions.MaxAttempts`（MassTransit の再試行
セマンティクスへの production 判定の依存）を追加したが、`using MassTransit;` は既存・
`PackageReference` は baseline 済みのため ratchet は動かない。

- **検査そのものは変えない。** [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の
  「ゲート一覧」の当該行と [`scripts/README.md`](../../scripts/README.md) に**測定範囲（＝検出しないこと）**を
  明記する。IADR-0138 §結果「検出しないこと」と同じ作法。

### Y-4（🟡）`Knowledge.IntegrationTests.csproj` の props 上書きを削除する

`src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj:3-5` の
`<TargetFramework>net10.0</TargetFramework>` / `<ImplicitUsings>enable</ImplicitUsings>` /
`<Nullable>enable</Nullable>` を削除する。3 つとも
[`src/Directory.Build.props`](../../src/Directory.Build.props) が既定で与えている（値も同一）。
`<IsPackable>false</IsPackable>` は props に無いので残す。

**実測で確認済み**: `src/`（AST を除く）と `templates/` 全体で `<TargetFramework>` を無条件に上書きする
`.csproj` は**この 1 件だけ**。`templates/unit-template/backend/Directory.Build.props.sample` の
`Condition="'$(TargetFramework)' == ''"` 付きは単独ビルド用フォールバック（IADR-0064）で正当。

### G-1（🟢）索引の Superseded 表記を 1 形式へ揃える

実測で 4 形式（`Superseded（by IADR-0020）` / `Superseded by IADR-0026` / `Superseded（[[IADR-0121]]）` /
`Superseded by IADR-0105`）。**揃える先は `Superseded by IADR-XXXX`** とする——同じ
`docs/adr/README.md` の §運用ルールが「旧 IADR に `Superseded by IADR-XXXX` を追記する」と
**既にこの形を規約として書いている**ため、新しい形を作らずに済む。

本体 frontmatter の `status:` は 4 件とも**素の `Superseded`**（列挙値）であり、これは変えない。
`status:` は列挙値、索引の状態列は列挙値 ＋ 参照先という役割分担になるので、**その対応規則を
§運用ルールに 1 行足す**（#581 が機械検査を作るときに突合規則を推測せずに済むように）。

### G-2（🟢）索引の ID セルにファイルリンクを張る — **張る**

139 行すべての ID セルを本体への相対リンク（`[` ＋ ID ＋ `](./` ＋ 本体ファイル名 ＋ `)`）にする。

- **張る判断の理由**: (a) ID → ファイル名の対応は一意で、生成が機械的（人手の写経を伴わない）。
  (b) 張れば [`check-doc-links.js`](../../scripts/check-doc-links.js) の検査対象に 139 件が入り、
  **ADR ファイルの改名・削除が索引を壊したまま素通りする経路が閉じる**（現在は索引がプレーン
  テキストなので、本体を消しても索引は緑のまま）。(c) 本体同士は既にリンクで相互参照しており、
  索引だけが辿れないのは一貫性を欠く。
- あわせて **IADR-0082 行の閉じ `|` を補う**（上記「監査の取りこぼし 2」）。

## やらないこと

- IADR-0061 本体の変更（本体が正）。
- `check-backend-libraries.js` の判定変更（Y-3 は文書のみ）。
- 新 ADR の起票（§新 ADR の要否）。
- `docs/specs/` / `feedback/` / `docs/superpowers/` の ADR-0003 引用の書き換え（§Y-2）。
- `planning/` / `src/ai-stock-trading/` の変更（pin を含め一切触らない）。

## 受け入れ基準 → 検証の写像

| # | 受け入れ基準（#580） | 検証 |
| --- | --- | --- |
| 1 | 索引の IADR-0061 行が本体（`Accepted` / 移行なし）と一致 | 索引 vs 本体の突合スクリプトを再実行し不一致 0（R-1 分） |
| 2 | `ADR-0003` の live 引用が全件 Superseded を示す | `git grep` で live 母集合 41 行に注記／`ADR-0027` 併記があること |
| 3 | ratchet の限界が文書に明記 | `TEST_STRATEGY.md` ゲート一覧・`scripts/README.md` に記載 |
| 4 | csproj が props を上書きしていない | `git grep '<TargetFramework' -- '*.csproj'` が AST 以外 0 件 ＋ `dotnet build src/knowledge/backend/backend.slnx` |
| 5 | Superseded 表記が 1 形式 | 索引の状態列の Superseded 形が 1 種 |
| 6 | 検査が exit 0 | `check-doc-links.js`（＋`--self-test`）／`check-backend-libraries.js`／`check-cpm-versions.js`／`scripts.test.js`／`check-commit-messages.js` |

### 検証の実測結果

| 検査 | exit code |
| --- | --- |
| `node scripts/check-doc-links.js --self-test`（自己試験 34 件） | **0** |
| `node scripts/check-doc-links.js`（446 件の Markdown） | **0** |
| `node scripts/check-backend-libraries.js` | **0**（新規混入 0 / 既知残件 42 件は baseline 済み） |
| `node scripts/check-cpm-versions.js` | **0** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（265 tests） | **0** |
| `node scripts/scripts.repo.test.js` | **0** |
| `node scripts/check-unit-dependencies.js` | **0** |
| `node scripts/check-test-spec-coverage.js` / `check-test-traceability.js` | **0** / **0** |
| `dotnet build src/knowledge/backend/backend.slnx`（SDK 10.0 コンテナ） | **0**（`Build succeeded` / 0 Errors / 2 Warnings＝既存の CS0618） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | **0** |
| `dotnet format .../Platform.Shared.Infrastructure.csproj --verify-no-changes` | **0** |

`dotnet build` は Y-4 の検証である。`Knowledge.IntegrationTests` の出力先が
`bin/Debug/net10.0/` になったことで、**削除した `<TargetFramework>` が
`src/Directory.Build.props` から正しく継承されている**ことを実測で確認した。

### 変異試験（G-2 が実際に検査されることの確認）

**2 通り試し、いずれも落ちた（＝ G-2 は見た目だけの変更ではない）。**

| 変異 | 結果 |
| --- | --- |
| 索引のリンク 1 本を実在しないファイル名へ書き換える | `check-doc-links.js` **exit 1**（`docs/adr/README.md` / `./IADR-0061_deploy-rename-migrationX.md` を報告） |
| **本体ファイルを 1 件改名する**（索引を触らない） | `check-doc-links.js` **exit 1**。改名を戻すと **exit 0** |

2 番目が本命である。**リンクを張る前は、ADR 本体を改名・削除しても索引はプレーンテキストのまま
緑だった。** この経路が閉じた。

### 機械検査を置いていない範囲（開示）

- **R-1 / G-1（索引と本体の状態突合）には機械検査が無い。** 本作業は使い捨てスクリプトで 139 件を
  照合しただけで、検査器はコミットしていない。**索引と本体が再び食い違っても CI は緑のまま**である。
  これは隣接 issue **#581（IADR 採番の機械検査）**の範囲であり、本作業は G-1（表記の統一）と
  §運用ルールの突合規則を書いてその**前提を整えるところまで**を担当する。
- **Y-2 にも機械検査が無い**（理由は上記「機械検査を作らない理由」）。

## 変更するファイル

| ファイル | 変更 |
| --- | --- |
| [`docs/adr/README.md`](../adr/README.md) | R-1（0061 行）／G-1（4 行の状態列）／G-2（139 行の ID セル＋0082 行の閉じパイプ）／§運用ルールへ突合規則 1 行 |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | Y-3: ゲート一覧の「ライブラリ標準」行へ測定範囲 |
| [`scripts/README.md`](../../scripts/README.md) | Y-3: 同上（`check-backend-libraries.js` の行が表に無いので追加） |
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | Y-2: Superseded な計画 ADR の引用規約 |
| `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj` | Y-4: 3 行削除 |
| `docs/adr/IADR-0001` `0042` `0043` `0051` `0137` | Y-2: 引用 11 行 |
| `docs/functional/FR-01` `FR-02` ／ `docs/data/data-source` `conversion-job` ／ `docs/tech/system-architecture` ／ `docs/tests/FR-01` | Y-2: 引用 13 行 |
| `src/**/*.cs`（8 ファイル）／`Platform.Shared.Infrastructure.csproj` ／ `deploy/docker-compose.yml` ／ `deploy/local/infra/rabbitmq.yaml` | Y-2: 引用 17 行 |
