---
title: 作業仕様書 — 対になる秘密のローテーション手順書の bash 部品を Windows（Git Bash）でも壊れない形にし、1-3 を「組み立てて確かめてから書く」順へ並べ替える（#1877）
type: spec
status: done
related_ids: [NFR-18, SC-22, ADR-0124]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1・4（対になる秘密は相手と保管先を対で書く運用手順で回す）
related_specs:
  - 20260928_issue-1682_paired-secrets-outside-sc22
  - 20260925_458_secret-rotation-runbook
issue: "#1877"
---

# 作業仕様書 — 対になる秘密のローテーション手順書の Windows（Git Bash）対応（#1877）

> 本仕様書は着手前に作成した（着手 2026-10-10）。基点は MSP `origin/develop` `d41e8641`。
> 文書だけの変更であり、コード・検査器・稼働クラスタには触れない。

## 起点（トレーサビリティ）

- 起点 issue: **#1877**（2026-10-10 の PoC 報告。利用者が Windows・Git Bash で手順 1 の 1-1〜1-3 を実行。回すことには成功し、値は一度も表示されていない）。
- 計画: **NFR-18**（秘密情報の管理）・**ADR-0124**（対になる秘密は SC-22 の対象外とし、相手と保管先を対で書く運用手順で回す）。計画は手順書の実行ホストの OS を定めていない（運用の細目）。**計画への環流は要らない。**
- 対象文書: `docs/operations/paired-secret-rotation-runbook.md`（#1682 で新設）。

## PoC の所見（issue の 5 点と並べ替えの依頼）

| # | 所見 | 本書での直し方 |
| --- | --- | --- |
| 1 | Windows の jq 1.8.2 は `-r` で CRLF を出す。1-1 のフォームの末尾に `\r` が付き `invalid_user_credentials`。`CID` にも `\r` | 値 1 つは `jq -j`。複数行（0-e）は `jq -r … \| tr -d '\r'` |
| 2 | 1-3 の `jq --rawfile s <(printf …)` はネイティブの jq が `/dev/fd/N` を開けない | 一時ファイル（0-0 の `$WORK`）に書いて渡す |
| 3 | MSYS の `/tmp/…` をネイティブの curl / jq へ渡すと `C:\tmp` を読み書きする。`MSYS_NO_PATHCONV=1` の下では変換されない | 0-0 に `np()` を置き、`uname -s` が `MINGW*` / `MSYS*` のとき `cygpath -m` で渡す |
| 4 | 大きな JSON（`kubectl get deploy -A -o json`）を jq へパイプすると時々止まる | 0-e をファイル経由にする |
| 5 | `set -o pipefail` の下で `curl … \| grep -q 200` が SIGPIPE で偽 | 1-3 の `PUT` の状態コードを `-o /dev/null -w '%{http_code}'` で変数に受けて比べる |
| 並べ替え | 旧 1-3 は保管先へ書いた後に新しい client の表現を組み立てていた。組み立てで止まると保管先だけ進む（PoC で版が 1→6） | 1-3 を「0-a → 表現を組み立てて確かめる（`body ok`）→ 0-b → 0-c → `PUT` → 同期 → 作り直し」へ並べ替える |

## 方針の選択

- **移植できる部品にする（「Linux / WSL で実行する」とは書かない）。** 依頼が移植を優先しているうえ、PoC のホストが Windows であり、運用者は同じホストで再実行する。部品は Linux・macOS・WSL でも同じ形のまま動く（`np` は MSYS 以外では恒等）。
- **「Windows（Git Bash）での注意」の小節を共通の部品の下に置く。** 症状・原因・避け方の表にする（部品を手で打ち直す人が旧い形へ戻さないため）。
- **1-1 のフォームは標準入力のまま渡す**（パスワードをファイルへ書かない）。CRLF は `-j` だけで避けられる。
- **値をファイルに置くのは 1-3 の 2 の間だけ**にし、使った直後に消す。`umask 077`・`trap … EXIT`・0-z の明示の片付けを置く。`rm -rf` は `"${WORK:?}"` で空展開を防ぐ。

## 同じ機会に直したこと（所見から導いたもの）

- **0-b に「控えるのは最初の試行の前の版」を足す。** PoC で中断した試行のたびに版が進んだ。やり直しのたびに控え直すと、相手（認証基盤）と一致しない版へ戻すことになる。
- **「途中で止まったとき」の段番号を並べ替えに追随させる**（群 1 の 3→4・4→5、戻しの書き込みは 1-3 の 2 と 5）。
- **「相手の書き込みが失敗した・成否が分からない」の確かめ方を、長さの比較から値を出さない等値の比較（`jq -e --rawfile s … '.secret == $s'`）へ改める。** 2 回目以降のローテーションでは旧も新も `openssl rand -hex 32` の 64 文字であり、長さでは区別できない（規則 10: この変更で新たに誤りになる自分の記述の引き直しで見つけた）。
- **本書の段番号を引く他の文書を追随させる**（規則 10）。`git grep paired-secret-rotation-runbook -- docs scripts deploy` で引いた参照のうち、段番号を引くのは
  `docs/migration/cutover-discard-and-rebuild.md`（「手順 1 の 4 で、Vault の値を認証基盤へ書き直す」）だけであった。「手順 1 の 1-3 の 2 と 5」へ直す。
  他の参照（`secret-rotation-runbook.md`・`secret-item-console-injection-runbook.md`・`deploy/` のコメント）は節名か文書名だけを引き、並べ替えの影響を受けない。
- 冒頭の注記・「限界」・「リハーサル記録」に 2026-10-10 の実行を記す。**直した後の部品は Windows では未実行**であることも記す。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査する）

走査: `origin/develop` `d41e8641` の追跡下の `docs/**/*.md`（`.ai-context/` は凍結記録なので対象外）。

| 引いた文字列 | ヒット | 扱い |
| --- | --- | --- |
| `<(`（プロセス置換） | `docs/how-to/plan-id-range-history-annex.md` 636・658・683・715・732 行（`diff <(git ls-tree …) <(seq …)`）／本書 184 行 | 本書は直す。別紙は**開発者が計画リポのレンジを引き直す記録**で、`diff`・`git`・`seq` はすべて MSYS 側のコマンド（`/dev/fd` を開ける）。運用者が PoC ホストで実行する手順ではない → **直さない** |
| `\| *grep -q`／`grep -q` | パイプの形は 0 件。`docs/operations/password-reset-relay-state-measurement-runbook.md` 41・90 行（`grep -q … scripts/…js` はファイルを直接読む形でパイプでない）／`deploy/local/vault/oidc/README.md` 46 行（Vault Pod 内の `sh`。pipefail なし） | SIGPIPE の形ではない → **直さない** |
| `jq -[…]r` | 本書 126・159・161・171・298 行／`docs/operations/operations.md` 1990 行（Docker Hub の匿名トークンを `jq -r .token` で取り `-H` へ渡す）／`docs/operations/ast-stale-copies-deletion-runbook.md` 83 行（`> stale-ids.txt` へ書く） | 本書は直す。`operations.md` 1990 行は**イメージの digest を解決する開発者の手順**（PoC ホストの運用手順ではない）。Windows のネイティブ jq で実行すると `TOKEN` に `\r` が付き得るので**残余として記す**。`ast-stale-copies-deletion-runbook.md` は id をファイルへ書き、同書の後段（MSYS の `while read` 等）で読む。`\r` が付けば id の末尾に残り得るが、**PoC ホストで実行した記録が無く、今回の所見の対象外** → 直さず残余として記す |
| `-o json \| jq`（大きな JSON のパイプ） | 本書 126 行のみ | 直す |
| `--data-binary @-`／`curl -K -` | 本書 161・186 行／`docs/operations/voyage-embedding-key-runbook.md` 183 行（Pod 内の `sh` で実行） | 本書は直す（161 は `-j` で足りる。186 は一時ファイルへ）。voyage は Pod 内 → **直さない** |
| `/tmp/` | `docs/operations/object-storage-seaweedfs-cutover-runbook.md` 88・91 行（`helm get values … > /tmp/msp-user-values.yaml` と `helm … -f /tmp/…`）／`secret-store-openbao-migration-runbook.md`（Pod 内）／`voyage-embedding-key-runbook.md`（Pod 内）／`operations.md` 159・160・229（コンテナ内のパスの説明）／`docs/how-to/obsidian-plugin-install.md` 175（例示の env） | seaweedfs の切替手順はネイティブの `helm` へ MSYS の `/tmp/…` を渡す形で、所見 3 と同型。**ただし PoC ホストで実行した記録が無く、ネイティブの引数の先頭が `/tmp/` なら MSYS の自動変換が掛かる形**（`MSYS_NO_PATHCONV=1` の下でだけ壊れる）→ 直さず残余として記す。他は Pod 内・説明文 → 対象外 |
| 同じ秘密群の手順書（`secret-rotation-runbook.md`・`secret-item-console-injection-runbook.md`・`secret-item-live-sync-check-runbook.md`） | `jq`・`<(`・`grep -q`・`/tmp/` のいずれも無い（`base64 -d \| wc -c` と `\| grep -E` のみ。いずれも MSYS 側で LF のまま） | 直すものなし |

**所見の直しを入れるのは本書だけ**である（段番号の追随は上の 1 件。PoC ホストで運用者が実行したのは本書の手順 1 であり、所見の 5 形がそろって現れるのも本書だけ）。

## 受け入れ基準

- [x] 本書の bash 部品から `jq -r` を値 1 つの取り出し・フォームの組み立てに使う形が消え、`-j` か `tr -d '\r'` になっている。
- [x] 本書からプロセス置換 `<(` が消えている。
- [x] ネイティブの `jq` / `curl` へ渡すファイルのパスが `np` を通っている（`uname -s` が `MINGW*` / `MSYS*` のとき `cygpath -m`）。
- [x] `kubectl get deploy -A -o json` をファイル経由で jq へ渡している。
- [x] 状態コードを変数に受けて比べている（`| grep -q` が無い）。
- [x] 1-3 が「表現を組み立てて確かめてから保管先へ書く」順になり、「途中で止まったとき」の段番号が追随している。
- [x] 「Windows（Git Bash）での注意」の小節がある。
- [x] `docs/` の表示テキストに計画 ID・IADR・仕様書名・修飾付き issue 参照を書いていない（trace ブロックへ #1877 と本仕様書を足した）。

## 検証

- 部品の論理: 本書の `bash` ブロックを抜き出し、`kubectl`・`curl`・`jq`・`uname`・`cygpath` を偽物に差し替えて `set -euo pipefail` の下で 1-1 → 1-2 → 1-3 → 0-e → 0-z を通した。偽の `jq` は Windows を模して `LF → CRLF` に変え、`/dev/fd/*` と素の `/tmp/*` を開けないようにした。**Linux の形・Windows を模した形の両方で `token ok`・`client ok`・`body ok`・`keycloak ok`、保管先と認証基盤へ同じ値が書かれ、client の他の欄が保たれ、一時ディレクトリが消えた。** 旧い形（`jq -Rr` のフォーム・`<(printf …)`・pipefail 下の `| grep -q`）は同じ偽物で `invalid_user_credentials`・`Could not open /dev/fd/63`・`false` を再現した。
- `shellcheck -s bash`: 占位子（`<client>`）による構文の指摘と、断片ゆえの未使用変数（SC2034）のほかは指摘なし。
- 稼働クラスタでは実行していない。**Windows の実機で直した後の部品を通すのは次の実行の機会**であり、本書の「限界」に書いた。
- 文書の検査: `check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`・`check-cross-repo-refs`・`check-doc-links`・`check-reading-budget`。

## 計画書との差異

- 差異: なし。
