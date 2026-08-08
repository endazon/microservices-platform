---
title: IADR-0145 着地件名は事後検知しかできず、ID の脱落そのものは機械では判定できないと明記する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0115, IADR-0140, IADR-0141, IADR-0144]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260808_issue-579_squash-landing-title.md
---

# IADR-0145: 着地件名は事後検知しかできず、ID の脱落そのものは機械では判定できないと明記する

- 状態: Accepted
- 日付: 2026-08-08
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（保守性・追跡可能性）
- 関連 issue: [#579](https://github.com/endazon/microservices-platform/issues/579)（起点。親 [#454](https://github.com/endazon/microservices-platform/issues/454)）／原因の PR [#568](https://github.com/endazon/microservices-platform/pull/568)
- 関連する実装 ADR: [[IADR-0140]]（CI 結線方式）／[[IADR-0141]]（母集合）／[[IADR-0144]]（姉妹検査器の作法・ラチェット）／[[IADR-0115]]（`scripts.test.js` は分類 A で変更不可）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「PR タイトル（スカッシュ後件名）の検査」

## 背景 —— 誰も見ていない第 3 の文字列があった

| 検査 | 見ているもの | `bc7bc8e` を見たか |
| --- | --- | --- |
| `pr-title.yml` | **PR タイトル** | 見た。`SC-07` を含む版で **green** |
| `ci.yml` の `commit-messages` | **`base..HEAD`** | 見ていない（スカッシュ件名はこの範囲に無い） |
| — | **実際に develop へ載る件名** | **誰も見ていなかった** |

規約は「PR タイトル ＝ スカッシュ後件名」を前提にしているが、**マージ時に件名を書き直せる**ので
その前提は破れる。PR #568 で破れ、`feat(FR-12,SC-07)` → `feat(FR-12)` と **`SC-07` が落ちた**。
同 PR は SC-07 の画面仕様書・テスト仕様書・実装を実際に改変している。**force push 禁止で事後修正できない。**

あわせて、実在性検査が `IADR` と `ADR` にしか無く、`feat(SC-99)` / `feat(FR-77)` / `feat(UC-88)` は
**いずれも exit 0 で受理**されていた（実測）。

## 決定

### 決定 1: 着地件名を走査する 3 本目の検査を置く

`scripts/check-landed-subjects.js` を新設し、`(#NNN)` で終わる着地件名を `validateSubject` ＋
`validateIdExistence` へ通す。CI は [[IADR-0140]] 決定 2 と同じ結線
（`scripts.repo.test.js` → `ci.yml` の `scripts-tests`。**新ワークフローは足さない**）。

### 決定 2: FR / UC / SC の実在性検査を `check-commit-messages.js` へ足す

レンジの正は `.claude/rules/traceability.md`「起点 ID の種別」節であり、**そのパーサは既に
`check-test-traceability.js`（#472）に在る**。**同じ事実を 2 本のパーサで持たない**
（[[IADR-0141]]「参照点を 1 つに畳む」）。

**fail の向きを 2 つに割る**（「見つからないから素通り」を一律には採らない）:

| 状況 | 扱い | 理由 |
| --- | --- | --- |
| モジュールが無い（キット派生リポの構成） | **skip ＋ notice** | 環境差であり、CI をローカル構成で落とさない |
| モジュールは在るが節をパースできない | **例外（fail）** | `traceability.md` は追跡下の必ず読めるファイル。読めないのは**規約側の破壊**であり、黙って通すとレンジの単一情報源が壊れたまま緑になる |

### 決定 3: **★ ID の脱落そのものは機械では判定できない**（本 ADR の主要な内容）

**#568 で実際に起きた事故は、決定 1 の検査でも検出できない。**
落ちた後の件名 `feat(FR-12): 変換ジョブの…` は**それ自体が完全に規約適合**であり、
「落ちた」と判定するには **PR タイトルとの突合**が要る。**PR タイトルはリポジトリの中に無い。**

したがって決定 1 が検出するのは次の 2 型に限られ、**どちらも恒久履歴に入ってから気づく事後検知**である。

1. 書式違反（`Claude/issue 71 20260705 1545 (#95)` のようなブランチ名由来の既定件名）
2. 実在しない起点 ID（`feat(SC-99)`。決定 2 の検査を着地面へも当てる）

**できないことを検査の中に書く。** スクリプト冒頭・規約・本 ADR の 3 箇所に同じ限界を明記した
——**「検査を入れた」が「守られている」と読まれるのが、この型の最も危険な誤解**だからである。

**根に最も近い対策は運用側にある**: **件名を書き直さない**。書き直す必要があるなら
**先に PR タイトルを直してからマージする**（そうすれば検査する文字列と着地する文字列が一致する）。
これは運用規律であって機械では守られない。**そう書き残すことが本決定の実体である。**

### 決定 4: 既存履歴はラチェットで据え置く

規約導入前の着地件名が恒久履歴に在り、force push で直せない。
`scripts/landed-subject-baseline.json` へ控え、**新規混入だけを落とす**。
baseline に在るのに違反しなくなった項目も fail させ、**baseline が縮む向きにのみ動かす**
（`backend-library-baseline.json` / `adr-index-title-baseline.json` と同じ作法）。

**件数は条文にも本 ADR にも書かない**（[[IADR-0144]] 決定 2 と同じ理由）。正は baseline ファイルである。

### 決定 5: 走査不能の 2 種を区別する（**skip と fail を混ぜない**）

着地件名は履歴を遡らないと見えない。浅いクローンでは母集合そのものが取れないので skip するが、
**「baseline のハッシュが解決できない」を一律に skip 扱いにしてはならない。**

**当初これを一緒にして穴を作り、変異試験で踏んだ** —— baseline へ存在しないハッシュ `0000000` を
**1 行足すだけで検査全体が exit 0 の緑になった**。よって:

| 判定 | 扱い |
| --- | --- |
| `git rev-parse --is-shallow-repository` = true | **skip ＋ notice**（母集合が取れない） |
| 履歴は完全なのに baseline のハッシュを解決できない | **fail**（打ち間違い・他リポの SHA の持ち込み） |

### 決定 6: #568 の記録は `changelog-overrides.json` の `remap` で是正する

履歴は不変・生成物のみ是正（規約が定める唯一の事後手段）。`scope` だけを `FR-12` → `FR-12,SC-07`
へ戻し、`type` と `desc` は元コミットの値を保つ——**誤っているのは落ちた ID だけ**だからである。

## 検出しないこと（本検査は網羅ではない）

- **PR タイトルからの ID 脱落**（決定 3）。**これが #579 の起点となった事故そのものである。**
- **マージ前の予防**。決定 1 は事後検知であり、恒久履歴への混入は止められない。
  予防は `pr-title.yml` が担うが、それは書き直される前の文字列しか見ない。
- **要約文の書き換え**。`#567` は要約が別文になったが ID は保たれており、規約上の問題ではない。
- **他リポジトリの計画 ID**（`AST/SC-01` 等）。修飾付き ID は自名前空間の突合対象外
  （`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」）。

## 影響

- `scripts/check-landed-subjects.js` ＋ `scripts/landed-subject-baseline.json` を新設。
- `scripts/check-commit-messages.js` に `loadExistingPlanIds` / `normalizePlanId` を追加し、
  `validateIdExistence` へ第 4 引数を足した（**既存の呼び出しは 3 引数のままでも動く** ——
  `planIds` が `undefined` なら当該検査をスキップする）。
- `.github/workflows/` は編集していない（GitHub App 権限で編集不可。[[IADR-0140]] 決定 2）。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| push 契機の新ワークフローで直近の着地件名を検査する | `.github/workflows/` は GitHub App 権限で編集できない。既存の呼び出し口へ相乗りすれば同じ検出力が得られる |
| 着地件名の総数を baseline に焼く | PR がマージされるたびに動き、無関係な PR を赤にする手作業の更新点になる（[[IADR-0144]] 決定 2） |
| ID 脱落を検出するため PR タイトルを API で引く | 検査がネットワークと権限に依存し、`scripts/` の「外部依存ゼロ」を崩す。**できないことを明記する方を選んだ**（決定 3。[[IADR-0144]] 決定 3 と同じ判断） |
| `validateSubject` へ実在性検査を混ぜる | 書式規約の単一情報源を汚す。allowlist の判定が実在性で揺れる（既存の `validateIdExistence` 分離と同じ理由） |
