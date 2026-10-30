#!/usr/bin/env bash
# NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, IADR-0663 決定 3・4 (#458 段 S4): データソースの設定の**平文の資格情報**を Vault へ移す（経路 B）。
#
#   bash scripts/migrate-datasource-credentials.sh --live --dry-run   # 平文を持つ行とキーを数えるだけ（何も書かない）
#   bash scripts/migrate-datasource-credentials.sh --live             # 移送する（--live か LIVE=1 が無ければ何もしない。#1550）
#
# 何をするか:
#   1. Vault の Pod の中で、移送専用の policy（datasource-credential-migrate。専用接頭辞の 1 セグメントに create・update・read）の
#      **短命のトークン**（既定 10 分・使用回数の上限つき）を発行する。
#   2. そのトークンを**標準入力で**、稼働の datasource-service の Pod の中の移送のコマンドへ渡す
#      （`dotnet DataSourceService.dll migrate-connector-credentials --token-stdin`）。接続文字列と Vault の所在は Pod の環境から読む。
#   3. 終わったらトークンを失効させる（成功でも失敗でも）。
# 🔴 トークンは引数・環境変数・ファイルに置かない（パイプだけ）。値・パスは出力に出ない（移送のコマンドは件数と行の番号だけを出す）。
# 🔴 **流す前に、datasource の DB のバックアップを取る**（移送は設定の平文を参照へ置き換える。DB の上では戻らない）。手順は
#    docs/operations/secret-rotation-runbook.md「データソースの資格情報の移送（平文 → Vault）」。
# 終了コード: 移送のコマンドの終了コード（0 = 全部移した・1 = 衝突／失敗が残った・2 = 使い方・構成の誤り）。前提が欠ければ 1。
set -euo pipefail

. "$(dirname "$0")/lib/live-opt-in.sh" || exit 3   # 判定器が読めなければ守れない —— 黙って続けず止める
live_opt_in_scan "$@"; set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "migrate-datasource-credentials.sh"

DRY_RUN=0
for a in "$@"; do
  case "$a" in
    --dry-run) DRY_RUN=1 ;;
    *) echo "ERROR: 未知の引数: $a（受け付けるのは --dry-run だけ）" >&2; exit 2 ;;
  esac
done

MSP_NS="${MSP_NS:-microservices-platform}"
INFRA_NS="${INFRA_NS:-platform-infra}"
TTL="${MIGRATION_TOKEN_TTL:-10m}"
APP="dotnet DataSourceService.dll migrate-connector-credentials"

kubectl -n "$MSP_NS" get deploy/datasource-service >/dev/null
if [ "$DRY_RUN" = 1 ]; then
  kubectl -n "$MSP_NS" exec deploy/datasource-service -- $APP --dry-run
  exit $?
fi

kubectl -n "$INFRA_NS" get deploy/vault >/dev/null
# vault Pod 内で root として vault コマンドを実行する（deploy/local/vault/eso/bootstrap.sh と同じ）。
vexec() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; '"$1"; }

vexec 'vault policy read datasource-credential-migrate >/dev/null' || {
  echo "ERROR: policy datasource-credential-migrate が無い。deploy/local/vault/eso/bootstrap.sh を先に流す" >&2; exit 1; }

# accessor だけを手元に持つ（失効に使う。トークンの値ではない）。値は変数にも置かず、パイプで渡す。
ACCESSOR_FILE="$(mktemp)"; trap 'rm -f "$ACCESSOR_FILE"' EXIT
set +e
vexec "vault token create -policy=datasource-credential-migrate -ttl=$TTL -explicit-max-ttl=$TTL -use-limit=500 -format=json" \
  | node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{const a=JSON.parse(d).auth;require("fs").writeFileSync(process.argv[1],a.accessor);process.stdout.write(a.client_token+"\n")})' "$ACCESSOR_FILE" \
  | kubectl -n "$MSP_NS" exec -i deploy/datasource-service -- $APP --token-stdin
code=$?
set -e
if [ -s "$ACCESSOR_FILE" ]; then
  vexec "vault token revoke -accessor $(cat "$ACCESSOR_FILE") >/dev/null" || echo "WARN: トークンを失効できなかった（TTL $TTL で切れる）" >&2
fi
exit "$code"
