---
title: 作業仕様書 — `Closes #NNN` の射程を明記し、機械化しない理由を残す
type: spec
status: done
related_ids:
  - NFR
  - IADR-0139
  - IADR-0207
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 機械化の是非は、まず母集合を測ってから決める)"
  - "../../planning/draft/feedback/20260816_closes-nnn-mechanization-scope.md"
related_specs:
  - "../adr/IADR-0139_domain-bundled-contract-prs.md"
  - "../adr/IADR-0207_pr-title-trailing-number-must-be-own.md"
---

# 作業仕様書: `Closes #NNN` の射程と、機械化しない判断

## 1. 起点となる ID（トレーサビリティ）

- **無採番 `NFR`**（文書統制・PR 運用。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 起票: [#812](https://github.com/endazon/microservices-platform/issues/812)（波 6 末クロス監査の 🟡 を、
  #799 の作業中に**引き直したら規模が桁違いだった**ため裁定依頼として切り出した）
- 裁定: **planning#388**（利用者 2026-08-16）。計画側の反映は
  [planning#389](https://github.com/endazon/project-planning/pull/389)

## 2. 母集合（当時の実測。**本 PR では引き直していない**）

監査は「スカッシュ本文に `Closes` が無い PR が **2 本**あった（#777 / #789）」と指摘した。
**指摘は正しい。しかし引き直すと規模が違った**（2026-08-16 / develop `d121ee8c` 時点）。

| 引き方 | 母集合 | `Closes` / `Fixes` / `Resolves` ＋ `#NNN` が**無い**もの |
| --- | ---: | --- |
| `develop` に着地したスカッシュ件名の本文 | 425 | **388 件（91%）** |
| merged PR の本文（GitHub API） | 431 | **267 件（62%）** |

**「2 本」は波 6 の範囲だけを見た数であり、全体では常態である。**

**計画側も独立に引き直し 91% を再現した**（planning#389。件数差は測定後に積んだコミット分）。
**本 PR は数値を再取得していない** —— 裁定は済んでおり、値は
[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の追記へ出典つきで記録した。

## 3. 決めたこと

### 3-1. 射程 —— 決定 3 は「全 PR に `Closes` 必須」ではない

定めているのは**束ねた PR の issue 別内訳**である。#777 / #789 は**どちらも束ねた PR ではない**。
**射程が違う。**

### 3-2. 機械化しない

| # | 理由 |
| --- | --- |
| 1 | planning#313「**検査器にしてよいのは例外が無いと言い切れる規則だけ**」に該当しない。正当な例外が 4 種（CHANGELOG 自動更新 / dependabot・renovate / issue を持たない小修正 / **裁定依頼そのもの**） |
| 2 | **目的は既に達成されている。** `Closes` の目的は issue の自動クローズであり機能している。欠けているのは「機械で強制する段」だけである |

**母集合を測る前に機械化していれば、既存の 9 割を違反として上げる検査器ができていた。**
本リポの先例どおり**検査そのものが外される**結末になる。

### 3-3. 将来機械化する場合の条件を残す

事前防止（PR 本文＝マージ前に止められる唯一の面）と事後検知（着地スカッシュ本文＝不変だが直せない）の
**両方**を見る。既存履歴は **baseline で除外**する。**変異試験で「壊すと落ちる」を実測**する。

**この 2 面の使い分けは #799（[IADR-0207](../adr/IADR-0207_pr-title-trailing-number-must-be-own.md)）で
確かめた構図と同じである。**

### 3-4. 「2 回起きた」は十分条件ではない

`CLAUDE.md` の「同型の事故が 2 回起きたら」は**必要条件**である。
planning#296（2 回の閾値）と planning#313（例外が無いと言い切れるか）を**併せて判定する**。
**本件は前者を満たし後者を満たさなかった。**

## 4. 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `docs/adr/IADR-0139_*.md` | 決定 3 へ `［2026-08-17 追記 / #812］`。**旧条文は消さない**。射程・実測・機械化しない理由・将来の条件を同じ場所へ |

**新 IADR は起こしていない。** 決定 3 の**射程を明記する追補**であり、決定内容を変えていない。
**必読規約（`traceability.repo.md`）にも足していない** —— 機械化しないと決めた以上、
毎セッション読ませる規範ではないためである（総量予算 51,200 B の余白は
[#847](https://github.com/endazon/microservices-platform/pull/847) /
[#848](https://github.com/endazon/microservices-platform/pull/848) 後で 465 B しかない）。

## 5. 検証（実測）

```text
node scripts/check-doc-links.js       exit=0
node scripts/check-adr-numbering.js   exit=0
node scripts/check-reading-budget.js  warn 50,132 バイト（97.9%・本ブランチは必読規約を変更していない）
```

## 6. 未了

- **将来機械化する場合の実装**（3-3 の条件つき）。**本 PR では実施しない。**
