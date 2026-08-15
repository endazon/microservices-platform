---
title: 固有デルタ 4 種に「キットが空欄・空配列で配り各リポが埋める」型が無い（AI_SETUP.md / commit-allowlist.json）
type: plan-feedback
status: accepted
category: 新たな制約(ADR要)
related_ids: [NFR, IADR-0115]
source_repo: microservices-platform
source_ref: "claude/issue-response-handoff-2hl25v / docs/specs/20260814_issue-736_kit-reflux.md（実装側 issue #736。2 件目は #739 で検出）"
author: Claude（実装）
created: 2026-08-14
dispatched: true
planning_issue: 339
---

# 環流（裁定依頼）: 固有デルタ 4 種に不足がある

## 事象

`IADR-0115` 決定 2 の 4 種に当たらないのに、**キット自身が「各リポで埋めること」を前提に配っている**ファイルが 2 件ある。

| 実例 | 根拠 |
| --- | --- |
| `AI_SETUP.md` | チェックボックス未選択で配る。**どのリポも必ず選択する**＝ 100% デルタが生じる |
| `scripts/commit-allowlist.json` | キットの `_note` が「配布時は空であり、各リポで必要になった分だけを追加する」と明記 |

## 実害

分類表で「4 種のどれか」を名乗らせる運用にすると、この 2 件が名乗れない。
実装側は暫定的に `X`（4 種に当たらない）＋ 追跡先必須で逃がしたが、
**`X` は本来「環流するか期限つき暫定にする」ためのもの**で、恒久的に正しいデルタを置き続けると分類の意味が薄れる。

**`X` が増えること自体は測定値として有用**（13 件 → 7 件へ減った）。
**「永久に `X` のまま正しい」ものが混ざると、その測定値が読めなくなる。**

## 提案

A（第 5 種を足す。素直だが `IADR-0115` の改定が要る）／ B（分類 C 扱い。土台の追随が効かなくなる）／
C（現状維持。追跡先が永久に閉じない issue になる）。**実装側は A が素直と考えるが、キットの規約なので上流の判断に従う。**

## 併せて報告: `commit-allowlist.json` の説明文に squash マージ下の限定が無い

category B は「プッシュ済み・force push 禁止のため書き換えられないコミット」を対象と説明しているが、
**規約 2「統合ブランチから到達可能であること」と両立しない場合がある。**
squash マージのリポジトリでは PR ブランチの SHA が統合ブランチへ載らないため、**登録すると必ず幻 SHA になる。**

実装側で実測した（#739）—— entry あり = `commit-messages` 緑 / `scripts-tests` 赤、entry なし = その逆。**両立しない。**
**`_note` か `_categories.B` へ「対象は既に統合ブランチに在るコミットに限る」と明記されたい。**
