---
title: 作業仕様書 条文が実装と食い違う箇所を是正する（決定は変えない・#591）
type: spec
status: done
related_ids: [NFR, IADR-0116, IADR-0121, IADR-0132, IADR-0135, IADR-0139, IADR-0140, IADR-0141]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0116_reimplementation-branching-and-pr-policy.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
  - ../adr/IADR-0139_domain-bundled-contract-prs.md
  - 20260808_issue-576_ast-id-qualification.md
  - 20260807_issue-594_audit-scope-and-population.md
---

# 仕様書: 条文が実装と食い違う箇所を是正する（#591）

> 本仕様書は実装着手前に作成した。**決定そのものは変えない。** 直すのは
> 「条文が述べる事実」と「実装の事実」のずれだけであり、決定を変える必要が生じたら
> 実装を止めて新 IADR を起票する（本作業ではその必要は生じなかった。§判断が要った点）。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#591**（親 #454）／起点 ID: **NFR**
- 対象の live な実装 ADR: [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) / [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) / [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)
- 規約: `.claude/rules/traceability.md`「是正・追随の母集合の取り方（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）」
  「Superseded / Deprecated な ADR を引用するときの書式（#580）」（**注記には起票 ID を添える**・
  **決定を変えない事実更新は日付つきの追記ブロック**）

## 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）

**「記録の追随のみ」**（規約の改定も検査器の新設も無い）—— クロス監査は **全面 1 巡で打ち切り**。
分類は件名ではなく差分から決めた（差分は `.md` と設定コメントの文言だけで、コード・検査器・
baseline・ワークフローを変えない）。

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = **`e2cf25e`**（PR #604 マージ後）。`git ls-files` から引き、
**パスの除外のみ**（`^planning/` `^src/ai-stock-trading/`）。**拡張子で絞らない**（`.json` /
`.dockerignore` / `.sh` も対象）。**行フィルタで継がない**（走査は 1 段で、パスから引く）。

### 起点 issue の件名が言う「3 件」は母集合ではない

**本作業では issue 本文を読めない**（GitHub へ到達できない環境。件名だけを与えられた）。
そのため**件数は最初から他人の数えとして扱い、条文と実装を突き合わせて自分で引いた**。
結果、起点の 3 本の ADR に閉じて数えても **3 型では足りず 4 型**あり、**同じ主張の反映先は
ADR の外（索引・通信仕様書・README・how-to・`.dockerignore`）に広がっていた**。

同型の先例は本リポジトリの記録で確認できる ——
[#576 の作業仕様書](20260808_issue-576_ast-id-qualification.md)（issue の「12 箇所」「6 箇所」が
どちらも母集合ではなかった）と
[#594 の作業仕様書](20260807_issue-594_audit-scope-and-population.md)（issue 本文の反映先リストに
無い箇所が増えた）。**件数は毎回引き直す。**

| 型 | 何が食い違うか | 走査（誤りの側から） | 走査の総数 | 是正する live | 除外 |
| --- | --- | --- | ---: | ---: | ---: |
| **1** | [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md)「型検査の網が張られているのは `AiAnswerDto` / `CitationDto` だけ」「残り 24 個は #519 待ち」 | `AiAnswerDto`＋`CitationDto`＋「だけ」／`#519 待ち`／`#519 が載せ替えて` | 6 | **6 occurrence / 3 ファイル** | 0 |
| **2** | [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)「`check-commit-messages.js` が見るのは件名の書式と ADR/IADR の実在だけ」 | `件名の書式と ADR/IADR の実在` | 4 | **3 occurrence / 2 ファイル** | 1 |
| **3** | 本リポのフロントを **npm** と述べる記述（実体は pnpm） | `(?<![a-zA-Z])npm[ \`)]` ＋ `npx `（全追跡ファイル） | 97 | **11 occurrence / 6 ファイル** | 86 |
| **4** | [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) が計画 ID レンジを**転記**しており、`FR-22` / `ADR-0044` / `ADR-0045` の新設（#599）に追随していない | `FR-01\.\.`／`ADR-0001\.\.` | 36 | **1 転記（2 行）/ 1 ファイル** | 34 |

**軸を 1 本で終わらせない**（規則 5）を型ごとに実行した結果、**型 1 は ADR 本体だけを見ていたら
3 occurrence で終わっていた**（索引行 2 と通信仕様書 1 が増えた）。型 3 は「IADR-0116 の 1 行」から
始めて **6 ファイル**まで広がった。

### 型ごとの内訳（是正する側）

| 型 | ファイル: 行 |
| --- | --- |
| 1 | `docs/adr/IADR-0132_*.md:139` / `:168` / `:171`、`docs/adr/README.md:188`（2 occurrence）、`docs/api/BFF_bff-surface.md:237` |
| 2 | `docs/adr/IADR-0139_*.md:129` / `:424`、`docs/adr/README.md:195` |
| 3 | `docs/adr/IADR-0116_*.md:108`、`README.md:95` / `:131`〜`:135`、`docs/how-to/adding-a-unit-submodule.md:86`、`docs/tech/system-architecture.md:218`、`templates/unit-template/frontend/package.json:6`、`.dockerignore:2` |
| 4 | `docs/adr/IADR-0116_*.md:25`〜`:26` |

### 除外したものと理由（黙って除外しない・規則 6）

| 除外先 | 件数 | 理由 |
| --- | ---: | --- |
| `docs/specs/` ・ `feedback/` | 型 2: 1／型 3: 43／型 4: 21 | **書いた時点の記録**。後から注記を足すのは記録の改竄（`.claude/rules/traceability.md`「母集合は live な権威文書とコードに限る」） |
| `CHANGELOG.md` | 全型 0（走査しても該当が無かった） | 生成物。該当したとしても `changelog-overrides.json` の `remap` でしか触らない |
| 当時の事実を述べる ADR の地の文（型 3: `IADR-0034` 2・`IADR-0056` 4・`IADR-0057` 2・`IADR-0070` 1・`IADR-0071` 1・`IADR-0081` 2・`IADR-0115` 1・`IADR-0121` 2・`IADR-0125` 1／型 2: `IADR-0140:47`） | 型 3: 16／型 2: 1 | **決定した当時の記述であって現在形の主張ではない**。とくに `IADR-0121` は npm workspaces → pnpm への移行そのものを決めた ADR であり、移行前の名を消すと決定が読めなくなる |
| キットのテンプレが持つ**他スタックの例示**（`.claude/settings.json` 1・`.github/workflows/ci.yml` 6・`claude-code-review.yml` 1・`claude-coding.yml` 1・`openapi.yml` 1・`scripts/README.md` 1・`scripts/setup.sh` 3〔コメントアウト〕） | 型 3: 14 | 本リポのフロントについての主張ではない。**キット側の記述**であり、直すならキット環流（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)）で行う |
| 「npm パッケージ」「pnpm は npm と違い」等の**正しい**言及（`CLAUDE.md` 1・`src/package.json` 1・`src/packages/ui/*` 5・`src/pnpm-workspace.yaml` 2・`.github/workflows/frontend.yml` 1） | 型 3: 10 | 誤っていない |
| 検査器のフィクスチャ・パーサの説明（型 3: `check-ai-workflow-config.js` 1・`check-permission-denials.js` 2／型 4: `check-test-traceability.js` 7・`scripts.repo.test.js` 2） | 型 3: 3／型 4: 9 | **パーサの入力として固定された文字列**。実データではないので追随の対象ではない（変えるとテストが壊れる） |
| `.claude/rules/traceability.md` 自身（型 4: 4） | 型 4: 4 | **レンジの正本**。ここは #599 で追随済みであり、本作業が直すのは「そこから転記した側」である |
| [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md)`:51`（着手時の実測「生成型を import しているのは SC-08 の 3 ファイルだけ」） | 1 | **着手時の実測は記録**。現在形の主張ではない |
| [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md)`:159`（`153 → 72` の省略可プロパティ数） | 1 | **その PR の効果の記録**。再走すると今日は 74 になるが、これは後続の契約追加（#533 / #538 / #541）が足した分であり、当時の記録が誤っていたのではない |

## 実測（条文と実装のどちらが正しいか）

### 型 1: 画面の載せ替えは済んでいる

| 観測 | 値 |
| --- | ---: |
| `bff.schemas.ts` の型を import する**非生成**ファイル | **21** |
| import されている**別個の生成型** | **24**（`AiAnswerDto` / `CitationDto` を含む） |
| 載せ替えのコミット | `ef1978e`（#559。件名に 9 個の SC を併記） |

**条文が古い。** 同じ事実は既に `IADR-0126` / `IADR-0127` / `IADR-0131` / `docs/api/BFF_bff-surface.md`
の各所で「#519 で載せ替え完了」と追記されており、**[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 系だけが取り残されていた**。

**契約側の数は変わっていない**（`components.schemas` **53** / 応答から到達 **36** / `required` あり
**31** / 決定 4 の除外 **5**、`required` と `default` の同居 **0**）。よって直すのは
「網が張られている範囲」の記述だけで、決定 1〜5 は 1 つも動かない。

### 型 2: 検査器は 3 つを見るようになった

`scripts/check-commit-messages.js` は [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md)（#507 / #584 / #603）以降、
**件名の書式・ADR/IADR の実在・他リポジトリ参照表記（`check-cross-repo-refs.js` 経由の件名 /
本文 / PR タイトル）** の 3 つを見る。`scripts/README.md:102` は既にそう書いており、
**[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) とその索引行だけが古い。**

**[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) が言いたいこと（`Closes` の有無は誰も検査しない）は今も真である。**
検査器が増えたのは表記の検査であって、issue のクローズとは無関係だからである。したがって
**決定 3 も「検出しないこと」も変えない。**

### 型 3: フロントのパッケージマネージャは pnpm である

| 観測 | 値 |
| --- | --- |
| `src/package.json` | `packageManager: pnpm@10.33.0`・**`workspaces` フィールドは無い** |
| ワークスペース定義 | `src/pnpm-workspace.yaml`（`'*/frontend'` ＋ `'packages/*'`） |
| CI | `frontend-tests.yml:67` = `pnpm run test:coverage`／`frontend.yml` も全て pnpm |
| イメージ | `src/platform/frontend/Dockerfile:28` = `corepack enable && pnpm install --frozen-lockfile` |
| 依存の書式 | `@platform/ui` は **`workspace:*`**（npm では解決できない） |

**`npm install` は成功しない。** `README.md` の手順は実行すると落ちる live な誤りであり、
[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) の受け入れゲート表は**そのまま打てないコマンド**を載せている。

### 型 4: 計画 ID レンジは #599 で動いた

`.claude/rules/traceability.md` が正で **`FR-01..22` / `UC-01..11` / `SC-01..21` / `ADR-0001..0045`**。
[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) は `FR-01..21` / `ADR-0001..0039` を**転記**しており腐った。同ファイル自身が
「**ここへ転記しない —— 転記すると計画側が動いたとき一斉に腐る**」と書いている型である。

## 是正の形（原文を消さない）

| 置き場所 | 形 | 先例 |
| --- | --- | --- |
| ADR 本体（`## 決定` / `## 結果` の地の文） | **原文を残し、日付つきの追記ブロックを 1 か所置く**。他の箇所からはそこを指す | `IADR-0116` の［2026-08-04 追記・事実の更新］／`IADR-0127:164`／`IADR-0139` の［2026-08-07 追記・前提の是正 / #594］ |
| `docs/adr/README.md` の索引セル | **時制を直す**（`起案時に〜だった`）＋ 起票 ID。**追記ブロックは置かない** | `scripts/scripts.repo.test.js` の `inspectAdrIndexTitles` が `title-addendum` を違反として数える |
| 仕様書・README・how-to・設定ファイルのコメント | **記述を実装の事実へ差し替え、括弧内に起票 ID と従前の記述**を残す | `.claude/rules/traceability.md`「注記には起票 ID を添える（#580）」 |

**したがって「走査して 0 件」にはならない**——原文が残るためである。是正できたことは
「各 occurrence に事実の更新が読める形が付いたか」で判定する。

## やること

1. **型 1**: [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) に日付つき追記（事実の更新）を **1 か所**置き、`結果` 側からはそこを指す。
   索引行（`docs/adr/README.md`）と `docs/api/BFF_bff-surface.md` の同じ主張を実測に合わせる。
2. **型 2**: [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の実測 (c) を実装の事実へ直し、`結果`「検出しないこと」からは**列挙を
   繰り返さず**実測 (c) を指す。索引行も直す。
3. **型 3**: `npm` → `pnpm`。[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) のゲート表・`README.md`・`how-to`・`system-architecture`・
   `templates/unit-template`・`.dockerignore`。
4. **型 4**: [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) の転記を**単一情報源への参照**へ畳む（値を書かない）。
5. `updated:` を前進させ、注記には起票 ID（#591）を添える。

## 本作業で塞がない穴（開示）

- **機械検査を足さない。** 「条文が実装と食い違っていないか」を見る検査器は無く、本作業でも作らない
  （型 1・2・4 は自然文の主張であり、機械判定は偽陽性が避けられない。[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 3 と同じ理由）。
  **型 3 だけは機械化の余地がある**（「`npm ` が live な文書に現れたら落とす」）が、正しい言及
  （「npm パッケージ」「pnpm は npm と違い」）と例示が母集合の 2/3 を占めるため、**除外リストが
  必要になり腐る**。別 issue の判断材料として残す。
- **キット側（`impl-handoff-kit`）の npm 前提は直さない**（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) の環流経路が別にある）。
- **`CHANGELOG.md` は触らない**（生成物）。
- **[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 実測 (b)（マージ設定）は再確認していない** —— GitHub API を叩けない環境のため。
  ただし **既定ブランチが `develop` であること**は `git ls-remote --symref origin HEAD` で再実測した
  （同 ADR「検出しないこと」の依存条件はいまも成立する）。

## 判断が要った点（新 IADR の要否）

- **どれも決定を変えない**と判断した。型 1 は「網が広がった」＝決定 5 の但し書きが述べた条件
  （その型を画面が読んでいるか）が満たされただけ、型 2 は検査器が別の目的で増えただけ、
  型 3・4 は名称と転記の腐りである。**新 IADR は起票しない。**
- 型 3 の射程は迷った。issue の件名は 3 本の ADR を挙げるが、**同じ誤りが ADR 外の 5 ファイルに
  広がっている**。「ADR だけ直して README の `npm install` を残す」は #570 が踏んだ
  「赤が入れ替わるだけ」であり、live な記述はすべて直す側に倒した（除外は上表に明記した）。
- **型 4 は件名の 3 本の ADR に含まれるが、issue が 3 件として数えていたかは分からない**
  （本文を読めないため）。**引いた結果として出たので直した**。値を持たない形（単一情報源への参照）に
  畳んだので、次に計画側が動いても腐らない。
- 型 1 の是正で「網はどこまで広がったか」を **数で書かなかった**。数を条文へ埋めると、画面が
  1 つ増減しただけで黙って古くなる（#590 が踏んだ型）。実測値は本仕様書 §実測 だけに置く。

## 受け入れ基準

- [x] 型 1〜4 の **live な occurrence すべて**に事実の更新が読める形が付いた（§是正の形。
      記録・例示・フィクスチャ・正しい言及は除外理由つきで残す）
- [x] **決定（`## 決定` 配下の条文）を 1 つも変えていない**（差分は `起点・関連` / `結果` /
      実測節 / 追記ブロック / 索引セル / how-to / README / 設定コメントのみ）
- [x] 同じ事実を 2 か所以上に書いていない（[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) は決定 5 の追記 1 か所、
      [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) は実測 (c) 1 か所。他所からは参照だけを置いた）
- [x] 索引（`docs/adr/README.md`）へ ［YYYY-MM-DD 追記］ ブロックを持ち込んでいない
      （`inspectAdrIndexTitles` の `title-addendum` を新規に増やさない）
- [x] 型 4 の是正で**値を書かない**（レンジの単一情報源へ参照を張っただけ）
- [x] 下記の検証がすべて緑

## 検証（実走。走査基準 `e2cf25e`）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | **exit 0**・`OK: 461 件`（未 populate の submodule 配下 1047 件は対象外） |
| `node scripts/check-cross-repo-refs.js` | **exit 0**・`OK: 537 件` |
| `node scripts/check-plan-id-qualification.js` | **exit 0**・`OK: 1172 件` |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **exit 0**・`288 tests passed` |

いずれも是正**前**にも実走して緑であることを確認している（本作業は検査器を動かさないため、
**この 4 本は「壊していないこと」しか言わない**。条文と実装のずれを見る検査器は無い＝§本作業で塞がない穴）。
