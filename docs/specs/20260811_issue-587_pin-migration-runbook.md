---
title: 作業仕様書 — ピン留めモデルの版数移行手順と利用不能時の振る舞いを運用仕様に明記する（#587）
type: spec
status: done
related_ids:
  - FR-11
  - IADR-0112
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 作業仕様書: ピン版数移行手順と利用不能時の振る舞い（#587）

## 起点

- **FR-11**（LLM エグレス・ルーティング）。実装 ADR: **[IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md)** 決定 3
- 起点 issue: **#587**（`priority:should`。**#382 の後継**）
- 関連: **#443**（可観測性の再実装。**利用実績の記録で射程が重なる**）／ **#440**（フォールバック実装）

> **★ 値の基準時点は develop `b993aca`（2026-08-11 実測）である。**

## ★★ 母集合 —— 3 軸で引いた

### 軸 a: **運用仕様の現状 —— LLM のピンを扱う節が 1 つも無い**

```console
$ ls docs/operations/
llm-cost-monthly-review-runbook.md
local-sso-recovery-runbook.md
operations.md
```

**`ピン` / `フォールバック` / `利用不能` / `版数` を引くと 3 件当たるが、いずれも別物である** ——
`operations.md:93-94` は**コンテナイメージの digest ピン**、`:313` は**環境変数未設定時の既定値**である。

> **★ 素の当たり数を「該当箇所」と読まない**（#583 の型）。
> **LLM のモデルピンを扱う記述は 0 件**であり、**本 issue は新設である。**

### 軸 b: **`#587` / `#382` を全数で引く**

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#587\|#382'
docs/adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md
docs/adr/README.md
docs/how-to/session-handoff.md
```

**実装指示は無い。** IADR-0112 は決定 3 で「Stage 0 再検証を実弾解禁の必須ゲートとして課す」と定めており、
**本 issue はその手続きを運用仕様へ書き下ろすものである。**

### 軸 c: **実データ —— ピンは 8 用途、フォールバックは未実装**

`src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/appsettings.json`（**単一情報源**）:

| 用途 | ピン |
| --- | --- |
| `rag-answer` | `claude-sonnet-5` |
| `analysis` | `claude-fable-5` |
| `diagram-coding` | `claude-haiku-4-5` |
| `report-monthly` / `report-weekly` | `claude-opus-5` |
| `report-daily` | `claude-sonnet-5` |
| **`trade-decision`** | **`claude-sonnet-5`**（IADR-0112 決定 3） |
| `default` | `claude-opus-5` |

`Endpoints[claude-managed].Models` は **6 モデル**を許可する
（`fable-5` / `opus-5` / `opus-4-8` / `sonnet-5` / `sonnet-4-6` / `haiku-4-5`）。

**★ フォールバック機構は存在しない。** `LlmGateway` 配下で `fallback` を引くと 3 件当たるが、
**2 件は既定値を埋める private ヘルパ（`Or(value, fallback)`）**、
**1 件はルーティング試験の名前**（`Route_RagAnswer_PinsSonnet5AndDoesNotFallBackToDefault`。
**用途ピンが既定モデルへ落ちないこと**を固定する試験で、**可用性のフォールバックではない**）。

## 判断

### 判断 1: **本 issue は「文書」に閉じる。実装は持たない**

#587 の「やること」は 3 点とも**運用仕様への明記**である。
**フォールバックの実装（確定事項の表 2）は #440 の表題（「…とフォールバック実装」）が持つ。**
軸 c のとおり**未実装であり、本 issue で実装しない。**

### ★ 判断 2: **ピンの値を Runbook へ複写しない**

**軸 c の表を運用仕様へ書き写すと、必ず古くなる。**
実際に **#440 が `analysis` を `claude-fable-5` → Opus 5 へ改める予定**であり、
**本 PR の時点で既に「変わることが分かっている値」である。**

**`appsettings.json` を単一情報源として指し、列挙の仕方（コマンド）を書く。**
[[IADR-0141]]「参照点を 1 つに畳む」。**#583 で同型を 3 回、#626 でも同じ判断をしている。**

### ★ 判断 3: **「利用不能時は発注しない」を『障害ではない』と併記する**

#382 が明示した懸念 ——「**書かないと、運用時に善意でフォールバックが追加される**」。
**禁止の記述だけでは足りない。** **なぜ落とさないのかを併記しないと、親切心で破られる。**

### 判断 4: **確定事項の表 4（利用実績の記録）は #443 が持つ**

**#587 は文書に閉じる（判断 1）。** 表 4 は**メトリクスの実装**であり、
**#443（可観測性・運用の再実装 — LLM 利用実績の用途別・モデル別計測）の表題そのもの**である。
**二重実装にしないため、#443 が持つ。** 本 issue の受け入れ基準 4 に従い **issue コメントへ記録する。**

### ★ 判断 5: **AST の計画 ADR は `related_ids` へ入れず `plan_refs` の実パスで指す**

**初版は `related_ids` へ裸の `ADR-0011` を書いていた。これは誤りである** ——
**`ADR-0011` は両プロジェクトに実在し、意味が違う**（実測）。

| 名前空間 | 実体 |
| --- | --- |
| **`AST/ADR-0011`** | `ADR-0011_llm-model-pinning.md`（**本 Runbook が指したいもの**） |
| **MSP/`ADR-0011`** | `ADR-0011_wiki-engine.md`（**裸で書くとこちらへ解決される**） |

**衝突域は `ADR-0001`〜`ADR-0028` の 28 番**（MSP 45 件 / AST 28 件の重なり）。

#### 機械はこれを止めない

`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」の適用箇所は
「コード内コメント・IADR / 仕様書の**本文**・テスト名、コミット / PR 件名」であり、
**frontmatter は挙がっていない**。`check-plan-id-qualification.js` も
**型 B（AST 文脈で裸の ID）は偽陽性を避けるため意図的に検出しない**。

#### 先例に従う（発明しない）

```console
$ # related_ids に `AST/` 修飾を使った先例
0 件
```

**`AST/ADR-0011` という書き方に先例は無い。** 一方 **`IADR-0102` とその作業仕様書
（`20260725_ast-adr-0011_...`）は、いずれも AST の計画 ADR を `related_ids` へ入れず、
`plan_refs` の実パスで指している。** 実パスは曖昧さが無く、`check-doc-links.js` が
リンク切れを検出できる。**先例に合わせる。**

#### 母集合（同型を全数で引いた）

```console
$ # docs/ の Markdown 548 件 / related_ids の裸 ADR 266 件
  MSP 計画に存在しない番号                   : 0 件
  本文が AST/ADR-<同番号> を引くのに裸       : 1 件  ← 本 Runbook のみ
```

**同型は 1 件（本 PR 由来）である。**`CLAUDE.md`「検査器の追加は**同型の事故が 2 回起きたら**」に
従い、**検査器は足さない**。本 Runbook の frontmatter を回帰テストで固定するに留める。

### ★ 判断 6: **差分ベースの検査器は「コミット後」に走らせる**

本 PR は手元で緑・CI で赤になった（`check-doc-updated.js` が `operations.md` の
`updated:` 据え置きを検出）。**検査器の欠陥ではなく、走らせた順序の問題である** ——
本検査器は `git show HEAD:` を読むため、**コミット前に走らせると変更を一切見ない。**

**検証順序を「コミット → 検査器 → push」に固定する。**
これは検証順序の齟齬で手元が偽の緑になった **2 度目**である。
**射程外**（本 issue は文書に閉じる。判断 1）とし、**別 issue として起票する。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#587） | 確かめ方 |
| --- | --- | --- |
| 1 | `docs/operations/` に版数移行手順があり、Stage 0 再検証が前提と読み取れる | 新設 Runbook §版数移行。**回帰テストで「Stage 0」の記載を固定** |
| 2 | 「利用不能時は発注しない・障害ではない」が明記されている | 同 §利用不能時。**回帰テストで固定** |
| 3 | 提供終了の監視対象と手段が**用途ごとに**列挙されている | 同 §提供終了の監視。**単一情報源を指し、列挙コマンドを書く**（判断 2） |
| 4 | 表 4 を本 issue と #443 のどちらが持つか**コメントに記録** | 判断 4。**issue へコメントする** |
| — | （回帰）AST の計画 ADR を裸の ID で `related_ids` へ入れていない | 判断 5。**機械が止めない型**なので回帰テストで固定 |

**回帰テスト**: Runbook が受け入れ基準の 3 点を落としていないことを固定する
（**文書は消えても CI が赤くならない**ため。#546 / #665 と同じ型）。

## 射程外

- **フォールバックの実装** —— #440（判断 1）
- **利用実績メトリクスの実装** —— #443（判断 4）
- **ピンの値そのものの改定** —— #440。**本 PR は値を動かさない**
- **AST 側の Stage 0 再実行** —— 別リポジトリ（AST#296）。**本リポからは実行できない**
- **差分ベース検査器の「コミット前実行」対策** —— 判断 6。**別 issue として起票する**
- **`related_ids` への ID 修飾規約の明文化** —— 判断 5。規約（`.claude/rules/traceability.md`）の
  適用箇所に frontmatter を加えるかは**規約の改定であり、本 issue の射程外**
