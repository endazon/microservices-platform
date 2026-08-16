---
title: SC-06 の登録フォームに department の入力欄を足す（#767 / #754 の切り出し 1/3）
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
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
---

# 仕様書: SC-06 登録フォームの `department` 入力欄

> 本仕様書は実装着手前に作成した。親 issue は #754（供給源 3 つのうち ② のみを切り出したのが #767）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（ABAC によるアクセス制御。文書属性 `department` が判定の連言に入る）、FR-01（データソースの登録）
- ユースケース（UC）: **UC-04** 基本フロー 1「管理者がソース（ファイルサーバー／Wiki／SaaS／業務DB）を登録する」
- 画面（SC）: **SC-06** データソース管理画面（主要素「ソース登録ボタン」「コネクタ設定」）
- 関連 ADR: `ADR-0004`（ABAC）、`ADR-0034`（グラフ探索でもホップごとに ABAC を強制する）、`ADR-0036`（所有者ベース裁量制御）。実装 ADR は [[IADR-0019]]（機密区分のフェイルセーフ既定）・[[IADR-0199]]（取り込み必須属性のフェイルセーフ）・[[IADR-0125]]（i18n カタログの網羅検査）
- 計画書リンク:
  - [`06_technical/09_datasource-connectors.md`](../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md) §システム投入経路での `owner` / `department` / `lifecycle`（確定・2026-08-15）
  - [`06_technical/07_abac-attribute-model.md`](../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md) §文書の基本属性（`department`＝所管部門・**必須**・部門コード）
  - [`05_screens/01_screens.md`](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-06

### 計画上の `department` の位置づけ（読み取り結果）

計画は `department` を **3 段**で決めると定めている（09_datasource-connectors §システム投入経路の表）。

| 段 | 供給源 | 本 issue の射程 |
| --- | --- | --- |
| 1 | 投入元（ソース）の所属を解決して入れる | **対象外**（フォルダ → 部門コードの写像規則が未裁定。planning#372） |
| 2 | **データソースの既定属性から補う** | **本 issue の対象**（管理者が値を打つ経路） |
| 3 | 解決できなければ予約値 `unassigned` | 実装済み（バックエンド。PR #753 / [[IADR-0199]]） |

**2 段目に入力経路が無いため、画面から登録した全データソースが 3 段目へ落ちる。**
計画は「`system` / `unassigned` は解決できなかったことの記録であり、既定ではない」「恒久的に積み上がるなら
コネクタが更新者・部門を運んでいないという報告である」と明記しており、**2 段目を塞いだまま予約値が積み上がる
状態は計画の意図に反する。** 本 issue はこの 2 段目だけを開ける。

**ADR 制約に反しないこと**: 契約（`docs/api/openapi.yaml`）を変えない。`CreateDataSourceRequestDefaultAttributes`
は `{ [key: string]: string } | null` の自由辞書であり、キーの追加に契約変更は要らない。`ADR-0034` が求める
ホップごとの ABAC 強制は後段（AuthorizationService）の責務であり、本変更は判定軸へ値を供給する側だけを触る
（判定規則には触れない）。

## 母集合の引き直し（着手時に自分で引いた。issue 本文の一覧は転記していない）

走査基準: worktree `feat/sc-06-department-input`（base `origin/develop` = `fde7252`）。
除外パスは `node_modules` / `.git` / `planning`（submodule） / `ai-stock-trading`（submodule） / `dist` / `coverage` のみ。
**拡張子では絞っていない**（規則 3）。**行フィルタで絞っていない**（規則 4）。

### 軸ごとの実測

| 軸 | 検索語 | 行 | ファイル | 読み |
| --- | --- | --- | --- | --- |
| 1 | `department`（**誤りの側＝不在の側から引く**。規則 1） | 333 | 87 | うち `src/knowledge/frontend/src/features/sc06-datasources/` は **0 行**（issue の実測を追試して一致） |
| 2 | `DEPARTMENT`（大文字。定数名の形） | **0** | **0** | フロントに部門の語彙定数がまだ無い |
| 3 | `defaultAttributes`（キャメル＝フロント側） | 71 | 19 | 非生成のフロントは **3 ファイル**（下記） |
| 4 | `DefaultAttributes`（パスカル＝バックエンド側） | 102 | 21 | すべて `src/*/backend/**` と生成物。**本 PR では触らない** |
| 5 | `CONFIDENTIALITY_KEY`（「語彙の単位」の置き方の手本） | 10 | 5 | `features/abac/confidentiality.ts` が単一情報源、利用は SC-05 2 ファイル・SC-06 1 ファイル・テスト 1 |
| 6 | `unassigned`（予約値） | 41 | 12 | バックエンド 2・生成物 2・`scripts/` 2・`docs/` 6 |
| 7 | `部門` | 106 | 57 | 大半は計画由来の記述・ABAC ポリシー例 |
| 8 | `所管部門` | 2 | 2 | `07_abac-attribute-model` の語（バックエンド `DataSource.cs` のコメントに写っている） |
| 9 | `既定の機密区分`（**SC-06 フォームの項目を列挙している側**から引く。規則 9） | — | **9** | 追随先の特定に用いた。下表 |

### 追随先の引き直し（2 巡目。`docs/` 配下・規則 5「軸を 1 本で終わらせない」）

1 巡目で挙げた 4 箇所（screens L117 / L205-212、tests L61 / L75）**をそのまま信じず**、`docs/` 全体を 6 軸で引き直した。
**`docs/screens/` と `docs/tests/` の SC-06 以外のファイルも対象に入れた。**

| 軸 | 検索語 | 行 | ファイル |
| --- | --- | --- | --- |
| a | `既定の機密区分` | 11 | 5 |
| b | `登録フォーム` | 21 | 9 |
| c | `入力項目` | 12 | 12 |
| d | `defaultAttributes` | 29 | 8 |
| e | `DataSourceForm` | 9 | 5 |
| f | `接続先 URI` | 5 | 3 |

**結果: 実際に古くなっていたのは 3 ファイル・9 箇所であり、1 巡目の 4 箇所では足りなかった。**

| ファイル | 箇所 | 1 巡目で挙げたか |
| --- | --- | --- |
| `docs/screens/SC-06_…` | §モックに無いが実装する要素（項目の列挙） | ○ |
| `docs/screens/SC-06_…` | §表示・入力項目（表に行が無い） | ○ |
| `docs/screens/SC-06_…` | §i18n（翻訳しない値の列挙に予約値が無い） | **×（軸 c で発見）** |
| `docs/screens/SC-06_…` | §関連仕様（本作業仕様書へのリンクが無い） | **×（軸 b で発見）** |
| `docs/tests/SC-06_…` | §UC-04 のフロー写像 | ○ |
| `docs/tests/SC-06_…` | §テストケース 4（送信内容の列挙） | ○ |
| `docs/tests/SC-06_…` | §テストケース 14（en。テストを拡張した） | **×（軸 a で発見）** |
| `docs/tests/SC-06_…` | §純関数（`department.test.ts` が載っていない） | **×（軸 a で発見）** |
| `docs/adr/IADR-0199_…` | L63 / L90-91 / L202（**入力欄が無いと明言している**） | **×（軸 b・e で発見）** |

### 3 巡目（`docs/data/` の領域が広がったため。**誤りの側の語で引き直した**）

**取りこぼしの型は「走査語の穴」ではなく「領域外として除外」であった** —— `docs/data/data-source.md` は
2 巡目の軸 b・f で**出ており**、下の除外表に理由つきで記録が残っていた。**1 巡目（軸 `既定の機密区分` のみ）
では出ていなかった**ので、穴があったのは 1 巡目である。それでも規則 5（軸を 1 本で終わらせない）に従い、
**誤りの側の語**で引き直して他の取りこぼしが無いかを確かめた。

| 軸 | 検索語 | 行 | ファイル | 新たに出た要追随箇所 |
| --- | --- | --- | --- | --- |
| g | `unassigned` | 39 | 9 | 0 |
| h | `予約値` | 48 | 8 | 0 |
| i | `供給源` | 10 | 5 | 0 |
| j | `#754` | 22 | 6 | 0 |
| k | `100%` | 13 | 10 | 0 |
| l | `入力欄` | 37 | 13 | 0 |

**新規の要追随は 0 件**。ただし 3 巡目で初めて出たファイルが 2 つあり、いずれも**調べたうえで対象外と判断**した。

- **`docs/adr/README.md`**（軸 g・j・h・l）: IADR 索引の 1 行要約（L255）。**「`department` は `unassigned` へ倒れる（#752 / #754）」は決定の要約であって現状の欠落の記述ではない**ため、本変更で古くならない。
- **`docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md`**（軸 i・j）: **確定済みの作業仕様書**。棚卸し時点の記録であり直さない。
- `docs/data/` の他 6 ファイル（`abac-policy.md` / `document-and-version.md` / `wiki-page-sync.md` 等）は `department` を**属性キーの例示**として挙げるのみで、SC-06 のフォームに触れていない（`docs/data/` 全体を `SC-06` / `department` / `登録フォーム` / `入力欄` で走査して確認）。

**除外したもの（2 巡目）**

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `docs/data/data-source.md` L84 / L88-94 | **変更（3 巡目で同 PR 内に消化）** | 「**加えて SC-06 の登録フォームに `department` の入力欄が無い**。追跡は #754」「どちらも実運用では事実上 100% が予約値へ倒れる」が**本変更で誤りになる**。**2 巡目では軸 b（`登録フォーム`）・軸 f（`接続先 URI`）で発見済みだったが、宣言済みファイル領域外として除外していた**（本表に記録あり）。**領域が広げられたので直した** |
| `docs/screens/SC-05_document-management.md`（軸 b・c） | 除外 | SC-05 自身の文書登録フォームの記述で、SC-06 の項目ではない |
| `docs/tests/SC-05_document-management.md`（軸 a） | 除外 | 機密区分の値集合が SC-05 / SC-06 で共有語彙であることの説明。`department` は SC-06 のみで、記述は古くならない |
| `docs/tests/FR-01_data-source-catalog.md`（軸 d） | 除外 | T-39〜T-42 はバックエンド（`DataSource.Create` の予約値補完）の記述で、フロントの送信形とは独立。本変更で古くならない |
| `docs/screens/` の他 8 ファイル（軸 c） | 除外 | 各画面自身の §表示・入力項目 であり SC-06 の列挙を持たない（**画面横断の一覧は存在しなかった**） |
| `docs/templates/screen_spec_template.md`（軸 c） | 除外 | 雛形。特定画面の項目を持たない |
| `docs/api/openapi.yaml`（軸 d） | 除外 | 契約は変わらない（宣言済み領域外でもある） |
| `docs/specs/20260805_issue-503_…` / `20260709_issue-132_…` / `20260815_issue-516_…` / `20260705_FR-01_…` / `20260808_issue-534-537_…` / `20260810_issue-658_…`（軸 a・b・d・e） | **除外（確定済み。記録のみ）** | いずれも `docs/specs/` の**確定済み作業仕様書**であり、`.claude/rules/traceability.repo.md` が「本文への後付け注記で書き換えない」と定める。とくに `20260815_issue-516_…`（軸 b・e）と `20260805_issue-503_…`（軸 a・b・e）は SC-06 の登録フォームが `confidentiality` だけを送ると書いており、**内容としては古くなるが、当時の実測の記録として正しい**。**直さない** |
| `docs/adr/IADR-0148` / `IADR-0162`（軸 d） | 除外 | `defaultAttributes` を契約・永続化の観点で扱っており、フォームの項目に触れていない |

軸 3 の非生成フロント 3 ファイル:
`features/sc06-datasources/DataSourceForm.tsx` / `features/sc06-datasources/DataSourceManagementPage.test.tsx` /
`features/adminFlow.test.tsx`。

軸 9 の 9 ファイル:
`i18n/locales/{ja,en}/messages.po`・`locales/ja/messages.ts`（カタログ。再生成で更新）／
`features/sc06-datasources/DataSourceForm.tsx`・`features/abac/confidentiality.ts`（コード）／
`docs/specs/20260805_issue-503_sc05-08-admin-screens.md`（**確定済みの作業仕様書**）／
`docs/tests/SC-05_document-management.md`（SC-05 自身のフォームの記述で、SC-06 とは無関係）／
`docs/tests/SC-06_datasource-management.md`・`docs/screens/SC-06_datasource-management.md`。

### ［2026-08-15 追記 / #774］追随先の引き直しの数に引き算と時点を足す

**上の「追随先の引き直し（2 巡目）」と「3 巡目」が挙げた数は、宣言した走査基準 `fde7252` では再現しない。**
走査対象（`docs/` 配下）に**この作業仕様書そのもの**が入っており、**記録を書く行為が母集合を動かした**ためである
——母集合の**規則 8**（「走査対象に自分の記録が入るときは、走査がそのまま返す数を先に出し、除外と時点を明示する」）の破れで、
planning#350 と同型である。**本文は確定済みのため書き換えず、ここに引き算と時点を足す。**

**数は自分で数え直した。** issue #774 本文にも表があるが、**他人の数えは転記していない**
（`.claude/rules/traceability.repo.md`「他人の数えを検証せず転記しない」）。全軸について
`git grep -c "<語>" fde7252 -- docs` と `git grep -c "<語>" a5bbad6 -- docs` を両方実行し、
差分の出どころは `git grep -l` のファイル単位の差集合で特定した。

**軸は 6 本ではなく 12 本ある。** issue #774 の表は 2 巡目の a〜f だけを挙げているが、
3 巡目の g〜l も同じ走査・同じ破れ方であり、**a〜f だけを直すと同型を残す**。12 本すべてに入れる。

- `既定の機密区分`: 11 行 / 5 ファイル（公開時点 `a5bbad6`）− 自己参照 4 行 1 ファイル = **7 行 / 4 ファイル**（走査基準 `fde7252`）
- `登録フォーム`: 33 行 / 9 ファイル（`a5bbad6`）− 自己参照 12 行 1 ファイル − 同 PR #771 の追随先 7 行 0 ファイル = **14 行 / 8 ファイル**（`fde7252`）
- `入力項目`: 15 行 / 12 ファイル（`a5bbad6`）− 自己参照 4 行 1 ファイル = **11 行 / 11 ファイル**（`fde7252`）
- `defaultAttributes`: 37 行 / 12 ファイル（`a5bbad6`）− 自己参照 9 行 1 ファイル − 同 PR #771 の追随先 6 行 4 ファイル = **22 行 / 7 ファイル**（`fde7252`）
- `DataSourceForm`: 11 行 / 5 ファイル（`a5bbad6`）− 自己参照 7 行 1 ファイル = **4 行 / 4 ファイル**（`fde7252`）
- `接続先 URI`: 6 行 / 3 ファイル（`a5bbad6`）− 自己参照 2 行 1 ファイル = **4 行 / 2 ファイル**（`fde7252`）
- `unassigned`: 42 行 / 9 ファイル（`a5bbad6`）− 自己参照 11 行 1 ファイル − 同 PR #771 の追随先 8 行 2 ファイル = **23 行 / 6 ファイル**（`fde7252`）
- `予約値`: 49 行 / 8 ファイル（`a5bbad6`）− 自己参照 15 行 1 ファイル − 同 PR #771 の追随先 7 行 2 ファイル = **27 行 / 5 ファイル**（`fde7252`）
- `供給源`: 14 行 / 5 ファイル（`a5bbad6`）− 自己参照 4 行 1 ファイル − 同 PR #771 の追随先 2 行 0 ファイル = **8 行 / 4 ファイル**（`fde7252`）
- `#754`: 32 行 / 7 ファイル（`a5bbad6`）− 自己参照 6 行 1 ファイル − 同 PR #771 の追随先 1 行 0 ファイル − 別 PR（#769 / #770）9 行 1 ファイル = **16 行 / 5 ファイル**（`fde7252`）
- `100%`: 17 行 / 10 ファイル（`a5bbad6`）− 自己参照 4 行 1 ファイル − 同 PR #771 の追随先 2 行 0 ファイル = **11 行 / 9 ファイル**（`fde7252`）
- `入力欄`: 41 行 / 14 ファイル（`a5bbad6`）− 自己参照 11 行 1 ファイル − 同 PR #771 の追随先 3 行 1 ファイル − 別 PR（#770）1 行 1 ファイル = **26 行 / 11 ファイル**（`fde7252`）

**「自己参照 N 行」の N がどのファイルの何行かを確かめてから書いた。** 引き算の各項の内訳は次のとおりで、
`fde7252` の値に足し戻すと `a5bbad6` の値に一致する（12 軸すべてで検算済み）。

| 区分 | ファイル | 各軸への寄与（行） |
| --- | --- | --- |
| **自己参照** | `docs/specs/20260815_issue-767_sc06-department-input.md`（本仕様書。`fde7252` には**存在しない**ので全 12 軸で ＋1 ファイル） | a+4 / b+12 / c+4 / d+9 / e+7 / f+2 / g+11 / h+15 / i+4 / j+6 / k+4 / l+11 |
| 同 PR #771 の追随先 | `docs/adr/IADR-0199_ingestion-required-attribute-failsafe.md` | b+2 / d+1 / g+1 / h+2 / j+1 / k+2 / l+1 |
| 同 PR #771 の追随先 | `docs/data/data-source.md` | b+3 / d+1 / g+1 / i+2 / l+1 |
| 同 PR #771 の追随先 | `docs/screens/SC-06_datasource-management.md` | d+1 / g+2 / h+2 / l+1 |
| 同 PR #771 の追随先 | `docs/tests/SC-06_datasource-management.md` | b+2 / d+3 / g+4 / h+3 |
| **別 PR（本作業と無関係）** | `docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md`（#770） | j+8 / l+1 |
| **別 PR（本作業と無関係）** | `docs/how-to/session-handoff.md`（#769） | j+1 |

**上表の記載値は base でも公開時点でもない**（軸 a のみ偶然 `a5bbad6` と一致する）。**作業途中の時点の値**であり、
どの sha を指すとも書けない——これが「手順どおり追試しても再現しない数」の実体である。

**射程の確認（規則 5・9 に従い、直す前に破れの範囲を引き直した）**: 前節「母集合の引き直し § 軸ごとの実測」の
軸 1〜9（リポジトリ全体の走査）は `fde7252` で**再現する**——`department` 333 行 / 87 ファイル・`DEPARTMENT` 0 / 0・
`defaultAttributes` 71 / 19・`DefaultAttributes` 102 / 21・`CONFIDENTIALITY_KEY` 10 / 5・`unassigned` 41 / 12・
`部門` 106 / 57・`所管部門` 2 / 2・`既定の機密区分` 9 ファイルがいずれも base の実測と一致した
（着手時に走査してから書いたためである）。**破れているのは追随先の引き直し（a〜l）だけ**であり、本追記の射程はそこに閉じる。

**本追記自体がこの走査に入る。** 上の数はすべて**本追記より前の時点 `a5bbad6` の値**であり、
本追記をコミットした後に同じ語で `docs` を引くと**さらに増える**（本ファイルは 12 軸すべての検索語を含む）。
入れ子は避けられないので、**値は sha で固定して読む**——これが規則 8 の言う「時点の明示」である。
追試する者は `-- docs` の走査に `fde7252` か `a5bbad6` を必ず添えること。

**検査器は足さない。** 本リポの規約は「検査器・規約の追加は**同型の事故が 2 回起きたら**」を条件とし、
1 回目は記録に留める。planning#350 は計画リポ側の事象なので、**本リポとしてはこれが 1 回目**である。
**次に同型（走査対象へ自分の記録が入ったまま数を公開する）が起きたら 2 回目であり、そのときは検査器を置く判断になる。**
記録先は issue #774 と本追記の 2 箇所である。

### 変更したもの / 除外したものと理由（規則 6）

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `features/abac/department.ts`（新規） | **追加** | 語彙の単位。`confidentiality.ts` と同じ置き方に揃える |
| `features/abac/department.test.ts`（新規） | **追加** | 語彙が黙って変わらないことを固定する |
| `features/sc06-datasources/DataSourceForm.tsx` | **変更** | 入力欄の追加（本体） |
| `features/sc06-datasources/DataSourceManagementPage.test.tsx` | **変更** | 受け入れ基準の写像 |
| `i18n/locales/{ja,en}/messages.{po,ts}` | **再生成** | `pnpm run i18n`。手では編集しない |
| `features/adminFlow.test.tsx` | **除外** | 一覧応答のフィクスチャで `defaultAttributes` を持つだけで、登録フォームを送らない。宣言済みファイル領域の外でもある |
| `docs/api/openapi.yaml` | **除外** | 自由辞書へのキー追加であり契約が変わらない（issue の判断を実測で追認した。`CreateDataSourceRequestDefaultAttributes = {[key: string]: string} \| null`） |
| `src/*/backend/**`（軸 4 の 21 ファイル） | **除外** | 本セッションに dotnet が無く `build` / `test` / `format` を走らせられない。DoD を満たせない変更は入れない。**読むのは行った**（下記「未入力時の挙動」の判断根拠） |
| `scripts/measure-abac-combinations.js` | **除外** | 予約値の減少の実測は DB / Keycloak 稼働が前提。issue のスコープ外 |
| `docs/specs/20260805_issue-503_sc05-08-admin-screens.md` | **除外** | 確定済みの作業仕様書は後付けで書き換えない（`.claude/rules/traceability.repo.md`） |
| `docs/tests/SC-05_document-management.md` | **除外** | SC-05 自身のフォームの記述で、SC-06 の項目ではない |
| **`docs/screens/SC-06_datasource-management.md`** | **変更（同 PR 内で消化）** | §表示・入力項目・§モックに無いが実装する要素・§i18n・§関連仕様が**本変更で古くなる**（規則 10 で検出）。**当初は「宣言済み領域外なので別 issue」と判断したが差し戻された** —— live な仕様書であり、古くなると分かっている記述を別 issue へ送るのは規則 10 が禁じる型そのものである。領域を広げて同 PR で直した |
| **`docs/tests/SC-06_datasource-management.md`** | **変更（同 PR 内で消化）** | §UC-04 のフロー写像・§テストケース・§純関数が同じく古くなる。理由は上に同じ |
| **`docs/adr/IADR-0199_ingestion-required-attribute-failsafe.md`** | **変更（日付つき追記）** | **3 箇所が本変更で事実として誤りになる** —— L63 の表「加えて SC-06 に入力欄が無い」・L90-91「`department` の入力欄も更新経路も無い／画面から登録した全データソースが `unassigned` になる」・L202「#754: … SC-06 の入力欄」。`Accepted` な ADR のため**本文は消さず、日付つき追記ブロック（`［2026-08-15 追記 / #767］`）で現状を併記**した（`.claude/rules/traceability.repo.md` §Superseded 引用の書式に倣う）。`updated:` は既に 2026-08-15 |

## 対象範囲

- 対象: SC-06 登録フォームへの `department` 入力欄、`defaultAttributes` への積載、語彙定数、ja / en 文言、テスト
- 対象外: フォルダ → 部門コードの写像（planning#372 の裁定待ち。**実装側で推定規則を決めない**）／ソース側権限情報のヒント取り込み（`src/*/backend/**`）／更新用 UI（SC-06 に更新経路が無い）／予約値の減少の実測

## 設計

### 語彙の単位（`features/abac/department.ts`）

`confidentiality.ts` と同じ理由で**画面フォルダではなく語彙フォルダ**へ置く。`department` は SC-06（データソースの
既定部門）だけでなく SC-01 / SC-08 の対象範囲軸（`scope-filter/scopeFilter.ts` の `SCOPE_AXES`）でも同じキーを
使うためである。本 PR では **SC-06 が使う 2 つの値だけ**を置く（`scopeFilter.ts` の書き換えは射程外。既存の
文字列リテラルを定数へ寄せる作業は別の変更である）。

- `DEPARTMENT_KEY = 'department'` — ABAC 属性辞書のキー
- `UNRESOLVED_DEPARTMENT = 'unassigned'` — 解決できなかったときの**予約値**（既定値ではない）

**値集合は持たない。** `confidentiality` と違い、部門コードの値域は計画に列挙が無く（07_abac-attribute-model は
「部門コード（人事/経理/開発 等）」と例示するのみ）、SC-09 の属性辞書が管理する。**実装が値集合を決めると
事実上の用語定義になる**ため、自由入力にする。

### フォーム（`DataSourceForm.tsx`）

- `Input`（テキスト・**任意**）。`既定の機密区分` の直後に置く（どちらも「既定属性」であるため）。
- **最大長を設けない。** 名前 200 / URI 500 は後段の検証に合わせた値だが、`department` には対応する後段の
  制約が無い（`DefaultAttributes` は自由辞書）。無根拠な上限は入れない。
- 送信時に `trim()` し、**非空のときだけ** `defaultAttributes` へキーを積む。
- 未入力時の扱いを画面上でも伝える補助文を置く（予約値 `unassigned` は**翻訳しない**——機密区分の値を
  翻訳しないのと同じ理由）。

### 未入力時の挙動（**決定: キーを送らない**）

**バックエンドのコードを実際に読んで決めた。**
`src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Domain/DataSource.cs`
の `FillIfBlank`（L136-140）は

```csharp
if (!attributes.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    attributes[key] = fallback;
```

であり、`WithRequiredAttributeFailsafe`（L100-133）から `DepartmentKey` に対して呼ばれている（L117）。
すなわち **`""`（空文字）も空白のみも `unassigned` へ倒れ、素通りしない**。`Create` / `Update` / `Patch` /
`GetEffectiveAttributes` のいずれもこの 1 本を通る。

したがって**今日の後段では、キーを送らない場合と空文字を送る場合の結果は同一である**。それでも
**キーを送らない**を採る理由は 3 つ。

1. **後段の空白判定に依存しない。** 空文字を送る形は「後段が `IsNullOrWhiteSpace` で潰してくれる」ことに
   依存する。判定が `TryGetValue` の有無だけに変わった瞬間、**画面から登録した全ソースの部門が空文字**になり、
   予約値との区別（＝環流債務の測定値。[[IADR-0199]] 決定 3）が壊れる。**壊れても静かである。**
2. **「解決できなかった」と「指定しなかった」を辞書の形で区別できる。** 空文字は「管理者が明示的に空を指定した」
   とも読め、計画が `unassigned` に持たせた意味（解決できなかったことの記録）を濁す。
3. **既存の送信形と一貫する。** 現状の POST 本文は `defaultAttributes: { confidentiality }` だけを積んでおり、
   送らない値はキーごと出さない形になっている。

## 受け入れ基準（issue #767 より転記）

- [x] SC-06 の登録フォームから `department` を送れる
- [x] `department` 未入力時の挙動が明示的である（**キーを送らない**に固定した。上記）
- [x] ja / en の文言が揃っている（`check-i18n-catalogs.js` が緑）
- [x] `pnpm run typecheck` / `lint` / `format:check` / `test:coverage` が緑。カバレッジ床を割らない
- [x] 変異試験の結果（何を壊すと何が落ちるか）を本仕様書に記録した（下記）

## テスト方針

| # | 受け入れ基準 / フロー | テスト |
| --- | --- | --- |
| T1 | UC-04 基本 1（値を入れて登録） | `registers a data source with a default department attribute`（POST 本文の**完全一致**で `defaultAttributes: { confidentiality, department }` を見る） |
| T2 | 未入力時の挙動（キーを送らない） | 既存の `registers a data source with a default confidentiality attribute` が POST 本文を `toEqual` で見ており、**空文字を送ると落ちる**。追加で「`department` キーを持たないこと」を明示アサートする |
| T3 | 前後の空白は落とす | T1 に空白つきの入力（`'  開発  '`）を混ぜる |
| T4 | 語彙の固定 | `abac/department.test.ts`（キーと予約値の文字列を固定） |
| T5 | en ロケール | 既存の `renders in English when the en locale is active` を拡張し、en で登録フォームを開いて `Default department` のラベルを見る |
| T6 | 部門は**任意**（計画に無い必須化を実装が足さない） | `does not require a department to enable the register button`。あわせて未入力時の説明文（予約値 `unassigned`）が出ることを見る |

## 変異試験（実施結果）

6 件すべて実際に適用して測った（宣言だけの記録にしない）。走査範囲は
`vitest run knowledge/frontend/src/features/sc06-datasources knowledge/frontend/src/features/abac`
（クリーン時は **40 件すべて緑**）。

| # | 変異 | 落ちた検査 | 実測 |
| --- | --- | --- | --- |
| M1 | `DataSourceForm.tsx` から `department` の `<Label>` / `<Input>` / 補助文を削除する | T1・T5・「任意であること」 | **3 件 fail**（`Unable to find a label with the text of: /既定の部門/`） |
| M2 | 送信時の `defaultAttributes` から `department` を落とす（入力欄は残す） | T1 | **1 件 fail**（POST 本文の `toEqual` 差分） |
| M3 | 未入力でもキーを送る（`[DEPARTMENT_KEY]: trimmedDepartment` を無条件に積む） | T2（既存テスト） | **1 件 fail** |
| M4 | `ja/messages.po` の `既定の部門` の `msgstr` を空にする | `check-i18n-catalogs.js` | **fail**（`ja: 未翻訳（msgstr が空）: "既定の部門"`） |
| M5 | `en/messages.po` の同じ `msgstr` を空にする | `check-i18n-catalogs.js` ＋ `lingui compile --strict` ＋ T5 | **fail**（下記の注意つき） |
| M6 | `abac/department.ts` の `UNRESOLVED_DEPARTMENT` を `'unresolved'` へ変える | T4 ＋ 補助文のアサート | **2 件 fail** |

**`check-test-spec-coverage.js` は本変更のテストを見ていない（裏取り済み）。** 同スクリプトを読むと、
方向 (b) は `TEST_CLASS_FILE = /(^|\/)([A-Za-z0-9_]+Tests)\.cs$/`、方向 (a) は
`CS_PATH = /(?<![\w./-])((?:src|deploy|scripts|tools|\.github|docs)\/[A-Za-z0-9_./-]+\.cs)/g` であり、
**どちらも `.cs` で終わるパスしか見ない**（冒頭 L29「見るのは `.cs` で終わるパスだけである」・偽陽性 6 クラスの
理由書き）。フロントの `*.test.tsx` / `*.test.ts` は原理的に対象外である。
`check-test-traceability.js` も「SC-06 が 1 件でも参照されていれば緑」であるため止まらない。
**したがって、テスト仕様書への記載漏れを止める機械は本変更の範囲には無く、書き手が守るしかない。**

**素通りした変異は無い。** 予測と実測がずれた点と、注意を要する点を開示する。

1. **M2 / M3 は「2 件落ちる」と予測したが 1 件だった。** T1 と T3（trim）、T2 と追加アサートは
   それぞれ**同じ `it` の中**にあるため、テストの件数としては 1 件に数えられる。アサートは両方落ちている。
2. **M5 は「`.po` を空にしただけ」では単体テストが素通りする。** 実行時に読まれるのは**コンパイル済みの
   `messages.ts`** であり、`.po` だけを壊しても古い `messages.ts` が残っていれば T5 は緑のままだった
   （実測: 35 件すべて緑）。`npx lingui compile` で再生成して初めて T5 が落ちた（1 件 fail）。
   **したがって en の未翻訳を止めているのは、実質 `check-i18n-catalogs.js` と `lingui compile --strict`
   （`pnpm run i18n` が非ゼロ終了する）の 2 本であり、単体テストではない。** この事実は
   [[IADR-0125]] 決定 4 が「再生成差分検査だけでは未翻訳を検出できない」と述べた穴の実測でもある。

## 計画書との差異

- 差異: **なし**。計画（09_datasource-connectors §システム投入経路）の 3 段のうち 2 段目だけを実装した。
  1 段目（フォルダ → 部門コードの写像）は planning#372 の裁定待ちであり、**実装側で推定規則を決めていない**。

## 未決事項

1. ~~**`docs/data/data-source.md` の追随が未消化。**~~ → **同 PR 内で消化した**（3 巡目）。
   L84 に日付つき追記を入れ、L88-94 の「事実上 100% が予約値へ倒れる」を**属性ごとの度合いの対比表**へ
   書き改めた —— **`owner` は事実上 100% のまま、`department` は「管理者が SC-06 で値を入れなければ倒れる」**
   である。**「もう倒れない」とは書いていない**（開いたのは供給源 3 つのうち登録フォームの 1 つだけで、
   フォルダ写像と権限情報の取り込みは入っていない。既存の登録済みソースも遡って値を得ない）。
   [[IADR-0199]] 側は同じ事実を**複写せず**、決定 2 の冒頭に短い日付つき注記を置いて対比表へ誘導した。
   **これで追随が要る箇所は残っていない**（走査 3 巡・12 軸の結果）。
2. **部門コードの値域と候補 UI**。現状は自由入力である。SC-09 の属性辞書が値集合を持ち、SC-01 / SC-08 は
   権限内候補 API（`ADR-0043`）で候補を出しているが、**管理者が新しい既定部門を設定する場面で「到達できる
   文書に実際に付与されている値のみ」を返す候補 API を使うのは誤り**である（まだ 1 件も無い部門を設定できない）。
   属性辞書側の候補口が要るかは未確定。
3. **フォルダ → 部門コードの写像**（planning#372）。裁定が下りるまで実装しない。

## バンドル初期ロードの床を更新した（CI が検出。**手元の検証が漏らしていた**）

`check-chunk-budget` が CI で fail した —— **初期ロード合計 622.36 kB > 床 622.06 kB（+0.30 kB）**。

**増加の出どころを特定してから床を上げた**（「意図した増加」を確かめずに `--update` を打たない）。

```console
$ grep -lo "既定の部門\|Default department" src/platform/frontend/dist/assets/*.js
src/platform/frontend/dist/assets/index-CoQ0WPnZ.js
```

**新しい文言は初期チャンク `index-*.js` に入っている。** Lingui のカタログ
（`platform/frontend/src/foundation/i18n/locales/*/messages.ts`）は foundation が**即時 import** するため、
遅延チャンクに逃がせない。一方 **UI 本体（`DataSourceForm` の入力欄と `features/abac/department.ts`）は
遅延チャンク `DataSourceManagementPage-*.js`（8.49 kB）側にある**。

つまり **+0.30 kB は「翻訳文言を 1 つ足したことの下限コスト」**であり、
[[IADR-0134]] の分割境界を変えても消せない。したがって床を更新した（`scripts/chunk-budget-baseline.json`
の `initialTotalBytes` を `622064` → `622363`）。**分割規則そのものは 1 行も変えていない。**

### 手元の検証手順に穴があった（記録）

**この検査器は `pnpm run build` の成果物を読む**ため、`typecheck` / `lint` / `format:check` / `test:coverage`
だけを回した手元検証では**原理的に発火しない**。CI が最初の検出者になった。

**次に i18n の文言を足す作業では、手元でも `pnpm run build` → `node scripts/check-chunk-budget.js --require`
まで回すこと。** 文言追加は必ず初期ロードを増やすので、**毎回この床に当たり得る**。
