# FR-14, IADR-0056: フロントエンド（npm workspaces: src/<unit>/frontend。platform=基盤アプリホスト、
# knowledge=可変機能）build → nginx 配信。実行時 config で接続先を注入する（環境非依存ビルド）。
# ビルドコンテキストはリポジトリルート（compose の context: ..）を想定。
FROM node:22-alpine AS build
WORKDIR /app
# 依存解決に必要な manifest 群を先にコピーしてレイヤキャッシュを効かせる
COPY src/package.json src/package-lock.json ./
COPY src/platform/frontend/package.json platform/frontend/
COPY src/knowledge/frontend/package.json knowledge/frontend/
# Issue #283, IADR-0070: 可変ユニット AST の frontend も workspace メンバ（npm ci が package-lock と
# 突合するため manifest を先に投入する）。ソースは合成点（features/index.ts）から取り込まれる。
COPY src/ai-stock-trading/frontend/package.json ai-stock-trading/frontend/
RUN npm ci
COPY src/platform/frontend/ platform/frontend/
COPY src/knowledge/frontend/ knowledge/frontend/
COPY src/ai-stock-trading/frontend/ ai-stock-trading/frontend/
RUN npm run build

FROM nginx:1.27-alpine AS runtime
# nginx テンプレート（BFF_UPSTREAM を envsubst で注入）と実行時 config テンプレート。
COPY src/platform/frontend/nginx.default.conf.template /etc/nginx/templates/default.conf.template
COPY src/platform/frontend/config.js.template /etc/microservices-platform/config.js.template
COPY src/platform/frontend/docker-entrypoint.d/40-render-config.sh /docker-entrypoint.d/40-render-config.sh
RUN chmod +x /docker-entrypoint.d/40-render-config.sh
COPY --from=build /app/platform/frontend/dist /usr/share/nginx/html
# 既定の BFF 上流（compose）。k8s は env で上書きする。
ENV BFF_UPSTREAM=http://bff:8080
EXPOSE 8080
