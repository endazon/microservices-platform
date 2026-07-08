# Issue #126: フロントエンド SPA（build → nginx 配信）。実行時 config で接続先を注入する（環境非依存ビルド）。
# ビルドコンテキストはリポジトリルート（compose の context: ..）を想定。
FROM node:22-alpine AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
# nginx テンプレート（BFF_UPSTREAM を envsubst で注入）と実行時 config テンプレート。
COPY frontend/nginx.default.conf.template /etc/nginx/templates/default.conf.template
COPY frontend/config.js.template /etc/knowledge-platform/config.js.template
COPY frontend/docker-entrypoint.d/40-render-config.sh /docker-entrypoint.d/40-render-config.sh
RUN chmod +x /docker-entrypoint.d/40-render-config.sh
COPY --from=build /app/dist /usr/share/nginx/html
# 既定の BFF 上流（compose）。k8s は env で上書きする。
ENV BFF_UPSTREAM=http://bff:8080
EXPOSE 8080
