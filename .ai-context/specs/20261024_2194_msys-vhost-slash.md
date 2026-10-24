---
title: 作業仕様書 — Windows の Git Bash で reconcile-users.sh の vhost「/」が Git の導入先のパスへ書き換わるのを、その呼び出しに限って止める（#2194）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0573
  - IADR-0577
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs: []
related_specs:
  - 20261024_2200_grep-q-pipefail-sigpipe
issue: "#2194"
---

# 作業仕様書 — Git Bash の vhost「/」

## 目的と射程

稼働 PoC（Windows の Git Bash）では、`k8s-local-up.sh` の ESO の段から呼ぶ `reconcile-users.sh` が止まった。最初のユーザーの `set_permissions -p "$VHOST"` で `Virtual host C:/Program Files/Git/ does not exist` が出ている。
Git Bash（MSYS2）は、MSYS のプロセスがネイティブの実行ファイル（`kubectl.exe`）を起こすとき、POSIX のパスに見える引数を Windows のパスへ書き換える。そのため、ちょうど `/` の引数が Git の導入先になった。
起動器全体に `MSYS_NO_PATHCONV=1` を付ける回避は使えない。起動器は一時ファイル（`/tmp/...`）のパスを kubectl へ渡しており、そちらは書き換えが要るからである。

**射程**: `reconcile-users.sh` の `set_permissions` の 1 か所と、同じ形の引数の走査。
**射程外**: 利用者に env を要求する形（PoC の回避 `RABBITMQ_USERS_VHOST=//`）。

## 方式

その呼び出しだけに `MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'` を前置する。

- 前者は Git for Windows の変数、後者は MSYS2 の変数である。どちらか一方が効けば書き換えは止まる。
- 他の環境（Linux・macOS）では意味を持たない。
- bash では、関数の呼び出しに前置した代入は、その関数の中で起こすコマンドの環境に入る。代入は呼び出しの後に残らない（実測で確かめた）。`timeout`（MSYS のプロセス）→ `kubectl.exe` の起動にも届く。

**`--vhost=/` の形は採らない。** MSYS2 は `--opt=/path` の `=` の後ろも書き換えの対象にするので、止まる保証が無い。
**PoC の回避（`RABBITMQ_USERS_VHOST=//`）は、この修正の後は外す。** 書き換えが止まるので、`//` がそのまま届き、存在しない vhost になる（PR の配備前の手順に書く）。

## 母集合（規則 9）

「ちょうど `/` の引数を `kubectl exec` 越しに渡す」形を探した。

- `git ls-files '*.sh'`（submodule を除く）に対して、`:-/}`・前後が引用符か空白の `/`・`-p /`・`vhost`・`=/` を grep で引いた。
- `rabbitmqctl` / `rabbitmqadmin` と、`exec … --` の後に `/` を渡す行も引いた。

| ヒット | 扱い |
| --- | --- |
| `deploy/local/rabbitmq-setup/reconcile-users.sh` の `set_permissions -p "$VHOST"`（既定 `/`） | **対象** |
| `deploy/local/rabbitmq-setup/disable-default-user.sh`（`list_users`・`list_connections`・`delete_user`） | 対象外。`/` の引数を渡さない |
| `scripts/measure-cutover-inventory.js`・`scripts/republish-document-updated.js` の rabbitmqctl | 対象外。node（ネイティブ）が起こすので、MSYS の書き換えを通らない |
| その他（`sed 's/^/ /'`・`awk … "/" …`・文字列中の「/」） | 対象外。kubectl の引数ではないか、ちょうど `/` ではない |

## 受け入れ基準

- [x] Git Bash の書き換えを模した kubectl の下でも、`set_permissions -p` に `/` がそのまま届く。
  模型は、書き換えを止める変数が無ければ、`/` で始まる引数を `C:/Program Files/Git…` にする。
- [x] 書き換えを止める変数は、その呼び出しだけに効く。他の rabbitmqctl の呼び出しには広がらない。
- [x] 変異: 前置を外すと、vhost が `C:/Program Files/Git/` になる（試験が検出力を持つ）。

## 検証

- `node scripts/k8s-local-up.test.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `shellcheck`・`bash -n`
