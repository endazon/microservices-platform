---
title: 作業仕様書 — pin cff0e7b へのキット追随と status 語彙の上流裁定への差し替え（#721）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0185
  - IADR-0187
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§6 裁定は小さく高頻度に流す)"
related_specs:
  - "../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md"
  - "./20260813_issue-712_feedback-status-vocabulary.md"
---

# 作業仕様書: キット追随と `status` 語彙の差し替え（#721）

## 起点

- **NFR**（文書統制。メタ作業なので無採番。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 起点 issue: **#721**。実装 ADR: **[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md)**
- 出所: **PR #723（#716）の作業中**に `planning` を populate して走らせて検出した

> **★ 値の基準時点は develop `7a9e5e9` / planning pin `cff0e7b`（2026-08-14 実測）である。**

## 事象

**`planning` を pin どおり populate すると `node scripts/scripts.test.js` が落ちる。**

```
AssertionError [ERR_ASSERTION]: キットとバイト一致でなくなった（IADR-0115 決定 2）
→ 494 件中 107 件で停止（未捕捉例外）
```

**clean な develop（`7a9e5e9`）で再現する。** **#718 が pin を `2cf0795` → `cff0e7b` へ進めたが、キット追随を伴っていない。**

## ★★ 母集合 —— 実測で引いた

### 軸 a: **キットとの乖離は 2 ファイル**

| 対象 | 分類 | 差分（行） |
| --- | --- | ---: |
| `scripts/check-feedback-dispatched.js` | **A**（バイト一致であるべき） | **536** |
| `feedback/README.md` | B（キット＋固有デルタ） | **101** |
| **`feedback/TEMPLATE.md`** | **A** | **差分あり**（`dispatched:` / `planning_issue:` ＋ 説明コメント 21 行） |

> **★ `TEMPLATE.md` は #712 の時点では**バイト一致だった**。pin が進んで差分が生まれた。
> **「前回バイト一致だったから今回も」は成り立たない** —— 毎回測る。

### ★★ 軸 b: **上流の裁定が [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) 決定 1 と逆だった**

**#712 で環流した planning#323 は 2026-08-13 に裁定され、キットへ反映された。**
**採られた分割は本リポの決定と逆である。**

| | **IADR-0185 決定 1**（本リポ・#712 でマージ済み） | **キットの裁定**（planning#323） |
| --- | --- | --- |
| `status` が表すもの | **伝達したか** | **計画側の裁定がどこまで進んだか** |
| `open` | **未伝達** | **裁定がまだ下りていない**（**伝達済みでも `open` でよい**） |
| 伝達の記録 | `status: triaged` | **`dispatched:` / `planning_issue:` 鍵** |
| 値域 | open / **triaged** / accepted / rejected | open / **awaiting-decision** / accepted / rejected |

**キットは本リポの実測を名指しで引いている。**

> 実測では、実装リポジトリが `triaged`（旧称）を「**実装側が伝達した**」の意味で 7 件に付け、
> 計画側は同じ語を「**計画側が反映した**」の意味で使っていた。**1 つの語が両側で正反対の主体を指していた**

**つまり本リポが裁定を仰いだ論点そのものに、上流が別の答えを出した。**

### 軸 c: **新検査器を当てても警告は増えない**

**移行の前に、新旧の検査器を 39 件へ当てて突き合わせた。**

| | 件数 |
| --- | ---: |
| 対象 | **39** |
| 旧検査器の警告 | **0** |
| **新検査器の警告** | **0** |
| 判定が変わる記録 | **0** |

**→ 検査器の差し替えだけでは記録の是正は要らない。** 是正が要るのは**語彙**の側である。

### ★★ 軸 d: **`triaged` 11 件は全数が伝達済みだった**

**キットは「意味を確かめてから移行する」と要求している。確かめた。**

| 確かめたこと | 結果 |
| --- | --- |
| `planning/draft/feedback/` に写しがあるか | **11 件すべてに在る**（記録ファイル経路で伝達済み） |
| 検査器が認める証拠を持つもの | 9 件（残り 2 件は PR 経路のため旧検査器が読めなかった） |

**→ 11 件すべてに `dispatched: true` が真である。**

> **★ `20260719_headlamp-k8s-management-ui.md` は検査器の証拠も planning issue 番号も持たず、
> 一見「未伝達」に見えた。** 実際には**写しが在り、計画側が `ADR-0040` / `ADR-0042` を起こしていた**
> （どちらも `Proposed`）。**確かめずに `open` のまま置いていたら、伝達済みの記録を未伝達として扱うところだった。**

## 判断

### 判断 1: **上流の裁定に従う。IADR-0185 決定 1 を差し替える**

[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) 決定 1 が**キットを単一情報源**と定めている。
**本リポが裁定を仰ぎ、上流が答えを出した以上、上流に従う** —— **仰いだ側が結論を選べるなら、裁定の意味が無い。**

**[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) を起こし、IADR-0185 決定 1 を Superseded にする**（規約: 既存決定を覆す場合は新 IADR）。

### 判断 2: **分類 A の 2 ファイルはキット原文で上書きする**

`check-feedback-dispatched.js` と `TEMPLATE.md` は**バイト一致へ戻す。**

### 判断 3: **`README.md` はキット本文 ＋ CI ジョブ名の 1 点のみ**

**#712 で置いた暫定デルタ（`status` の語彙節）は撤去する** ——
**キット側に `## \`status\` の語彙（4 値）` が入ったので役目を終えた**（[IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) 決定 4 が定めた往復の完了）。

**残す固有デルタは CI ジョブ名（`doc-links` → `feedback-dispatched`）だけ**である（IADR-0115 決定 2 の 2）。

### ★ 判断 4: **`triaged` 11 件は `open` ＋ `dispatched: true` へ移行する。裁定の転記はしない**

**キットの移行指示に従う。**

> **「伝達した」の意味で付けていたなら `open` ＋ `dispatched: true` が正しく、`awaiting-decision` ではない。**

**本リポの `triaged` は「実装側が伝達した」の意味である**（IADR-0185 決定 1）。**したがって全 11 件が該当する。**

**`awaiting-decision` / `accepted` / `rejected` は書かない。** キットの表が
**「誰が書き換えるか」を計画側と定めている** —— **実装側が推測で埋めると、
`triaged` で起きた「両側が別の意味で同じ語を使う」の再発になる。**

| 例 | 実測 | それでも `open` にする理由 |
| --- | --- | --- |
| headlamp | 計画側が `ADR-0040` / `ADR-0042` を起票（**`Proposed`**） | **`awaiting-decision` は計画側が書く欄**。実装側が代わりに書かない |

### 判断 5: **既存 28 件（`accepted` 26 / `rejected` 2）は触らない**

**値域は新旧で共通**であり、**計画側が書いた裁定結果**である。**遡及して `dispatched:` を足さない**
（#706 / #707 / #710 / #712 と同じ判断。**規約が無かった時期の記録へ後から規約を遡及適用しない**）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#721） | 確かめ方 |
| --- | --- | --- |
| 1 | `planning` populate で `scripts.test.js` が全数 pass | **populate して実走**（本作業の起点） |
| 2 | 検査器がキット `cff0e7b` とバイト一致 | **既存の #710 回帰テストが固定** |
| 3 | `README.md` の固有デルタが CI ジョブ名の 1 点のみ | **#712 の回帰テストを改修して固定** |
| 4 | 警告件数が新旧で増えていない | **新旧突合で実測**（軸 c。0 → 0） |
| 5 | CI で検出できるか / できないかを明記 | **判断 6**（後述） |

**#712 の回帰テスト 6 件は旧語彙を固定しているので改修する** ——
`triaged` の行・`open` ＝ 未伝達・暫定デルタの環流先を検査していた。**これらは上流裁定で誤りになった。**

**#700 の語彙テスト**（`STATUSES`）も `triaged` → `awaiting-decision` へ差し替える。

### 判断 6: **CI では検出できないことを明記する（populate しない）**

`scripts-tests` は `planning` を populate しない（トークンが要る。`doc-links` と同じ既知の制約）。
**本作業では CI を変えない** —— **ワークフローの変更は起動条件・必須チェックに波及する**ため別の判断である。
**#712 で入れた notice**（未 populate 時に「この範囲は検査されていない」と出す）が引き続き役割を果たす。

## ★★ 追加で判明した制約 —— **分類 A の検査器群は相互依存で、1 本だけ進められない**

**検査器を新版へ上げると `scripts/scripts.test.js` が落ちる。全面追随を試みたら連鎖した。**

| 段 | 実測 |
| --- | --- |
| 1 | キット版 `scripts.test.js` → **`isBotLogin is not a function`**（`check-commit-messages.js` が旧版） |
| 2 | 検査器 4 本も同期 → **companion が `isBotAuthorName is not a function`** |
| 3 | **`PLAN_PROJECT` が `<project-name>` のまま**（キットの置換点。バイト一致コピーでは壊れる） |

**キット全体の乖離を全数で測った**（母集合を軸で引き直した）。

| | 件数 |
| --- | ---: |
| キットのファイル | **108** |
| バイト一致 | **65** |
| **内容差分** | **35** |
| 本リポに無い | 8（すべて `*.example.yml`。実名を持つため） |

**これは #713 そのものである。** **本 PR では `scripts.test.js` の 2 テストだけを暫定デルタで直し、
コメントで #713 を参照する**（[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 5）。

## 着地の実測

| | 値 |
| --- | --- |
| **`planning` populate での `scripts.test.js`** | **107/494 で停止 → 493 件 全数 pass**（#721 の起点が解消） |
| 分類 A のバイト一致 | **`check-feedback-dispatched.js` / `feedback/TEMPLATE.md` の 2 本が回復** |
| `feedback/README.md` の固有デルタ | **CI ジョブ名の 1 点のみ**（暫定デルタは撤去） |
| `status` 分布 | **`accepted` 26 / `open` 11 / `rejected` 2**（`triaged` **11 → 0**） |
| 検査器の警告 | **0 件**（新旧とも 0。移行で増えていない） |
| 文書系検査 7 本 | **すべて exit=0** |
| 変異試験 | **7 変異すべてを検出**（P1〜P7。`planning` populate 状態で実施） |

> **★ 変異試験は `planning` を populate した状態で回した** —— **PR #715 で「分岐に一度も入っていない
> 変異試験」を踏んだ教訓の適用である**（[IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) の追記）。

## 射程外

- **`scripts-tests` に `planning` を populate させるか** —— CI の変更。判断 6
- **AST 側の SSH.NET** —— #722
- **`IADR-0179` の適用範囲** —— #724
