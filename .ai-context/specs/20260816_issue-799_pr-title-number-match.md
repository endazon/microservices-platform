---
title: 作業仕様書 — PR タイトル末尾の `(#NNN)` が PR 自身の番号と一致することを検査する（#799）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0116
  - IADR-0139
  - IADR-0141
  - IADR-0145
  - IADR-0183
  - IADR-0192
  - IADR-0207
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../../docs/how-to/commit-message-rules-annex.md"
  - "../adr/IADR-0207_pr-title-trailing-number-must-be-own.md"
  - "../adr/IADR-0192_kit-sync-classification-and-check.md"
  - "../adr/IADR-0145_landed-subject-check-scope.md"
---

# 作業仕様書: PR タイトル末尾の `(#NNN)` を PR 自身の番号へ縛る（#799）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: **`NFR`（無採番）**。検査基盤・規約統制のメタ作業であり、計画側の非機能要件表
  （`NFR-01`〜`NFR-27`）に当たる番号が無い。`.claude/rules/traceability.md`「起点 ID の種別」の
  **2 の場合**（ID 列はあるが、その作業に当たる番号が無い）に該当するため、**環流しない**。
- 関連 ADR: [IADR-0145](../adr/IADR-0145_landed-subject-check-scope.md)（着地件名の検査）／
  [IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md) 決定 2（分類 X には追跡 issue が必須）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1・
  [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 決定 1（1 issue = 1 PR と束ねの条件。
  **`Closes` の穴を本 issue へ束ねない判断の根拠**）
- 規約の入口: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  「コミットメッセージの機械チェック」＋別紙
  [`commit-message-rules-annex.md`](../../docs/how-to/commit-message-rules-annex.md)

## 直す問題

`check-commit-messages.js` の単一件名モード（`--title` / `PR_TITLE`）は、末尾の `(#NNN)` を
`\s*\(#\d+\)\s*$` で**剥がしてから**書式を見るだけで、**その番号が PR 自身のものかを一切見ていない**。

規約側（`traceability.md`）は「半角スペース + `(#123)` は**スカッシュマージ既定件名として**許容」と
書いており、**GitHub が「その PR 自身の番号」を自動付加する挙動**を前提にしている。前提は条文に
明示されていない。

**実害はマージ操作者に依存する。**

| マージのしかた | 起点 issue の番号をタイトルへ書いた場合の結果 |
| --- | --- |
| スカッシュ件名を**明示的に渡す** | 渡す側が差し替えれば正しくなる（手作業に依存） |
| **GitHub の UI からマージ** | 既定件名は「PR タイトル ＋ 自動付加」なので **`… (#796) (#798)` と二重付加**になる |

`develop` は force push 禁止のため、載った件名は事後に直せない（直せるのは
`changelog-overrides.json` による生成物の補正だけ）。

## 母集合（自分で引いた。誤りの側から引く）

**issue 本文の表（3 本）は母集合ではない。** 着手時に自分で引き直した。

### 軸 1: PR タイトル（GitHub API・全 PR）

`GET /repos/endazon/microservices-platform/pulls?state=all` を 5 ページ全取得（**443 件**。
2026-08-16 取得）。**誤りの側の形** = 「タイトル末尾に `(#NNN)` があり、その `NNN` が PR 番号と違う」で引いた。

| 量 | 値 |
| --- | --- |
| 全 PR | **443** |
| タイトル末尾に `(#NNN)` を持つ PR | **66** |
| そのうち **PR 自身の番号と一致するもの** | **0** |
| そのうち **一致しないもの（＝違反）** | **66** |

**66 / 66 が誤りだった。正しく自番号を書いた例は 1 件も無い。** これは当然で、GitHub は
マージ時に自動付加するため、**タイトル側に自番号を書く動機がそもそも無い**。
issue 本文の「3 本続けて」は**直近の 3 本**であり、実際には運用開始以来ずっと同型である
（最古は PR #67、最新は PR #795。#798 はレビュー指摘を受けてタイトルから番号を外したため、
現在のタイトルには末尾番号が無く、この軸には現れない）。

### 軸 2: `develop` に着地した件名（実データ・git）

**規則 5「軸を 1 本で終わらせない」**に従い、タイトルではなく**着地した恒久履歴**からも引いた。

| 量 | 値 |
| --- | --- |
| `develop` の件名で **`(#A) (#B)` と番号が二重に付いたもの** | **58 件** |
| 同じく `(#N)` を 2 個以上含むもの（末尾連続に限らない） | **59 件** |

59 件目は `1dda3f97 fix(#34): EFCore.Relational を 10.0.9 に直接ピンし MSB3277 を解消 (#36)` で、
**スコープが `(#34)` という別型**（規約導入前）である。二重付加ではないので除外した。

**58 件は既に恒久履歴へ載っており、本作業では直せない**（force push 禁止）。本検査は
**再発防止のみ**を目的とする。既存履歴を落とさないことは §変異試験で実測する。

### 軸 3: 着地件名の末尾番号そのもの

merged かつ軸 1 に該当する **65 件**について、`merge_commit_sha` の件名の**末尾**番号を PR 番号と
突き合わせた → **65 件すべて末尾は自番号で正しい**（GitHub の自動付加が最後に付くため）。
すなわち**壊れているのは「末尾」ではなく「末尾の 1 つ手前」**である。
これは検査の実装方針に効く —— **着地件名側で「末尾が自番号か」を見ても検出できない**。
検査すべき面は **PR タイトル**である（着地する前の、唯一直せる面）。

### 引いたが除外したものと、その理由

| 引いたもの | 件数 | 除外理由 |
| --- | --- | --- |
| 件名中の**丸括弧に入っていない** `#NNN`（`refs #123` 等） | 33 | 別の規約（クロスリポ参照の修飾）の射程であり、`check-cross-repo-refs.js` が既に見ている |
| `origin/main` の件名 | 1（二重 0） | `main` は `develop` の集約であり、独立の母集合にならない |
| 上記 59 件目（`fix(#34):` のスコープ型） | 1 | 二重付加ではない。規約導入前の書式違反であり `commit-allowlist.json` の射程 |
| **PR 本文の `Closes #NNN`** | 後述 | **本 issue の射程外と判断した**（§`Closes` の穴） |

## 設計（issue 本文の案を再判定した結果）

issue 本文の案（`PR_NUMBER` を渡し、未設定なら形状のみ）を**そのまま採る**。再判定の結果、
変更点は無い。加えて実装上の細部を 3 点決める。

1. **番号一致は「PR 番号が読めたときだけ」見る。** `PR_NUMBER` が未設定・空文字なら
   **従来どおり形状のみ**。コミット件名モード（`main()` のレンジ検査）へは**一切渡さない** ——
   スカッシュ後の履歴コミット（`… (#794)`）が 425 件あり、番号一致を課すと全滅する。
2. **末尾の `(#NNN)` 自体は引き続き任意。** 番号が無いタイトルは pass（**現在の推奨形**でもある。
   軸 1 の実測どおり、自番号を書く動機は無い）。
3. **`PR_NUMBER` が設定されているのに数値として読めない場合は、検査をスキップしたことを
   `notice` で可視化する**（終了コードは変えない）。配線ミスで**黙って検査が消える**のを防ぐ
   （`check-cross-repo-refs.js` の 0 件走査の門・IADR-0130 と同じ考え方）。
   `notice` は**呼び出し側（`main()`）でのみ出す** —— `checkSingleTitle()` の中に置くと、
   単体テストが本物の CI アノテーションを漏らす（`check-commit-messages.js` 内の既存の注記と同型）。

失敗メッセージは**直し方を書く**:

> 末尾の `(#796)` が PR 自身の番号（#798）と一致しない。**末尾の `(#NNN)` を外すか、PR 自身の
> 番号にすること。起点 issue は本文の `Closes #NNN` で示す**（GitHub はスカッシュ時に PR 番号を
> 自動付加するため、通常はタイトルへ番号を書かない）

### 変更ファイル

| ファイル | 変更 |
| --- | --- |
| `scripts/check-commit-messages.js` | `validateTitlePrNumber()` を新設し `checkSingleTitle(title, author, prNumber)` へ結線。`--pr-number` / `PR_NUMBER` を読む |
| `.github/workflows/pr-title.yml` | `PR_NUMBER: ${{ github.event.pull_request.number }}` を渡す |
| `scripts/scripts.repo.test.js` | 4 方向の変異試験＋ワークフロー配線の回帰テスト |
| `scripts/kit-sync-classification.json` | `check-commit-messages.js` を **B 種 5 → B〔X〕** へ落とす（環流債務の可視化） |
| `docs/how-to/commit-message-rules-annex.md` | 検査の説明（規約の入口は `traceability.md` のまま） |
| `docs/adr/IADR-0207_*.md` ＋ `docs/adr/README.md` | 実装 ADR と索引 |

### 起動条件・必須チェックが変わらないこと

`pr-title.yml` の変更は `env:` の 1 行追加のみ。`name` / `on:` / `jobs.pr-title` のジョブ ID は
**不変**であり、必須チェックの context（`pr-title`）も変わらない。

### 規約側（`traceability.md`）の扱い

**キット配布物なので直接編集しない。** 本リポの必読規約（`CLAUDE.md` ＋ `.claude/rules/*.md`）は
**残余 1,118B**（50,082 / 51,200）であり、**正味で増やさない**。よって:

- **規範の一文はキットへ環流する**。現行条文「半角スペース + `(#123)` はスカッシュマージ既定件名
  として許容」に **「その番号は PR 自身のものに限る」**を足すよう依頼する。
  **記録は本作業では `feedback/` へ置かない**（理由と草案は §付録）。
- **本リポ側の記述は別紙**（`docs/how-to/commit-message-rules-annex.md`）に置く。必読規約は 0 バイト増。
- `traceability.repo.md` にも**足さない**（同じ理由）。

### キット同期の分類（IADR-0192 決定 2）

`scripts/check-commit-messages.js` は現在 **B 種 5**（置換点 `PLAN_PROJECT` を埋めるだけ）である。
本変更で**キットに無いロジック**が入るため、**B〔X〕へ落とす**。X は環流債務の測定値であり、
**追跡先の issue 番号が必須**なので `#799` を置く（環流記録の草案は §付録）。

**別スクリプトへ切り出す案は採らない。** PR タイトル規約の実装が 2 本に割れ、
`pr-title.yml` の「規約の単一情報源を二重実装しない」に反するうえ、
**キット側に穴があるという事実が分類表から見えなくなる**（X が増えないため）。

## ★ `Closes` の穴（波 6 末クロス監査の指摘）を射程に入れるか

**入れない。** 理由を 3 つ、実測とともに残す。

### 1. 入力が違う（同じ資源ではない）

`Closes #NNN` は **PR 本文**にあり、`check-commit-messages.js` の単一件名モードには**渡っていない**。
検査するには `PR_BODY`（またはスカッシュ本文）という**新しい入力**が要る。
`IADR-0139` 決定 1 の束ねの条件は「**同じ API 資源または同じ DTO 群に閉じること**」であり、
判定の単位は**ドメインではなく資源**である。**同じファイルに触ること**は束ねの根拠にならない。

### 2. 母集合が桁違いで、規約の新設に当たる（裁定が要る）

監査は「2 本あった（#777 / #789）」と述べているが、**自分で引き直したら桁が違った**。

| 引き方 | 母集合 | `Closes` / `Fixes` / `Resolves` ＋ `#NNN` が無いもの |
| --- | --- | --- |
| **`develop` に着地したスカッシュ件名の本文**（`%b`） | 425 | **388 件（91%）** |
| **merged PR の本文**（GitHub API） | 431 | **267 件（62%）** |

**「2 本」は波 6 の範囲だけを見た数であり、全体では常態である。** すなわちこれは
「守られている規約が 2 回破れた」のではなく、**「そもそも運用されていない規約を、今から
全 PR へ課す」**という話になる。**検査器の追加ではなく規約の新設**であり、
`CLAUDE.md`「裁定依頼は小さく高頻度に計画リポへ流す」の対象である。
（また `IADR-0139` 決定 3 が定めるのは**束ねた PR の issue 別内訳**であって、
「全 PR に `Closes` を必須とする」ではない。射程が違う。）

### 3. 事前防止できる面が違う

PR 本文は**マージ後にも編集できる**が、着地したスカッシュ本文は**不変**である。
本文の `Closes` を機械で担保するなら、
「PR 本文を見る（事前・編集で崩せる）」か「着地本文を見る（事後・直せない）」かの
**選択そのものに判断が要る**。`pr-title.yml`（事前）と `check-landed-subjects.js`（事後）の
使い分けと同じ論点で、片手間に決めてよい粒度ではない。

### 起票案（起票は人間が判断する）

> **タイトル**: `PR 本文の Closes #NNN を機械で担保するかを決める（IADR-0139 決定 3 の 3 段目が常用で欠けている）`
>
> **本文の骨子**:
> - `IADR-0139` 決定 3 は「PR タイトル・スカッシュ本文の issue 別内訳・`refs/pull/<PR>/head` の
>   3 段で担保する」と定め、**同決定は「CI も見ない」と自認している**。
> - **実測（2026-08-16 / develop `d121ee8c`）**: 着地スカッシュ件名 425 件のうち本文に
>   `Closes` / `Fixes` / `Resolves` ＋ `#NNN` を持つのは **37 件（9%）**。merged PR 431 件の
>   本文で見ても **164 件（38%）** しかない。**3 段目が常用で欠けている**という監査の指摘は
>   正しいが、規模は「2 本」ではなく**常態**である。
> - **決めるべきこと**: (a) 全 PR に必須とするか、束ねた PR に限るか（`IADR-0139` の射程どおり）。
>   (b) 見る面は PR 本文（事前・可変）か着地本文（事後・不変）か。
>   (c) `Closes` を持たない正当な PR（CHANGELOG 自動更新・dependabot・issue を持たない
>   小修正）の除外規則。
> - **ラベル**: `decision-needed` ／ 起点: `NFR` ／ 関連: #799・`IADR-0139` 決定 3

## 受け入れ基準 → 変異試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 誤った番号（`… (#796)` を PR 798 として）→ **fail** | `checkSingleTitle(t, null, 798)` が 1 |
| 2 | 正しい番号（`… (#798)` を PR 798 として）→ **pass** | 同 0 |
| 3 | 番号なし → **pass** | 同 0 |
| 4 | `PR_NUMBER` 未設定（コミット件名モード）→ **pass**（形状のみ） | 同 `undefined` / `null` で 0 |
| 5 | 既存履歴で fail しない | `origin/develop` の実件名 425 件を通して違反 0 |
| 6 | 起動条件・必須チェックが変わらない | `pr-title.yml` のジョブ ID・`on:` が不変であることをテストで固定 |

## 検証（IADR-0183 の順序）

`git add -A` → 検査器 → コミット → `check-doc-updated.js` / `check-commit-messages.js`（HEAD を読む）。

- `node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
  （**`scripts.repo.test.js` を単体で叩かない** —— companion 形式のため 1 件も走らず沈黙の exit 0 になる）
- `check-kit-sync` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
  `check-doc-type-vocabulary` / `check-adr-numbering` / `check-reading-budget`
- `planning` submodule は**先に populate する**（未 populate だと `check-kit-sync` が throw して
  `scripts.test.js` の後続テストが 1 件も走らず、違反が隠れる）

## やらないこと

- 既存の 58 件の二重付加件名の是正（force push 禁止。生成物の補正が必要になったら
  `changelog-overrides.json` の射程）
- コミット件名モードでの番号一致検査（§設計 1）
- `.claude/rules/` への加筆（必読予算。§規約側の扱い）
- `Closes` の検査（§`Closes` の穴）

---

## 付録: キットへの環流（記録の草案。**まだ `feedback/` へ置いていない**）

**なぜ `feedback/20260816_*.md` を本 PR に含めないか** —— `scripts/scripts.repo.test.js` の
`#712: 警告は 0 件で、終了コードは 0 のまま` が **未送付の環流記録を 0 件で固定するラチェット**
（`check-feedback-dispatched.js`）である。記録を置くと `dispatched: false` で 1 件になり、**CI が落ちる**。
**ラチェットの設計どおり「記録の作成」と「伝達」は同時に済ませる必要がある**が、
**伝達（計画リポジトリへの起票、または `planning/draft/feedback/` へのコピー）は本作業の射程外**である
（本作業は push せず、`planning/` も編集しない）。

**`dispatched: true` と書いて回避することはしない**（IADR-0184 の「記録へ嘘を書かない」）。

**したがって、下の草案を `feedback/20260816_kit-pr-title-number-mismatch.md` として置くのは、
伝達を行うのと同じ変更でなければならない。** 分類表（`kit-sync-classification.json`）と
[IADR-0207](../adr/IADR-0207_pr-title-trailing-number-must-be-own.md) は、環流先として
**#799 を追跡 issue に置いている**（IADR-0192 決定 2 の「追跡先の issue 番号が必須」は満たす）。

### 草案本文

#### フィードバック: キットの PR タイトル検査が末尾番号の一致を見ていない

> **草案である。伝達は未了であり、`feedback/` にも置いていない**（上記の理由）。
> **「環流済み」ではない。**

##### 種別

キット配布物（`tools/impl-handoff-kit/`）の規約文と検査器の穴。**配布先すべてに同じ穴がある。**

##### 起点となる計画書

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: 本リポの [IADR-0207](../adr/IADR-0207_pr-title-trailing-number-must-be-own.md)
- 計画書リンク: `planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md`
  「コミットメッセージの機械チェック（CI・再発防止）」／同 `scripts/check-commit-messages.js`／
  同 `.github/workflows/pr-title.yml`

##### 現状（キットの記述 / As-Is）

`traceability.md`:

> - **末尾の PR 番号**: 半角スペース + `(#123)` はスカッシュマージ既定件名として許容。

`check-commit-messages.js` の `validateSubject()`:

```js
// 末尾の PR 番号 " (#123)" は除去して判定する。
const s = subject.replace(/\s*\(#\d+\)\s*$/, '').trim();
```

`pr-title.yml` は `PR_TITLE` と `PR_AUTHOR` を渡すが、**PR 番号は渡していない**。

##### 問題点 / あるべき姿（To-Be）

条文は「**スカッシュマージ既定件名として**許容」と書いており、**GitHub がその PR 自身の番号を
自動付加する**挙動を前提にしている。**しかしその前提（番号は PR 自身のもの）が明文化されておらず、
検査も形状しか見ていない。**

結果、**起点 issue の番号をタイトルへ書いた PR が素通りする**。

- スカッシュ件名を明示的に渡してマージするなら、渡す側が差し替えれば正しくなる（**手作業に依存**）
- **GitHub の UI からマージすると `… (#796) (#798)` と二重付加**になる

統合ブランチの件名は force push 禁止で事後修正できないため、**「誰がマージするか」で
規約が守られるかが変わる**状態になっている。

**あるべき姿**:

1. 条文へ **「その番号は PR 自身のものに限る」** を明記する。
2. `pr-title.yml` が `PR_NUMBER: ${{ github.event.pull_request.number }}` を渡す。
3. `check-commit-messages.js` が末尾番号と PR 番号を突き合わせ、違えば fail する。
   **`PR_NUMBER` が未設定なら従来どおり形状のみ**（コミット件名モードには PR 番号が無く、
   未設定を fail にすると `commit-messages` ジョブが全滅する）。
   **コミット件名モードでは絶対に番号一致を要求しない**（スカッシュ後の履歴コミットが全滅する）。
4. 失敗メッセージに直し方を書く（「末尾の `(#NNN)` を外すか、PR 自身の番号にすること。
   起点 issue は本文の `Closes #NNN` で示す」）。

##### 実装で判明した経緯

本リポ #799（発見は PR #798 の AI レビュー 🟡）。**着手時に母集合を自分で引き直した**
（作業仕様書 `docs/specs/20260816_issue-799_pr-title-number-match.md`）。

| 実測（2026-08-16・全 PR 443 件を GitHub API から取得） | 値 |
| --- | --- |
| タイトル末尾に `(#NNN)` を持つ PR | 66 |
| **PR 自身の番号と一致するもの** | **0** |
| **一致しないもの（＝違反）** | **66** |
| `develop` に着地した件名で `(#A) (#B)` と二重になったもの | **58** |

**正しく自番号を書いた例は 1 件も無い。** issue 本文が挙げた「3 本」は直近の 3 本にすぎず、
**運用開始（PR #67）以来ずっと同型**であった。

##### 提案（キットへの反映案）

- 反映先候補: **キット配布物の是正**（`repo-template/.claude/rules/traceability.md` /
  `repo-template/scripts/check-commit-messages.js` / `repo-template/.github/workflows/pr-title.yml` /
  `repo-template/scripts/scripts.test.js`）
- 提案内容: 本リポの実装（#799 / IADR-0207）をそのまま取り込む。
  - `validateTitlePrNumber(subject, prNumber)` を新設し、`checkSingleTitle(title, author, prNumber)`
    の 3 引数目として結線する（**既存の 2 引数呼び出しは挙動不変**）。
  - `normalizePrNumber()` は未設定を `null`（＝検査しない）、読めない値を `NaN` にし、
    **`main()` が notice を出してスキップする**（黙って検査が消える偽の緑を作らない）。
  - 条文へ「その番号は PR 自身のものに限る」を 1 文足す。**総量予算があるため 1 文に留める。**

##### 影響範囲

- **配布先すべて**（本リポ／ai-stock-trading ほか）。同じ穴があり、同じ二重付加が起こり得る。
- 取り込みまでの間、本リポの `scripts/check-commit-messages.js` は
  `scripts/kit-sync-classification.json` で **B〔X〕**（環流債務）として測られる。
  着地したら **種 5** へ戻す。
- **`Closes #NNN` の担保（`IADR-0139` 決定 3 の 3 段目）は本記録の射程外**である。
  入力が違い（PR 本文であって件名ではない）、実測の母集合も桁違いで
  （着地本文 425 件中 388 件・merged PR 本文 431 件中 267 件が `Closes` 無し）、
  **検査器の追加ではなく規約の新設**に当たる。別途裁定を要する。

## ★［2026-08-16 追記］レビュージョブが権限拒否で落ちた —— **本 PR が引き起こした**

初回の `claude-review` が **failure** で終わった。レビュー本文は完走・指摘なしで投稿されており、
落ちたのは**その後の `check-permission-denials.js`** である。

```
##[error]AI の実行中にツールの権限拒否が 5 件発生した（CI には承認する人間が居ないため、
これらの作業は実行されていない）: mcp__github__list_pull_requests（5 件）。対処は 2 通りである:
(a) 必要なツールを claude_args の --allowedTools に加える、
(b) そのツールを使わせないようプロンプト側で作業手順を狭める。
どちらも取らずに放置すると、ジョブは success のまま成果物だけが欠けた状態が続く
```

### 原因は本 PR の主張の性質にある

本 PR の中心的な実測は「**全 PR 443 件のうち 66 件が末尾番号を持ち、一致 0 件**」である。
**これを裏取りするには PR の一覧が要る。** レビュアーは `mcp__github__list_pull_requests` を
5 回試し、5 回とも拒否された（許可一覧に無い）。**一覧に無いことを知らされていなかった**ため、
諦めるまで繰り返した。

**検査器は正しく働いた。** `check-permission-denials.js` が無ければ、レビューは
「未検証」と書いたまま **success** で通り、**中心的な主張が誰にも検証されないまま**マージされていた。

### 対処: (a) を採り、(b) も併せた

| 対処 | 内容 |
| --- | --- |
| **(a)** | `claude-code-review.yml` の `--allowedTools` へ `mcp__github__list_pull_requests` を足した |
| **(b)** | プロンプトの「使えるツールの一覧」へ、**母集合の主張はこれで引き直して裏を取ること**を明記した |

**(a) だけにしなかった理由**: 一覧に載っていても、**使うべき場面が書かれていなければ使われない**。
逆に**(b) だけ（＝「使えないので未検証と書け」）にもしなかった** —— 本リポの規律は
「主張は実測で裏を取る」であり、**母集合の主張を構造的に検証できない状態を固定するのは筋が悪い**。

**権限は広げた分だけ risk が増える**が、`list_pull_requests` は**読み取り専用・同リポスコープ**で、
レビューが既に持つ `pull_request_read` / `get_pull_request` と同じ面である。**書き込み権限は増えていない。**

### 起動条件は変えていない

`on: pull_request: types: [opened, synchronize, reopened]` は不変、ジョブ名も不変
（`CLAUDE.md`「ワークフローを変更したら、その変更で起動条件・必須チェックが変わらないかを確かめること」）。
差分は **4 行追加 / 1 行削除**のみ。
