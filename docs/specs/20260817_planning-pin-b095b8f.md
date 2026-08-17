---
title: 作業仕様書 — 計画 pin を b095b8f へ進め、traceability.md のブロッカーが上限から下限へ移ったことを記録する
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0173
  - IADR-0190
  - IADR-0192
  - IADR-0204
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md"
  - "../../planning/tools/impl-handoff-kit/repo-template/docs/ai-workflow.md"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 必読規約の予算 51,200 バイト)"
related_specs:
  - "20260817_issue-846_planning-pin-f216783-mcp-skill.md"
  - "20260817_planning-pin-767a9d48.md"
---

# 作業仕様書: 計画 pin `b095b8f` の追随

## 1. 起点となる ID（トレーサビリティ）

- **無採番 `NFR`**（キット追随・pin 更新＝メタ作業。`.claude/rules/traceability.md`「無採番 `NFR` を許す
  2 つの場合」の**場合 2**。計画側の非機能要件は稼働する製品の要件であり、工程の管理は別の軸である）。
- 関連: `IADR-0192`（キット追随の分類表）/ `IADR-0204`（分類 X のラチェット）/
  `IADR-0190`・`IADR-0173`（必読の恒久的な余白）。

## 2. 母集合の引き方（実測）

**走査基準**: `claude/pr-847-review-fixes-3fqg6o` `60920ed`。**pin**: `f216783` → `b095b8f`（**1 コミット**）。

```text
git -C planning log --oneline f216783..b095b8f
  b095b8f docs(kit): 改番の追随先へ計画リポを加え、CI 配線を別紙へ移して純減させる（planning#406） (planning#407)

git -C planning diff --stat f216783..b095b8f
  feedback/20260817_adr-0047-iadr-number-renumbered.md          |  6 +++++
  tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md | 15 ++++++------
  tools/impl-handoff-kit/repo-template/docs/ai-workflow.md      | 28 ++++++++++++++++++++++
```

**分類 A のファイルは 1 件も変わっていない。** 変わったのは分類 X の `traceability.md` と分類 B（種 2）の
`docs/ai-workflow.md` の 2 件であり、いずれも `check-kit-sync.js` のバイト一致検査の対象外である。
`feedback/` は kit の配布物ではない。**したがって pin を進めても分類 A の drift は発生しない。**

**除外したもの**: `feedback/20260817_adr-0047-iadr-number-renumbered.md`（計画リポ側の記録であり
本リポの追随対象ではない）。

## 3. この pin で何が変わったか

キット版 `traceability.md` が **25,963B → 25,207B（-756B）へ純減**した。中身は削除ではなく**別紙への移設**である。

| 移した内容 | 移設先 |
| --- | --- |
| `check-cross-repo-refs.js` の置換点の一覧と各々の失敗の形 | `docs/ai-workflow.md` |
| `pr-title.yml` の bot 判定（`user.type != 'Bot'` で弾いてはならない理由の詳細） | `docs/ai-workflow.md` |

あわせて改番の追随先に **5. 計画リポジトリが引く自番号** が加わった（fail-open。主たる担保は計画側の
機械検査。planning#395）。

## 4. 🔴 `traceability.md` のブロッカーは「上限の超過」から「下限の不足」へ移った

本リポの `traceability.md` は分類 **X**（期限つきの暫定）であり、「キット版が大きすぎて取り込めない」ことが
その理由であった。**pin を進めたことで理由の中身が変わったため、記録を引き直した。**

### 実測（pin `b095b8f` 時点）

```text
node scripts/check-reading-budget.js
  Claude Code: 50,132 バイト（予算 51,200 の 97.9%）
    CLAUDE.md                            19,981
    .claude/rules/traceability.md        24,592
    .claude/rules/traceability.repo.md    5,559

wc -c <キット版 traceability.md @ b095b8f>   ->  25,207
```

| 判定 | 値 |
| ---: | --- |
| 現在 | total **50,132B** / 余白 **1,068B** |
| 取り込みコスト | 25,207 − 24,592 = **+615B** |
| 取り込み後 | total **50,747B** / 余白 **453B** |
| **上限 51,200B**（#724） | ✅ **収まる**（従来は 302B 超過していた） |
| **下限 1,000B**（#730 / `IADR-0190`） | ❌ **547B 不足** |

**上限の超過は解消したが、下限ラチェットに掛かるため依然として取り込めない。** 下限は
`scripts.repo.test.js` の `#730: 必読の余白が確保した水準を割っていない`（`FLOOR = 1000`）が守っており、
**取り込むと CI が落ちる**。

> **この点は当初「今なら収まる」と誤って判断した。** 上限（51,200B）だけを見て下限ラチェットを見落とした
> ためである。**必読集合の増加は上限と下限の両方に照らす**こと —— 上限だけを見ると、
> `IADR-0190` が別紙化で作った余白を静かに食い潰す変更が通ってしまう。

### さらに: 取り込みは `docs/ai-workflow.md` への転記とセットである

キット版 `traceability.md` は移設した内容を**相対リンク（`../../docs/ai-workflow.md`）で参照するだけ**に
なった。**片方だけ取り込むとリンク先に中身が無い。** したがって分類 A へ戻す作業は次の 3 つを同時に行う。

1. 必読集合から **547B 以上**の減量（規範でない部分の別紙化。`IADR-0173` / `IADR-0190` 決定 2）
2. キット原文で `.claude/rules/traceability.md` を上書き
3. キット版 `docs/ai-workflow.md` の +28 行を本リポの同ファイルへ転記（分類 B・種 2 のデルタは保つ）

**失う規範は無い。** 移設された内容は本リポにも既に在り（`traceability.md` の現行版が本文で持っている）、
実装（`pr-title.yml` / `check-commit-messages.js` / `check-cross-repo-refs.js`）も揃っている。
**失うのはバイト一致だけである。**

## 5. 変更内容

| ファイル | 変更 |
| --- | --- |
| `planning` | gitlink を `f216783` → `b095b8f` |
| `scripts/kit-sync-classification.json` | `.claude/rules/traceability.md` の分類 X の理由を上記の実測へ引き直した |

**`.claude/rules/traceability.md` 自体は変更しない**（上記のとおり取り込めないため）。
**`docs/ai-workflow.md` も変更しない** —— 現行の `traceability.md` が移設対象の内容を本文で持っており、
先に転記すると**同じ規範を 2 箇所に持つ**ことになる（`CLAUDE.md` 冒頭「同じ必読集合の中で二重に持たない」）。
**3 点は同時に行う。**

## 6. 検証

```text
node scripts/check-adr-numbering.js           OK                                   exit=0
node scripts/check-doc-links.js               OK: 691 件                           exit=0
node scripts/check-cross-repo-refs.js         OK: 走査 1780 / 除外 73              exit=0
node scripts/check-plan-id-qualification.js   OK: 1452 件                          exit=0
node scripts/check-reading-budget.js          warn 50,132B / 51,200B（97.9%）      exit=0
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   ✓ 651 tests passed
```

### 検査していない範囲

- **`node scripts/check-kit-sync.js` は planning submodule が未 populate のため skip**（`warn` / exit 0）。
  **分類 A のバイト一致は本環境では検査されていない。** ただし §2 のとおり pin 間で分類 A のファイルは
  1 件も変わっていないため、drift が新たに生じる余地は無い。判定は CI を正とする。
- `check-commit-messages.js` の**計画 ADR 実在性検査も同じ理由で skip**（実効しているのは IADR 検査のみ）。

## 7. 未了

| # | 内容 | 理由 |
| --- | --- | --- |
| 1 | **必読集合の 547B 以上の減量と、`traceability.md` の分類 A への復帰** | 別作業（#793 系）。本 PR の射程外。上記 §4 の 3 点を同時に行う必要がある |
