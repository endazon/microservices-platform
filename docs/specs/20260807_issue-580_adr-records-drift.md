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
| 裸 `ADR-0003` の総出現 | **79 行 / 40 ファイル** | （総数の記載なし） | — |
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

> **［2026-08-07 追記 / クロス監査 G-a］** 上表の「総出現」のファイル数は当初 **45** と書いていたが
> 誤りで、正しくは **40 ファイル**（79 行は正しい）。45 は `IADR-0003`（別 ADR）を含む緩い走査
> （90 行）のファイル数を取り違えたものである。§Y-2 の内訳（live 22 ファイル ＋ 一時点記録
> 18 ファイル ＝ **40**）と一致する。着手時点 `ef588ce` で数え直した拡張子別の行数は
> `.md` 62 / `.cs` 14 / `.csproj` 1 / `.yml` 1 / `.yaml` 1 ＝ 79。

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

**注記の書式は live 母集合の全 22 ファイルへ一様に適用する**（同規約「注記には起票 ID を添える」）。
Markdown の 11 ファイル（`docs/adr/` 5 本 ＋ live な仕様書 6 本）は本文の注記へ `・注記は #580` を
添え、frontmatter の `updated:` を `2026-08-07` へ前進させる。**ADR 本体だけに適用して仕様書を
外す選択は採らない**——(a) 規約の適用範囲は既に「live な権威文書とコード」で切ってあり、
`docs/functional/` `docs/data/` `docs/tech/` `docs/tests/` はその内側である、(b) 「原文と後付けの
注記が見分けられない」という規約の理由は仕様書にも等しく当てはまる（むしろ ADR より改訂が多い）、
(c) 規約に実装を合わせるのではなく規約側を縮めて辻褄を合わせるのは、本 issue が是正しようとしている
「記録と実態のずれ」そのものである。`.cs` / `.csproj` / `deploy/` の 11 ファイルは**対象外**とする
（規約が「ADR / 仕様書の本文」と書いているとおり）——frontmatter を持たず `updated:` を前進させる
先が無いうえ、注記の由来は `git blame` と当該コミットの起点 ID で 1 行単位に辿れるため、規約の
理由（原文と後付けの見分けが付かない）が当てはまらない。この線引きを規約側にも明記する。

> **［2026-08-07 追記 / #580 再監査］** 上段落の「`.cs` / `.csproj` / `deploy/` の 11 ファイルは
> **対象外**」は撤回する。理由 2 つはどちらも成立しなかった——(i)「frontmatter が無く `updated:` を
> 前進させる先が無い」は**要件 2 つのうち片方しか否定しない**（注記へ ID を書く方は無傷）、
> (ii)「`git blame` と起点 ID で辿れる」は **`.md` にも等しく当てはまる**ので、コードだけを外す差に
> ならない。加えて本作業自身が `Knowledge.IntegrationTests.csproj` へ `<!-- NFR / #580, IADR-0048
> 決定 1: … -->` と**設定ファイルに `#580` を書いており**、同一コミット群の中で矛盾していた。
> 既存の除外（`docs/specs/` / `feedback/` / `docs/superpowers/`）は「**書いた時点の記録だから後付けは
> 改竄**」という一貫した基準で切られており、コードはその基準に当たらない。
> **是正**: 規約を一様に適用し、`.cs` / `.csproj` / `deploy/` の **17 行**にも `・注記は #580` を
> 添えた（`ADR-0003（Superseded by ADR-0027・注記は #580）`。一括置換の目印 `Superseded by ADR-0027`
> は連続したまま保つ）。`updated:` は前進先が無いので求めず、規約側へ「**frontmatter を持たない
> ファイルは注記 ID だけでよい**」と明記した（`.claude/rules/traceability.md`）。これで規約の
> 母集合定義「live な権威文書とコード」との自己矛盾も解消する。

> **［2026-08-07 追記 / #580 再監査］** コミット `3f456c1` のメッセージは live な仕様書へ足した注記を
> 「**8 行**」と書いたが、**実測は 9 行**である（`conversion-job` 1 / `data-source` 2 /
> `functional/FR-01` 2 / `functional/FR-02` 1 / `tech/system-architecture` 1 / `tests/FR-01` 2）。
> コミットメッセージは書き換えられない（push 済み・force push 禁止）ため、正しい値をここに残す。
> 再実測: `git show 3f456c1 -- docs/functional docs/data docs/tech docs/tests | grep -c '^+.*注記は #580'` → 9。

#### 機械検査を作らない理由（測定範囲の明示）

「Superseded な計画 ADR を無注記で引用していないか」を機械検査するには、計画 ADR の `status` を
読む必要がある。しかし **PR で起動する決定的な検査ジョブ**（`ci.yml` の `doc-links` /
`scripts-tests` / `commit-messages` 等、`pr-title.yml`）は**どれも planning submodule を populate
しない**ため、その検査は CI で常に skip され、**緑のまま素通りする**（＝ Y-3 で問題にしているのと
同じ「緑を担保と読む」構図を新たに作る）。よって本作業では検査を作らず、規約文書に留める。

> **［2026-08-07 追記 / クロス監査 §5］** 上の断定は当初「PR の CI は**どのジョブも** populate
> しない」と書いていたが、字義として誤りだった。`claude-code-review.yml`（`on: pull_request`・
> L84-93）だけは `PLANNING_REPO_TOKEN` で `git submodule update --init --recursive` を実行する。
> ただし**これは AI レビューであってマージを止める決定的ゲートではない**ので、結論
> （計画 ADR の `status` を読むゲートは作れない）は変わらない。恒久ルール文書
> [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) 側も同じ限定へ揃えた。

> **［2026-08-07 追記 / #580］** 上の「だけは」は誤り。`claude-coding.yml` も同じ
> `PLANNING_REPO_TOKEN` で submodule を populate する
> （[`.github/workflows/claude-coding.yml`](../../.github/workflows/claude-coding.yml):77-86。
> `issue_comment` / `pull_request_review_comment` / `pull_request_review` で PR 文脈でも起動する）。
> よって populate するワークフローは **2 本**である。**ゲートでない**という結論は不変
> （どちらも AI 実行系でマージを止めない）。規約側は `3f456c1` で 2 本の表へ直したが、本追記ブロックが
> 同コミットで追随しておらず live に誤りが残っていたため、ここで是正する。

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

実測で **Superseded 行は 4 行・表記は 3 形式**（`Superseded（by IADR-0020）`＝`IADR-0013` /
`Superseded（[[IADR-0121]]）`＝`IADR-0033` / `Superseded by IADR-XXXX`＝`IADR-0017`・`IADR-0084`）。
**書き換えたのは前 2 形式の 2 行**で、既に規約形だった `IADR-0017`・`IADR-0084` の状態列は変えていない。**揃える先は `Superseded by IADR-XXXX`** とする——同じ
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

### A-1（🟡・クロス監査で追加）索引の「全 ID セルがリンク」を機械検査へ載せる

**［2026-08-07 追記 / クロス監査 A-1］本節は方針転換である。** 当初は §機械検査を置いていない範囲で
「R-1 / G-1 / G-2 に機械検査は無い（#581 の範囲）」と開示するに留めた。**方針を変えた理由は、
破れることが実測で確定したからである。**

- `check-doc-links.js` は「**あるリンクが壊れているか**」しか見ず、「**リンクが無いこと**」を検出しない。
- 実際、本作業中に develop が `36a8bc8` → **`7d68e42`（#582）** へ進み、**新規 `IADR-0139` 行と
  改定 `IADR-0116` 行がどちらもリンク無しのプレーンテキストで追加された**。
  G-2 の不変条件は、開示したとおり**次のマージで無検査のまま破れていた**。
- 「破れると開示済み」は「破れてよい」ではない。**開示は検査の代わりにならない。**

**決定**: 索引の**行の形**を機械検査する。

| 見るもの | 見ないもの（重複を作らない） |
| --- | --- |
| ID セルがリンク形式か（`not-linked`） | リンク先の**実在**（`check-doc-links.js` の担当） |
| リンク先ファイル名が当該 ID で始まるか（`id-file-mismatch`。実在検査では捕まらない型） | 状態列と本体 `status:` の突合・**採番の連続性**・索引行の欠落（**#581 の担当**） |
| 行末の閉じ `|`（`no-trailing-pipe`。#581 が厳密パーサを使う前提） | |

**置き場所**: [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js)。
`.github/workflows/` は GitHub App 権限で編集できないため、`ci.yml` の `scripts-tests` ジョブ
（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → companion 自動読込）に載せる
（#507 が確立した経路。`check-i18n-catalogs` / `check-test-spec-coverage` の実データ検査と同じ結線）。
**`scripts/scripts.test.js` はキットとバイト一致に保つ必要があり変更しない**（IADR-0115 分類 A）。

**fail-open の塞ぎ方（下限を固定値にしない）**: 走査の正規表現が壊れて 0 件になると
「違反 0 件」で緑になる。これを塞ぐ下限を置くが、**下限は実在する ADR 本体（`docs/adr/IADR-*.md`）の
件数から導く**。当初は `>= 140` の固定値にしていたが、**固定値は他 PR が ADR を 1 本足すたびに動き、
無関係な PR を赤にする手作業の更新点になる**（#454 原則 12）。実際、本 PR の作業中だけで
**#582 が `IADR-0139` を、#584 が `IADR-0140` を足しており 2 回動いた**。
副作用として「索引に行が無い ADR がある」ことも検出するが、それは #581 の範囲なので、
#581 が入った時点で本ブロックごと統合・削除する（下記の申し送りのとおり）。

**#581 への申し送り**: #581 が採番の機械検査（索引 vs 本体の突合・採番の連続性）を入れるときは、
**本ブロックを #581 側の検査へ統合し、`scripts.repo.test.js` からは削除する**。
**同じ不変条件の検査を 2 本残さない。** 本ブロックの検査は「行の形」だけに閉じてあるので、
#581 の突合検査が索引行をパースする時点で自然に吸収できる（同旨をテスト本体のコメントにも書いた）。

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
| `node scripts/check-doc-links.js`（451 件の Markdown） | **0** |
| `node scripts/check-backend-libraries.js` | **0**（新規混入 0 / 既知残件 42 件は baseline 済み） |
| `node scripts/check-cpm-versions.js` | **0** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（**274 tests**。§A-1 の索引検査 5 件を含む） | **0** |
| `node scripts/scripts.repo.test.js` | **0** |
| `node scripts/check-unit-dependencies.js` | **0** |
| `node scripts/check-test-spec-coverage.js` / `check-test-traceability.js` | **0** / **0** |
| `dotnet build src/knowledge/backend/backend.slnx`（SDK 10.0 コンテナ） | **0**（`Build succeeded` / 0 Errors / 2 Warnings＝既存の CS0618） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | **0** |
| `dotnet format .../Platform.Shared.Infrastructure.csproj --verify-no-changes` | **0** |

`dotnet build` は Y-4 の検証である。`Knowledge.IntegrationTests` の出力先が
`bin/Debug/net10.0/` になったことで、**削除した `<TargetFramework>` が
`src/Directory.Build.props` から正しく継承されている**ことを実測で確認した。

### 変異試験 1（A-1 の索引検査が CI 呼び出し口から落ちること）

**実リポジトリの `docs/adr/README.md` を変異させ、CI が実際に叩く入口
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` から落ちることを実測した**
（作業ツリーを汚さないよう `git archive` で書き出した使い捨てツリーで実施）。

| 変異 | CI 呼び出し口の exit code |
| --- | --- |
| なし（正常な索引） | **0**（**274 tests passed**） |
| **`IADR-0139` 行のリンクを外す**（#582 が実際に入れた形の再現） | **1**（`README.md:172 not-linked`） |
| **`IADR-0140` 行のリンクを外す**（#584 が実際に入れた形の再現） | **1**（`README.md:173 not-linked`） |
| 変異を戻す | **0** |
| **`IADR-0082` 行の閉じ `|` を外す**（着手時の実測形の再現） | **1**（`README.md:115 no-trailing-pipe`） |

負例 3 種（`not-linked` / `no-trailing-pipe` / `id-file-mismatch`）と正例は、実データを汚さない
純関数の単体テストとしても固定してある。

**同じ入口に相乗りしている #507 / IADR-0140 の検査も落ちることを確かめた**（衝突解消で
どちらかを黙って落としていないことの確認）: `.claude/rules/traceability.md` から
「修飾語と番号の間に空白を入れない」（型 3 の条文）を消すと **exit 1**（`型 3 の規約が消えている`）、
戻すと **exit 0**。

### 変異試験 2（G-2 が実際に検査されることの確認）

**2 通り試し、いずれも落ちた（＝ G-2 は見た目だけの変更ではない）。**

| 変異 | 結果 |
| --- | --- |
| 索引のリンク 1 本を実在しないファイル名へ書き換える | `check-doc-links.js` **exit 1**（`docs/adr/README.md` / `./IADR-0061_deploy-rename-migrationX.md` を報告） |
| **本体ファイルを 1 件改名する**（索引を触らない）。対象 = **`IADR-0052_load-test-tooling-k6.md`** | base（`7d68e42`・索引はプレーンテキスト）**exit 0**（＝素通り）／ HEAD（索引はリンク）**exit 1**（`docs/adr/README.md` / `./IADR-0052_load-test-tooling-k6.md` を報告）／ 改名を戻すと HEAD **exit 0** |

2 番目が本命である。**リンクを張る前は、ADR 本体を改名・削除しても索引はプレーンテキストのまま
緑だった。** この経路が閉じた。

> **［2026-08-07 追記 / クロス監査 §4］変異の対象を取り直した。** 当初この行は
> **`IADR-0061`** を改名して「exit 1、戻すと 0」と書いていたが、**それは結論を証明していなかった**。
> `IADR-0061` は索引以外に 3 本の被リンク（`docs/migration/rename-knowledge-platform.md` ほか）を
> 持つため、**リンクを張る前の base でも同じ変異で exit 1 になる**。つまり「G-2 が経路を閉じた」
> ことを識別できない変異だった。
>
> **識別可能な変異の条件は「索引以外からの被リンクが 0 件の ADR」であり、実測でそれは 140 件中
> 9 件しかない**（`IADR-0012` / `0044` / `0045` / `0047` / **`0052`** / `0053` / `0054` / `0055` /
> `0075`）。**無作為に選ぶと約 94% の確率で非識別的な変異になる。** 後続がこの表を「識別済み」と
> 読んで同じ非識別的変異を再生産しないよう、対象ファイル名と母集合の実測値をここに残す。
> （数え方: 追跡ファイル全件から `planning/` と `src/ai-stock-trading/` を除き、各 ADR の
> ファイル名を `docs/adr/README.md` と自分自身**以外**が言及しているかを数えた。）

### 機械検査の有無（開示）

- **G-2（全 ID セルがリンク）は機械検査へ載せた**（§A-1）。当初は「無い」と開示するに留めたが、
  **#582 で実際に破れたため方針を変えた**。
- **R-1 / G-1（索引と本体の状態突合・採番の連続性）には機械検査が無い。** 本作業は使い捨て
  スクリプトで照合しただけで、検査器はコミットしていない。**索引の状態列と本体 `status:` が再び
  食い違っても CI は緑のまま**である。これは隣接 issue **#581（IADR 採番の機械検査）**の範囲であり、
  本作業は G-1（表記の統一）・§運用ルールの突合規則・§A-1 の行の形の検査を置いて、その
  **前提を整えるところまで**を担当する。
- **Y-2 にも機械検査が無い**（理由は上記「機械検査を作らない理由」）。

> **［2026-08-07 追記 / #580 再監査］** 索引タイトル列について 2 点是正した。
>
> 1. **緩和の根拠が実測と逆向きだった。** `docs/adr/README.md` は「索引は短縮形を、本体は完全な決定文を
>    書いており」と書いていたが、**実測は逆**である。字義一致しない 96 行のうち **87 行は索引の方が長く**
>    （索引に決定文がまるごと貼られている）、「索引が要約・本体が完全形」は **9 行**しかない。索引が本体より
>    100 字以上長い行は **64 行**、タイトルセル長は中央値 146 字・最大 3318 字・**200 字超が 65 行**。
>    よって是正の方向は「字義一致を課す」ではなく「**索引タイトルセルを要約へ縮める**」である。
>    あわせて「退行ではなく元からの設計」は裏付けが無い（本 PR 以前にタイトル列の突合規則は存在しない）ため
>    「**develop（`cf15568`）時点から同じ状態＝本 PR による退行ではない**」に留めた。develop でも同じ
>    141 行中 96 行（索引が長い 86 / 短い 10）で、本 PR が R-1 で `IADR-0061` を 64 → 127 字へ広げた
>    1 行だけが「短い」から「長い」へ移っている。
> 2. **緩めた結果タイトル列が完全に無検査になっていた**（再監査が変異試験で実測）。字義一致の免除は
>    維持しつつ、**縮める方向へ効く不変条件をラチェットで固定**した——タイトルセルは空でない /
>    状態語（`Superseded by IADR-XXXX`）を書かない / `［YYYY-MM-DD 追記］` を含めない / **200 字以内**。
>    現在の違反（`title-addendum` 13・`title-too-long` 65・計 65 行）を
>    [`scripts/adr-index-title-baseline.json`](../../scripts/adr-index-title-baseline.json) へ baseline 化し、
>    新規混入と「直したのに baseline に残る stale」を [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js)
>    が落とす（`backend-library-baseline.json` と同じ作法。テストは 274 → 280 件）。
>    変異試験（実測）: baseline 外の行への `［追記］` 混入 → fail / 他の決定の全文をタイトルへ貼る
>    （再監査が使った変異の型）→ `title-too-long` で fail / baseline の縮め忘れ → stale で fail。
>    **200 字以内のきれいな文で別の決定へ書き換える 1 型だけは検出できない**（字義一致でしか捕まらず、
>    免除している範囲そのもの。索引を縮め切った後に #581 が字義一致を掛けられる）。
>
> 索引タイトル列の `［追記］` 13 行は、**本体にも同じ追記がある**（13 本すべての本体が対応する追記を持ち、
> うち 11 本は同じ日付つき形式）。すなわち索引側だけが無検査の複製という状態だった。

> **［2026-08-07 追記 / #580 最終巡］** 上の 2 点をさらに是正した（数値は本巡で再計測した実測値）。
>
> 1. **`IADR-0061` の索引タイトルを 28 字の要約へ戻した。** 上の追記が書いたとおり、R-1 で 64 → 127 字へ
>    広げた際に「初版の Blue/Green 案は 2026-07-12 に棄却」を足していたが、これは**本体本文が既に持って
>    いる**内容（`IADR-0061_deploy-rename-migration.md` の `- 状態:` / `- 日付:` / §決定）で、新設した
>    `title-addendum` が防ごうとしている「索引側だけの複製」そのものだった（形が `［YYYY-MM-DD 追記］`
>    でないため検査に掛からなかっただけ）。R-1 の是正（索引の状態列が本体の `Accepted` と一致すること・
>    棄却された Blue/Green 案を索引が掲げないこと）は保ったまま縮めた。これにより索引が本体より長い行は
>    **86 行 / 短い行は 10 行**（＝develop と同じ内訳）に戻り、上の追記の 87 / 9 はこの巡で解消した。
> 2. **「200 字以内のきれいな文で別の決定へ書き換える型は検出できない」を撤回し、塞いだ。** 索引タイトル
>    セルと本体 `title:` の**文字単位 LCS** を測る `title-drift` をラチェットへ 1 kind 追加した。実測（141 行）:
>    LCS の分布は 最小 11 / p10 32 / 中央値 49 / 最大 188 で、**素の `LCS < 12` に該当するのは 1 行だけ**
>    （`IADR-0000`。索引と本体が**字義一致**しており共有できる上限が本体の 11 字しかない）＝素の下限値は
>    最も正しい行を最初に赤にする。よって下限を**短い側の長さで頭打ち**にし
>    （`LCS < min(minTitleOverlap=12, 本体長, タイトル長)`）、**現状の違反 0 行**＝baseline へ足す行なしで
>    導入した。閾値を上げると是正の目標形（短い要約）から先に赤くなる（20 では既に要約済みの
>    `IADR-0010`（19 字）・`IADR-0011`（18 字）が違反）ので上げない。
>    変異試験（実測）: 実データの `IADR-0005` 行を 200 字以内・清潔・全く別の決定へ差し替え →
>    `README.md:59 IADR-0005 title-drift` で **exit 1**、戻すと **exit 0**（284 tests passed）。
>    **取りこぼし（撤回した断定の代わりに残す実測）**: 「別の決定の**本体 `title:` をそのまま貼る**」型は
>    総当たり 141 × 140 = 19,740 通りのうち **69% しか落ちない**（残り 31% は助詞・語尾だけで 12 字を
>    偶然共有する）。完全にするには字義一致が要り、それは索引を要約へ縮め切った後に **#581** が掛けられる。
> 3. **fail-open ガードを本ブロック自身に持たせた。** タイトルラチェットの走査ガードは `actual.length > 0`
>    だけで、**行を索引から隠す**変異（先頭に空白を入れて `^|` アンカーから外す）を検出しなかった。実際に
>    それを落としていたのは隣接ブロック（全行リンク形式）の行数下限で、そこには「#581 が入った時点で
>    本ブロックごと削除する」申し送りがある。よって同じ下限（索引行数 ≥ ADR 本体数、本体 `title:` を
>    読めた数 ≥ 本体数）をタイトルラチェット側にも置いた。実測: 隣接ブロックを削除した状態（＝#581 後を
>    模した木）で `IADR-0042` 行を空白 1 字で隠すと、**タイトルラチェット側のメッセージで exit 1**。
> 4. **閾値の据え置きを固定した。** `maxTitleChars` / `minTitleOverlap` の値そのものをテストで固定し、
>    JSON 側でこっそり緩める抜け道を塞いだ（緩めるなら同じ PR でテストも直る＝diff に載る）。
>    あわせて「上限 400 なら 201 字の貼り付けが通る」ことも固定し、閾値が効いていることを示す。

## 変更するファイル

| ファイル | 変更 |
| --- | --- |
| [`docs/adr/README.md`](../adr/README.md) | R-1（0061 行）／G-1（**2 行**の状態列＝`IADR-0013`・`IADR-0033`。`IADR-0017`・`IADR-0084` は既に `Superseded by IADR-XXXX` 形で無変更）／G-2（**141 行**の ID セル＋0082 行の閉じパイプ。#582 の `IADR-0139`・#584 の `IADR-0140` を含む）／§運用ルールへ突合規則 |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | **A-1**: 索引の行の形の機械検査（正例 1・負例 3・実データ 1 の計 5 テスト）／索引タイトルセルのラチェット（`title-missing` / `title-status-word` / `title-addendum` / `title-too-long` / `title-drift` の 5 kind・実データ 1 を含む計 10 テスト。スイート全体は 284 件） |
| [`scripts/adr-index-title-baseline.json`](../../scripts/adr-index-title-baseline.json) | 索引タイトルセルの既知違反 65 行 ＋ 閾値（`maxTitleChars` 200 / `minTitleOverlap` 12） |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | Y-3: ゲート一覧の「ライブラリ標準」行へ測定範囲 |
| [`scripts/README.md`](../../scripts/README.md) | Y-3: 同上（`check-backend-libraries.js` の行が表に無いので追加） |
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | Y-2: Superseded な計画 ADR の引用規約 |
| `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj` | Y-4: 3 行削除 |
| `docs/adr/IADR-0001` `0042` `0043` `0051` `0137` | Y-2: 引用 11 行 |
| `docs/functional/FR-01` `FR-02` ／ `docs/data/data-source` `conversion-job` ／ `docs/tech/system-architecture` ／ `docs/tests/FR-01` | Y-2: 引用 13 行 |
| `src/**/*.cs`（8 ファイル）／`Platform.Shared.Infrastructure.csproj` ／ `deploy/docker-compose.yml` ／ `deploy/local/infra/rabbitmq.yaml` | Y-2: 引用 17 行 |
