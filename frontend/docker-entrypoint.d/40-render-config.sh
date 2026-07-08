#!/bin/sh
# Issue #126: コンテナ起動時に実行時 config（config.js）を環境変数から生成する。
# nginx 公式イメージは /docker-entrypoint.d/*.sh を起動前に実行する。
set -eu

: "${BFF_BASE_URL:=/bff}"
: "${OIDC_AUTHORITY:=http://localhost:8080/realms/knowledge-platform}"
: "${OIDC_CLIENT_ID:=spa-web}"
# Issue #136 / SC-10: 外部ツール導線 URL（未設定は空文字＝画面に導線を出さない）。
: "${GRAFANA_URL:=}"
: "${JAEGER_URL:=}"
: "${KIALI_URL:=}"
# Issue #130 / SC-04: Wiki.js 基点 URL（未設定は空文字＝導線を出さない）。
: "${WIKI_BASE_URL:=}"
export BFF_BASE_URL OIDC_AUTHORITY OIDC_CLIENT_ID GRAFANA_URL JAEGER_URL KIALI_URL WIKI_BASE_URL

envsubst '${BFF_BASE_URL} ${OIDC_AUTHORITY} ${OIDC_CLIENT_ID} ${GRAFANA_URL} ${JAEGER_URL} ${KIALI_URL} ${WIKI_BASE_URL}' \
  < /etc/knowledge-platform/config.js.template \
  > /usr/share/nginx/html/config.js

echo "rendered /usr/share/nginx/html/config.js (BFF_BASE_URL=$BFF_BASE_URL, OIDC_AUTHORITY=$OIDC_AUTHORITY)"
