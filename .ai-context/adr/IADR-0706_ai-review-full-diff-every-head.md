---
title: IADR-0706 AI レビューは同じ PR への 2 回目以後の push でも、この head の全差分を読み直す。前回のレビューの結果を流用しない（prompt で定め、試験で固定する。検査器は緩めない）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0538, IADR-0643]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - ../specs/20261108_ai-review-full-diff-every-head.md
---

# IADR-0706: 2 回目以後の push でも全差分を読み直す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08）

## 起点・関連

- 起点: #2509 の `claude-review` が 2 回目以後の push で 5 回続けて下限違反で落ちた（25/50・44/51・10/55・8/55・1/55）。
- 前提の実装 ADR: [IADR-0538](./IADR-0538_review-floor-verdict-check.md)（検証の下限と検査器）。変えない。
- 作業仕様書: [20261108_ai-review-full-diff-every-head](../specs/20261108_ai-review-full-diff-every-head.md)。

## コンテキストと課題

- `track_progress: true` の組み込みプロンプトは、過去のコメント（前回までの AI レビュー本文）を `<comments>` に、
  `pull request synchronize` を `<trigger_context>` に載せる（run 42394244730 のログで確認）。
- prompt の【検証の下限】は「変更ファイルは全部読む」だけで、前回の結果の流用を禁じていなかった。
  レビューは前回以後のコミットだけを読み、残りを「前回までのレビューで読んだ」として M に入れなかった。前回の本文がその読み方を書いているため、回を追って範囲が狭まった。
- 検査器は正しく落としていた。直すべきは、レビューに読み方を選ばせた prompt の空白である。

## 決定

1. prompt の【検証の下限】節の先頭に次を足す: 前回のレビュー（スティッキーコメント・過去のコメント）の結果を流用しない。push のたびに、
   この head の `git diff origin/develop...HEAD` の全ファイルを読み直す。前回以後のコミットだけを読むのは下限を満たさない。
   過去のコメントは前回の指摘の解消を確かめる手掛かりにだけ使い、M・確認の根拠・未読の理由に使わない。M はこのセッションで読んだファイルだけ。
2. `scripts.repo.test.js` が節の中の 5 つの語句を固定し、消す・節の外へ移すと赤にする（陰性対照つき）。
3. `check-review-verdict.js` は変えない。必須チェック名・起動条件・`track_progress` / `use_sticky_comment` も変えない。

## 代替案

- **`track_progress: false` にして過去のコメントを渡さない**: 採らない。組み込みプロンプトと投稿の経路（`update_claude_comment`）が変わり、
  検査器が読む投稿の形（IADR-0538）と進捗の表示が同時に変わる。影響の検証が今回の範囲を越える。prompt の指示で効かなければ次の手として残す。
- **検査器で「前回以後」の語を落とす**: 採らない。既に M + K < N で落ちている。足りないのは落とす力でなく、正しく読ませる指示である。

## 結果

- 良い: 2 回目以後の push でも下限を満たす読み方が明文になる。文が消えれば試験が赤になる。
- 悪い: prompt が 6 行増え、毎回の送信量がわずかに増える。毎 push で全差分を読むため、大きい PR では時間が増える（ジョブの上限 20 分の内）。
- 残余: prompt は指示であり強制ではない。それでも流用したときは従来どおり検査器が落とす（緑で通らない）。同型が続けば `track_progress` の見直しを起票する。
