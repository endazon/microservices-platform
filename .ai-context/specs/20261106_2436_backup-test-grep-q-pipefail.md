---
title: 作業仕様書 — backup.test.sh の保持の判定（printf | grep -q）が、source で受け継いだ pipefail の下で偶発的に反転する（#2436）
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-11-06
updated: 2026-11-06
plan_refs: []
related_specs:
  - 20261024_2200_grep-q-pipefail-sigpipe.md
issue: "#2436"
---

# 作業仕様書 — source で受け継いだ pipefail の下の `| grep -q`（#2436）

> 本仕様書は実装着手前に作成した（着手 2026-11-06。develop `4b90c510`）。起点は NFR（メタ作業。試験と検査の統制）。

## 事象と原因

- `deploy/local/platform-backup/script/backup.test.sh` の T-1560-20（`prune_list` の `-keep` の 7 年の境目）が、手元で 1 回だけ赤になった（#2406 の作業中）。
  `prune_list` は固定の日付を引数で受け、現在時刻・TZ・ファイルを読まない純関数である。結果は毎回同じである。
- 判定の側の `has_del` / `has_del4` は `printf '%s\n' "$DEL" | grep -qx -- "$1"` である。
- 試験は 661 行で `BACKUP_LIB=1 . "$HERE/backup.sh"` を source する。`backup.sh` は `set -o pipefail` を立てるので、それより後の行は pipefail の下で走る。
- bash の `printf` は 1 行ごとに write する（strace で 40 行に 41 回）。`grep -q` は一致した時点で終わり、まだ書いている `printf` が SIGPIPE で落ちる。
  pipefail はパイプライン全体を失敗にするので、「消す一覧に在る」が「無い」に反転する。
- 実測: 同じ形を 3000 回回すと、何も走っていない状態で 2 回、4 本の CPU を塞いだ状態で 6 回反転した。
- 疑ったほかの要因（いずれも原因でない）:
  - 現在時刻: `prune_list` は `now` を引数で受ける。境目の日付 `cutoff_date` は `now` の文字列から組み立てる。`date` を呼ばない。
  - TZ・`date` の実装差: 試験の材料（57 日の日次）は `date -u -d` で作るが、`-keep` の 2 つの回（2019-09-15・2019-09-30）は字面で与える。境目の判定に `date` は関わらない。
  - 並列実行の共有状態: `prune_list` は配列を `local` で持ち、ファイルに触れない。結果は `$(…)` で受ける。
- #2200 は同じ形を `deploy` と `scripts` の `.sh` から除いたが、この試験を「`set -u` だけ」として対象から外した。source で受け継ぐ pipefail を見ていなかった。
  同じ形の事故は #2200・#2202（workflow の step）に続いて 3 回目である。検査を足す条件（同型の事故が 2 回）を満たす。

## 方式

1. `has_del` / `has_del4` を here-string（`grep -qx -- "$1" <<< "$DEL"`）にする（#2200 と同じ直し方）。
2. 検査を `scripts/scripts.repo.test.js` に足す（CI の scripts-tests が走らせる）。
   - 対象: 追跡下の `deploy/` と `scripts/` の bash の `.sh`（shebang が `#!/bin/sh` のものを除く）。
   - pipefail の下とみなす範囲: そのファイルが `set -o pipefail`（`set -euo pipefail` などの組み合わせを含む）を立てた行より後、
     または、pipefail を立てるファイルを source した行（`. <パス>` / `source <パス>`。`$HERE`・`$DIR` 等はそのファイルの置き場所として読む）より後。
     pipefail を立てるファイルから source される lib は、ファイル全体を pipefail の下とみなす（#2200 の lib と同じ扱い）。
   - 見つける形: コメントでない行で、パイプの右側にある `grep` が `-q`（`-qx`・`-Fxq` などの組み合わせ）か `--quiet` を持つもの。
   - 対象外: Pod の中の `sh -c` へ渡す文字列（`vexec '…'`）。その `sh` は pipefail を立てない。
   - 陰性対照: 直す前の形・source で受け継ぐ形・自分で立てる形を合成した材料で赤になること、`#!/bin/sh` と `vexec` を見つけないこと。

## 母集合（規則 9）

`git ls-tree -r --name-only origin/develop -- deploy scripts` の `.sh`（48 件）で、コメントでない行のパイプの右側の `grep -q` を引いた（13 か所）。

| 場所 | pipefail | 扱い |
| --- | --- | --- |
| `deploy/local/platform-backup/script/backup.test.sh` 674・712 行（`has_del` / `has_del4`） | 有り（661 行の source から） | **直す**（here-string） |
| `deploy/local/platform-backup/script/backup.test.sh` 228・572 行（`head -c 4 … \| grep -q`） | 無し（source の前） | 対象外。書き手は 4 バイトを 1 回で書く |
| `deploy/local/platform-backup/vault/bootstrap.sh` 37 行・`deploy/local/vault/eso/bootstrap.sh` 126 行・`deploy/local/vault/bff-writer/bootstrap.sh` 25 行 | 外側は有り | 対象外: `vexec '…'` の文字列で、Pod の `sh -c` が走らせる |
| `deploy/local/vault-persistence/vault-entrypoint.sh` 161・172 行 | 無し | 対象外: `#!/bin/sh` |
| `scripts/verify-qdrant-attribute-payload.sh` 106・108・120 行・`scripts/verify-qdrant-fulltext-index.sh` 199 行 | 無し | 対象外: `set -u` だけ（source する `lib/live-opt-in.sh` も pipefail を立てない） |

直すのは 2 か所、対象外は 11 か所である（計 13）。検査はこの表と同じ判定になること（直す前に 2 件、直した後に 0 件）を受け入れ基準にする。

## 受け入れ基準と試験

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | `has_del` / `has_del4` がパイプを使わずに判定する | 検査（下）と `backup.test.sh` の全件 |
| AC2 | 検査が直す前の develop の 2 か所を見つけ、直した後は 0 件 | `scripts.repo.test.js` の #2436 の節（直す前に赤を確かめる） |
| AC3 | 陰性対照: 自分で立てる形・source で受け継ぐ形・lib の形を赤にし、`#!/bin/sh`・`vexec '…'`・pipefail の無いファイルを赤にしない | 同上（合成した材料） |

## 対象外

- 読み手が早く終わる `| head`（#2200 と同じく射程外）。
- `.github/workflows` の step（#2202 が別の検査で見ている）。
