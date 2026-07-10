# FR-14, IADR-0056: フロントエンド（npm workspaces: platform=基盤アプリホスト + knowledge=可変機能）
# build → nginx 配信。実行時 config で接続先を注入する（環境非依存ビルド）。
# ビルドコンテキストはリポジトリルート（compose の context: ..）を想定。
FROM node:22-alpine AS build
WORKDIR /app
# 依存解決に必要な manifest 群を先にコピーしてレイヤキャッシュを効かせる
COPY src/frontend/package.json src/frontend/package-lock.json ./
COPY src/frontend/platform/package.json platform/
COPY src/frontend/knowledge/package.json knowledge/
RUN npm ci
COPY src/frontend/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
# nginx テンプレート（BFF_UPSTREAM を envsubst で注入）と実行時 config テンプレート。
COPY src/frontend/platform/nginx.default.conf.template /etc/nginx/templates/default.conf.template
COPY src/frontend/platform/config.js.template /etc/knowledge-platform/config.js.template
COPY src/frontend/platform/docker-entrypoint.d/40-render-config.sh /docker-entrypoint.d/40-render-config.sh
RUN chmod +x /docker-entrypoint.d/40-render-config.sh
COPY --from=build /app/platform/dist /usr/share/nginx/html
# 既定の BFF 上流（compose）。k8s は env で上書きする。
ENV BFF_UPSTREAM=http://bff:8080
EXPOSE 8080
