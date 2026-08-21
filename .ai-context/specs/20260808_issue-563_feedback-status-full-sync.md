---
title: 作業仕様書 feedback/ の status を計画側の実態へ全数追随させる（#563）
type: spec
status: done
related_ids: [NFR, IADR-0141]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:draft/feedback/README.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - 20260805_issue-497_feedback-status-sync.md
  - 20260808_issue-576_ast-id-qualification.md
---

# 仕様書: `feedback/` の status を計画側の実態へ全数追随させる（#563）

> 本仕様書は実装着手前に作成した。**#497（PR #523）の続き**である —— あちらは「考え方の整理 ＋
> 10 件」で、**全数の追随は明示的に範囲外**だった。本作業がその全数を引き受ける。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#563**（親 #454）／起点 ID: **NFR**
- 先行: **#497**（PR #523。[作業仕様書](20260805_issue-497_feedback-status-sync.md)）
- 契機: PR #561（#560）のマージ前監査 —— **pin を進めるたびにずれが増える構造**である
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  §「是正・追随の母集合の取り方」（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）
- status の語彙の正本: planning `draft/feedback/README.md`（計画リポ）
  `:19` / `:135`（**`open` / `triaged` / `accepted` / `rejected` の 4 値**）

## 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）

**「状態欄の追随のみ」**。新規の機械検査もコードもテストも作らない（§対象外・§申し送り）。

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: 作業ツリーのベース `65d5a2d`（`chore/NFR-576-ast-id-qualification`。`develop` = `7b6232b`
を含む）。planning submodule pin = **`891b199`**（`git submodule status` で実測。**pin は動かさない**）。

**引き方**: `feedback/` と `planning/draft/feedback/` の**全ファイルの frontmatter を読み込んで
突合する**。`grep` の行フィルタで絞らない（規則 4）。**issue #563 の DIVERGE 表を母集合にしない**
（規則: 他人の数えを転記しない）—— issue の実測は **pin `e36b592` 時点**であり、本作業の pin とは
別物である。

### 対象ファイル数（実測）

| 集合 | 件数 |
| ---: | ---: |
| `feedback/*.md`（`README.md` / `TEMPLATE.md` を除く） | **32** |
| `planning/draft/feedback/*.md`（`README.md` を除く） | **63** |

### 軸と実測値

**ファイル名だけで突き合わせる軸は 1 本目に過ぎない**（issue が指摘した改名ケースはそこで落ちる）。
軸を 5 本引いた（規則 5）。

| 軸 | 突合キー | 実測 |
| --- | --- | --- |
| 軸 1 | **同名**（`feedback/X` ↔ `draft/feedback/X`）の `status` 突合。24 組 | **相違 13 件** |
| 軸 2 | **日付前綴を外した接尾辞**（`20260804_a.md` ↔ `20260805_a.md`）。2 組 | **相違 1 件**（うち 1 組は両方 `accepted` で相違なし） |
| 軸 3 | **`title` / H1 の一致**（改名 ＋ 改題の両方に耐える） | **追加ヒット 0 件**（軸 2 で出た 2 組を再検出しただけ） |
| 軸 4 | **同名ペアの `title` 一致**（同名だが別記録が紛れていないか） | **5 組で `title` が異なる**。いずれも計画側がトリアージ時に改題したもので、`source_ref` が実装側のファイル名を名指ししており**同一記録**と確認した |
| 軸 5 | **`status` の語彙**（4 値以外の一点物が無いか） | **着手時点**で impl = `open` 16 / `accepted` 14 / `rejected` 2、plan = `accepted` 54 / `open` 7 / `rejected` 1 / `triaged` 1。**語彙外は 0**（#497 が畳んだ `closed` の再発なし） |

### ★ issue の「13 件」は本作業の pin では **14 件**である

| | issue #563（pin `e36b592`） | 本作業（pin `891b199`） |
| --- | ---: | ---: |
| 同名で `status` 相違 | 12 | **13** |
| 改名でペアリングが壊れた分 | 1 | **1** |
| **実質的な drift** | **13** | **14** |

増えた 1 件は **`20260807_kit-cross-repo-issue-ref-check.md`**（impl `open` / plan `accepted`）で、
**pin `891b199` への前進そのものが新たに作ったずれ**である（planning 側は 2026-08-07 に
`accepted` でトリアージ済み。planning#249）。**issue の起票時点では存在しなかった。**
issue 自身が「pin を進めるたびに不整合が増える構造」と書いたことの実例が、
**同じ issue を実装する間に 1 件増えた**形になる。

**なお本作業で書き換えるのは 13 件**である（14 件のうち 1 件 = `sc11-wireframe-drawio` は
**実装側が正**で書き換えない。§向きが逆の 1 件）。**14 と 13 は別の数であり、
issue の 13 と一致したわけではない。**

## 突合結果（14 件・すべて自分で開いて確認した）

`impl` = `feedback/`、`plan` = `planning/draft/feedback/`（pin `891b199`）。

| # | 記録 | impl | plan | 実施 | 計画側の根拠（実測） |
| ---: | --- | --- | --- | --- | --- |
| 1 | `20260707_iadr-0017-superseded-mesh-mtls.md` | open | accepted | **accepted** | 「トリアージ結果（2026-08-04）」節あり。planning#188 |
| 2 | `20260709_config-version-history-source-gitops.md` | open | accepted | **accepted** | 同上。planning#190 |
| 3 | `20260709_fr01-connector-and-nfr-verification-status.md` | open | accepted | **accepted** | 同上。planning#189 |
| 4 | `20260709_sc11-wireframe-drawio.md` | **rejected** | **open** | **変更なし** | §向きが逆の 1 件 |
| 5 | `20260719_headlamp-k8s-management-ui.md` | open | **triaged** | **triaged** | §`triaged` を控えでも使うか |
| 6 | `20260803_ai-review-execution-permissions.md` | open | accepted | **accepted** | 「トリアージ結果」節あり。planning#168 |
| 7 | `20260803_ai-workflow-grep-sort-and-submodule-git-c.md` | open | accepted | **accepted** | 同上。planning#163 |
| 8 | `20260803_doc-links-code-extensions.md` | open | accepted | **accepted** | 同上。planning#167 |
| 9 | `20260805_abac-attribute-combination-measurement-result.md` | open | accepted | **accepted** | 同上。planning#203 |
| 10 | `20260805_kit-pr-title-bot-author-gate.md` | open | accepted | **accepted** | 同上。planning#202 |
| 11 | `20260805_sc05-07-admin-contract-gaps.md` | open | accepted | **accepted** | 同上。planning#198 |
| 12 | `20260805_sc09-11-admin-ops-contract-gaps.md` | open | accepted | **accepted** | 同上。planning#199 |
| 13 | `20260807_kit-cross-repo-issue-ref-check.md` | open | accepted | **accepted** | 「トリアージ結果（2026-08-07）」節あり。planning#249 |
| 14 | `20260804_sc01-03-bff-contract-gaps.md`（改名ペア） | open | accepted（`20260805_` 付き） | **accepted** | §改名でペアリングが壊れた 1 件 |

**#1〜#3 と #6〜#8 の 6 件は、#497 の申し送り 1 が「後続 issue は 6 件で起票すること」と
名指ししていたものである**（そのとおり残っていた）。

## 改名でペアリングが壊れた 1 件 —— **改名前後のどちらが正か**

| | 実装リポジトリ | 計画リポジトリ |
| --- | --- | --- |
| ファイル名 | `feedback/20260804_sc01-03-bff-contract-gaps.md` | `draft/feedback/20260805_sc01-03-bff-contract-gaps.md` |
| frontmatter `created:` | **2026-08-04** | **2026-08-05** |
| `status` | open | accepted |
| `source_ref` | `feat/SC-01-03-search-flow / docs/specs/20260804_issue-502_sc01-03-search-flow.md（#502）` | `planning#197 / 実装側 feedback/20260804_sc01-03-bff-contract-gaps.md・#502・PR #505 / planning d980a01` |

**結論: どちらも自分のリポジトリでは正しい。「改名」ではなく、両リポジトリが別々の日に作った
別ファイルである。**

- 両リポジトリの命名規則は `<YYYYMMDD>_<概要>` で、**日付はそのファイルが作られた日**である。
  実装側は 2026-08-04 に記録を作り（`created: 2026-08-04`）、計画側は **planning#197 の
  Issue 経路**で 2026-08-05 に取り込んだ（`created: 2026-08-05`）。**どちらの日付も自分の
  `created:` と一致しており、片方を誤りと呼べる根拠は無い。**
- ペアであることは**ファイル名ではなく内容で確定できた** —— 計画側の `source_ref` が
  **実装側のファイル名を名指ししている**。
- したがって **実装側のファイルは改名しない。** 改名すると `created:` と食い違い、
  この記録を指す既存の参照（#497 の作業仕様書・計画側 `source_ref`）が一斉に切れる。
- **直すのは `status` だけである。**

> **issue の「ファイル名で突き合わせる限りこのずれは永久に見えない」という指摘は正しい。**
> ただし対処は改名ではなく、**突合キーを名前以外に持つこと**である（issue のやること 2 =
> 安定 ID。**本作業の範囲外**。§申し送り 1）。

## 向きが逆の 1 件（`20260709_sc11-wireframe-drawio.md`）—— **実装側が正。書き換えない**

impl = `rejected` / plan = `open` で、唯一「控えが原典より先行している」ケースである。
**どちらが実態に合っているかは計画書本体で決まる**ので、計画書を読んで確かめた。

```console
$ grep -n 'draw\.io' planning/projects/microservices-platform/05_screens/01_screens.md
45:…（ワイヤーフレームは HTML モックアップ〔mockups/wireframe/〕を正とし、draw.io ワイヤーフレームは作成しない。…）
```

**計画書自身が「draw.io ワイヤーフレームは作成しない」と明文で定めている**（pin `891b199` でも
そのまま）。記録の提案（`sc-11.drawio` を計画リポへ追加する）は計画方針に反するので、
**`rejected` が実態である**。計画側 `draft/feedback/README.md` の「未処理（status: open）」表に
残っているのは**トリアージ未実施**であって、計画が採用を検討中という意味ではない。

- **`rejected` のまま据え置く。** 控えを `open` へ戻すのは、実態から遠ざける後退である。
- 既存の `status_note: 計画側原典は open（planning 未追随。控えが #497 で先行）` は
  **pin `891b199` でも依然として正しい**（実測）。触らない。
- 計画側のトリアージは計画リポジトリの作業である（#497 申し送り 3）。**本リポジトリからは触らない。**

## `triaged` を控えでも使うか（`20260719_headlamp-k8s-management-ui.md`）

#497 は「控えを `accepted` にすると計画側より進んだ状態になる。`triaged` を控えでも使うかは
運用判断が要る（**要裁定**）」として保留していた。**実測で解ける**と判断した。

- status の語彙の正本は計画側 `draft/feedback/README.md` `:135` であり、
  **`open` / `triaged` / `accepted` / `rejected` の 4 値**である。控えだけが使えない値は無い。
- 控えは原典の写しなので、**原典より進んだ値も遅れた値も付けない**のが素直である
  （`accepted` にすると裁定待ちが消えたと偽り、`open` のままだとトリアージ済みを隠す）。
- `closed` のような**語彙外の一点物を新設するわけではない**（#497 が畳んだ型と別）。

→ **`triaged` を採る。** これは #497 が保留した論点への回答であり、
「4 値の語彙を控えでもそのまま使う」という運用の確認である。

## `updated:` の扱い

`status` を書き換えた 13 件は `updated: 2026-08-08` を置く（**既にある 4 件は前進、無い 9 件は
`created:` の直後に追加**）。#497 が「`updated:` も追随」で採った先例に合わせる。
**`updated:` は本文の主張ではなく、この控えファイル自身の状態欄である。**

## 対象範囲

| # | 作業 | 出力 |
| ---: | --- | --- |
| 1 | frontmatter `status:` の追随（13 件） | `feedback/*.md` × 13 |
| 2 | frontmatter `updated:` の追随（同 13 件） | 同上 |
| 3 | 本作業仕様書 | 本ファイル |

**1 ファイルあたり 2 行**しか変えない。

## 対象外（**除外したものと理由**・規則 6）

黙って除外しない。引いた母集合のうち、手を触れないものを全部書く。

| 除外対象 | 件数 | 理由 |
| --- | ---: | --- |
| **記録の本文**（frontmatter 以外） | 全件 | `feedback/` は**計画リポへ送った内容の写し**であり、`.claude/rules/traceability.md` §母集合 が「書いた時点の記録。後から注記を足すのは記録の改竄」と定める。**直してよいのは現在の状態を表す欄（`status` / `updated`）だけ**である。#497 は日付つき追記を足したが、その規約は #580 で後から確立したもので、**本作業は追記を足さない** |
| `20260709_sc11-wireframe-drawio.md` | 1 | **実装側が正**（上記）。計画側の追随は計画リポの作業 |
| impl にしかない記録 | 6 | `20260801_impl-handoff-kit-gaps.md` / `20260802_review-allowlist-diff-and-denial-labeling.md` / `20260804_frontend-migration-staging-interpretation.md` / `20260807_fr17-21-gate-scope-ambiguity.md` / `20260807_kit-audit-rounds-and-population.md` / `20260808_kit-plan-id-qualification-check.md`。**軸 3（`title` / H1）でも計画側に対応が見つからない**（本文キーワードでの逆引きも実施）。原典が無い以上 drift は定義できない。前 3 件が `accepted` なのは計画側 issue の追跡結果（#497 が確認済み）で、**本作業では検証も変更もしない** |
| `20260710_repo-positioning-and-unit-structure.md` | 1 | 改名ペア（plan は `20260712_`）だが**両方 `accepted`** で相違なし |
| plan にしかない MSP 由来の記録 | 5 | `20260706_abac-spec-implementation-gaps` / `20260706_adr-0010-model-decision-b` / `20260706_tech-stack-implementation-status` / `20260716_platform-arch-overview-diagram-out-of-sync` / `20260801_adr-file-rename-downstream-refs`。**控えが実装側に無い** = 記録の欠落であって status のずれではない。控えを新設すると計画側本文を実装リポへ複製することになり、本作業の性質（状態欄のみ）を超える。**§申し送り 2** |
| plan にしかない AST 由来の記録 | 32 | 別プロジェクトの環流であり、本リポジトリの `feedback/` は宛先ではない |
| `planning/`（submodule）の内容と pin | — | **読み取り専用**。pin は動かさない |
| `.github/workflows/` | — | 本エージェントの権限では編集不可。CI 結線は §申し送り 3 |
| `scripts/`（突合スクリプトの新設） | — | issue の「やること 3」。**本作業の範囲外**（§申し送り 1） |
| `src/` ・テスト | — | 1 行も変えない |

## 検証

- `node scripts/check-doc-links.js`（既定 = `docs/`）
- `node scripts/check-doc-links.js --dir feedback`（**既定では検査されない経路**。#497 の申し送り 2）
- `node scripts/check-cross-repo-refs.js`
- `node scripts/check-plan-id-qualification.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 差分が `feedback/*.md` 13 件（各 2 行）と本仕様書だけであること。
  `planning` の pin が差分に現れないこと。
- **是正後に軸 1〜3 を引き直す**（残存 drift の確認）。

### 実測（すべて実走した）

| 検証 | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | **exit 0**（461 件。未 populate な `src/ai-stock-trading` 配下 2 件は対象外の notice） |
| `node scripts/check-doc-links.js --dir feedback` | **exit 0**（34 件） |
| `node scripts/check-cross-repo-refs.js` | **exit 0**（537 件） |
| `node scripts/check-plan-id-qualification.js` | **exit 0**（1172 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **288 tests passed** |
| 差分 | `feedback/*.md` 13 件 ＋ 本仕様書のみ。`git submodule status` の planning は `891b199` のまま |
| 是正後の引き直し（軸 1〜3） | 同名の相違 **1 件**（= `sc11-wireframe-drawio`。**意図した据え置き**）／改名ペア 2 組は**いずれも相違なし** |

## 申し送り

1. **issue #563 の「やること」2〜4 は未消化である。** 本作業が消化したのは **1（13 件の追随）**
   だけで、次が残る。**受け入れの観点「突合スクリプトが 13 件すべてを検出することを現状の
   ツリーで実測してから直す」は満たしていない** —— 本作業は突合を**使い捨てのスクリプトで
   手元実測**しただけで、リポジトリへ検査器を置いていない。
   - やること 2: 日付に依存しない安定 ID（`feedback_id` 等）—— **改名ペアを名前以外で突き合わせる**
     手段。本作業は「計画側の `source_ref` が実装側ファイル名を名指ししている」ことに頼って
     手作業で解決したが、これは保証された規約ではない。
   - やること 3: 突合スクリプト（`scripts/check-feedback-status.js` 等）の新設。
     **pin を動かす PR で必ず走らせる**こと —— 本作業で実測したとおり、
     **pin を進めた分だけずれが増える**（issue 起票後に 1 件増えた）。
   - やること 4: `feedback/` を `check-doc-links.js` の検査経路へ載せる（`.github/workflows/` の
     編集が要る）。
2. **計画側にしかない MSP 由来の記録が 5 件ある**（上表）。実装側に控えが無く、
   `feedback/README.md` が言う「実装リポジトリ側の控え」が片肺になっている。
   控えを作るべきか（＝ `feedback/` は実装発の記録だけを置く場所なのか、計画側の MSP 記録すべての
   鏡なのか）は**運用の定義が未確定**である。**要裁定。**
3. **`feedback/` は CI のどの経路でも検査されない**（#497 の変異試験 M2 で実測済み・本作業でも
   変わらず）。`check-doc-links.js` の既定走査は `docs/` だけで、`ci.yml` も `doc-links-planning.yml` も
   `--dir feedback` を渡さない。
4. **本作業は「状態欄だけを直す」線引きで実施した。** `feedback/` の本文へ追記する運用
   （#497 が採った形）と、本文を触らない運用（#580 の母集合規約）が併存している。
   **既存の追記を消しはしない**（それこそ記録の改竄になる）が、**今後どちらを採るかは
   まだ 1 つに決まっていない**。
