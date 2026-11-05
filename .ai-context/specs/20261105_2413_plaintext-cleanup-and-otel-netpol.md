---
title: 作業仕様書 — 復号物・秘密を手元に残さない形へ揃え（リストア試験・手順書）、otel-collector の到達を opt-in で絞る（#2413）
type: spec
status: done
related_ids: [NFR-18, NFR-21, IADR-0471, IADR-0589, IADR-0682]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2413"
---

# 作業仕様書 — 復号物・秘密の後始末と otel-collector の到達の制限（#2413）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。develop `b05b19b9`）。

## 事象

1. **リストア試験の平文**: `scripts/backup-restore-drill.sh` は使い捨ての Postgres に復号した DB を戻す。
   - イメージは `VOLUME /var/lib/postgresql/data` を宣言している。
   - 片付けの `rm -f`（`-v` なし）は名前の無いボリュームを残す。戻した平文（WAL を含む）がディスクに残る（実測）。
2. **秘匿管理のリストア試験**（手順書の §3 の 5）: スナップショットと init のファイル（unseal 鍵・root トークン）を手元の `mktemp -d` へ復号していた。
   - 片付けは最後の `rm -rf` だけで、trap が無い。途中で失敗・中断すると残る。
3. **手順書の秘密の扱い**:
   - 固定の値（確認の言葉・開発用の client secret）を `export` させている。
   - 秘密を手元のシェルの環境へ `export` している。
   - トークン・パスワードをコマンドの引数に載せている。
   - ダンプ・書き出しを平文のままファイルへ書いている。
4. **otel-collector** には NetworkPolicy が無い（Loki・Tempo・mail-relay は collector からの到達だけを許している）。

## 方針

1. リストア試験:
   - 使い捨ての Postgres のデータ領域を `--tmpfs` に置き、`PGDATA` をそこへ固定する。
   - 片付けは `rm -f -v`。INT / TERM / HUP でも EXIT の片付けへ進める。
2. 秘匿管理のリストア試験は新しいスクリプト `scripts/vault-restore-drill.sh` にする（同じ形の自己試験つき）。
   - 復号物は `age -d | docker exec -i … cat > /drill/…` でコンテナの tmpfs へ直に流す。手元に一時ファイルを作らない。
   - 片付けは trap（EXIT・INT・TERM・HUP）で、名前の無いボリュームごと消す。
   - 手順書はスクリプトを呼ぶ形にする。稼働側へ戻す手順の Pod 内の一時ファイルにも trap を付ける。
3. 手順書:
   - `read -rs` で読み、`export` は `( )` の中で 1 回に限る。
   - トークンは `-H @<(printf …)`（ネイティブの curl を使う手順では `$WORK` の 0600 のファイル）で渡す。
   - パスワードは `--data-urlencode name@<(…)` か標準入力（`key=-`）で渡す。
   - ダンプ・書き出しは、バックアップと同じ受取人（ConfigMap `platform-backup-age-recipients`）の `age -R` へパイプで渡す。
   - 回帰は `scripts/runbook-secret-handling.test.js` が止める。
4. otel-collector: `deploy/local/otel-collector-netpol/`（kustomize の overlay。既定の経路 B に入れない＝opt-in の段 1）。
   - ingress / egress の NetworkPolicy を置く。宛先と送り手は collector の設定と宣言から導いて試験で突き合わせる。

## 受け入れ基準

- [x] 修正の前に、改めた自己試験が赤になる（`backup-restore-drill.sh --self-test` で 7 件）。
- [x] 成功・失敗（流し込みの失敗・コンテナの起動の失敗）・中断（SIGTERM）のどれでも、使い捨てのコンテナをボリュームごと消す（自己試験）。
- [x] 実 Docker で次を確かめた。
  - 旧版は平文を含む名前の無いボリュームを 1 つ残す。新版は 0。
  - 失敗（違う鍵）・中断（SIGTERM）でも 0。
- [x] 秘匿管理のリストア試験は、実 OpenBao（2.7.1）の写しで合格し、コンテナ・ボリュームを残さない。違う鍵では失敗して残さない。
- [x] 手順書の走査（`docs/`・`deploy/` の .md のコード）で違反が 0。直す前の字面は全件検出される。
- [x] otel-collector の NetworkPolicy は宣言から導いた送り手・送り先を全部通し、それ以外を通さない。規則を 1 つ外す変異で必ず赤になる。

## 母集合（規則 9・10）

- **規則 9**: 次の語で走査した。
  - 語: `export <秘密らしい名前>=`・`Authorization: Bearer $`・`-d password=`・`pg_dump`・`pg_dumpall`・`kcadm.sh get realms`・`tar … -cf -` ＋ `>`・`age -d`・`mktemp` ＋ 復号・`rm -f`（コンテナ）・`otel-collector`。
  - 範囲: `docs`・`deploy`・`scripts`・`.github/workflows`。
  - 改める:
    - `scripts/backup-restore-drill.sh`。
    - 手順書: `platform-infra-backup-runbook.md`（§3 の 5・§5 の Pod 内の一時ファイル）・`local-sso-recovery-runbook.md`・`loki-log-read-runbook.md`・
      `ast-stale-copies-deletion-runbook.md`・`paired-secret-rotation-runbook.md`・`secret-rotation-runbook.md`・`operations.md`（H2 の退避・compose の退避・
      Docker Hub のトークン）・`cutover-discard-and-rebuild.md`・`deploy/local/vault/oidc/README.md`・`deploy/local/wiki-oidc/README.md`。
  - 対象外とした理由:
    - Pod の中の `export VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"`: コンテナの中の変数の受け渡しで、手元のシェルにも履歴にも値が出ない。
    - `run-integration-tests-without-docker.md` の `kp` / `guest`: 手元の試験用コンテナの固定の開発値で、秘密ではない。
    - `search-rerank-s3-measurement-runbook.md` の `--dump`: 検索の評価の結果で、DB・資格情報のダンプではない。
    - `deploy/local/platform-backup/script/backup.sh`: 既に age へパイプで渡している（方式の手本）。
    - `deploy/cloud/`: 並行の作業で develop に無い。
- **規則 10**: 次を本変更で改める。
  - 手順書の §3 の 5 の後ろの「`| wc -l` を付ける」（スクリプトが数を出すので古くなる）。
  - `ast-stale-copies` の `unset TOKEN` の位置（後の削除の手順でも使うので、手順の終わりへ移す）。
- **規則 11**: 窓を扱う是正ではないので当てない。

## 残余リスク

- tmpfs の大きさはメモリが上限になる（大きな DB を試すときは手元のメモリに注意）。
- SIGKILL では trap が走らない。そのときコンテナが残るので、名前（`platform-backup-drill-<PID>`・`vault-restore-drill-<PID>`）で消す。tmpfs なので、コンテナを消せば平文は残らない。
- otel-collector の NetworkPolicy は opt-in で、統合スタックでの強制の実測はまだ無い（段 2 で既定へ入れる前に測る）。
- 手順書の guard は字面の検査である。変数の名前が秘密らしく見えない書き方はすり抜ける。

## 検証（2026-11-05）

- `bash scripts/backup-restore-drill.sh --self-test`: 39 件が合格した（修正前は 7 件が赤）。
- `bash scripts/vault-restore-drill.sh --self-test`: 24 件が合格した。
- `node scripts/runbook-secret-handling.test.js`: 2 件が合格した。
- `node scripts/otel-collector-network-policy.test.js`: 5 件が合格した。
- 実 Docker の確かめ: 上の受け入れ基準のとおり。手元のコンテナだけを使い、稼働クラスタには触れていない。
