---
title: 作業仕様書 — check-nul-bytes の変異試験を、作業ツリーを書き換えない一時のリポジトリで行う（#2482）
type: spec
status: done
related_ids: [NFR, IADR-0247, IADR-0690]
author: claude
created: 2026-11-07
updated: 2026-11-07
issue: "#2482"
plan_refs: []
---

# 作業仕様書 — check-nul-bytes の変異試験の一時のリポジトリ化（#2482）

> 本仕様書は実装着手前に作成した（着手 2026-11-07）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業。NFR）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（検査器の作法は IADR-0247、試験の形の前例は IADR-0690 の検査器の試験）
- 計画書リンク: なし

## 目的・背景

- `scripts/scripts.repo.test.js` の check-nul-bytes の変異試験は、追跡下の `docs/README.md` に NUL を 1 バイト足して `finally` で戻していた。試験が強制終了されると、作業ツリーに NUL が残る。
- #2480 で check-advisory-ids の試験に入れた形（一時ディレクトリに `git init`・`GIT_CEILING_DIRECTORIES`・走査の根の差し替え）にそろえる。

## 対象範囲

- 対象: `scripts/check-nul-bytes.js`（走査の根を差し替える口 `NUL_BYTES_ROOT` を足す）、`scripts/scripts.repo.test.js`（当該の変異試験）。
- 対象外: 検査器の判定・CI の配線・他の試験（挙動は変えない）。

## 設計

- `NUL_BYTES_ROOT` が与えられたときだけ、`REPO_ROOT` をそのディレクトリにする（`check-advisory-ids.js` の `ADVISORY_IDS_ROOT` と同じ形）。与えなければ従来どおり。
- 変異試験は、一時ディレクトリに `git init` したリポジトリを作る。走査件数の門（`MIN_SCANNED`）を越えるだけの小さなファイルと `docs/README.md` を追跡下に置く。CLI は `NUL_BYTES_ROOT` と `GIT_CEILING_DIRECTORIES`（一時ディレクトリの親）を与えて呼ぶ。
  - 対照: NUL を入れる前は exit 0。
  - 変異: `docs/README.md` に NUL を 1 バイト足すと exit 1 で、ファイル名と位置を名指しする。
  - 一時ディレクトリは `finally` で消す。本物の作業ツリーは書き換えない。

## 母集合（規則 9）

- 追跡下のファイルを書き換える check-nul-bytes の試験は 1 件だった（`scripts.repo.test.js` の当該の変異試験）。試験名は本ファイルの外から引かれていない（走査で 0 件）。

## 受け入れ基準

- [x] 変異試験が本物の作業ツリーを書き換えない（試験の後に `git status --porcelain` が空）。
- [x] CLI が NUL を含む追跡下のファイルを exit 1 で名指しすることを、一時のリポジトリで確かめる（対照つき）。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書の検査が緑。

## テスト方針

- 上記の変異試験そのもの。走査の根の差し替えが効かない（本物を走査する）と、本物は clean なので「NUL を入れたのに exit 0」で落ちる。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
