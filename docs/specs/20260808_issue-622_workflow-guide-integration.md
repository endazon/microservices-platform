---
title: 作業仕様書 計画リポの実装作業運用ガイドを CLAUDE.md / AGENTS.md へ組み込み、計画 pin を前進させる
type: spec
status: done
related_ids: [NFR, IADR-0116]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - ../../planning/docs/ai-implementation-workflow-guide.md
related_specs: []
---

# 仕様書: 計画リポの実装作業運用ガイドを CLAUDE.md / AGENTS.md へ組み込み、計画 pin を前進させる

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#622** ／起点 ID: **NFR**（運用保守）
- 計画リポの正本: [`planning/docs/ai-implementation-workflow-guide.md`](../../planning/docs/ai-implementation-workflow-guide.md)（planning#294 で 2026-08-08 に確定）

## 背景

計画リポで実装作業の運用標準（フェーズ分割・並列実装・監査・裁定の流し方・メタ作業の統制）が確定した。実装セッションは自リポの `CLAUDE.md` / `AGENTS.md` しか読まないため、組み込まない限りガイドは効かない。本リポは必読規約が既に約 131KB あり、ガイド自身が定める総量予算（50KB 目安）を大きく超過しているため、**追加は要約に留め、削減は別 issue（#623）とする**。

## やったこと

1. **planning submodule の pin を `d9c2014` → `356e8c7`（ガイドを含む main の先頭）へ前進**。pin 前進は従前の慣行（#620）どおり独立コミットとした。
2. **`CLAUDE.md` に「実装作業の進め方（計画リポの運用ガイド）」節を追加**（15 行以内）。正本への参照と、拘束点（並列判定 = ファイル領域の非重複 / FIFO マージ / 同型変更の束ね〔IADR-0116 の限定例外〕 / フェーズ末監査の証跡必須 / 裁定の小分け / blocked 再検証 / 検査器追加は同型事故 2 回から / 必読規約 50KB 予算 / 人間の関与 3+1 点）の要約を記載した。
3. **`AGENTS.md` に同内容の 3 行要約を追加**し、詳細は `CLAUDE.md` の当該節へ委ねた。

## スコープ外

- 必読規約の総量削減（#623）
- ガイド本文の変更（計画リポ側の成果物であり、本リポからは参照のみ）

## 受け入れ基準の充足

| issue の基準 | 結果 |
| --- | --- |
| planning pin を `356e8c7` 以降へ前進 | ✅ `356e8c7`（planning main の先頭。ガイド初版 planning#294 を含む） |
| `CLAUDE.md` に節を追加し拘束点の要約を 15 行以内で記載 | ✅ 見出し込み 11 行（本文 9 行） |
| `AGENTS.md` に 3〜5 行の要約を追加 | ✅ 見出し除き 3 行 |

## 補記

- ~~pin 先 `356e8c7` のガイドのメタ情報は `状態: draft` のままである。状態表記の更新は計画リポ側の追随事項である。~~ ［2026-08-08 解消］計画リポ planning#298 で `fixed` 化がマージされ、本 PR の pin を `90f5251` へ前進させたことで pin 先の実体と「fixed」表記が一致した。

## 検証

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | OK |
| `node scripts/check-cross-repo-refs.js` | OK |
| `node scripts/check-plan-id-qualification.js` | OK |
| `node scripts/check-commit-messages.js`（対象コミット） | OK |
