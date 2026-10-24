---
title: 作業仕様書 — pipefail の下の「… | grep -q」が書き手を SIGPIPE で落として判定を反転させる形を、deploy と scripts の .sh から除く（#2200）
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
related_specs: []
issue: "#2200"
---

# 作業仕様書 — pipefail の下の `| grep -q`

## 目的と射程

`deploy/local/rabbitmq-setup/reconcile-users.sh` の「ユーザーが在るか」の判定（`printf '%s\n' "$existing" | grep -Fxq -- "$u"`）が、`set -o pipefail` の下で偶発的に反転した。
`grep -q` は一致すると読むのをやめて終わる。書き手の `printf` がまだ書いている途中なら SIGPIPE（141）で落ち、pipefail がパイプライン全体を失敗にする。
その結果、在るユーザーを「無い」と読む。CI（MSP#2198 の static-checks）が赤になり、起動器の ESO の段でも `--check` の偽の不一致や `add_user` / `change_password` の取り違えが起き得る。

**射程**: deploy と scripts の追跡下の `.sh` にある `| grep -q`（`-Fxq`・`-qx`・`-Eq`・`-qi` などを含む）。
**射程外**: 同じく読み手が早く終わる `| head`（25 か所）。#2200 の対象の形ではないため、報告に留める。同型の事故は今回が 1 回目なので、検査器は足さない（運用ガイド §8「2 回から」）。

## 方式

- 変数の値を調べるものは here-string（`grep -q PAT <<<"$var"`）にする。パイプを使わないので、書き手の SIGPIPE が起きない。
- コマンドの出力を調べるもの（`kubectl …`・`curl --help all`）は、`-q` を外して `>/dev/null` にする。grep が入力を最後まで読むので、書き手は SIGPIPE で落ちない。
  pipefail の下では書き手自身の失敗もパイプラインの失敗になる。この点は従前と同じである。
- here-string は bash の機能である。対象のファイルは全て bash である（shebang が bash か、bash の起動器から source される lib）。

## 母集合（規則 9）

`git ls-files 'deploy/*.sh' 'scripts/*.sh'` に、`grep` の後に `-…q` / `--quiet` を持ち、パイプの右側にある行を正規表現で引いた（コメント行を除く）。結果は 26 か所である（issue 本文の「17 か所」は検証せずに転記しない。規則 10）。
行をまたぐパイプ（前の行が `|` で終わり、次の行が `grep … -q`）も awk で探したが、0 件だった。

| 場所 | pipefail | 扱い |
| --- | --- | --- |
| `deploy/local/rabbitmq-setup/reconcile-users.sh` 88・95 行 | 有り（`set -euo pipefail`） | **直す**（here-string） |
| `deploy/local/vault/oidc/bootstrap.sh` 37 行 | 有り | **直す**（here-string） |
| `scripts/istio-edge-down.sh` 59 行（`kubectl get ds … \| grep -q`） | 有り | **直す**（`>/dev/null`） |
| `scripts/lib/local-image-tag.sh` 25 行 | 呼び出し側（`k8s-local-up.sh`・`k8s-local-images.sh`）が有り | **直す**（here-string） |
| `scripts/lib/mesh-mtls-mode.sh` 129 行 | 呼び出し側（`k8s-local-up.sh`・`istio-edge-up.sh`・`istio-edge-down.sh`）が有り | **直す**（here-string） |
| `scripts/verify-oidc-edge-flow.sh` 117・312・418・538・543・1117 行 | 有り（`set -uo pipefail`） | **直す**（117 は `>/dev/null`、他は here-string） |
| `scripts/verify-tool-oidc-logins.sh` 94・187 行 | 有り（`set -uo pipefail`） | **直す**（94 は `>/dev/null`、187 は here-string） |
| `deploy/local/platform-backup/vault/bootstrap.sh` 37 行・`deploy/local/vault/eso/bootstrap.sh` 89 行 | 無し | 除外: `vexec` の引数の文字列で、Vault の Pod の `sh -c` が実行する。そのシェルに pipefail は無い |
| `deploy/local/vault-persistence/vault-entrypoint.sh` 161・172 行 | 無し | 除外: `#!/bin/sh` で、pipefail を立てていない（パイプラインの終了コードは grep のもの） |
| `deploy/local/platform-backup/script/backup.test.sh` 167・320・422・460 行 | 無し | 除外: `set -u` だけ |
| `scripts/verify-qdrant-attribute-payload.sh` 106・108・120 行 | 無し | 除外: `set -u` だけ（source する `lib/live-opt-in.sh` も pipefail を立てない） |
| `scripts/verify-qdrant-fulltext-index.sh` 199 行 | 無し | 除外: 同上 |

直したのは 14 か所（7 ファイル）、除外は 12 か所である（計 26）。

## 受け入れ基準

- [x] `reconcile-users.sh` の判定は、宣言のユーザーを先頭に置いた大きな一覧（詰め物 2 万件・パイプの容量より大きい）でも反転しない。
  `--check` は揃っていると判定する（5 回とも）。apply は在るユーザーへ `change_password` を使う（`add_user` を使わない）。
- [x] 変異: 旧い形（`printf … | grep -Fxq`）へ戻した写しは、同じ入力で `--check` も apply も反転する（試験が検出力を持つ）。
- [x] 直した 7 ファイルで構文（`bash -n`）と shellcheck の指摘が増えていない。

## 検証

- `node scripts/k8s-local-up.test.js`（#2200 の試験を含む全件）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `bash -n`・`shellcheck -x`（変更前後で指摘の集合が同じ）

## 残り

- `| head` の 25 か所は、読み手が早く終わる同じ型である。今回は対象にしていない。報告に留める。
- `password_line "$u" | rmqctl_stdin …` も、rabbitmqctl が標準入力を読まずに終わればパイプラインが失敗する。ただしそれは rabbitmqctl 自体が失敗したときに限られ、判定は反転しない。対象外とする。
