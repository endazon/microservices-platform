---
title: フェーズ B 末クロス監査の指摘を是正する
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0116, IADR-0134, IADR-0139, IADR-0141, IADR-0145, IADR-0146, IADR-0147]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
related_specs:
  - 20260808_phaseA-audit-followup.md
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
  - ../adr/IADR-0145_landed-subject-check-scope.md
  - ../adr/IADR-0146_apifetch-reentry-guard.md
  - ../adr/IADR-0147_chunk-rule-presence-check.md
  - ../../docs/how-to/session-handoff.md
---

# フェーズ B 末クロス監査の指摘を是正する

- 起点: **フェーズ末クロス監査**（`adr-guardian` ＋ `traceability-auditor`・2026-08-08・develop `ae66549`）／起点 ID: **NFR**
- 監査の巡数分岐は [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 2・3・4 に従う。

## 1. 母集合（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

### 1.1 監査対象の母集合 —— **引継資料の range は誤っていた**

**★ 引き継いだ資料は `96c2dbe..74d9d7f`（6 コミット）を渡せと書いていたが、これは母集合ではない。**

| | 値 |
| --- | --- |
| 前回（フェーズ A 末）監査の実施時点 | develop **`2cd8508`**（`20260808_phaseA-audit-followup.md:23` に記録） |
| 本監査の時点 | develop **`ae66549`** |
| **正しい母集合** | **`2cd8508..ae66549` = 9 コミット** |

引継資料の range が落としていたもの: **`4e06353`（#614 / #555・IADR-0146 新設）**・`e4c1157`（#613）・`96c2dbe`（#615）。
入っていなかったもの: **`ae66549`（#624）**。
とくに #614 は**機械検査の新設**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 で「全面 1 巡 ＋ 是正差分 1 巡」に当たる分類）であり、落としてはならない対象だった。

**これは [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1 規則 1 が名指しした型の 3 度目の再演である**——フェーズ A では「6 PR を渡したが実体は 8 コミット」で 2 件落ち、引継資料はその教訓を §4 に書きながら、**自分が渡す range で同じ誤りを犯していた。**

### 1.2 決定 4 の分類（**件名ではなく差分から当てた**。`git show --stat` を全 9 件に実行）

| コミット | PR | 分類 | 巡数 |
| --- | --- | --- | --- |
| `4e06353` | #614（#555） | 機械検査を新設（ESLint）＋ ADR 新設 | 全面 1 ＋ 是正差分 1 |
| `e4c1157` | #613 | **規約を改定する**（当初「記録の追随のみ」と当てたが、監査が是正。§3 の 🟡-G） | 全面 1 ＋ 是正差分 1 |
| `96c2dbe` | #615 | 機械検査を改修 ＋ 規約改定 | 全面 1 ＋ 是正差分 1 |
| `0121f78` | #616（#554） | 機械検査を改修 | 全面 1 ＋ 是正差分 1 |
| `44a3141` | #618（#556） | 機械検査を新設 ＋ ADR 新設 | 全面 1 ＋ 是正差分 1 |
| `ce96eb8` | #619（#562） | 機械検査を新設 ＋ 規約改定 | 全面 1 ＋ 是正差分 1 |
| `dec2736` | #620 | 記録の追随のみ | 全面 1 で打ち切り |
| `74d9d7f` | #621 | 記録の追随のみ | 全面 1 で打ち切り |
| `ae66549` | #624 | **規約を改定する**（CLAUDE.md / AGENTS.md へ拘束点を書き込む）＋ pin | 全面 1 ＋ 是正差分 1 |

### 1.3 是正ごとの母集合（**引いた結果と、除外したものとその理由**。規則 6）

| 是正 | 引き方（軸） | 引いた結果 | 除外したものと理由 |
| --- | --- | --- | --- |
| 着地件名の ID | `git log 2cd8508..ae66549` の全 9 件 × 変更パス実測 | **脱落 0・実体なき付加 0** | — |
| `scripts/README.md` の未掲載 | `ls scripts/*.js`（`.test.js` を除く）**28 本**を全数で README と突合 | **未掲載 1 本**（`check-chunk-budget.js`） | `*.test.js` は README のスクリプト表の対象外（実行される検査器ではない） |
| 実測値 `319` の埋め込み | `grep -rn "319"` を live な条文・スクリプト全体へ | **4 ファイル 6 箇所** | `docs/specs/` は書いた時点の記録なので対象外 |
| ADR→仕様書の逆リンク | **リポジトリ全体**の `docs/specs/*.md` → `related_specs` の IADR 参照 **355 対**を全数で突合 | **欠落 251 対（71%）** | §4 のとおり**是正しない**（母集合の実測が監査の前提を覆した） |
| `bffFetch` の再混入余地 | `grep -rn bffFetch` を platform / knowledge の全 `.ts` `.tsx` へ | features からの import **0 件**（生成物のみ 4 ファイル） | 生成物（`foundation/api/generated/`）は features ではないので禁止対象外 |
| planning の ID レンジ | pin `90f5251` の実クローンで `FR` / `UC` / `SC` / `ADR` を全数列挙 | §2 のとおり**不変** | `projects/mondriq`（第 3 のプロジェクト）は本リポが参照 0 件のため対象外（§4 に記録） |

## 2. planning の ID レンジ実測（監査 🔴-1 の解決）

**監査は「pin を進めたのにレンジを引き直していない」を 🔴 として挙げ、本環境では検証不能とした。**
`planning` submodule は認証の都合で populate できないが、**計画リポを別途クローンして実測した**（`90f5251` = 現 pin と一致）。

| 種別 | 条文（`891b199` 時点）の主張 | pin `90f5251` の実測 | 判定 |
| --- | --- | --- | --- |
| `FR` | `FR-01..22` | `FR-01`〜`FR-22`（欠番なし） | **不変** |
| `UC` | `UC-01..11` | `UC-01`〜`UC-11`（欠番なし） | **不変** |
| `SC` | `SC-01..21` | `SC-01`〜`SC-21`（欠番なし） | **不変** |
| `ADR` | `ADR-0001..0045` | 45 件・`ADR-0001`〜`ADR-0045`（欠番なし） | **不変** |
| `Proposed` な ADR | 6 件（0023 / 0038 / 0039 / 0040 / 0041 / 0042） | **同一の 6 件**（＋`ADR-0003` の `Superseded` 1 件） | **不変** |

**結論: レンジは動いていない。** 手続（pin 前進時のレンジ引き直し）は #624 で飛ばされたが、**結果として齟齬は生じていない。**
是正は条文の走査基準を `891b199` → `90f5251` へ前進させ、**「実測して不変」を走査基準つきで記録する**ことに留める。

> **規約の新設は行わない。** `CLAUDE.md` は「検査器・規約の追加は**同型の事故が 2 回起きたら**を条件とする（1 回目は記録に留める）」と定める。
> `planning` を実際に動かしたコミットを `git log -- planning` で全数確認したところ、**直近の意図的な pin 前進 5 件（`dec2736` / `7b6232b` / `2cc567e` / `a9c0e6b` / `ab26b7d`）はすべてレンジ検査を実施しており、飛ばしたのは `ae66549` が初回**である。
> **監査は「#620 が実践し #624 が落とした＝同型 2 回目」と述べたが、これは誤りである**——#620 は成功例であって事故ではない。**1 回目なので記録に留める。**

## 3. 是正する指摘

| # | 監査 | 指摘 | 対応 |
| --- | --- | --- | --- |
| A | ADR 🔴-1 | `docs/adr/README.md` の索引行が「機械では判定できない」のままで、[IADR-0145](../adr/IADR-0145_landed-subject-check-scope.md) 本体の是正（「PR タイトルとの突合はできない／パス由来は可能だが偽陽性で方向が合わない」）に追随していない | 索引行を実体へ合わせる |
| B | 両監査 🔴-2 | `CLAUDE.md` / `AGENTS.md` の「同型・低リスクの変更は 1 PR に束ねる」が [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) / [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の限定例外を無条件へ広げ、引継資料 §1 と真逆 | ADR の条件へ縛り直す（§5 の裁定事項） |
| C | ADR 🟡-3 | 実測値 `319`（着地件名）が走査基準なしで 4 ファイル 6 箇所に埋まっており、**同じフェーズ内で既に 328 へ動いた** | 各箇所へ走査基準（`2cd8508` 時点）を添える |
| D | 両監査 | `scripts/README.md` に `check-chunk-budget.js` が無い（28 本中唯一） | スクリプト表と結線表へ追加 |
| E | ADR 🟡-5 | [IADR-0146](../adr/IADR-0146_apifetch-reentry-guard.md) の禁止が `apiFetch` に閉じており、同等の口である `orvalMutator.bffFetch` を数えていない | ESLint の禁止対象へ追加し、変異試験で確認 |
| F | ADR 🟡-10 | ESLint の案内メッセージが「`apiFetch` を使う」と案内するが、同ブロックが features では `apiFetch` を error にする（**案内に従うと別の error**） | メッセージを features / foundation で割る |
| G | ADR 🟡-6 | `check-chunk-budget.js` の self-test が region 内の**裸の文字列リテラル**を拾うため、述語側のリテラル（`startsWith('lodash')`）をチャンク名と誤認する潜在的偽陽性。[IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md) 決定 4 の「偽陽性は塞ぐ」に反する | 抽出を `return` 句に限定し、変異試験で確認 |
| H | ADR 🟡-9 | `session-handoff.md:307` が「フェーズ A・B の監査は実施済み」と書くが、**B は未実施だった**。しかも「陳腐化しにくい」とされた §7 にある | 現在地を実体へ更新 |
| I | traceability 🔴-1 | pin 前進に対しレンジ条文が 2 つぶん古い | §2 の実測で走査基準を前進 |
| J | traceability 🟡-5 | [IADR-0146](../adr/IADR-0146_apifetch-reentry-guard.md) が本文で `ADR-0031` を引きながら `related_ids` から落としている（機械の ID 突合に見えない） | `related_ids` へ追加 |

## 4. **是正しない**と判断した指摘（根拠つき）

| # | 監査 | なぜ是正しないか |
| --- | --- | --- |
| traceability 🟡-4 | 「`IADR-0134` **だけが**逆リンクを欠く」 | **母集合を引き直すと前提が崩れた。** リポジトリ全体で仕様書→ADR の参照 **355 対のうち 251 対（71%）に逆リンクが無い**（実測）。逆リンクが在るのは**ADR と仕様書を同じ PR で新設した場合**にほぼ限られ（`IADR-0146`/`IADR-0147` がそれ）、既存 ADR を引く仕様書では張られていない。**`IADR-0134` だけを直すと、251 件のうち 2 件だけが揃った不統一な状態になる。** 規約の解釈（`CLAUDE.md`「相互リンク」の義務が ADR 側にも及ぶか）を決めてからの一括対応が要るため、**別 issue として起票する**。 |
| ADR 🟡-8 | 2 本の仕様書に §母集合 が無い | 対象は `docs/specs/20260808_issue-622_*` と `20260808_planning-pin-*` で、**どちらも着地済み（過去 PR）の作業仕様書**である。`.claude/rules/traceability.md` は「確定済みの `docs/specs/` は**書いた時点の記録**であり、後から注記を足すのは記録の改竄にあたるので書き換えない」と定める。**指摘は正しいが、是正手段が規約で禁じられている。** 記録に留める。 |
| traceability 🟡-7 | #624 の仕様書が pin を `356e8c7` と書くが着地は `90f5251` | 同上（着地済みの作業仕様書）。**事実誤りだが書き換えない。** 本仕様書 §2 に正しい値を記録することで、次に引く人が正を辿れるようにする。 |
| traceability 🟡-8 | `ae66549` のコミット本文が空で #622 へ辿れない | **履歴は不変**（force push 禁止）。事後修正の手段が無い。<br>**なお監査の「他 8 件はすべて `Refs`/`Closes` を持つ」は誤りである**——実測すると `e4c1157` と `96c2dbe` にも footer が無く、**footer を欠くのは 3 件**である（本文自体が空なのは `ae66549` のみ）。 |
| traceability 🟡-6 | pin 前進コミットのスコープが `dec2736` と `ae66549` で不統一 | 基準が条文に無いため**違反と断定できない**（監査自身もそう述べている）。§2 と同じく**同型 1 回目**なので、`CLAUDE.md` の「2 回起きたら」条件により記録に留める。 |
| ADR 🟡-7 | `session-handoff.md:31`「承認待ちは不要」がリポジトリ内で唯一の記載で対応する IADR が無い | **利用者裁定の記録であり、実装側が ADR 化を独断で決められない。** §5 に裁定事項として挙げ、issue 化する。 |

### 4.1 本仕様書で新たに見つけたこと（監査は挙げていない）

- **計画リポに第 3 のプロジェクト `projects/mondriq` が存在する**（独自に `ADR-0001..0006` を採番）。
  `.claude/rules/traceability.md` の名前空間節は `AST`（ai-stock-trading）しか定義していない。
  **本リポからの参照は実測 0 件**なので現時点では実害が無く、本 PR では是正しない。**将来 mondriq を参照するなら修飾子の定義が要る**（§5）。

## 5. 裁定・方針決定が要る事項（実装側では決められない）

1. **指摘 B の「束ねる」条項**（最重要）。本 PR は「**ADR の条件へ縛り直す**」を採る——`CLAUDE.md` 自身が「ADR で確定した制約の無断逸脱」を禁止しており、条件を落とした一般化はその逸脱に当たるため、**新 IADR を起票せずに広げることはできない**からである。
   **実際に制約を広げたいのであれば、[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) / [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の改定 IADR が必要であり、その判断は利用者にある。**
2. `session-handoff.md:31`「承認待ちは不要」の ADR 化（ADR 🟡-7）。
3. ADR→仕様書の逆リンク 251 件の扱い（§4）。
4. 第 3 プロジェクト `mondriq` の名前空間修飾子（§4.1）。

## 6. 受け入れ基準

- [ ] §3 の A〜J がすべて着地している。
- [ ] 既存の検査器がすべて緑（`check-doc-links` / `check-adr-numbering` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-landed-subjects` / `REQUIRE_REPO_TESTS=1 scripts.test.js`）。
- [ ] 指摘 E・G は**変異試験で「壊すと落ちる」を実測**している（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）。
- [ ] フロントの `lint` / `typecheck` / `format:check` / `build` / 単体テスト / E2E が通る。
- [ ] §4 の「是正しない」判断に根拠が書かれている。
- [ ] 是正が**実際に着地したことを走査で数え直している**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)「置換したは置換されたを意味しない」）。

## 7. 検証記録

**件数は最終コミットの内容で取り直した**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) のフォローアップおよび #620 のレビュー指摘。本仕様書ファイル自身を含む）。

### 7.1 検査器・テスト（すべて実走）

```
check-doc-links              OK: 481 件（未 populate の planning 配下 1058 件は対象外）
check-adr-numbering          OK（重複・欠番なし、索引と双方向一致・昇順）
check-cross-repo-refs        OK: 558 件
check-plan-id-qualification  OK: 1184 件
check-landed-subjects        OK: 着地件名 328 件
check-chunk-budget --self-test   ✓ 13 件すべて通過
check-chunk-budget --require     OK: 必須チャンク 3 本すべて実在 / 初期ロード 577.68 kB（床 577.68 kB）
REQUIRE_REPO_TESTS=1 scripts.test.js   ✓ 293 tests passed
pnpm run format:check        All matched files use Prettier code style!
pnpm run lint                0 errors（既存 warning 9 件）
pnpm run typecheck           Done（両ユニット）
pnpm run build               ✓ built
pnpm run test:coverage       Statements 96.36% / Branches 90.4% / Functions 91.7%
pnpm run test:e2e            13 passed
```

> **★ `check-cross-repo-refs` の件数を一度 `557` と誤記した（AI レビューの 🟢 指摘で発覚・実測して是正）。**
> 原因は **[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) のフォローアップと #620 のレビューが名指しした「作業仕様書ファイル自身を足す前に測った値を持ち越す」型そのもの**である。
> **2 本の検査器は走査対象の取り方が違う**ため、同じ時点で測っても本仕様書ファイルの扱いが割れる。
>
> | 検査器 | 走査対象の取り方 | 未追跡の本仕様書 |
> | --- | --- | --- |
> | `check-doc-links.js` | `readdirSync`（**ファイルシステム走査**） | **数える** → 481 |
> | `check-cross-repo-refs.js` | `git ls-files`（**追跡下のみ**） | **数えない** → 557 |
>
> `git add` 後は両方が数えるので **558 が正**である。
> **なお AI レビューが挙げた原因（レビュー環境が `CLAUDE.md` / `.claude/` を develop 版へ復元するため）は誤りである** ——
> 当該 2 ファイルを develop 版へ差し替えて実走しても **558 のまま**で、件数は変わらなかった（実測）。
> **他人の切り分けも、自分で当ててから受け入れる。**

> **`--require` は引数を取らない真偽フラグである。** 本仕様書の執筆中に `--require <dist>` と
> 誤って書き、`✗ 未知の引数` で気づいた（`scripts/README.md` へ書いた説明も同時に是正した）。
> **あわせて、最初の実行を `src/` から行って `node scripts/...` がパス不一致で落ちた** ——
> #554 が直した「空振り」と同じ型を検証側で踏みかけたので、ルートから引き直している（§5 型 3 の再演）。

### 7.2 変異試験（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4・機械検査の新設/改修に必須）

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| features から `bffFetch` を import | error | **`'bffFetch' import from '@foundation/api/orvalMutator' is restricted`（1 error）** |
| `foundation/api/generated/` の既存利用 | 通る | **0 errors**（同ディレクトリは ESLint の ignore 対象。生成物は features ではない） |
| `manualChunks` へ述語に文字列を使う規則を 1 本足す（`startsWith('lodash')`） | 述語のリテラルを拾わない | **旧抽出: `lodash,ui,vendor-lodash,…`（誤認）→ 新抽出: `ui,vendor-lodash,…`（誤認せず）** |
| 現行の 3 規則 | 抽出結果が変わらない | **旧・新とも `ui,vendor-query,vendor-react`**（退行なし） |

### 7.3 是正の着地確認（走査で数え直した）

| 是正 | 走査 | 結果 |
| --- | --- | --- |
| A | `grep -c 'PR タイトルとの突合はできない' docs/adr/README.md` | 1 |
| B | `CLAUDE.md` / `AGENTS.md` の「6 条件」 | 各 1 |
| B' | `session-handoff.md` の限定例外補記 | 1 |
| C | `319` を含む 6 箇所のうち走査基準つき | **6 / 6** |
| D | `scripts/README.md` の `check-chunk-budget` | 2（スクリプト表＋結線表） |
| E | `src/eslint.config.js` の `NO_BFFFETCH_IN_FEATURES` | 2（定義＋`paths` への展開） |
| I | `.claude/rules/traceability.md` の `走査基準: planning \`90f5251\`` | 1 |
| J | `IADR-0146` の `related_ids` に `ADR-0031` | 1 |
