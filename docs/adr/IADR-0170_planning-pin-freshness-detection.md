---
title: IADR-0170 計画 pin の鮮度は夜間に検知し、通知は「赤」ではなく issue で出す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0119
  - IADR-0169
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/README.md"
---

# IADR-0170: 計画 pin の鮮度検知（#589）

- 状態: Accepted
- 日付: 2026-08-11
- 決定者: claude（実装）

## 起点・関連

- **NFR**（着手ゲートの検知）。実装 issue: **#589**（出所は #572 施策 7。親 #454）
- 作業仕様書: [20260811_issue-589](../specs/20260811_issue-589_planning-pin-freshness.md)
- 制約: **[IADR-0119](./IADR-0119_replan-execution-order.md)**（着手条件＝前提 ADR が `Accepted`）
- 先例: **#507 / [IADR-0140](./IADR-0140_cross-repo-issue-ref-checker.md)**（既存の呼び出し口へ相乗りする経路）

## 文脈 —— **待っていたのではなく、気づいていなかった**

#572 の施策 5 は「A 群（裁定待ち）の**待ち時間そのものは縮められない**」と結論した。
**縮められないのは「回答が来るまで」であって、「回答が来てから実装側が気づくまで」は縮められる。**

**同型は 3 回起きている。** #548 / #560 / #589 は**いずれも人が気づいて起票した issue** である。

**着手時点でも乖離していた**（develop・2026-08-11 実測）:

```console
$ git submodule status planning        →  2cf0795
$ git -C planning rev-parse origin/HEAD →  14aed71   （3 コミット先）
```

| 差分 22 ファイルのうち、着手可否に効くもの | |
| --- | --- |
| `ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md` | **`Proposed` → `Accepted`** |
| `02_requirements/01_requirements.md` | 受け入れ基準が変わった可能性 |

**フィクスチャを作らずに、実データで検出を確かめられた。**

## ★★ #589 が置いた前提のうち、誤っているもの

#589 は「**CI 結線の制約**」として「`.github/workflows/` は GitHub App 権限で編集できない」と書き、
**案 1〜3 をその制約の下で並べた。誤りである。**

```console
$ git log --oneline -3 -- .github/workflows/
4dbd501 chore(NFR): 2 回持ち越された負債を回収する（… frontend-tests.yml の paths） (#671)
ce96eb8 chore(NFR): フロントエンドに prettier の format ゲートを新設し CI へ結線する (#619)
44a3141 feat(NFR,IADR-0134,IADR-0147): manualChunks の規則構成を守る機械検査を新設し CI へ結線する (#618)
```

> **★ これは [IADR-0169](./IADR-0169_cross-repo-ref-scan-beyond-markdown.md)（#583）で
> 直したばかりの前提と同じものである。** IADR-0140 決定 4 も「編集不可だから対象外」で
> 1 件を残していた。**同じ誤った前提が 2 つの issue の設計を歪めていた。**
> `CLAUDE.md` にも同じ記述が残っている（**本 ADR の射程外。#623 の減量と併せて扱う**）。

## 決定 1: **案 4 を採る —— 専用の夜間ジョブで検知する**

| 案 | 判定 | 理由 |
| --- | --- | --- |
| 案 1: pin の古さ（日数・コミット数）だけを見る | **不採用** | 重要な裁定か否かを判別できず、**pin が古いだけで鳴り続ける**（狼少年になる） |
| 案 2: pin を進める PR のときだけ中身を検査 | **不採用** | **検知したい場面（pin が古いまま放置）で走らない** |
| 案 3: セッション開始時（`setup.sh`）の警告 | **採用（従）** | ローカルで確実に目に入る。ただし CI には出ない |
| **案 4: トークン付きで planning を populate する夜間ジョブ** | **採用（主）** | **#589 が誤った制約の下で見落としていた選択肢** |

**新しいワークフロー（`planning-pin-freshness.yml`）にする。** 既存の
`doc-links-planning.yml` へ相乗りすると、**ジョブ名（`doc-links (planning)`）と中身が食い違い**、
かつ **doc-links の失敗に引きずられて pin 検査が走らなくなる**。目的ごとに分ける。

## ★ 決定 2: **通知手段は「赤」ではなく issue にする**

#589 は「**判定は警告であって失敗ではない**（赤にすると pin が古い間ずっと CI が止まる）」と
指定した。**正しい。だが夜間ジョブで警告を出すだけなら、それは誰も読まないログである。**

**`doc-links-planning.yml` 自身がこう書いている** ——
「夜間のみで運用する場合は、失敗に気付く導線（issue 自動起票・通知）を必ず用意すること。
**導線が無いと『誰も見ない赤』が常態化する**」。実際に **14 夜連続の失敗が放置された実績**がある。

**ジョブは緑のまま終わり、乖離を検知したら issue を起票する。**
既に open の検知 issue があれば**起票せず comment する**（`doc-links-planning.yml` と同じ型）。

> **★ #548 / #560 / #589 は 3 回とも「人が気づいて起票」だった。**
> **自動化しているのは検知ではなく、その手作業（起票）そのものである。**

## 決定 3: **着手可否に効く差分だけを鳴らす**

**鳴りすぎると読まれなくなる。**

| 鳴らす | 鳴らさない |
| --- | --- |
| `07_adr/ADR-*.md` の **`status:` の変化** | 本文だけの変更 |
| `02_requirements/*.md` / `05_screens/*.md` の変更 | `draft/` `tools/` `INDEX.md` `07_adr/README.md` |

**`Proposed` → `Accepted`（`adr-unblocked`）と、それ以外の status 変化を区別する。**
着手ゲートが外れるのは前者だけであり、**まとめると鳴らす理由が読めなくなる。**

実測の差分では **22 件中 19 件が「鳴らさない」側**へ落ち、理由は 2 件に絞られた。

## 決定 4: **fail-open。ただし「検査していない」と「乖離なし」を読み分ける**

| 場面 | 挙動 |
| --- | --- |
| planning が未 populate（PR CI） | **検査せず、その旨を出して exit 0。「乖離が無いことを意味しません」と書く** |
| planning の既定ブランチを取れない | 同上 |
| pin == HEAD | 「一致しています」と出す |
| **pin != HEAD なのに差分 0 件** | **exit 1**（配管が壊れている合図。ここだけ fail-open の例外） |

`scripts/setup.sh` からの呼び出しも **fail-open**（タイムアウト付き・失敗しても継続）。
**pin 検査よりセットアップを壊さないことを優先する。**

## 結果

- `scripts/check-planning-pin-freshness.js`（新規。自己試験 15 件・外部依存ゼロ）
- `.github/workflows/planning-pin-freshness.yml`（新規。夜間 ＋ 手動。**issue 起票の導線つき**）
- `scripts/setup.sh` へセッション開始時の警告を追加（fail-open）
- `scripts/scripts.repo.test.js` に回帰テストを追加

### 出力の分離

**1 回の実行で「注釈」と「素のテキスト」を別々の出口へ出す。**
Actions 上の stdout は `::warning::…`（注釈の書式）であり、**そのまま issue へ貼ると読めない。**
2 回走らせると fetch が 2 回走り結果がずれうるので、`PIN_REPORT_PATH` に素のテキストを書く。

## ★ 限界（確かめられないこと）

**夜間ワークフローの実走は確かめられない。** GitHub Actions をこの環境から起動できない。

**確かめたのは次までである:**

| 確かめた | 方法 |
| --- | --- |
| 実データの乖離を検出する | populate 済みの木で実行し、`adr-unblocked` 1 件 ＋ `gate-doc-changed` 1 件を得た |
| 未 populate で緑を返しつつ「検査していない」と出す | populate されていない worktree で実行 |
| Actions 用の注釈と素のテキストを出し分ける | `GITHUB_ACTIONS=true` / `PIN_REPORT_PATH` を与えて実行 |
| `GITHUB_OUTPUT` へ `drifted=true` を書く | 同上 |
| `setup.sh` が落ちない | 実行して exit 0 を確認 |

**確かめていない:** ワークフローが実際に起動すること、`actions/checkout` が submodule 側へ
資格情報を持ち越して `git -C planning fetch` が通ること、`gh issue create` が動くこと。
**「配線した」と「配線が働いている」は別である。**

## 射程外

- **pin を実際に進めること** —— 検知であって更新ではない（#586 / #599 の型）。**本 PR では pin を動かさない。**
- **`CLAUDE.md` の「`.github/workflows/` は編集不可」の記述** —— #623（規約の減量）の射程。
