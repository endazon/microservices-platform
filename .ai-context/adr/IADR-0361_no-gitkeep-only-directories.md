---
title: IADR-0361 ユニット直下と雛形の空枠も撤去し、「.gitkeep のみのディレクトリが無いこと」を 1 述語で機械検査する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - ADR-0065
  - ADR-0069
  - ADR-0077
  - IADR-0183
  - IADR-0218
  - IADR-0309
  - IADR-0321
  - IADR-0325
  - IADR-0333
author: Claude
created: 2026-09-03
updated: 2026-09-03
related_specs:
  - ../specs/20260903_issue-1195_empty-scaffolding-frames-removal-and-check.md
---

# IADR-0361: ユニット直下と雛形の空枠も撤去し、「`.gitkeep` のみのディレクトリが無いこと」を 1 述語で機械検査する

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: implementation-agent（#1195。計画 `ADR-0069`（Accepted 2026-09-02）の実装側への写像）

## 文脈

[IADR-0325](./IADR-0325_unit-level-scaffolding-frames-await-arbitration.md) 決定 1 は
**「ユニット直下（`src/` 最上位）と雛形の枠は裁定まで残す」**とし、23 件を残したまま
撤去の可否を planning#510 へ委ねた。**その裁定が計画 `ADR-0069` として下りた。**

`ADR-0069` は 5 つを定めた。実装側に効く要点は 3 つである。

1. **決定 1**: `.gitkeep` のみのディレクトリを置かない。射程は **feature 内部・ユニット直下・雛形の
   3 者すべて**。`IADR-0321`（feature 内部 30 件の撤去）を**追認**し、`IADR-0325` が残した
   23 件も撤去してよい。**雛形と実装ユニットは同時に動かす。** `docs/` の 4 件は射程外で、
   `IADR-0325` 決定 2 を**追認**する。
2. **決定 2**: `IADR-0325` 決定 1 が根拠にした「planning#445 はどちらの側も支えない」を**否定**する。
   同 issue の列挙は**非適合の実測**であって必須項目の一覧ではなく、
   「名前だけを揃える対応は採らない」が**空枠を明示的に排除している**。
3. **決定 5**: 「`.gitkeep` のみのディレクトリが無いこと」を機械検査に載せる。**述語は 1 つだけ。**

**本 IADR は `IADR-0325` 決定 1 と、`IADR-0321` 決定 4 ／ `IADR-0325` 決定 5（いずれも
「機械検査は追加しない」）を置き換える。** `IADR-0325` 決定 2・3・4 は生きている。
**確定済み記録である `IADR-0321` / `IADR-0325` の本文は書き換えない**
（`.claude/rules/traceability.repo.md` §凍結の射程）。索引の `IADR-0325` 行へ後継 ID を併記した。

### 実測（自分で全数走査した。転記ではない）

基点は `origin/develop` を取り込んだ本ブランチ。`git rev-parse --is-shallow-repository` = **`false`**。

追跡下の `.gitkeep` は **27 件**。**27 件それぞれについて、同階層の兄弟と配下の全子孫の両方を数えた
結果、27 件とも「`.gitkeep` 以外の追跡ファイルが 0 件」**であった（＝真に空）。
射程内 23 件（knowledge 8 / platform 4 / 雛形 11）を撤去し、`docs/` の 4 件を残す。
1 件ずつの判定表は[作業仕様書](../specs/20260903_issue-1195_empty-scaffolding-frames-removal-and-check.md) §2 が持つ。

**陽性対照**（走査が機能していることの証明）: `src/platform/frontend/src/utils/` は追跡ファイル 4 件、
`templates/unit-template/frontend/src/features/` は 8 件で、**同じ引き方で「空枠でない」ものが出る。**
別名の枠（`.keep` / `.placeholder` / `.empty` / `PLACEHOLDER`）は 0 件である。

## 決定

### 決定 1 — 空枠の定義を「配下に `.gitkeep` 以外の追跡ファイルが 1 件も無いこと」とする

`ADR-0069` 決定 5 の述語「`.gitkeep` のみのディレクトリ」を、機械が判定できる形へ落とす。

> あるディレクトリの直下に `.gitkeep` があり、**その配下（任意の深さ）に `.gitkeep` 以外の
> 追跡ファイルが 1 件も無い**とき、そのディレクトリは**空枠**である。

🔴 **子孫まで見るのは、実体と同居する `.gitkeep` を枠と呼ばないためである。**
`IADR-0325` 決定 2 が撤去した 11 件（`.ai-context/specs` に 549 件、`docs/tests` に 53 件……）は
**ディレクトリが実体で存在しており、`.gitkeep` は何も keep していない残骸**であった。
**これは「空枠」とは別の型である** —— 同じ述語で落とすと、枠ではないものまで枠と呼ぶことになる。
入れ子（`a/.gitkeep` と `a/b/.gitkeep`）は**両方とも空枠**である（どちらも実体を持たない）。

**残骸の型（実体と同居する `.gitkeep`）は本検査の対象にしない。** `IADR-0325` 決定 5 が
「2 回目が起きたら足す」とした判定であり、**2 回目はまだ起きていない**（実測: 該当 0 件）。

### 決定 2 — 検査する述語は 1 つだけにする。区分ごとの不変条件は対象にしない

`IADR-0321` 決定 4 は「#1078（i18n カタログの網羅）・#1066（feature 区分の実体）・#1100
（撤回済み規範の残置）は『伸ばし忘れ』としては同型でも、**検査すべき不変条件が違う**」と述べた。
**この指摘は正しい。** だからこそ検査するのは**撤回済み規範の残置という 1 つの述語**だけとする
（`ADR-0069` 決定 5 が明示的にそう限った）。

🔴 **型 (b)（関心はあるが置き場所が違う）は検査しない。** `ADR-0069` §結果 が
「決定 5 の検査は『空枠が無いこと』しか見ない」と自ら記しているとおりである。
**常設検査を置くかは本 PR で決めない**（同 フォローアップ 4。要ると判断したら別 issue にする）。

### 決定 3 — `IADR-0321` 決定 4 ／ `IADR-0325` 決定 5（機械検査を追加しない）を置き換える

条件は満たされた。**同型の入口が 3 度使われた**（#1066 → #1100 → #1122）。
planning#490 の環流記録 §判定手順への申し送り は「フロントエンドに同型の入口が残る」ことを
**名指しで予告していた**。`CLAUDE.md`「検査器・規約の追加は『同型の事故が 2 回起きたら』」を満たす。

**検査器は `scripts/check-scaffolding-frames.js`。** 作法は既存の検査器へ揃える。

- 走査母集合は `git ls-files`（**クラス B**。`MODE.TRACKED` を宣言し `TRACKED_CHECKERS` へ載せた。
  #683 / `IADR-0183`）。**未追跡は定義上の対象外**である。
- **0 件走査で静かに緑にしない**（`MIN_SCANNED` の門）。
- 🔴 **射程外は `ALLOWED_EMPTY_FRAMES` へ理由つきでしか宣言できない**（`ADR-0069` 決定 5）。
  **理由が 10 文字未満の行・実在しない行があれば検査器自身が fail する** ——
  黙って外す道も、腐った除外を残す道も作らない（`check-route-manifest.js` の除外宣言と同じ作法）。
- CI は `static-checks` ジョブへ **2 ステップ**（自己試験 → 本走査。いずれも `if: !cancelled()`）。
  **起動条件（`on:`）とジョブ名（＝必須チェック名）は変えていない。**

### 決定 4 — 除外は `docs/` の 4 件のみとし、先回りで広げない

`ADR-0069` 決定 1 は「本リポジトリ自身の `/new-project` が置く `.gitkeep` も射程外」と述べているが、
**本リポジトリの `.claude/commands/` は 7 本（`adr-check` / `impl-feature` / `new-spec` /
`plan-feedback` / `plan-to-tasks` / `trace-check` / `verify`）で `new-project` を含まず、
`git grep -i gitkeep -- .claude/` は 0 件である**（**陽性対照**: 同じ引き方の
`git grep -c -i gitkeep -- scripts/` は `check-nul-bytes.js:1` を返す。0 件は「無い」であって
「引けなかった」ではない）。**したがって除外リストへ足さない。** 実際に置かれたときに人が判断する。

**除外が黙って伸びないよう、`scripts.repo.test.js` が現在の 4 件を字句で固定する。**

### 決定 5 — 枠を根拠にした記述を 2 つの README から剥がし、雛形は正本を指す

`IADR-0325` 決定 4 が「枠そのものより、枠を根拠にした記述のほうが有害である」と述べたとおり、
**撤去とこの追随は同じ PR で動かす**（片方だけ直すと「消した」と「消さない」が同居する）。

- `src/platform/frontend/README.md`: 「答えが出るまで消さない」を撤去し、
  **`ADR-0069` 決定 3 の (a)/(b) の区別を表として残した** —— 既存の注記が持っていた
  「区分ごとに空の理由が違う」という情報は捨てず、**型 (a) の側の説明として位置づけ直した。**
  🔴 **`platform` の `utils/` が型 (b) であったこと**（#1131）を、その実例として明示した。
- `templates/unit-template/README.md`: ツリーから `.gitkeep` の 11 行を落とし、
  ［2026-08-31 追記 / #1122］の「裁定を待つ」ブロックを撤去した。
  🔴 **ツリー全体の正本が計画 `13_frontend-stack` §ディレクトリ構成 であることを README が指す**
  （`ADR-0069` §結果 悪い影響 1 —— 枠が消えると複製者がコードから全体像を読めなくなる）。

**あわせて `templates/unit-template/backend/Services/SampleService/README.md` の「操作＝HTTP 端点」
前提を直した**（9・15 行目）。**新しい決定はしていない** —— PR #1205（#1196 / 計画 `ADR-0077`）が
`templates/unit-template/README.md` へ書いた語義と同じものを、同 PR が宣言ファイル領域の制約で
**残すと申告した**もう 1 枚の雛形 README へ写しただけである。本 PR が `templates/unit-template/**` を
触るので、ここで閉じた。

## 影響

- **コードは 1 バイトも動かない。** 削除したのは 0 バイトのファイル 23 件だけで、
  import グラフ・バンドル・ルート定義・ルート manifest に触れていない。
  `scripts/chunk-budget-baseline.json` の更新は不要である。
- **撤去した 12 区分を指すエイリアス・設定は 0 件である。** `src/vitest.config.ts` /
  `src/lingui.config.ts` / `src/platform/frontend/{vite.config.ts,tsconfig.app.json}` /
  `src/knowledge/frontend/tsconfig.json` / `templates/unit-template/frontend/tsconfig.json` /
  `src/eslint.config.js` の 7 ファイルを走査した結果、向き先はすべて `platform/frontend/src/` の
  **実体を持つ**区分であった。**陽性対照**: `platform/frontend/src/locales` は
  `lingui.config.ts` と `eslint.config.js` が名指ししており、同じ引き方で 0 件でないものが出る。
- **`.gitkeep` の総数は 27 → 4 になった。**
- **検査器の母集合が 48 → 49 本**（`scripts.repo.test.js` のラチェットが設計どおり発火した）。
  クラス B（`git ls-files` を読む）は 3 → 4 本。

## 残余リスク

- 🔴 **型 (b)（置き場所違い）は依然として検査されない。** `IADR-0333` が採った走査
  （`components/` 配下の JSX 有無）は **1 度きりの実測であり常設の検査ではない**。
  枠の撤去は型 (b) の検出を弱めないが（弱いのは元から検査であって枠ではない）、**強くもしない。**
  `ADR-0069` フォローアップ 4 として計画側に残る。
- **`docs/DEFINITION_OF_DONE.md` §検証の順序 の内訳（A=2 / B=2 / C=29・計 4 本）は、
  本 PR より前から実データと食い違っていた**（実データは A=3 / B=3 = 6 本）。
  本 PR で B が 4 本になるが、**この食い違いは本 PR が作ったものではない**ため同じ diff で直さない
  （「消した」と「直した」を混ぜない。#1100 以来の制約）。**別 issue で直す。**
- **除外リストは足せば足すだけ検査されなくなる。** 現在 4 件で、`scripts.repo.test.js` が
  字句で固定している。**足すときは本 IADR の後続記録へ理由を残すこと。**
