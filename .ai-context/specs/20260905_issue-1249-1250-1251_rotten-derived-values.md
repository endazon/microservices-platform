---
title: 作業仕様書 — live 文書に残った腐った導出値 3 件（環流中の主張・画面 feature 列挙・Kernel 参照比率）を是正し、導出値そのものを本文から外す（#1249 / #1250 / #1251）
type: spec
status: draft
related_ids:
  - NFR
  - ADR-0030
  - ADR-0031
  - ADR-0041
  - ADR-0065
  - ADR-0068
  - IADR-0282
  - IADR-0334
  - IADR-0371
  - IADR-0383
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30 / updated 2026-09-03) 決定 3・決定 4
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md ［2026-08-30 改定 / ADR-0065］
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: live 文書の腐った導出値 3 件の是正（#1249 / #1250 / #1251）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 直接の起点は無い（`NFR`。文書統制のメタ作業。`traceability.md` の無採番 `NFR` の 2 に当たり、環流はしない）
- 関連 ADR: `ADR-0065`（サービス標準構成 / Tests 鏡写し）・`ADR-0030` / `ADR-0041`（バックエンド標準ライブラリ・Result 型）・`ADR-0031`（フロントエンドスタック）・`ADR-0068`
- 関連 IADR: `IADR-0282`（単一プロジェクト＋VSA）・`IADR-0334`（鏡写し先の解決）・`IADR-0371`（参照実装 1 本）・`IADR-0383`（本作業の決定）

## 目的・背景

フェーズ末監査バッチ③が、live 文書に残った **導出値（ツリー・計画側の状態から導ける値）の腐り** を 3 件起票した。
いずれも `.claude/rules/traceability.repo.md` の **規則 9**（誤りの側の文字列で全文書を走査してから母集合を挙げる）
または **規則 10**（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）の破れである。

| issue | 腐った導出値 | 破れた規則 |
| --- | --- | --- |
| #1249 | 「計画側条文の改定を**環流中**」（実際は `ADR-0065` Accepted・planning 側条文も改定済み・planning#490 は closed） | 規則 9（#1146 が雛形 README だけ直し母集合を引き直さなかった） |
| #1250 | knowledge 画面 feature の列挙「`home, sc01..sc11`」（実測は `sc01`〜`sc12`・`sc17`〜`sc21` の 17 件・`home` は無い） | 規則 10（同ファイルを直近 2 PR が編集したが引き直していない） |
| #1251 | 「実測 2026-09-04: `Platform.Shared.Kernel` への `ProjectReference` **0/14 サービス**」（それを書いた PR 自身が同一コミットで 1/14 にした） | 規則 10 |

## 対象範囲

- 対象: 追跡下の **live（凍結記録でない）文書** に残る上記 3 種の導出値、およびその走査で見つかった同型の残存。
- 対象外:
  - `.ai-context/adr/` `.ai-context/specs/` `.ai-context/superpowers/`（凍結記録。当時の実測と当時の判断を述べており、後付けで書き換えない。`traceability.repo.md`「凍結の射程は記録種ごとに違う」）
  - `src/ai-stock-trading`（submodule。本リポの追随義務の外）
  - `CHANGELOG.md`（生成物。手で書き足さない）
  - 既存コミット履歴（force push 禁止）

## 束ねる理由（1 issue = 1 PR の例外）

原則は 1 issue = 1 PR（IADR-0116 規約 1）だが、本 3 件は **同じ 2 ファイル（`docs/tech/tech-requirements.md` と
`src/README.md` / `src/platform/frontend/README.md`）の同種の是正**であり、別 PR にすると互いに衝突して
FIFO のマージ列を無駄に詰まらせる。実測でも母集合が交差する:

- `src/README.md` は #1249（83 行目「改定を環流中」）と #1250（38 行目「`home, sc01..sc11`」）の**両方**に当たる
- `docs/tech/tech-requirements.md` は #1249（112 / 190-191 行目）と #1251（249-251 行目）の**両方**に当たる

よって IADR-0139 決定 1 の「裁定済みの同型な契約追加」ではなく、**同一ファイルの同型な是正**として 1 PR に束ねる。
射程は上の「対象範囲」を出ない（新機能・新規約は足さない）。

## 母集合（1 回目・是正前）

走査母集合は追跡下の全ファイル（`git grep`。**拡張子で絞らない**——規則 3。パス除外のみ）。
`':!src/ai-stock-trading'` は未 populate / 別リポのため除外する。

### 軸 A1 — `環流中`（#1249。誤りの側の語）

```console
$ git grep -n "環流中" -- . ':!src/ai-stock-trading'
docs/tech/composable-component-guide.md:218:  で環流中。上流が改版されたら本書 §1（接続仕様）を照合すること。
docs/tech/tech-requirements.md:112:**同裁定で撤回**され、計画側条文の改定を環流中である —— 8 つの**関心**はフォルダと
docs/tech/tech-requirements.md:191:種別区分の計画側条文は改定を環流中）。**`.csproj` の実名はホスト種別を含めず `<Name>.Tests` とする**
src/README.md:83:改定を環流中である（planning#490 のコメント）。
templates/unit-template/README.md:140:  種別区分の計画側条文は `ADR-0065` 決定 3 として **Accepted 済み**であり「環流中」ではない）。
```

**5 件 / 4 ファイル。** 内訳と扱い:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/tech/composable-component-guide.md:218` | **除外** | **別主題**である。計画 `10_composability-design` §2〜§5 との相互参照の照合を環流中と述べており、`ADR-0065` とも 8 要素条文とも無関係（前後 6 行を読んで確認した） |
| `docs/tech/tech-requirements.md:112` | 是正 | 8 要素条文の改定を「環流中」と述べている |
| `docs/tech/tech-requirements.md:191` | 是正 | `Tests` の種別区分条文を「環流中」と述べている |
| `src/README.md:83` | 是正 | 同上（8 要素条文） |
| `templates/unit-template/README.md:140` | **変更不要** | #1146 が既に「Accepted 済みであり『環流中』ではない」へ是正済み。**この行は「環流中ではない」と否定形で述べている**ため、語としては当たるが誤りではない（規則 1 の「誤りの側から引く」を守ると否定形も当たる。生の出力で判断した） |

### 軸 A2/A3 — `環流` の他の形（規則 2: あり得る形をすべて列挙してから引く）

```console
$ git grep -nE "環流(中|待ち|予定|し中|している最中)|改定を環流|改定待ち|条文の改定|未改定" -- . ':!src/ai-stock-trading'
```

→ A1 の 5 件に加えて `.ai-context/` 配下 6 件のみ（`IADR-0204:135` / `specs` 5 件）。**すべて凍結記録なので除外**する
（「環流待ち planning#379」等は当時の事実の記録である）。live 側の新規は 0 件。

### 軸 A4 — `planning#490` を引く箇所（別軸。規則 5）

```console
$ git grep -nE "planning#490|project-planning#490" -- . ':!src/ai-stock-trading'
```

→ 21 件。live は 4 件（`docs/tech/tech-requirements.md:14` = frontmatter の issues リスト、`src/README.md:83`、
`scripts/check-scaffolding-frames.js:20`、`src/platform/frontend/README.md:41`）。
`src/README.md:83` 以外は **「planning#490 の環流記録が予告していた」「環流 planning#510」等の履歴の記述**であり、
現況の主張ではないので除外する。frontmatter のリストは参照であって主張ではない。

### 軸 A6/A7 — 鏡写しの範囲（#1249 やること 2）

```console
$ git grep -n "鏡写" -- . ':!src/ai-stock-trading' ':!.ai-context'
$ git grep -n "Tests/Features" -- . ':!src/ai-stock-trading'
```

live で **`Features/` と `Domain/` の 2 つだけ**を挙げているのは:

| 箇所 | 扱い |
| --- | --- |
| `docs/tech/tech-requirements.md:123`（ツリー内コメント `# テストは 1 プロジェクト（フォルダは実装の鏡写し: Features/・Domain/）`） | **是正**（issue が名指ししていないが同じ誤り。規則 9 の走査で拾った） |
| `docs/tech/tech-requirements.md:190` | 是正 |
| `docs/how-to/adding-a-unit-submodule.md:35-37` | **変更不要**。既に `Domain/ Infrastructure/<Sub>/ Common/<Sub>/` と広い |
| `docs/tests/TEST_STRATEGY.md:310-312` | **変更不要**。PR #1170 が既に是正済み（本作業の写像先） |
| `templates/unit-template/**` | **変更不要**。#1146 が是正済み |

### 軸 A12 — `scNN..scNN` の範囲短縮形（#1250。誤りの側の形）

```console
$ git grep -nE "sc[0-9]{2}\.\.sc?[0-9]{2}" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md:74:   `knowledge/frontend` = features（home・sc01..sc11）。
.ai-context/specs/20260710_FR-14_repo-restructure-platform-knowledge.md:79:│   ├── frontend/                  # ナレッジ画面 features（home, sc01..sc11）
.ai-context/specs/20260710_FR-14_repo-restructure-platform-knowledge.md:99:- **knowledge/frontend**: `features/`（home・sc01..sc11）一式。
src/README.md:38:    frontend/                  ←   ナレッジ画面 features（home, sc01..sc11）
src/platform/frontend/README.md:36:    src/features/<screen>/       # home, sc01..sc11。FeatureModule を公開し features/index.ts へ登録
```

🔴 **issue #1250 は `src/platform/frontend/README.md` の 1 箇所しか挙げていないが、`src/README.md:38` に
同じ列挙がもう 1 箇所ある。** 誤りの側の形で引いたことで見つかった（規則 9 が働いた例）。`.ai-context/` の
3 件は凍結記録なので除外する（区切りが `・` と `,` で揺れているため、**区切りを固定せずに引いた** —— 規則 2）。

別軸（規則 5）として `home` の側からも引いた:

```console
$ git grep -nE "home, ?sc|features/home|'home'" -- . ':!src/ai-stock-trading'
```

→ 上の 2 件に加えて `src/platform/frontend/src/app/routing/breadcrumbs.{ts,test.ts}` の
`BreadcrumbSegmentKind = 'home'`（パンくずの種別。feature ディレクトリとは無関係）→ **除外**。

### 軸 A8/A10 — `N/14` と `0 件`（#1251）

```console
$ git grep -nE "[0-9]+/14( ?サービス)?" -- . ':!src/ai-stock-trading'
```

→ live は `docs/tech/tech-requirements.md:251` の 1 件のみ。他は `.ai-context/adr/IADR-0371*`（決定当時の実測）・
`.ai-context/specs/`（同）・`.ai-context/adr/README.md:447`（IADR-0371 の索引行）・
`src/vitest.config.ts:260`（カバレッジの内訳。無関係）。**凍結記録は除外**する。

```console
$ git grep -nE "(FluentValidation|Mapperly).{0,80}0 ?件|0 ?件.{0,80}(FluentValidation|Mapperly)" -- . ':!src/ai-stock-trading'
```

→ live は `docs/tech/tech-requirements.md:250` のみ（`.ai-context/` 4 件は凍結）。

## 正しい値（走査ではなく計算し直した実測。2026-09-05・develop `3d5f8c99`）

`git rev-parse --is-shallow-repository` = **`false`**（履歴は完全。`git log` を出典に使ってよい）。

### (1) 計画側の状態（#1249）

**ローカルの隣接クローンではなく GitHub API で引いた**（隣接クローンは pin 無し・鮮度が保証されないため）。

```console
$ gh issue view 490 --repo endazon/project-planning --json number,state,closedAt
{"closedAt":"2026-08-30T03:38:33Z","number":490,"state":"CLOSED", ...}
$ gh api "repos/endazon/project-planning/contents/.../07_adr/ADR-0065_backend-service-single-project-vsa.md" --jq .content | base64 -d | head -25
status: Accepted
created: 2026-08-30
updated: 2026-09-03
$ gh api "repos/endazon/project-planning/contents/.../06_technical/12_backend-application-stack.md" --jq .content | base64 -d | grep -n "改定"
38:**［2026-08-30 改定 / ADR-0065］サービスは単一プロジェクトとし、関心はフォルダで分ける。** …
120:  **［2026-08-30 部分改定 / ADR-0065 決定 3］内部のフォルダ区分を「本体の鏡写し」（`Tests/Features/` ／ `Tests/Domain/`）へ改めた。**
```

→ **`ADR-0065` は Accepted、planning 側条文は 2026-08-30 に改定済み、planning#490 は closed。「環流中」は偽。**

なお計画側条文が挙げるのは字義どおり `Tests/Features/` ／ `Tests/Domain/` の 2 つであり、それを
「検証する本体の要素が置かれたディレクトリ」へ解決したのは実装側（`IADR-0334`）である。**この関係を
本文に書く**（「計画の 2 つが誤り」ではなく「実装側が解決した」）。

### (2) knowledge 画面 feature（#1250）

```console
$ git ls-files src/knowledge/frontend/src/features/ | awk -F/ 'NF>6 {print $6}' | sort -u
sc01-search sc02-results sc03-document sc04-wiki sc05-documents sc06-datasources sc07-conversions
sc08-analysis sc09-admin-abac sc10-operations sc11-config sc12-mcp-clients sc17-users sc18-graph
sc19-private-notes sc20-obsidian-settings sc21-ai-suggestions
$ … | wc -l
17
```

→ **17 件。`home` は無い。** 陽性対照: 同じ走査で `features/index.ts` ほか 5 つの直下ファイルが
`NF==6` 側に出る（走査は機能しており、0 件を「無い」と読んでいない）。

同 README の他の導出値も突き合わせた（#1250 やること 2）:

```console
$ git grep -ln "zustand" -- src/platform/frontend/src
src/platform/frontend/src/components/ai-chat/aiChatStore.ts          ← 1 本
$ git grep -ln "aiChatStore'" -- src/ ':!src/ai-stock-trading' | wc -l
4                                                                     ← 参照元 4 件（全て components/ai-chat/ 配下）
```

→ 「`stores/` の Zustand は 1 本だけで、その 4 つの参照元がすべて `components/ai-chat/` 配下」は **一致**。
**ただしこれも腐る導出値である**ため、本作業で件数を落とす（後述の決定）。

### (3) `Platform.Shared.Kernel` ほか計画スタック 3 種の参照（#1251）

```console
$ git ls-files 'src/*/backend/Services/*/*.csproj' | wc -l
28                                        ← 14 サービス本体 + 14 テスト
$ git grep -n "<ProjectReference" -- 'src/*/backend/Services/*/*.csproj' | grep Kernel
src/knowledge/backend/Services/FeedbackService/FeedbackService.csproj:45:    <ProjectReference Include="..\..\..\..\platform\backend\Shared\Platform.Shared.Kernel\Platform.Shared.Kernel.csproj" />
                                          ← 1 件（1/14）
$ git grep -h "PackageReference" -- '*.csproj' ':!src/ai-stock-trading' | grep -ci "FluentValidation"
2                                         ← FeedbackService + 雛形 SampleService
$ git grep -h "PackageReference" -- '*.csproj' ':!src/ai-stock-trading' | grep -ci "Mapperly"
2                                         ← 同上
```

🔴 **issue #1251 は `Platform.Shared.Kernel` の 0/14 だけを挙げているが、同じ文の
FluentValidation **0 件** と Riok.Mapperly **0 件** も同じ PR が偽にしていた**（いずれも実測 2 件）。
規則 10 のとおり「この変更で新たに誤りになる記述」を**同じ文の中で**引き直した結果である。
陽性対照: 同じ走査で `WolverineFx` は 14 件を返す（走査器は生きている）。

## 設計（何をどう直すか）

🔴 **方針: 直した数を書き直すのではなく、導出値そのものを本文から外す。** 数を書き直すだけだと
次に木が動いた瞬間にまた腐り、本 issue が 3 度目の同型事故として再発する。決定は `IADR-0383` に残す。

| # | ファイル / 行 | 現状（誤り） | 是正後 |
| --- | --- | --- | --- |
| 1 | `docs/tech/tech-requirements.md:112` | 「計画側条文の改定を環流中である」 | 「計画側条文も同日付で改定済みである」（時点つき） |
| 2 | `docs/tech/tech-requirements.md:123` | ツリー注記の鏡写し先が `Features/・Domain/` の 2 つ | 「本体の鏡写し（相手が在るぶんだけ）」へ。区分の列挙を落とす |
| 3 | `docs/tech/tech-requirements.md:190-191` | 「`Tests/Features/`・`Tests/Domain/`。… 計画側条文は改定を環流中」 | 計画側条文は改定済みと述べ、鏡写し先の解決規則は `TEST_STRATEGY.md` と同じ範囲（`Infrastructure/<Sub>/`・`Common/<Sub>/`・`Domain/Ports/` を含む）へ |
| 4 | `docs/tech/tech-requirements.md:249-251` | 「実測 2026-09-04: FluentValidation 0 件・Mapperly 0 件・Kernel 0/14 サービス」 | **比率と件数を書かない。** 「着手前はいずれも参照ゼロだった」という**着手前の事実**と「参照実装は `FeedbackService` の 1 本で、残りの展開は別 issue が持つ」に置き換える |
| 5 | `src/README.md:38` | 「ナレッジ画面 features（home, sc01..sc11）」 | 「ナレッジ画面 features（`scNN-<name>`。一覧の正本は `knowledge/frontend/src/features/index.ts`）」 |
| 6 | `src/README.md:83` | 「計画側の 8 要素条文は改定を環流中である（planning#490 のコメント）」 | 「計画側の 8 要素条文も 2026-08-30 に改定済みである（環流 planning#490 は closed）」 |
| 7 | `src/platform/frontend/README.md:36` | 「`home, sc01..sc11`。FeatureModule を公開し…」 | 「`scNN-<name>`。一覧の正本は `knowledge/frontend/src/features/index.ts`。FeatureModule を公開し…」 |
| 8 | `src/platform/frontend/README.md:58`（`stores/` の説明） | 「Zustand は 1 本だけで、その 4 つの参照元がすべて…」 | 件数を落とし「Zustand は `components/ai-chat/` に閉じている」へ（実測は変わらないが、腐る形をやめる） |

`docs/` 配下（1〜4）は **表示テキストへ計画 ID・IADR・仕様書名を書かない**（ADR-0048 決定 4）。
trace ブロックへ `IADR-0334` / `IADR-0383` と本仕様書名を足し、`updated:` を前進させる。
`src/**/README.md`（5〜8）は `docs/` ではないので従来どおり本文に ID を書いてよい。

## 検査器を足すか（IADR-0383 の主題）

CLAUDE.md の条件「同型の事故が 2 回起きたら」は**満たしている**（#1064 の 4/14 誤記 → #1232 の 0/14 →
本 3 件）。それでも**足さない**と決める。理由は `IADR-0383` に書く。要点だけ:

- 述語が閉じない。`N/14`・`N サービス`・`scNN..scNN` のいずれの形も、**時点つきの歴史記述**
  （「実測 2026-08-21: `.csproj` 14 → 0」）や**文脈依存の指示**（「3 サービスから申告を集める」）として
  正当に現れる。実測でも `N サービス` は live だけで 30 行以上あり、ほぼ全部が正当である
- 値を消せば検査対象が消える。本作業は数を**書き直す**のではなく**外す**ので、検査器は
  「もう本文に無い値」を見張ることになる
- 既に機械化できている部分は既存検査器が持つ（`check-doc-links.js` / `gen-knowledge-graph.js --check` が
  指し先の実在を、`check-scaffolding-frames.js` が木の側の不変条件を見る）

## 受け入れ基準

- [ ] `git grep -n "環流中" -- . ':!src/ai-stock-trading'` に、`ADR-0065` の条文を「環流中」と**肯定形で**述べる行が 0 件（`composable-component-guide.md:218` は別主題として残る。`templates/unit-template/README.md:140` は否定形で残る＝陽性対照）
- [ ] `docs/tech/tech-requirements.md` の鏡写しの範囲が `docs/tests/TEST_STRATEGY.md` と同じ（`Infrastructure/<Sub>/`・`Common/<Sub>/`・`Domain/Ports/` を含む）
- [ ] `git grep -nE "sc[0-9]{2}\.\.sc?[0-9]{2}" -- . ':!src/ai-stock-trading' ':!.ai-context'` が 0 件（陽性対照: `.ai-context/` の 3 件は残る）
- [ ] `docs/tech/tech-requirements.md` §浸透の状況 に `Platform.Shared.Kernel` の比率・FluentValidation / Mapperly の件数が書かれていない
- [ ] `node scripts/check-doc-links.js` / `check-doc-updated.js` / `check-doc-status-vocabulary.js` / `check-doc-type-vocabulary.js` / `check-trace-blocks.js` / `gen-knowledge-graph.js --check` / `check-reading-budget.js` が緑
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑（`docs/` をコミットしてから実行する）
- [ ] 母集合の 2 回目の引き直し（新しい語）の結果を本仕様書 §母集合（2 回目）へ記録した

## テスト方針

文書の是正であり実行コードを変えないため、機械検査（上記）と走査の再実行が検証手段である。
**新しい語での引き直し（規則 10）を是正後に必ず行い、結果を本書へ書く。**

## 計画書との差異

- 差異: なし。計画 `ADR-0065` の Accepted と planning 側条文の改定に**本リポの記述を合わせる**作業である。

## 母集合（2 回目・是正後の引き直し）

規則 10 のとおり、**是正で新しく本文へ入れた語**と、**是正で 0 件になったはずの旧語**の両方で引き直した
（2026-09-05・是正後の作業ツリー）。

### 旧語の再走査（0 件になったことの確認 ＋ 陽性対照）

```console
$ git grep -n "環流中" -- . ':!src/ai-stock-trading'
docs/tech/composable-component-guide.md:218:  で環流中。上流が改版されたら本書 §1（接続仕様）を照合すること。
templates/unit-template/README.md:140:  種別区分の計画側条文は `ADR-0065` 決定 3 として **Accepted 済み**であり「環流中」ではない）。
```

→ **2 件。どちらも 1 回目で「除外 / 変更不要」と判定した行そのもの**である（別主題・否定形）。
**これが陽性対照になっている** —— 走査器は生きており、「0 件だから無い」と読んでいない。
`ADR-0065` の条文を肯定形で「環流中」と述べる行は **0 件**。

```console
$ git grep -nE "sc[0-9]{2}\.\.sc?[0-9]{2}" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md:74
.ai-context/specs/20260710_FR-14_repo-restructure-platform-knowledge.md:79,99
```

→ **3 件。すべて `.ai-context/` の凍結記録**（1 回目の判定どおり）。live は **0 件**。

```console
$ git grep -nE "[0-9]+/14( ?サービス)?" -- . ':!src/ai-stock-trading' ':!.ai-context'
src/vitest.config.ts:260:  //  全ユニット横断  lines/statements 96.80%（6290/6498）/ …
```

→ **1 件。カバレッジの内訳で無関係**（1 回目の判定どおり）。live の文書は **0 件**。

### 新語での引き直し（この変更で新たに誤りになる記述を探す）

| 引いた語 | 結果 | 判定 |
| --- | --- | --- |
| `改定済み` | live 3 件（`tech-requirements.md:112,191` / `src/README.md:84`）＝本作業で書いた行のみ | 他に追随が要る記述は無い |
| `一覧の正本` | 2 件（`src/README.md:38` / `src/platform/frontend/README.md:37`）＝本作業の行のみ | — |
| `sc<NN>` / `sc<NN>-<name>` | 本作業の 2 件に加え `scripts/check-route-manifest.js`・`scripts/README.md`・`router.test.ts`・`.ai-context/specs/` が **既に同じ `sc<NN>-*` 表記を使っている** | 🔴 **新表記は既存の慣習と一致している**（勝手な新語ではない）。追随不要 |
| `残り ?[0-9]+ ?サービス` | live **0 件**（`.ai-context/` の 14 件はすべて凍結記録） | 「残り 13 サービス」を「残りのサービス」へ直した結果、live からこの形が消えた |

### 🔴 2 回目で新たに見つかった残存（1 回目では引けなかった）

**`docs/tech/tech-requirements.md` の `Tests` の段落に、現在形の導出値がもう 1 つあった。**

> （実装の現況は **14 サービス全件**が `Services/<Name>/Tests/<Name>.Tests.csproj` であり、
> 旧名 `<Name>.Api.Tests` / `<Name>.Worker.Tests` は **0 件**である）

実測では **14/14・旧名 0 件で値としては正しい**（`git ls-files 'src/*/backend/Services/*/Tests/*.csproj' | wc -l` = 14、
`git ls-files 'src/*/backend/Services/*/*.csproj' | wc -l` = 28 ＝ 本体 14 ＋ テスト 14、
`git ls-files | grep -E '\.(Api|Worker)\.Tests\.csproj' | wc -l` = 0）。**誤ってはいない。**

それでも直す理由は 2 つある:

1. **同じ段落を本作業が編集している。** ここを残すと、本 PR が `IADR-0383` 決定 1 を立てた当の
   ファイル・当の段落で、その決定に反する書き方を温存することになる（#1251 とまったく同型の
   「自分の変更で自分の記述を置いていく」）
2. サービスが増えれば `14` は即座に腐る。**分母 14 は `Kernel` の `0/14` と同じ分母**であり、
   #1230 / #1248 の展開波では動かないが、サービス追加では動く

**1 回目で引けなかった理由**: 1 回目の軸は `環流中` / `scNN..scNN` / `N/14` / `0 件`（ライブラリ名の近傍）で
引いており、この行は `14 サービス全件` という**別の言い回し**だった。`N/14` にも
「`(FluentValidation|Mapperly)` の近傍 80 字以内の `0 件`」にも当たらない。
**規則 10 が言う「是正前の語で引いても捕まらない」の実例そのもの**である。

是正後は決定 1 の (b)（走査コマンドを指す形）へ置き換えた。**3 回目の引き直しでは、当初
「0 件になるはず」と見込んだが実際には 3 件残った** —— 見込みを書かずに引いたのが正しかった:

```console
$ git grep -nE "[0-9]+ ?サービス全件|全 ?[0-9]+ ?サービス" -- . ':!src/ai-stock-trading' ':!.ai-context'
docs/tech/tech-requirements.md:170:… **14 サービス全件が新配置へ移送済み**であり `Services/<Name>/src/` は 1 つも残っていない。
docs/tests/TEST_STRATEGY.md:299:（14 サービス全件。ホスト種別を接尾辞に持たせる旧名 … は
docs/tests/TEST_STRATEGY.md:307:**鏡写しは 14 サービス全件と雛形で済んでいる。**
```

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/tech/tech-requirements.md:170` | **変更不要** | 🔴 **`［2026-08-28 追記］` の日付つき歴史記述**であり、「移送は完了した」「移送済み」と**過去形で出来事を述べている**。`IADR-0383` 決定 2 の除外そのもの（木が動いても偽にならない）。**決定 2 の判別基準が実際に効くことを示す実例**として残す |
| `docs/tests/TEST_STRATEGY.md:299` | **本 PR では変更しない**（申し送り） | 「実装の現況は…（14 サービス全件。…旧名は 0 件である）」＝**現在形の導出値**であり、決定 1 に照らせば直す対象である。**ただし値は現在正しく（実測 14/14・旧名 0 件）、腐ってはいない** |
| `docs/tests/TEST_STRATEGY.md:307` | 同上 | 「鏡写しは 14 サービス全件と雛形で済んでいる」＝同型 |

**`TEST_STRATEGY.md` を本 PR で触らない理由**（射程の線引き）:

1. **本 PR の宣言ファイル領域の外**である。`docs/tests/TEST_STRATEGY.md` は #1249/#1250/#1251 の
   いずれも名指ししておらず、直近では別 PR（#1170）が是正に入っている。**並列作業の非重複判定は
   宣言済みファイル領域で機械的に行う**（上流ガイド §2）ため、ここへ手を伸ばすと直列化が必要になる
2. **腐っていない。** 3 箇所とも実測と一致しており、#1249〜#1251 が起票した「腐り」ではない。
   決定 1 は**これから書くとき**と**腐りが見つかったとき**の規範であって、
   正しい既存記述を一斉に書き換える義務ではない（大規模リファクタの禁止）
3. 分母 `14` が動くのは**サービス追加時**であり、進行中の #1230 / #1248（既存サービスへの展開波）では動かない。
   **緊急性が無い**

→ **別 issue として起票し、`IADR-0383` のフォローアップに記録する**（本仕様書 §未決事項）。

## 未決事項

- 🔴 **`docs/tests/TEST_STRATEGY.md` の 2 箇所（299 / 307 行）に、同型の現在形の導出値
  （「14 サービス全件」）が残っている。** 値は実測と一致しており**腐ってはいない**が、
  `IADR-0383` 決定 1 に照らせば本文から外すべき記述である。**本 PR の宣言ファイル領域の外**
  （並列作業の非重複判定を壊さないため）かつ緊急性が無いため、**別 issue へ切り出す**。
  理由の詳細は §母集合（2 回目）の 3 回目引き直しの表を参照。
