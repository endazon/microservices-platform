#!/bin/sh
# Issue #126: コンテナ起動時に実行時 config（config.js）を環境変数から生成する。
# nginx 公式イメージは /docker-entrypoint.d/*.sh を起動前に実行する。
set -eu

: "${BFF_BASE_URL:=/bff}"
: "${OIDC_AUTHORITY:=http://localhost:8080/realms/knowledge-platform}"
: "${OIDC_CLIENT_ID:=spa-web}"
export BFF_BASE_URL OIDC_AUTHORITY OIDC_CLIENT_ID

envsubst '${BFF_BASE_URL} ${OIDC_AUTHORITY} ${OIDC_CLIENT_ID}' \
  < /etc/knowledge-platform/config.js.template \
  > /usr/share/nginx/html/config.js

echo "rendered /usr/share/nginx/html/config.js (BFF_BASE_URL=$BFF_BASE_URL, OIDC_AUTHORITY=$OIDC_AUTHORITY)"
