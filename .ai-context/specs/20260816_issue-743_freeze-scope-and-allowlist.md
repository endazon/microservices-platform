---
title: 作業仕様書 — IADR-0191 決定 2 の射程を「frontmatter の二重記述」へ絞り、baseline を allowlist 化する（#743）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0166
  - IADR-0185
  - IADR-0187
  - IADR-0191
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:CLAUDE.md (§中間成果物「裁定・決定の内容そのものは必ずリポジトリへ残す」)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md"
  - "../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md"
  - "../adr/IADR-0185_feedback-status-vocabulary.md"
  - "20260815_issue-733_remove-feedback-body-addenda.md"
---

# 作業仕様書 — 凍結の射程を絞り、baseline を allowlist 化する（#743）

## 1. 起点と根拠

- 実装 issue: **#743**（[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 と「裁定の記録は残す」の衝突）
- **裁定 planning#369（2026-08-16 に利用者から伝達）**: **案 A。凍結の射程を「frontmatter の二重記述」に限定する。**
  - **トリアージ結果・裁定の記録は対象外**
  - **第 3 の型「自己是正の訂正」も対象外**（**元の記述を消さないことが条件**）
  - **マージ前の同一 PR 内の推敲も対象外**
- 起点 ID は **`NFR`（無採番）**。文書統制・規約の射程確定というメタ作業であり、計画側の非機能要件表に
  当たる番号が無い（`.claude/rules/traceability.md` の場合 ② / [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2）。環流しない。

## 2. 母集合（**自分で引き直した**。issue コメントの表は転記していない）

**走査は worktree `/home/user/wt-0191`（`origin/develop` = `3818c08`）に対して行った。**
**`git fetch --unshallow` 済み**である —— shallow のままだと `git log --diff-filter=A` が
どのファイルも「境界で追加された」ように見せる（baseline の `$comment` が記録している事故）。

### 2.1 軸 1 —— 検査器と同じ述語（本文のみ・frontmatter を除く）

`scripts/scripts.repo.test.js` の `isAddendumHeading`（見出し / 引用 / 強調で始まる ＋ `追記` ＋ 年月日）を
そのまま `feedback/*.md`（`README.md` を除く 47 件）へ当てた。

```
19 ブロック / 15 ファイル
```

**baseline（`scripts/feedback-body-addendum-baseline.json`）の `files` 合計と一致した**
（`15 ファイル / 19 ブロック`）。**これが本作業の作業対象である。**

### 2.2 軸 2 —— `feedback/` 全体で `追記` を含む全行（行頭条件なし・frontmatter も含む）

`grep -n 追記 feedback/*.md` = **22 ファイル**にヒット。うち**ブロック見出しの形をしているのは軸 1 の 19 行だけ**で、
残りは地の文（「計画側へ追記する」「下記『追記 2』を参照」等）である。**軸 1 の取りこぼしは無い。**

### 2.3 軸 3 —— **`追記` の語を要求しない**（見出し / 引用 / 強調 ＋ 年月日）

**37 行**がヒットする。うち**後付け注記の形をしたもの**（`反映済` / `取り下げ` / `裁定` / `triage` /
`失効` / `解消` / `受理` / `是正` のいずれかを含む）は **20 行**である。

**この 20 行は検査器の述語に掛からない**（`追記` の語が無いため）。**本作業では撤去しない。**
理由は 2 つある。

| # | 理由 |
| --- | --- |
| 1 | **三値を当てると、20 行のどれも ①（frontmatter の二重記述）ではない。** 大半は②（トリアージ結果・裁定の記録）か③（自己是正）であり、**裁定の下では撤去対象にならない**。すなわち撤去漏れは生じていない |
| 2 | **検査器を広げると、対象外と判定した記録を新たに allowlist へ載せることになる。** 利用者決定は「検査器は現在の形のまま。弱めない」であり、**広げる決定は出ていない**。述語の穴として本仕様書に記録し、必要なら別 issue とする |

**判断の内訳**（20 行のうち、後付け注記として実体のあるもの）:

| ファイル:行 | 形 | 三値 |
| --- | --- | --- |
| `20260703_wiki-selfhosted-supersedes-adr-0011.md:20` | `⚠️ 取り下げ（withdrawn, 2026-07-05）` | ② 裁定（issue #66）の記録 |
| `20260709_composability-safety-net-gaps.md:17` | `triage 結果（2026-07-10, planning#16）` | ② トリアージ結果 |
| `20260709_conversion-job-query-reconvert-api.md:13` | `［2026-08-04］反映済み` | ② トリアージ結果（反映先を名指し） |
| `20260709_frontend-sc-screens-implemented-status.md:13` | `［2026-08-04］反映済み` | ② 同上 |
| `20260709_dotnet10-target-framework-deviation.md:17` | `［2026-08-05 / #497］この行は失効した` | ③ 自己是正（**元の行を `~~取り消し線~~` で残している**） |
| `20260709_sc11-wireframe-drawio.md:17` | `## 取り下げ（2026-08-05 / #504）` | ② 取り下げの判断の記録 |
| `20260804_frontend-migration-staging-interpretation.md:15,19,25,150` | 裁定の記録・反映済み | ② 利用者裁定の記録 |
| `20260805_sc05-07-admin-contract-gaps.md:128` | `［2026-08-10 / #543］提案 1 は解消した` | 実装の消化結果（①ではない） |
| `20260807_kit-cross-repo-issue-ref-check.md:23` | `上表の「未実施」は 2026-08-07 に解消した（誤りではなく、当時の記述である）` | ③ 自己是正（**元の表を残している**） |

残る行は**記録作成時からの本文の節見出し**（`## 裁定（2026-08-04・利用者）`・`### 実測（…2026-08-11）`・
`**planning#323 として起票済み**（2026-08-13）` 等）であり、後付け注記ではない。

### 2.4 軸 4 —— ブラケット ＋ 日付（`［YYYY-MM-DD` / `[YYYY-MM-DD`）を本文のどこかに持つ行

**24 行 / 19 ファイル**。軸 1 の 19 行 ＋ 軸 3 で拾った注記 4 行 ＋ **計画側文書の引用 1 行**
（`| 同 :125 | ［2026-08-04 更新］…` = 計画書の記述をそのまま表へ引いたもの）である。**新規は出ない。**

### 2.5 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `feedback/README.md` | **記録ではなく live な運用ガイド**である。検査器も `x !== 'README.md'` で外している |
| frontmatter（`---` で挟まれた先頭ブロック） | [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 の「可」の側。**本作業では触らない** |
| `planning/` 配下 | 別リポジトリ。本作業では編集しない（pin も動かさない） |
| `docs/specs/` の既存仕様書 | **確定済みの記録**であり書き換えない（`.claude/rules/traceability.repo.md`） |
| `docs/how-to/session-handoff.md` | 並行 OPEN の PR #789 と交差するため触らない |
| 軸 3 の 20 行 | 上記 2.3 のとおり。**①に当たるものが 1 件も無い**ため撤去漏れにならない |

## 3. 三値の当てはめ（**19 ブロック全数**）

**判定の決め手は「そのブロックが frontmatter の状態欄の変更を本文で言い直しているか」の一点である。**
裁定が凍結の射程を①に限定した以上、**①でないものはすべて対象外**である
（②③は裁定が名指しした代表例であって、網羅的な区分ではない）。

| # | ファイル | ブロック | 三値 | 根拠 | 扱い |
| --- | --- | --- | --- | --- | --- |
| 1 | `20260703_conversion-retry-vs-image-fallback.md` | `［2026-08-05 追記 / #497］` | ② | **「判定: accepted。提案 (a) が計画書へ反映済み」＋ 計画側 5 文書の確認表**。frontmatter は `accepted` としか言わず、**どの提案がなぜ通ったかは持たない** | 残す |
| 2 | `20260703_wiki-selfhosted-supersedes-adr-0011.md` | 同上 | ② | **「判定: rejected（取り下げ）」＋ 理由 ＋ ADR-0011 が `Superseded` でないことの確認表** | 残す |
| 3 | `20260704_plan-status-reflux-fr-adr.md` | 同上 | ② | **「判定: accepted（一部は個別判断で据え置き）」＋ 提案 1〜7 の採否の確認表** | 残す |
| 4 | `20260705_internal-service-auth-nfr-deviation.md` | 同上 | ② | **「判定: accepted」＋ 提案 1〜4 の到達状況** | 残す |
| 5 | `20260705_wiki-js-deployment-follows-adr-0011.md` | 同上 | ② | **「判定: accepted」＋ ADR-0011 の ABAC 強制点明確化の確認** | 残す |
| 6 | `20260709_composability-open-items-resolved.md` | 同上 | ② | **「判定: accepted」＋ 提案 1〜3 の反映先** | 残す |
| 7 | `20260709_composable-implementation-guide-upstream.md` | 同上 | ② | **「判定: accepted（`reflected` ではない）」＋ 廃語 `reflected` を採らない理由**。**語彙判断そのものが記録の中身**である | 残す |
| 8 | `20260709_dotnet10-target-framework-deviation.md` | 同上 | ② | **「判定: accepted」＋ ADR-0020 / 技術スタック表の確認表** | 残す |
| 9 | `20260709_sc11-wireframe-drawio.md` | 同上 | ② | **「判定: rejected（別解で解消）」＋ 語彙 `closed` を `rejected` へ揃えた経緯** | 残す |
| 10 | `20260710_repo-positioning-and-unit-structure.md` | 同上 | ② | **「判定: accepted」＋ ADR-0019 として確定した旨** | 残す |
| 11 | `20260803_ai-review-execution-permissions.md` | `## 追記（2026-08-03）` | ③ | 下記 §3.1 | 残す |
| 12 | 同上 | `## 追記 2（2026-08-03）` | ③ | 下記 §3.1 | 残す |
| 13 | 同上 | `## 追記 3（2026-08-03）` | ③ | 下記 §3.1 | 残す |
| 14 | 同上 | `## 追記 4（2026-08-03）` | ③ | 下記 §3.1 | 残す |
| 15 | `20260805_sc09-11-admin-ops-contract-gaps.md` | `［2026-08-09 追記］提案 1 …消化しきった` | ② | 下記 §3.2 | 残す |
| 16 | 同上 | `［2026-08-09 追記］提案 7 は「運用者も含む」と裁定され` | ② | 下記 §3.2 | 残す |
| 17 | `20260809_document-write-machine-client.md` | `［2026-08-13 追記 / #712］` | **①** | 下記 §3.3 | **撤去** |
| 18 | `20260809_sc06-manual-sync-role-classification.md` | `［2026-08-13 追記 / #712］` | **①** | 下記 §3.3 | **撤去** |
| 19 | `20260811_nfr-numbering-has-no-slot-for-meta-work.md` | `［2026-08-13 追記 / #712］` | **①** | 下記 §3.3 | **撤去** |

**撤去 3 件 / 残置 16 件。**

> **★ 見込みの「5 件撤去」は当たらなかった。** issue コメントは `#712` 3 ＋ ID 無し 2 = 5 を見込みとして
> 挙げていたが、**ID 無しの 2 件は本文を読むと①ではない**（§3.2）。**裏取りで 2 件減った。**

### 3.1 `20260803_ai-review-execution-permissions.md` の 4 ブロック（**断定せず本文と履歴で確かめた**）

**結論: ③ 自己是正の訂正。対象外（残す）。**

| 観点 | 実測 |
| --- | --- |
| **①か** | **違う。** 4 ブロックはいずれも `status` / `dispatched` / `updated` に一言も触れない。中身は **AI レビュー実走の権限拒否件数の実測**（`7 → 3 → 1` 件）と、**その都度キットへ送る追加提案**（`planning#168 へ追記する内容`）である。**frontmatter は当時 `open` で、`accepted` になったのは 4 日後の別コミット `bf5a2c37`（PR #610）である** —— 二重に述べようがない |
| **後から差し込んだ注記か** | **そうである。** ファイル追加は `1e629d0d`（PR #475・2026-08-04）、4 ブロックを入れたのは `1a16140a`（PR #480・同日）。**別 PR** なので「マージ前の同一 PR 内の推敲」には当たらない。**`git fetch --unshallow` して測った** |
| **③の条件（元の記述を消さない）を満たすか** | **満たす。** `git show --stat 1a16140a` = **237 insertions(+), 0 deletions**。**1 行も消していない**。各ブロックは前のブロックを「上記『追記』」「『追記 2』で塞いだ型」と参照しており、**前の記述が残っていることが読解の前提**になっている |
| **③に当てはまる中身か** | **当てはまる。** 追記 1 は本文が挙げた拒否の型が 3 つでは足りなかったこと（4 つ目 = 環境変数の前置き）を、追記 3 は追記 2 の対策の効果が不十分だったことを、追記 4 は追記 3 の「未許可の名指しリスト」に穴（`wc`）があったことを、**それぞれ自分の直前の記述に対する訂正として書いている** |

> **★ 断定を避けた点を残す。** 「実測ラウンドの継続記録」とも読め、その読みでは③ではなく**新しい記録の追加**である。
> **どちらの読みでも帰結は同じ**（①ではない ⇒ 対象外）ため、**帰結を変えない曖昧さとして残す**。
> baseline には③として記録し、**この揺れも `reason` に書く**。

### 3.2 ID 無しの 2 ブロック（`20260805_sc09-11-admin-ops-contract-gaps.md`）

**結論: ② 裁定・決定の記録。対象外（残す）。**

| ブロック | 中身 | 判断 |
| --- | --- | --- |
| 提案 1（タグ辞書の契約） | **「計画側は『契約を定める』側を選んだ」** という**計画側の選択の記録**と、それを消化した実装 issue の対応表（(a)(b) = #634 / (c) = #635 / BFF 書き込み口 = #640）。**「提案 2 は #535 で解消済み・残るのは提案 3〜7」**という**残件の確定**を含む | **①ではない**（frontmatter は `accepted` としか言わず、**8 提案のうちどれが片付いたかは持たない**）。中身は計画側の選択＝**決定の記録**であり②。**残す** |
| 提案 7（SC-10 の閲覧ロール） | **「『運用者も含む』と裁定され（Q19 / Q28）、#544 で実装した」** ——**裁定そのものの記録**。予告した (a)〜(d) を全て実施した旨と、**触っていない範囲**（`POST /dashboard/events`）の明示 | **②。残す。** 計画リポ `CLAUDE.md` §中間成果物 が「**裁定・決定の内容そのものは必ずリポジトリへ残す**」と定める対象そのものである（質問票 Q19 / Q28 はリポジトリに実体を持たない） |

> **★ 見込みとの差はここで出た。** issue コメントは ID 無し 2 件を「**要判断**」としつつ撤去見込み 5 件へ数えていた。
> **本文を読むと、2 件とも frontmatter に無い情報（どの提案がどう決まったか）を持つ**ため、①に当たらない。

### 3.3 `#712` の 3 ブロック

**結論: ① frontmatter の二重記述。射程内 —— 撤去する。**

- 本文は **「`status` を `open` → `triaged` へ是正した」** とだけ述べる。**frontmatter の状態欄の変更そのもの**である。
- **同一コミットで frontmatter と本文の両方を変えている**ことを実測した。`git show 62d8896f`（PR #715）:
  `-status: open` / `+status: triaged` / `-updated: 2026-08-11` / `+updated: 2026-08-13` と、**同じ内容の引用ブロックの追加**。
- **二重記述が腐ることも実測できた。** その後 `status` は `triaged` → `open`（`22ae1db4`）→ `accepted`（`d5c33e77`）と動いており、
  **本文の「`triaged` へ是正した」はどのファイルでも現在の frontmatter と一致しない**。
  `20260809_document-write-machine-client.md` に至っては frontmatter が `open` に戻っている。
- **失われる情報は無い。** 遷移の事実は frontmatter の値と `git log -p feedback/`、理由は
  [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) 決定 2 と [`docs/specs/20260813_issue-712_feedback-status-vocabulary.md`](20260813_issue-712_feedback-status-vocabulary.md)にある。

> **★ 1 点だけ迷いを残す。** `20260809_document-write-machine-client.md` のブロックは 4 行あり、
> 後半 2 行が **`check-feedback-dispatched.js` の既知の偽陽性が 1 件解消したこと**と
> **記録ファイル経路（3-a）が証拠と認められない件が未解決であること**に触れる。
> **これは状態の言い直しではない。** ただし**同じブロックの主題は `status` の是正**であり、
> ブロックの一部だけを残すと引用ブロックの主語が消える。**内容は
> [IADR-0184](../adr/IADR-0184_feedback-dispatch-checker-verbatim.md) 決定 2 / [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) と planning#319 知見 1 に残っている**ため、
> **ブロックごと撤去する**。この判断は報告に明記する。

## 4. 実施内容

### 4.1 [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 の改定（**旧条文を消さない**）

- **`Superseded` にはしない。** 決定の射程が狭まるだけで、決定そのものは生きている。
- 決定 2 の直後に**日付つき追記ブロック `［2026-08-16 追記 / #743］`** を置き、**三値の表**を明示する。
- `updated:` を `2026-08-16` へ前進させる。

### 4.2 射程内 3 ブロックの撤去

- 対象は §3 の #17 / #18 / #19。**本文から当該ブロックのみを消す。frontmatter は触らない。**

### 4.3 baseline の allowlist 化

**形を変える**（利用者決定「各エントリに残す理由を付ける」を、**理由と件数が離れない形**で満たすため）。

| | 旧 | 新 |
| --- | --- | --- |
| `files` の値 | `1`（数値） | `{ "count": 1, "verdict": "…", "reason": "…" }` |

- `verdict` の値域は **`triage-record`（②）/ `self-correction`（③）** の 2 語。
  **①（`frontmatter-duplicate`）は allowlist に載ってはならない**ため、値域から外す（載れば検査器が落とす）。
- `$comment` を **「まだ消せていない残件」から「裁定 planning#369 により残すと決めた記録の明示リスト」へ**書き直す。

### 4.4 検査器（`scripts/scripts.repo.test.js`）の追随

**弱めない。既存の 3 判定（新規混入 fail / 残件許容 / 消えたのに残っていれば fail）はそのまま**、
値の読み方を `.count` へ変え、**allowlist の健全性検査を足す**。

| 追加する検査 | 落ちる条件 |
| --- | --- |
| 各エントリが `count`（正の整数）/ `verdict` / `reason` を持つ | いずれか欠落・空文字 |
| `verdict` が値域内 | `frontmatter-duplicate` 等を書いた場合 |
| `#712` の追記ブロックが `feedback/` から消えている | 戻した場合（`#721` と同型の固定） |

- **`scripts/scripts.test.js` は触らない**（キット配布物）。

### 4.5 新たに誤りになる自分の記述の引き直し（規則 10）

「19 ブロック」「15 ブロック」を持つ live な記述を全走査し、追随させる。

| 箇所 | 対応 |
| --- | --- |
| `scripts/feedback-body-addendum-baseline.json` `$comment` | 全面書き換え（4.3） |
| `scripts/scripts.repo.test.js:7027` の「残る 15 ブロック」 | 裁定後の説明へ書き換え |
| `docs/adr/IADR-0191_…:118-119` の `［2026-08-15 追記 / #733］` | **書き換えない**（当時の記録）。**新しい追記ブロックが後継の数を述べる** |
| `docs/adr/IADR-0185_…` 決定 2 の補足 3 | **`#712` の追記を「規約が指定している形そのもの」と正当化している。** 撤去したので日付つき追記で失効を明記する |
| `docs/adr/IADR-0187_…` 決定 2 の補足 | **追随不要。** 既に `［2026-08-15 追記 / #733］`で誤りと明記済みで、`#721` の 11 件は①（`triaged` → `open` ＋ `dispatched`）であり**裁定後も射程内**のまま。撤去の判断は覆らない |
| `docs/specs/` の既存仕様書 | **書き換えない**（確定済みの記録） |

## 5. 受け入れ基準

- [x] [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 に射程を絞る追記があり、**旧条文が残っている**
- [x] **三値（①射程内 / ②対象外 / ③対象外＋条件）が明示されている**
- [x] **19 ブロックすべてに当てはめの判断と根拠がある**（本仕様書 §3）
- [x] 射程内と判定したブロックが `feedback/` の本文から消えている（frontmatter は不変）
- [x] baseline が allowlist として読め、**各エントリに残す理由が付いている**
- [x] **変異試験**: ①戻すと fail ②baseline に残した記録を消すと fail ③新規追記を混入させると fail

## 6. 検証（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）

`git add -A` → 検査器 → コミット → `check-doc-updated.js` / `check-commit-messages.js`（HEAD を読む）。

`node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（**companion を単体で叩かない**）/
`check-kit-sync` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
`check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-adr-numbering` / `check-reading-budget` /
`check-feedback-status-sync`。

**`CLAUDE.md` / `.claude/rules/` は増やさない**（着手前の実測 = **50,061 バイト / 余白 1,139 B**）。

## 7. 実測（変異試験と検証）

**変異試験**（いずれも `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。**すべて exit 1**）:

| # | 変異 | 落ちた検査 |
| --- | --- | --- |
| 1 | 撤去した `#712` ブロックを 1 つ戻す | ラチェット「新規に足された」 `…sc06…: 0 → 1` |
| 1b | 戻したうえで **allowlist にも載せて握り潰す** | 「`#712` の追記ブロックが残っている」（**allowlist では隠せない**） |
| 2 | allowlist に残した記録（`#497`）を本文から消す | 「baseline の減らし忘れ」 `…repo-positioning…: 1 → 0` |
| 3 | 新しい追記ブロックを混入（`## 追記 5（2026-08-16）` = **日付が後ろの形**） | ラチェット「新規に足された」 `…mesh-mtls…: 0 → 1` |
| 4 | allowlist から `reason` を消す | 「reason が無い / 短すぎる」 |
| 5 | `verdict` に `frontmatter-duplicate`（①）を書く | 「verdict が値域外」 |
| 6 | allowlist を旧形式（数値）へ戻す | 「値がオブジェクトでない（旧形式の数値のままか）」 |

**検証**（すべて exit 0）: `scripts.test.js` / `REQUIRE_REPO_TESTS=1 scripts.test.js`（**621 tests passed**）/
`check-kit-sync` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
`check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-adr-numbering` /
`check-feedback-status-sync`、コミット後に `check-doc-updated` / `check-commit-messages`（件名・PR タイトル）。

**必読規約の総量は不変**（前後とも **50,061 バイト / 余白 1,139 B**。`check-reading-budget` は
着手前と同じ 97.8% の warn）。
