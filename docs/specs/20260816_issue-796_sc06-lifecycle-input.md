---
title: SC-06 の登録フォームに lifecycle の入力欄を足す（#796 / #754 の切り出し 3/3）
type: spec
status: done
related_ids:
  - FR-05
  - FR-01
  - UC-04
  - SC-06
  - ADR-0004
  - ADR-0034
  - ADR-0036
  - IADR-0019
  - IADR-0125
  - IADR-0199
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
---

# 仕様書: SC-06 登録フォームの `lifecycle` 入力欄

> 本仕様書は実装着手前に作成した。親 issue は #754（供給源のうち **3 つ目の既定属性**だけを切り出したのが #796）。
> 同じ切り方の先例は #767（`department` 欄。作業仕様書 [20260815_issue-767_sc06-department-input.md](20260815_issue-767_sc06-department-input.md)）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（ABAC。文書属性 `lifecycle` が `read` の連言に入る）、FR-01（データソースの登録）
- ユースケース（UC）: **UC-04** 基本フロー 1「管理者がソースを登録する」
- 画面（SC）: **SC-06** データソース管理画面
- 関連 ADR: `ADR-0004`（ABAC）、`ADR-0034`（ホップごとの ABAC 強制）、`ADR-0036`（所有者ベース裁量制御）。
  実装 ADR は [[IADR-0019]]（機密区分のフェイルセーフ既定）・[[IADR-0199]]（取り込み必須属性のフェイルセーフ。
  決定 4 が `lifecycle` の終端 `active` を持つ）・[[IADR-0125]]（i18n カタログの網羅検査）
- 計画書リンク:
  - [`06_technical/09_datasource-connectors.md`](../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md)
    §システム投入経路での `owner` / `department` / `lifecycle`（確定・2026-08-15。`lifecycle` は同日追補）
  - [`06_technical/07_abac-attribute-model.md`](../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md)
    §文書の基本属性（`lifecycle`＝状態・**必須**・値域 `draft` / `active` / `archived`）
  - [`05_screens/01_screens.md`](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-05 / §SC-06

### 一次情報を読んだ位置（**pin を実測した。develop の pin では 1 行が読めない**）

**develop の submodule pin は `4d6a7d6` である**（`git -C planning log -1` で実測）。この pin で読めた／読めなかったものは次のとおり。

| 参照 | pin `4d6a7d6`（develop） | 読み |
| --- | --- | --- |
| `07_abac-attribute-model.md:41` `\| lifecycle（状態） \| 必須 \| draft / active / archived \|` | **読める** | **値域の正本はここである** |
| `05_screens/01_screens.md:271`「状態の語彙は…`draft` / `active` / `archived` を正とする」「`normalized` / `published` は計画側の語彙ではない」 | **読める** | 語彙の確認 |
| `09_datasource-connectors.md:55-63`（既定属性 3 つの表）・`:71`（`lifecycle` の終端 `active` の理由）・`:74`（「終端の `active` は**指定が無いときだけ**効く」） | **読める** | 挙動の正本 |
| `05_screens/01_screens.md:320`「登録・更新フォームは**データソースの既定属性 3 つ**を持つ」（裁定 planning#372・2026-08-16 確定） | **読めない**（この pin に当該記述が無い。`git grep 既定属性` が 0 件） | **pin が古い。** 隣接 worktree `/home/user/wt-pin/planning`（pin `8cae89d`）で当該行を読み取り専用で確認した |

**pin は更新しない。** submodule の前進は別 PR（#795）の領域であり、本 PR は `planning` を 1 バイトも触らない。
**本作業の実装内容は pin `4d6a7d6` で読める記述だけで決まる** —— 値域も挙動も終端も 4d6a7d6 に揃っており、
`8cae89d` の L320 は「3 つ目の欄を足してよい」という**着手の根拠**を与えるだけである。

### 計画上の `lifecycle` の位置づけ（読み取り結果）

計画は `lifecycle` を **`department` と同じ 3 段の形だが 1 段目が無い**と定める（09_datasource-connectors §システム投入経路）。

| 段 | 供給源 | 本 issue の射程 |
| --- | --- | --- |
| 1 | ソースから解決 | **構造的に存在しない**（計画が「ファイルの状態を `draft` / `active` / `archived` へ写像できない」と明記） |
| 2 | **データソースの既定属性** | **本 issue の対象**（管理者が値を選ぶ経路） |
| 3 | 終端 `active` | 実装済み（バックエンド `DataSource.DefaultLifecycle`。[[IADR-0199]] 決定 4） |

**2 段目に入力経路が無いため、ソース単位で下書き扱いにする手段が画面に無い。**
計画は「**ソース単位で下書き扱いにしたい場合は、データソースの既定属性で `draft` を指定する。終端の `active` は
指定が無いときだけ効く**」（09_datasource-connectors L74）と明記しており、**指定できる口が要る**。本 issue はこの 2 段目だけを開ける。

**ADR 制約に反しないこと**: 契約を変えない（下記の実測）。`ADR-0034` が求めるホップごとの ABAC 強制は
後段（AuthorizationService）の責務であり、本変更は判定軸へ値を供給する側だけを触る。

## 契約側が `lifecycle` を受けられるかの実測（**着手条件。受けられないなら実装しない**）

3 点を実際に読んで確かめた。**結論: 受けられる。フロントだけが先行して壊れる形にはならない。**

1. **契約（`docs/api/openapi.yaml` L2462-2466）** —— `CreateDataSourceRequest.defaultAttributes` は
   `type: object` / `additionalProperties: { type: string }` / `nullable: true` の**自由辞書**であり、
   キーは固定列挙ではない。しかも `description` が
   「未指定時は後段が必須属性のフェイルセーフを通る（confidentiality=internal / owner=system /
   department=unassigned / **lifecycle=active**）。明示指定は上書きしない」と**`lifecycle` を名指しで書いている**。
   L1004 / L1051（POST / PUT の `description`）にも同じ 4 属性が並ぶ。
2. **orval 生成物（`src/platform/frontend/src/foundation/api/generated/bff.schemas.ts` L475 / L483）** ——
   `export type CreateDataSourceRequestDefaultAttributes = {[key: string]: string} | null;` であり、
   キーの追加に型変更も再生成も要らない。
3. **バックエンドの受け口（`.../DataSourceService.Api/Foundation/Domain/DataSource.cs`）** ——
   `LifecycleKey = "lifecycle"`（L51）・`DefaultLifecycle = "active"`（L69）が既にあり、
   `WithRequiredAttributeFailsafe` が `FillIfBlank(result, LifecycleKey, DefaultLifecycle)`（L130）を通す。
   `FillIfBlank` は**欠落・空白のみのときだけ**埋めるので、**明示した `draft` / `archived` は保持される**。
   `Create` / `Update` / `Patch` / `GetEffectiveAttributes` の 4 経路が同じ関数を通る（[[IADR-0199]] 決定 1）。

**したがって「キーが固定列挙だからフロントだけ出すと壊れる」という中止条件には当たらない。**
バックエンドは 1 行も触らない（本セッションに `dotnet` が無く DoD を満たせない）。

## 母集合の引き直し（着手時に自分で引いた。issue 本文の一覧は転記していない）

走査基準: worktree `feat/SC-06-lifecycle-default-attribute`（base `origin/develop` = **`dca76ce`**）。
`git grep -I` で**追跡下の全ファイル**を引き、除外は pathspec `':!src/ai-stock-trading'`（別リポの submodule）と
`':!planning'`（計画リポの submodule）**のみ**である。**拡張子で絞っていない**（規則 3）。**行フィルタで絞っていない**（規則 4）。

**規則 8（自己参照）の扱い**: 下表の値は**すべて base `dca76ce` のもの**であり、**本仕様書はこの時点に存在しない**。
本仕様書をコミットすると全軸が増える（本書は下表の検索語をすべて含む）。**追試する者は `dca76ce` を添えて引くこと。**

### 軸ごとの実測（23 軸）

| 軸 | 検索語 | 行 | ファイル | 読み |
| --- | --- | --- | --- | --- |
| 1 | `lifecycle`（**誤りの側＝不在の側から引く**。規則 1） | 120 | 26 | **`src/*/frontend/**` の非生成コードは 1 件のみ**（`features/abac/department.ts` のコメント）。フォームには無い |
| 2 | `Lifecycle`（パスカル。バックエンド側の形） | 17 | 11 | すべて `src/*/backend/**` と `docs/`。**本 PR では触らない** |
| 3 | `LIFECYCLE`（大文字。フロントの定数名の形） | **0** | **0** | **語彙定数がまだ無い**（`department` 着手時の `DEPARTMENT` と同じ状態） |
| 4 | `ライフサイクル`（訳語。**別表記から引く**。規則 2） | 27 | 25 | 大半は計画由来の記述と無関係な語義（「エンティティのライフサイクル」等） |
| 5 | `defaultAttributes`（キャメル＝フロント / 契約側） | 90 | 24 | 非生成のフロントは 3 ファイル（下記） |
| 6 | `DefaultAttributes`（パスカル＝バックエンド側） | 108 | 22 | すべて `src/*/backend/**` と生成物・文書 |
| 7 | `既定の機密区分`（フォームの項目を列挙している側。規則 9） | 20 | 10 | 追随先の特定に用いた |
| 8 | `既定の部門`（同上。**直前の先例**） | 17 | 9 | 同上 |
| 9 | `DataSourceForm` | 41 | 9 | 同上 |
| 10 | `登録フォーム` | 36 | 11 | 同上 |
| 11 | `入力欄` | 52 | 17 | 同上 |
| 12 | `入力項目` | 16 | 12 | 同上 |
| 13 | `archived`（値域の語） | 67 | 29 | 大半は ABAC ポリシー例・シード |
| 14 | `draft`（値域の語。**文書状態と無関係な用法が多い**） | 332 | 168 | 仕様書の `status: draft` が大半。値域としての用法は ABAC 文書のみ |
| 15 | `既定属性` | 54 | 22 | 追随先の特定に用いた |
| 16 | `#754`（親 issue） | 34 | 8 | 追跡記述の所在 |
| 17 | `#767`（先例） | 29 | 8 | 同上 |
| 18 | `planning#372`（本裁定） | 9 | 4 | 同上 |
| 19 | `全件`（**「全件 `active` になる」を捕まえる語**） | 219 | 121 | 大半は無関係。`lifecycle` 文脈は 1 箇所 |
| 20 | `どの画面も送っていない`（**誤りになる文そのもの**） | **2** | **2** | [[IADR-0199]] L184（live）／`docs/specs/20260815_issue-516_…`（**確定済み**） |
| 21 | `値のばらつき` | **1** | **1** | [[IADR-0199]] L209（live） |
| 22 | `終端` | 62 | 28 | `lifecycle` の終端記述の所在 |
| 23 | `planning#361`（`lifecycle` 終端の裁定） | 19 | 8 | 同上 |

軸 5 の非生成フロント 3 ファイル: `features/sc06-datasources/DataSourceForm.tsx` /
`features/sc06-datasources/DataSourceManagementPage.test.tsx` / `features/adminFlow.test.tsx`。

### 変更したもの / 除外したものと理由（規則 6）

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `features/abac/lifecycle.ts`（新規） | **追加** | 語彙の単位。`confidentiality.ts` / `department.ts` と同じ置き方に揃える |
| `features/abac/lifecycle.test.ts`（新規） | **追加** | 値域・キー・終端値が黙って変わらないことを固定する |
| `features/sc06-datasources/DataSourceForm.tsx` | **変更** | 入力欄の追加（本体） |
| `features/sc06-datasources/DataSourceManagementPage.test.tsx` | **変更** | 受け入れ基準の写像（**未指定でキーが入らない／指定でキーが入るの両方向**） |
| `i18n/locales/{ja,en}/messages.{po,ts}` | **再生成**（`pnpm run i18n`） | 手では編集しない。`msgstr` の訳文だけ人手で入れる |
| `docs/screens/SC-06_datasource-management.md` | **変更** | §モックに無いが実装する要素・§表示・入力項目・§i18n・§関連仕様が**本変更で古くなる**（軸 7〜12 で検出） |
| `docs/tests/SC-06_datasource-management.md` | **変更** | §UC-04 のフロー写像・§テストケース・§語彙が同じく古くなる |
| `docs/adr/IADR-0199_ingestion-required-attribute-failsafe.md` | **変更（日付つき追記）** | **2 箇所が本変更で事実として誤りになる** —— L184「**現在どの画面も送っていない**」（SC-06 の既定属性としては送るようになる。文書の人手経路と読み分けが要る）／ L209「`lifecycle` は**全件 `active`** になるため値のばらつきが無い」（`draft` を選べるようになる）。`Accepted` な ADR のため本文は消さず、日付つき追記ブロックで併記する |
| `docs/data/data-source.md` | **変更（1 行の日付つき注記）** | L119「ソース単位で下書き扱いにしたい場合は既定属性で `draft` を指定する」が**経路を書いていない**。API しか無いと読めるため、SC-06 の欄を指す 1 文を足す。**理由書きは複写しない**（正は [[IADR-0199]]。#767 が同じ扱いを採った） |
| `docs/adr/README.md` L255（軸 16・23） | **除外** | IADR 索引の 1 行要約。「lifecycle の終端は active」は**決定の要約**であり、本変更で古くならない（終端は依然 active で、指定が無いときだけ効く） |
| `docs/tests/FR-01_data-source-catalog.md` T-44 / T-45（軸 1・22） | **除外** | バックエンド単体（`DataSource.Create`）の記述。**T-45 は既に「`lifecycle` を明示（`draft`）→ 明示値が保持される」を固定しており**、本変更でも正しいまま |
| `docs/specs/20260815_issue-516_…` / `20260815_issue-767_…` / `20260805_issue-503_…` / `20260709_issue-132_…` / `20260805_issue-517_…` / `20260805_issue-456_…` / `20260815_issue-602_…`（軸 1・7〜12・20） | **除外（確定済み。記録のみ）** | `docs/specs/` の**確定済み作業仕様書**。`.claude/rules/traceability.repo.md` が「本文への後付け注記で書き換えない」と定める。とくに `20260815_issue-516_…` L99 は軸 20 に当たり**内容としては古くなるが、当時の実測の記録として正しい** |
| `feedback/20260815_ingestion-lifecycle-default-unadjudicated.md` / `feedback/20260805_…`（軸 1・15・22） | **除外** | 環流記録（`status: accepted`・`dispatched: true`）。同じく後付けで書き換えない |
| `docs/screens/SC-05_document-management.md`・`docs/tests/SC-05_document-management.md`（軸 7・10） | **除外** | SC-05 自身の文書登録フォーム／公開・アーカイブ操作の記述。SC-06 の項目ではない。**`archived → active` の議論には踏み込まない**（計画が未定と明記） |
| `docs/screens/` の他 7 ファイル（軸 11・12） | **除外** | 各画面自身の §表示・入力項目 であり SC-06 の列挙を持たない |
| `docs/templates/`（軸 12） | **除外** | 雛形。特定画面の項目を持たない |
| `docs/api/openapi.yaml`（軸 1・5） | **除外** | **契約は変わらない**（自由辞書。上記の実測で追認。しかも `lifecycle` は既に `description` に載っている） |
| `src/*/backend/**`（軸 2・6 の 22 ファイル） | **除外** | 本セッションに `dotnet` が無く `build` / `test` / `format` を走らせられない。DoD を満たせない変更は入れない。**読むのは行った**（上記「契約側の実測」の根拠） |
| `src/platform/frontend/src/foundation/api/generated/**`（軸 1・5） | **除外** | orval 生成物。契約が変わらないので再生成差分も出ない |
| `features/adminFlow.test.tsx`（軸 5） | **除外** | 一覧応答のフィクスチャで `defaultAttributes` を持つだけで、登録フォームを送らない |
| `features/sc03-document/attributes.test.ts`（軸 1） | **除外** | SC-05 / SC-03 の**文書**属性ラベルの話（`known = ['confidentiality','department']`）。**データソースの既定属性とは別の経路**であり、本 PR の射程外（[[IADR-0199]] 決定 5 が「保存前検証は強めない」と定める） |
| `deploy/local/abac-seed/**`・`scripts/measure-abac-combinations.js`（軸 1・13） | **除外** | 開発シードと測定スクリプト。DB / Keycloak 稼働が前提で、フォームの項目に依存しない |
| `CHANGELOG.md`（軸 1） | **除外** | 生成物（`gen-changelog.js`）。手で編集しない |
| `scripts/scripts.repo.test.js`（軸 1・22） | **除外** | 検査器の自己テスト。`lifecycle` は無関係な文脈（フィードバック状態同期）で現れる |

**「フォルダ → 部門コードの写像表」は実装しない**（裁定が「部門コードの値域が定まるまで写像は行わない」と明記。
09_datasource-connectors L89 相当）。これは #754 に残る。**`lifecycle` には 1 段目そのものが無い**ため、
本 PR では写像の議論自体が生じない。

## 対象範囲

- 対象: SC-06 登録フォームへの `lifecycle` 入力欄、`defaultAttributes` への積載、語彙定数、ja / en 文言、テスト、追随する文書
- 対象外: 更新フォーム（**SC-06 に編集フォームの画面実装がまだ無い**。`DataSourceManagementPage.tsx` L34-35 が
  「契約側の更新 API は揃ったが編集フォームは射程外」と明記。**本 PR で編集フォームを新設しない** —— 計画外の
  画面追加になる）／ソース側からの解決（**構造的に存在しない**）／`archived → active` の遷移（計画が未定と明記）／
  バックエンド（`dotnet` が無い）／SC-05 の文書 `lifecycle` ラベル（別経路）

### 「登録・更新フォーム」と書かれているのに登録だけを実装する理由

issue と計画 L320 は「登録・**更新**フォーム」と書く。**更新フォームは本 PR 以前から存在しない**（#767 も同じ状態で
登録だけを実装し、その作業仕様書が「更新経路は #754 に残る」と記録している）。**無い画面に欄は足せない。**
更新フォームの新設は SC-06 の画面実装そのものであり、1 issue = 1 PR の単位として別である。**#754 が引き受けたまま残る。**

## 設計

### 語彙の単位（`features/abac/lifecycle.ts`。新規）

`confidentiality.ts` / `department.ts` と同じく**画面フォルダではなく語彙フォルダ**へ置く。`lifecycle` は
SC-06 の既定属性だけでなく SC-05 の公開・アーカイブ操作でも同じキーを指すためである。

- `LIFECYCLE_KEY = 'lifecycle'` —— ABAC 属性辞書のキー（バックエンド `DataSource.LifecycleKey` と同値）
- `LIFECYCLE_VALUES = ['draft', 'active', 'archived'] as const` —— **値域**。正本は計画
  `07_abac-attribute-model.md:41` の `lifecycle` 属性表（`05_screens/01_screens.md:271` が「状態の語彙は…を正とする」と再確認）
- `DEFAULT_LIFECYCLE: Lifecycle = 'active'` —— **終端の既定値**（バックエンド `DataSource.DefaultLifecycle` と同値）

**`normalized` / `published` は入れない。** 計画が「実装の端点名は `publish` / `archive` であり、
環流記録が用いた `normalized` / `published` は**計画側の語彙ではない**」と明記している（`05_screens/01_screens.md:271`）。

**`department` と違い値集合を持つ。** 部門コードは計画に列挙が無いので自由入力にしたが、
`lifecycle` は 3 値が明記されているため**選択式**にする（`confidentiality` と同じ扱い）。

### フォーム（`DataSourceForm.tsx`）

- `Select`（**任意**）。「既定の部門」の直後に置く（3 つとも既定属性であるため、計画 L320 の並び順に揃える）。
- **既定の選択は「未指定」**（空文字）である。`confidentiality` は常に値が乗る（フェイルセーフ既定を画面が
  先に見せる）が、`lifecycle` は**指定が無いときだけ終端 `active` が効く**という計画の条件（L74）を
  画面が壊してはならない。**既定で `active` を選ばせると「管理者が明示的に active を指定した」と
  「指定しなかった」の区別が消える**。
- 値そのものは**翻訳しない**（`confidentiality` と同じ。`draft` / `active` / `archived` は保存値である）。
  翻訳するのは「未指定」という選択肢のラベルと、欄のラベル・補助文だけである。
- 送信時、**空でないときだけ** `defaultAttributes` へ `lifecycle` キーを積む（`department` と同じ形）。
- 未指定時に何が起きるかを補助文で伝える（**「予約値」ではなく「既定値」と書く** —— [[IADR-0199]] が
  「`active` は予約値ではなく既定値であり、件数を環流債務として数えない」と明記している。**語を取り違えると
  測定の意味が変わる**）。

### 未指定時の挙動（**決定: キーを送らない**）

`department`（#767）と同じ結論だが、**理由が 1 つ増える**。

1. **計画が「指定が無いときだけ効く」と書いている。** 空文字を送る形は「後段が `IsNullOrWhiteSpace` で
   潰してくれる」ことに依存する。判定がキーの有無だけに変わった瞬間、画面から登録した全ソースの
   `lifecycle` が空文字になり、**終端の既定が効かなくなる**。
2. **「指定しなかった」と「`active` を選んだ」を辞書の形で区別できる。** `lifecycle` は
   `department` と違い**終端が予約値ではなく正規の値**なので、両者は値としては見分けられない。
   キーの有無だけが区別を持つ。
3. **既存の送信形と一貫する**（`confidentiality` 以外は送らない値をキーごと出さない）。

## 受け入れ基準（issue #796 より転記）

- [x] SC-06 の登録フォームに `lifecycle` の入力欄があり、`draft` / `active` / `archived` と「未指定」を選べる
- [x] 未指定のとき `defaultAttributes` に `lifecycle` キーが**入らない**（テストで固定）
- [x] 指定したとき、その値が `defaultAttributes.lifecycle` として送られる（テストで固定）
- [x] 値域が計画の語彙と一致し、計画に無い値（`normalized` / `published` 等）を導入していない
- [x] Lingui カタログが ja / en とも未翻訳キー無しで `check-i18n-catalogs.js` が通る
- [x] `pnpm run typecheck` / `lint` / `format:check` / `test:coverage` が通る
- [x] `pnpm run build` → `check-chunk-budget.js --require` が通る
- [x] 画面仕様書・テスト仕様書に `lifecycle` 欄の記述がある

## テスト方針（**両方向を固定する**）

| # | 受け入れ基準 / フロー | テスト |
| --- | --- | --- |
| T1 | **未指定ならキーが入らない** | 既存 `registers a data source with a default confidentiality attribute` の POST 本文完全一致（`toEqual`）＋ `expect(Object.keys(attrs)).not.toContain('lifecycle')` を名指しで追加 |
| T2 | **指定したら入る** | `registers a data source with a default lifecycle attribute`（`draft` を選び、POST 本文の完全一致で `{ confidentiality, lifecycle: 'draft' }` を見る） |
| T3 | 値域が計画どおり | フォームの `<option>` が `draft` / `active` / `archived` の 3 つ ＋「未指定」であること（**`normalized` / `published` が無いこと**も名指しで見る） |
| T4 | 任意である（計画に無い必須化を足さない） | `does not require a lifecycle to enable the register button`。あわせて補助文（未指定なら既定値 `active`）が出ることを見る |
| T5 | 語彙の固定 | `abac/lifecycle.test.ts`（キー・3 値・終端値の文字列を固定。**計画に無い値が混ざっていないことも見る**） |
| T6 | en ロケール | 既存 `renders in English when the en locale is active` を拡張し、en で `Default lifecycle` のラベルを見る |

## 変異試験（実施結果）

7 件すべて実際に適用して測った（宣言だけの記録にしない）。走査範囲は
`vitest run knowledge/frontend/src/features/sc06-datasources knowledge/frontend/src/features/abac`
（クリーン時は **47 件すべて緑**）。**素通りした変異は無い。**

| # | 変異 | 落ちた検査 | 実測 |
| --- | --- | --- | --- |
| M1 | `DataSourceForm.tsx` から `lifecycle` の `<Label>` / `<Select>` / 補助文を削除する | T2・T3・T4・T6 | **4 件 fail** |
| M2 | 送信時の `defaultAttributes` から `lifecycle` を落とす（入力欄は残す） | T2 | **1 件 fail**（POST 本文の `toEqual` 差分） |
| M3 | 未指定でもキーを送る（`[LIFECYCLE_KEY]: lifecycle` を無条件に積む） | T1（＋ `department` の既存テスト） | **2 件 fail** |
| M4 | **初期選択を `active` にする**（`useState(DEFAULT_LIFECYCLE)`） | T1・T3（＋ `department` の既存テスト） | **3 件 fail** |
| M5 | **計画に無い値 `published` を値域へ足す** | T5（L2・L3）・T3 | **3 件 fail** |
| M6 | `ja/messages.po` の `既定のライフサイクル状態` の `msgstr` を空にする | `check-i18n-catalogs.js` | **exit 1**（`ja: 未翻訳（msgstr が空）`） |
| M7 | `en/messages.po` の同じ `msgstr` を空にする | `check-i18n-catalogs.js` ＋ `lingui compile --strict` | **どちらも exit 1**（後者は `Missing 1 translation(s)`） |

**M3 / M4 が `department` のテストまで落とすのは意図どおりである。** 既存 2 件は POST 本文を
`toEqual`（完全一致）で見ているため、**送信辞書へ余計なキーが増えた瞬間に落ちる**。
「未指定ならキーが入らない」は 1 本の名指しアサートだけでなく、**既存の完全一致 2 本にも守られている。**

**M4 は今回の設計の要点そのものを突く変異である。** 初期選択を `active` にしても
「画面は動く・値も送れる」ので**目視では気づけない**が、**「明示的に active を指定した」と
「指定しなかった」の区別が消える**（計画が「終端は指定が無いときだけ効く」と定めた条件が壊れる）。
これを落とせるのは、T3 が**既定の選択が空であること**を名指しで見ているからである。

**M5 は「計画に無い語彙を実装が持ち込む」型の事故を突く。** issue と計画がともに
`normalized` / `published` を名指しで否定しているため、**否定形のテスト（T5 L3）を置いた。**
値域を増やす変異は T5 L2（完全一致）でも落ちるが、L3 は**なぜ落ちるべきかを名前で残す**。

**M7 には #767 が記録した注意がそのまま当てはまる。** 実行時に読まれるのは**コンパイル済みの
`messages.ts`** であり、`.po` だけを壊しても再コンパイルまでは単体テスト（T6）が緑のままになり得る。
**en の未翻訳を止めているのは `check-i18n-catalogs.js` と `lingui compile --strict` の 2 本である。**

## 計画書との差異

- 差異: **なし**。計画（09_datasource-connectors §システム投入経路）の 3 段のうち **2 段目だけ**を実装した。
  1 段目は**構造的に存在しない**（計画が明記）。3 段目は既にバックエンドにある。
- **値域は計画のものをそのまま使い、実装が語彙を作っていない**（`normalized` / `published` を入れていない）。
- **「登録・更新フォーム」のうち更新側は実装していない** —— 更新フォームが本 PR 以前から存在しないためである
  （§対象範囲 に理由を書いた）。**#754 が引き受けたまま残る。**

## 検証の実測（すべて実走した）

| コマンド | exit | 要点 |
| --- | --- | --- |
| `pnpm run typecheck`（`src/`） | 0 | 全 workspace（**`src/ai-stock-trading` の submodule を populate してから**。未 populate だと `@ai-stock-trading/features` が解決できず platform/frontend が落ちる） |
| `pnpm run lint` | 0 | error 0 / warning 9（いずれも既存の `react-refresh/only-export-components`） |
| `pnpm run format:check` | 0 | 初回 1 件 warn → `prettier --write` で解消 |
| `pnpm run test:coverage` | 0 | Statements **96.98%** / Branches **91.05%** / Functions **93.25%** / Lines **96.98%** |
| `node scripts/check-i18n-catalogs.js` | 0 | 2 ロケールに未翻訳・fuzzy・obsolete なし |
| `pnpm run build` | 0 | — |
| `node scripts/check-chunk-budget.js --require` | 0（**床の更新後**） | 下記 |
| `node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 …` | 0 / 0 | **610 件**（companion 込み。`planning` を populate 済みなので `check-kit-sync` が throw せず後続も走っている） |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-type-vocabulary` | 0 | — |
| `check-doc-updated` / `check-commit-messages`（**コミット後**） | 0 / 0 | `docs/` 5 件の `updated:` 据え置き無し |

### カバレッジの床は上げていない（理由）

`src/vitest.config.ts` の `thresholds` は **91 / 91 / 88 / 87** であり、実測はいずれも大きく上回る。
**この床は #676 以降 1 度も動いておらず、PR ごとに引き上げる運用にはなっていない**（`git log -L` で実測）。
**本 PR は床を割らない**（新規コードはテストで全経路が通る）。床の引き上げは全 PR を巻き込む変更であり、
**1 issue = 1 PR の単位として別である。**

### バンドル初期ロードの床を更新した（**手元で当てた。CI を最初の検出者にしていない**）

`check-chunk-budget --require` が **622.67 kB > 床 622.36 kB（+0.31 kB）** で fail した。
#767 の作業仕様書が「次に i18n の文言を足す作業では手元でも `build` → `check-chunk-budget` まで回すこと」と
書き残していたため、**CI に出す前に当てられた。**

**増加の出どころを特定してから床を上げた**（「意図した増加」を確かめずに `--update` を打たない）。

```console
$ grep -lo "既定のライフサイクル状態\|Default lifecycle state" src/platform/frontend/dist/assets/*.js
src/platform/frontend/dist/assets/index-BNz2LTAP.js
$ grep -lo "ds-lifecycle" src/platform/frontend/dist/assets/*.js
src/platform/frontend/dist/assets/DataSourceManagementPage-CAX0EasH.js
```

**新しい文言は初期チャンク `index-*.js` に入り、UI 本体は遅延チャンク側にある。** Lingui のカタログは
foundation が即時 import するため遅延チャンクへ逃がせない。すなわち **+0.31 kB は「翻訳文言を 3 つ足した
ことの下限コスト」**であり、[[IADR-0134]] の分割境界を変えても消せない。
`scripts/chunk-budget-baseline.json` の `initialTotalBytes` を `622363` → `622675` へ更新した。
**分割規則そのものは 1 行も変えていない。**

## 未決事項

1. **更新フォーム**（SC-06 に編集フォームの画面実装が無い）。#754 に残る。
2. **フォルダ → 部門コードの写像**（planning#372 の裁定待ち）。#754 に残る。**`lifecycle` には 1 段目が無いため無関係。**
3. **`archived → active`（アーカイブ解除）** は計画が「要否・可否は未定」としており、踏み込まない。
4. **テスト仕様書 §実行 の件数が古かった**（「純関数 7 ＋ 画面 15」）。#537 / #538 / #767 の追加に
   追随していなかったため、**導出値として計算し直して**「純関数 12 ＋ 画面 26 ＋ 語彙 9」へ置き換えた
   （規約「導出値は走査ではなく計算し直す」）。**同型の古さが他の画面のテスト仕様書にも無いかは
   本 PR の射程外**であり、見つけたら別 issue にする。
