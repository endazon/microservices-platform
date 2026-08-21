---
title: 作業仕様書 — 計画 pin を ce9abd2 へ進め、キット同期 11 件を追随する
type: spec
status: done
related_ids:
  - NFR
  - ADR-0036
  - ADR-0046
  - IADR-0115
  - IADR-0170
  - IADR-0193
  - IADR-0198
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
related_specs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0198_kit-delta-fifth-kind-and-review-verdict.md"
---

# 作業仕様書: 計画 pin を `ce9abd2` へ進め、キット同期を追随する

## 1. なぜ進めるのか

**#516 が着手可能になったが、その根拠が本リポから読めない。**

私が出した裁定依頼 2 件に計画側が回答し、いずれもマージされた。

| 裁定依頼 | 回答 | 実装側への影響 |
| --- | --- | --- |
| planning#344（ABAC の `owner` が実データ 0 件） | PR planning#345（`a9b847e`） | **#516 の着手条件が確定した。** `09_datasource-connectors.md` に §システム投入経路での `owner` / `department` が新設された |
| planning#346（Wiki.js の個人スコープ） | PR planning#347（`dca9891`） | **`ADR-0046` が新設**され、個人資料を Wiki.js へ同期しないと確定（案 A） |

**pin が `130a109` のままだと、#516 の作業仕様書が参照すべき節が本リポに存在しない。**
根拠を読めない状態で実装すると、pin が存在する意味が無くなる。

## 2. 母集合

### 2.1 pin の差分

`130a109..ce9abd2` は **10 コミット**。

```console
$ git -C planning log --oneline 130a109..ce9abd2 | wc -l
10
```

### 2.2 計画 ID レンジの引き直し（`.claude/rules/traceability.md` の義務）

**新 pin で実測した。**

| 種別 | 旧（走査基準 `cff0e7b`） | 新（走査基準 `ce9abd2`） | 差 |
| --- | --- | --- | --- |
| `FR` | `01..22` | `01..22` | 不動 |
| `UC` | `01..11` | `01..11` | 不動 |
| `SC` | `01..21` | `01..21` | 不動 |
| **`ADR`** | **`0001..0045`** | **`0001..0046`** | **+1（`ADR-0046` 新設）** |

**欠番は無い**（46 ファイル・連番）。

### 2.3 キット同期の母集合（**11 件**）

`node scripts/check-kit-sync.js` を新 pin に対して実行して引いた。**記憶で挙げていない。**

| 区分 | 件数 |
| --- | --- |
| **drift**（分類 A なのにバイト不一致） | **4** |
| **unclassified**（キットに増えて分類表に無い） | **7** |

**逆方向も確認した** —— 分類 B の 13 件のうち、新キットとバイト一致になったものは **0 件**である
（環流済みと書かれた項目も、キットは同じ形では取り込んでいない）。

## 3. 変更内容

### 3.1 drift 4 件 —— **いずれもキット側が先に進んでいる**

**本リポに意図した固有デルタがあるのではなく、キットの更新に追随できていなかった。** 原文を取り込む。
**変化量は `git diff --numstat`（追加 / 削除）で数えた。**

| ファイル | キット側の変化 |
| --- | --- |
| `.claude/hooks/check-impl.js` | **+12 / -1**（`docs/` 直下のフロントマター非保持ファイルの扱いを明文化） |
| `scripts/check-feedback-dispatched.js` | **+1 / -1**（表記の是正）（`planning issue #217` → `planning#217`）。**planning#349 の依頼 1 がキット側で解決した** |
| `scripts/check-permission-denials.js` | **+319 / -16**（引用符内の `\|` を判定する字句解析の追加） |
| `scripts/commit-allowlist.json` | `_note` / `_categories` に**固有デルタ第 5 種**の説明が入った（裁定 planning#339） |

**`commit-allowlist.json` は分類 A のまま丸ごと取り込んだ。**
着手時は「本リポの `allow` には実在エントリがあるので `_note` だけ取り込んで B へ移す」と書いていたが、
**実測すると `allow` は空だった**（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) の第 1 ラウンドで空へ戻し済み）。したがってキット原文と
バイト一致し、**A のままで矛盾しない**。

> **キット新版の `_note` は「この `allow` 配列は『キットが追記を委ねている欄』＝固有デルタ第 5 種であり、
> 埋まっていること自体は追随漏れではない」と述べている**（裁定 planning#339）。
> **将来 `allow` が埋まった時点で B へ移す**ことになる。いまは空なので A である。

### 3.2 unclassified 7 件

| ファイル | 分類 | 理由 |
| --- | --- | --- |
| `docs/templates/how_to_template.md` | **A** | 差分は本リポが足した `#675 / [[IADR-0167]]` の出典注記 10 行のみ。**キット新版は `status` 値域検査に関する案内を持つ上位互換**であり、出典は `IADR-0167` 自身が保持する。原文を取り込む |
| `docs/templates/runbook_template.md` | **A** | 同上（差分 8 行） |
| `scripts/check-kit-sync.js` | **B**（X） | **本リポが originate した**（#734）。キットが後追いで**別実装**を持った（`git diff --numstat` で +60 / -265） |
| `scripts/check-planning-pin-freshness.js` | **B**（X） | **本リポが originate した**（#680 / [IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md)）。**キット版は目的が別物**（pin の経過日数を見るオフライン方式。本リポ版は着手可否の分類を持つ）。+347 / -122 |
| `scripts/check-feedback-status-sync.js` | **B**（X） | **本リポが originate した**（#738 / [IADR-0193](../adr/IADR-0193_feedback-status-sync-check.md)）。キットが後追いで**別実装**を持った（+73 / -155）。追跡: #751 / #736 |
| `scripts/check-review-verdict.js` | **A** | **キットの新規配布物。本 PR で採用する**（利用者裁定 2026-08-15）。CI 配線も行う |
| `AI_SETUP.md`（既存 B の是正） | **B（5）** | **裁定 planning#339 が第 5 種の代表例として名指ししている。** 当初「第 5 種にも当たらない」と書いたのは 3 つの一次資料に反していた（[IADR-0198](../adr/IADR-0198_kit-delta-fifth-kind-and-review-verdict.md) 決定 1）。あわせて**キットの 2 ブロックの追随漏れ**も取り込み、残差を `- [x]` の 1 行だけにした |
| `scripts/kit-sync-classification.example.json` | **notApplicable** | 本リポは実体 `kit-sync-classification.json` を持つ。`.example` は雛形であり対象外（既存の `*.example.yml` 8 件と同じ扱い） |

> **B の 3 件は「追随漏れ」ではない。** 向きが逆で**本リポが先**であり、キットは後追いである。
>
> **★ ただし種別は `3`（本リポにしか存在しない）ではない**（PR #750 の ADR 監査が指摘）。
> **3 件とも現キットに実在する**ため、第 3 種の定義に当たらない。「**本リポ originate ＋ キットが別実装で後追い**」は
> [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) 決定 2 の 4 種にも、planning#339 が新設した第 5 種にも当たらない**新しい型**である。
> したがって **`X`（4 種に当たらない）＋ 追跡先**とした。追跡先は **#751**（上流の `--require-planning` の追随）と **#749**。
>
> **★ この表と `kit-sync-classification.json` の分類値がずれたことがある**（PR #750 の AI レビューが検出）。
> **3 件を X へ改める作業で、表の 1 行（`check-feedback-status-sync.js`）だけを取りこぼした。**
> 実体は JSON 側であり CI は緑のままだったため、機械では出ない。
> **是正後は目視ではなく、JSON の値と表の値を突合するスクリプトで確認した。**
> **同型は今回が 1 回目なので検査器は置かない**（`CLAUDE.md`「同型の事故が 2 回起きたら」）。記録に留める。

> **★ 「キット原文で上書きすると退行する」は `check-planning-pin-freshness.js` については弱い**（AI レビューの 🟡）。
> **キット版は `origin/HEAD` 等のリモート参照に一切依存しない別設計**であり、**#749 が踏んだ不具合の型を構造的に持たない**。
> **#749 の対応では、キット版への差し替えも比較検討の俎上に載せる**（同 issue へ追記済み）。

### 3.3 `check-review-verdict.js` の採用と CI 配線

**「緑だが検査されていない」を止める検査器**である。`claude-code-action` は AI が判定
（🔴 / 🟡 / 🟢）を投稿しないままターンを終えても `success` で終わるため、
**「レビュー済み・指摘なし」と読まれるが実際には何も判定されていない**状態がマージを通過する。

**`check-permission-denials.js` では捕まらない** —— あちらは「ツールを 1 つも実行できなかった」形を見る。
本件は**ツールは動いており最後の投稿だけが無い**。入力（実行ログ）と配線は同じで `parseEvents` を借りる。

### ★ 配線先は `claude-code-review.yml` の **1 本だけ**である（PR #750 の AI レビューが 🔴 で指摘）

**当初は 2 本のワークフローへ配線した。誤りだったので撤去した。**

根拠にしたのはキットのヘッダの「**2 本セットで配布すること**」だが、**これはスクリプト 2 本の配布**
（`check-review-verdict.js` が `check-permission-denials.js` の `parseEvents` を借りるため単独では動かない）
**を指しており、両方のワークフローへ配線せよという意味ではない。** 機械的に読み替えていた。

**実測すると `claude-coding.yml` へ配線してはならない。**

| | `claude-code-review.yml` | `claude-coding.yml` |
| --- | --- | --- |
| 用途 | **自動 AI レビュー** | **`@claude このタスクを実装してください` への応答**（`docs/ai-workflow.md` §2） |
| `prompt:` | **持つ**（`:244`。判定 🔴 / 🟡 / 🟢 の形式を指示する） | **持たない**（本文駆動。`--append-system-prompt` にも判定を出す指示は無い） |
| 判定見出しの有無 | 出る | **出ない**（実装タスクは判定を投稿しない） |

`check-review-verdict.js` は判定見出しが無ければ `ALLOW_MISSING_VERDICT=1` が無い限り **exit 1** で落ちる（`:246-263`）。
したがって配線したままだと、**正常に完了した実装タスクでもこのステップだけが恒常的に赤くなる。**

`ALLOW_MISSING_VERDICT=1` を付けて黙らせる案は採らない —— **常に警告だけを出すステップは意味が無く、
「検査している」という誤った印象だけを残す**（本検査器がまさに止めようとしている型である）。

## 4. 併せて起票すること —— pin 鮮度チェッカの盲点

**`check-planning-pin-freshness.js` が、ADR-0046 が新設された状態で `exit 0` を返し
「着手可否に効く変更はありません」と報告した。**

原因は比較対象である。同スクリプトは submodule 内の `origin/main` を見る（`:192-194`）が、
**submodule の `origin` は GitHub ではなくローカルの隣接クローン `/home/user/project-planning` を指す**。
そのクローンの `main` は誰も更新しないため古いままで、**pin より後ろ**にある。
結果、**新しい pin と古い ref を逆方向に比較して「差分 66 件はすべて draft / tools / 索引」と報告した。**

**これは #747 と同型である**（検査器が黙って何も検査していない）。**本 PR の射程外**として別 issue に切り出す。

## 5. 受け入れ基準

- [x] `git -C planning rev-parse HEAD` が `ce9abd2` である
- [x] `node scripts/check-kit-sync.js` が exit=0（A 73 / B 16 / C 17 / 対象外 9）
- [x] `node scripts/scripts.test.js` が exit=0（**519 passed**）
- [x] `.claude/rules/traceability.md` の ADR レンジが `0001..0046`・走査基準が `ce9abd2` である
- [x] `check-review-verdict.js` が **`claude-code-review.yml` の 1 本へ**配線され、`--self-test` が通る（12 件）
- [x] pin 鮮度チェッカの盲点を別 issue として起票した（#749）

### 途中で追加で発火した追随（いずれも設計どおり）

| 検査 | 内容 |
| --- | --- |
| **検査器の母集合ラチェット** | 35 → **36 本**（`check-review-verdict.js` の採用。`scripts.repo.test.js`） |
| **`check-feedback-status-sync`** | **6 件の `status: open` → `accepted`**。**pin が進んで計画側のトリアージ結果が見えるようになったため**（planning#342 が環流 5 件を、planning#347 が Wiki.js の 1 件を裁定した）。frontmatter の状態欄は書き換え対象である（[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) ／ 規約 §#717） |
| **必読 2 ファイルの予算** | ID レンジの注記を加筆したら**余白が 927B まで減り下限 1000B を割った**。**条文の注記を短くし、経緯は条文へ書かず別紙へ書いた**（既存本文を別紙へ移したのではない。条文は正味 +14B で、余白は 1,005B へ回復した）。[IADR-0173](../adr/IADR-0173_annex-extraction-keeps-heading-stubs.md) の設計どおり |

## 6. この作業で扱わないこと

| 対象 | 理由 |
| --- | --- |
| **#516 の実装** | **本 PR は pin を進めるところまで。** 1 issue = 1 PR（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1）。#516 は次の PR |
| **`ADR-0046` に伴う実装の追随** | 個人資料を Wiki.js へ同期しない決定は **#449 / #451（大玉）**の射程 |
| **pin 鮮度チェッカの是正** | §4 のとおり別 issue |
