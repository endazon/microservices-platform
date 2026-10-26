---
title: 作業仕様書 — k8s-local-up.test.js の変異の写しを作業ツリーへ置かず、#2132 の IMAGE_TAG の試験の間欠的な反転を止める（#2291）
type: spec
status: done
related_ids: [NFR-20, IADR-0087, IADR-0605, IADR-0616]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2291"
---

# 作業仕様書 — 試験はリポジトリの作業ツリーへ書かない（#2291）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。develop の CI（static-checks）が間欠的に赤になる件の是正である。

## 現状（実測）

- #2132 の試験「IMAGE_TAG 未指定は … -dirty- … ビルドと helm が同じ値」は、`git status --porcelain` を 2 回読む。
  - 1 回目は起動器の中、2 回目は期待値を出す `scripts/lib/local-image-tag.sh` である。
- 並列の別シャードで走る #2195（IADR-0616）の変異試験 `runMutatedUp` は、起動器の写しを `scripts/.k8s-local-up.mutant-2195-<pid>.sh` に書き、終わると消す。
- 並列実行の最中に `git status --porcelain --untracked-files=all` を 50ms ごとに読むと、出てくるのはこの写しだけだった（4 並列・412 件）。
  - 2 回の読みの間に写しが現れるか消えると、`-dirty-` が片側だけに付く。
  - CI では両方の向きが出ている（develop 79a13d2b の run 40278406803 と、#2290 の 9b270554 の run 40281408349）。

## 方針

- 変異の写しを一時ディレクトリへ置く。`runUp` に `source` を足し、`bash -c 'f="$1"; shift; . "$f" "$@"' scripts/k8s-local-up.sh <写し> <cluster>` で走らせる。
  - 起動器は ROOT と lib の読み込みを `$0` だけから決める（`BASH_SOURCE` は使わない。実測）。そのため `$0` を実物のパスにしておけば、実物と同じ ROOT で走る。
- 回帰の防止として 2 つの守りを置く。
  1. 試験のプロセス（各シャード・直列の実行）で、REPO_ROOT の下へ書く `fs` の呼び出しを例外にする。一瞬だけ置くファイルも捕まえる。
  2. 親（並列の実行）で、全シャードの前後の `git status --porcelain` を突き合わせる。子の bash が書いたものも捕まえる。
- #2132 の主張は緩めない。skip・再試行もしない。
  - 試験の設計としては競合しない。作業ツリーへ書く者がいなければ、2 回の読みは同じ値になる。

## 受け入れ基準

- [x] 守り 1 を入れただけ（写しはまだ scripts/）の状態で、#2195 の変異試験が赤になる（修正前の赤）
- [x] 是正後、`node scripts/k8s-local-up.test.js`（並列）が全件緑で、実行中に作業ツリーの状態が変わらない（50ms ごとの `git status` で空）
- [x] 変異試験は引き続き変異を見分ける（写しを一時ディレクトリから走らせても主張が通る）

## 母集合（規則 9）

- `git grep -n "REPO_ROOT, rel\|writeFileSync(path.join(REPO_ROOT" -- scripts/` → `k8s-local-up.test.js` の `runMutatedUp` の 1 か所。
- `scripts/*.test.js` の `cpSync(path.join(REPO_ROOT, …), <dst>)`（helm 系の試験・scripts.repo.test.js）→ 写し先はいずれも一時ディレクトリ。外す。
- 並列実行の最中の `git status` の観測 → 上の写しだけ。

## 規則 11（窓）

- 時間差を扱う是正ではない。窓を閉じるのではなく、書く者を消す。表は描かない。
