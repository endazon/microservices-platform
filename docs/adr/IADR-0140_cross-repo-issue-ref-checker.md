---
title: IADR-0140 他リポジトリ issue 表記の検査は「表示テキストのみ」を見て、既存の CI 呼び出し口へ相乗りする
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260807_issue-507_cross-repo-issue-refs.md
  - ../specs/20260804_issue-478_staged-policy-citation-fix.md
---

# IADR-0140: 他リポジトリ issue 表記の検査は「表示テキストのみ」を見て、既存の CI 呼び出し口へ相乗りする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（保守性・追跡可能性。計画と実装の相互追跡が誤リンクで壊れないこと）
- 関連 issue: [#507](https://github.com/endazon/microservices-platform/issues/507)（起点。親 [#454](https://github.com/endazon/microservices-platform/issues/454)）
- 関連する実装 ADR: [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)
  （キット同期規約。本 ADR が触る `check-commit-messages.js` は**分類 B**、新設した
  `check-cross-repo-refs.js` は**固有デルタ種 3**＝本リポにしか存在しないスクリプト）
- 関連する実装仕様書: [20260807_issue-507](../specs/20260807_issue-507_cross-repo-issue-refs.md)
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)

## 背景

`.claude/rules/traceability.md` は他リポジトリの issue / PR 番号について 2 つを定めている。
(1) 短縮形（`planning#NNN` / `AST#NNN`）へ寄せ、フルパス形式と混在させない。
(2) **列挙形でも各番号を修飾する**（`planning#206 / #207` の `#207` は本リポジトリの実在 issue へ
誤リンクする）。

**どちらも守られていなかった。** 実測の母集合は 88 occurrence（型 1 = 55 / 型 2 = 33）。
しかも型 2 は **PR #561 が、規約の書いてある当のファイルを編集する PR でありながら**犯した。
`check-commit-messages.js` は件名の**書式**しか見ず、`check-doc-links.js` は**相対リンク**しか見ない。
**止める機械はひとつも無かった。**

## 決定

### 決定 1: 検査対象は「表示テキスト」——コードスパン／コードフェンスの中は見ない

型 2 の実害は **GitHub の自動リンク**であり、自動リンクはインラインコードとコードフェンスの中では
効かない。ここを対象外にすると、次の 3 つが**除外リストを持たずに**同時に解決する。

1. 規約自身が書いている反例（`` 誤: `planning#146 / #149 / #160` ``）が書ける。
2. 是正作業を記録した仕様書が「誤った文字列そのもの」を引用できる
   （`docs/specs/20260804_issue-478_*` で 12 件、`docs/specs/20260806_issue-560_*` で 2 件）。
3. grep 式・コマンド例をそのまま貼れる。

**これは例外リストではなく害の定義そのもの**なので腐らない。実測では型 2 の 33 件のうち
**17 件がコードスパン内**であり、除外の設計を誤ると「規約を書けない検査」になっていた。

型 1（表記ゆれ・実害なし）にも同じ文脈規則を適用する。規則を型ごとに変えると説明も実装も割れる。
**ただし是正は検査より広く行う**——コードフェンス内の型 1（`deploy/local/README.md` の 1 件）も、
同じファイル内で表記が割れないよう直した。

### 決定 2: 検査は既存の CI 呼び出し口へ相乗りする（新ワークフローを足さない）

`.github/workflows/` は GitHub App 権限で編集できない。**新スクリプトを置いても、それを呼ぶ
ジョブを足せないので CI に載らない。** 実測した既存の呼び出し口のうち 2 つへ結線する。

| 結線先 | ワークフロー | 何を守るか |
| --- | --- | --- |
| `scripts/scripts.repo.test.js`（companion。`node scripts/scripts.test.js` が読み込む） | `ci.yml` の `scripts-tests`（`REQUIRE_REPO_TESTS=1`） | **リポジトリの `*.md` 全体** ＋ 検査器の `--self-test` ＋ 検出力の実地確認（違反フィクスチャで exit 1） |
| `scripts/check-commit-messages.js`（`require`） | `ci.yml` の `commit-messages` ／ `pr-title.yml` | **コミット件名・本文・PR タイトル** |

2 面に分けたのは、**`.md` の走査ではコミットメッセージへ届かない**からである。PR #561 は件名・本文・
PR タイトルの 3 面すべてで犯しており、片面だけでは同じ事故が再発する。

`check-commit-messages.js` への追加は `require` と呼び出し 2 箇所に限り、規約の単一情報源である
`validateSubject` は**変更しない**（書式規約と参照表記の規約は別物。`scripts.test.js` が固定している
「allowlist は規約に準拠した件名を無意味に除外していない」判定を表記の是非で揺らさないため）。

### 決定 3: 型 2 は「修飾語の直後に続く列挙」だけを裸と判定する

裸の `#NNN` 一般を違反にすると、**本リポジトリの正当な参照（`#454`、`#450（FR-17/18）・#451（FR-19/20）`）
が全部止まる**。偽陽性を 1 件でも出せば検査は外される。したがって

- 修飾付き参照（`planning#NNN` / `AST#NNN` / `<owner>/<repo>#NNN`）の**直後**に、
- 区切り（`/` `／` `,` `，` `、` `・` `･`。前後の空白可）＋ 裸の `#NNN`

が続く形だけを検出する。**空白のみの区切りは採らない**——スカッシュ既定件名の ` (#123)` と衝突し、
正当な件名を落とすためである（既知の限界。`planning#206 と #207` のような助詞区切りも検出しない）。

### 決定 4: 検査対象ファイルは追跡下の `*.md` に限る

母集合 88 件は**実測で 100% が `.md`** であり、自動リンクが効くのも `.md` だけである。
`.js` / `.json` 等へ広げると、**検査器とその自己試験・repo テストが検出対象文字列を必ずソース中に
持つ**ため、自己参照の偽陽性を除外特例で潰す運用が要る。**除外の腐りは新たな穴**になるので広げない。
コード内コメントの表記は、コミットメッセージ側の検査とレビューで担保する。

## 影響

- 型 1 は develop 全域で 0 件になった（`git grep -nE '(^|[^\w/-])(project-planning|ai-stock-trading)#[0-9]+'` = 0）。
  **#507 本文の検索式は `project-planning` しか見ておらず、同型の `ai-stock-trading#NNN` を 41 件
  取りこぼしていた。** 母集合は「誤りの側から引く式」で取り直した（#541 の教訓）。
- 型 2 は表示テキストで 0 件。コードスパン内の 17 件は決定 1 により**意図的に残る**。
- `check-commit-messages.js` が `%b`（本文）も収集するようになった。
- 変異試験（M1〜M5）で「壊すと落ちる」ことを実測済み（仕様書に表を置いた）。うち 2 つ
  （フィクスチャによる検出力の確認・正しい表記で落ちないことの確認）は
  `scripts.repo.test.js` に**常設**した。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| `check-doc-links.js` に相乗りする（`--self-test` も同ジョブにある） | 同スクリプトは「相対リンクの実在」を見る道具であり、走査対象は `--dir docs` に閉じている。`.claude/` と `feedback/` を見るには対象決定の規則を二重化するしかなく、責務が割れる |
| 新しいワークフローを足す | `.github/workflows/` は GitHub App 権限で編集できない（本 PR の前提制約） |
| 裸の `#NNN` を一律で違反にする | 本リポジトリの正当な参照が全部止まる（偽陽性ゼロが要件） |
| 反例を除外リスト（ファイル名・行番号・マーカー文字列）で管理する | 除外が腐る。コードスパン除外なら「自動リンクが効かない＝害が無い」という**害の定義**で説明でき、リストの保守が要らない |
| Markdown リンクの `project-planning#NNN` はフルパス形式として残す（#507 の選択肢） | 規約が許すフルパス形式は `<owner>/<repo>#NNN` であり、`project-planning#NNN` は**そのどちらでもない第 3 の表記**。「残す」という選択肢がそもそも成立しない |

## 環流

規約自体は impl-handoff-kit が配布する `.claude/rules/traceability.md` に書かれているため、検査器ごと
キットへ環流する価値がある。[`feedback/20260807_kit-cross-repo-issue-ref-check.md`](../../feedback/20260807_kit-cross-repo-issue-ref-check.md) に記録した。

## 採番に関する注記

`docs/adr/` の最大は `IADR-0138` だが、`IADR-0139` は並行 PR（#575）が予約しているため `IADR-0140` を
採った。索引に一時的な欠番が生じるが、当該 PR のマージで埋まる（先着尊重。
`.claude/rules/traceability.md`「採番衝突時の改番手順」）。
