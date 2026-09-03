#!/bin/sh
# Issue #126 / #1135: コンテナ起動時に実行時 config（config.js）を環境変数から生成してから Caddy を起動する。
#
# nginx 公式イメージは /docker-entrypoint.d/*.sh を起動前に実行する規約を持っていたが、Caddy 公式イメージは
# 持たない（entrypoint が caddy そのもの）。そのため描画は本スクリプトが担い、最後に exec で Caddy へ渡す。
#
# 🔴 **描画に失敗したらコンテナを起動しない**（set -eu ＋ exec）。従前は readiness=/config.js が 404 で
# 落ちる形の fail-safe だったが（IADR-0078 決定 2）、それより早く落ちる方向であり弱くはならない。
# readiness の宛先自体は /config.js のままである（生成完了を確かめる意味を薄めない）。
set -eu

: "${BFF_BASE_URL:=/bff}"
: "${OIDC_AUTHORITY:=http://localhost:8080/realms/platform}"
: "${OIDC_CLIENT_ID:=platform-spa}"
# Issue #136 / SC-10: 外部ツール導線 URL（未設定は空文字＝画面に導線を出さない）。
: "${GRAFANA_URL:=}"
: "${JAEGER_URL:=}"
: "${KIALI_URL:=}"
# Issue #130 / SC-04: Wiki.js 基点 URL（未設定は空文字＝導線を出さない）。
: "${WIKI_BASE_URL:=}"
export BFF_BASE_URL OIDC_AUTHORITY OIDC_CLIENT_ID GRAFANA_URL JAEGER_URL KIALI_URL WIKI_BASE_URL

# envsubst に置換対象を明示列挙する（列挙外の ${...} はテンプレートに literal で残る）。
# 意味論を nginx 時代とバイト等価に保つためで、sed で書き直すと値に含まれる & / \ の
# エスケープという新しい壊れ方を持ち込む。
envsubst '${BFF_BASE_URL} ${OIDC_AUTHORITY} ${OIDC_CLIENT_ID} ${GRAFANA_URL} ${JAEGER_URL} ${KIALI_URL} ${WIKI_BASE_URL}' \
  < /etc/microservices-platform/config.js.template \
  > /usr/share/caddy/config.js

echo "rendered /usr/share/caddy/config.js (BFF_BASE_URL=$BFF_BASE_URL, OIDC_AUTHORITY=$OIDC_AUTHORITY)"

exec "$@"
