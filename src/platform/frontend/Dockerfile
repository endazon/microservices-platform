# FR-14, IADR-0056 / IADR-0121: フロントエンド（pnpm workspace: src/<unit>/frontend。platform=基盤アプリホスト、
# knowledge=可変機能）build → nginx 配信。実行時 config で接続先を注入する（環境非依存ビルド）。
# ビルドコンテキストはリポジトリルート（compose の context: ..）を想定。
#
# Issue #325, IADR-0081: base イメージは docker.io を直参照せず、Google の Docker Hub プルスルー
# ミラー（mirror.gcr.io/library・匿名 pull 可）を既定にする。
#   根本原因: Rancher Desktop(containerd/nerdctl) 環境では、レジストリが匿名アクセスに 401 Bearer
#   チャレンジを返すと nerdctl が資格情報ヘルパを呼び、それが errorCode 255 で失敗して pull 不能になる。
#   docker.io・public.ecr.aws・ghcr.io は 401 を返すため全滅する（実測）。mcr.microsoft.com（.NET が
#   成立）・mirror.gcr.io はチャレンジ無し（200）で応答するためヘルパを呼ばず成立する（実測 pull 成功）。
#   frontend だけが唯一の docker.io 依存だったため本 issue が顕在化した。
# mirror.gcr.io は Docker Hub 公式イメージのプルスルーキャッシュで、同一 digest（byte 等価）を返す。
# BASE_REGISTRY は上書き可（docker.io/library や社内 Harbor 等）。既定を渡さないため compose/MAPPING に
# build args は不要＝#275 ドリフト検査は 2 フィールドのまま緑。詳細は docs/adr/IADR-0081。
ARG BASE_REGISTRY=mirror.gcr.io/library
FROM ${BASE_REGISTRY}/node:22-alpine AS build
WORKDIR /app
# 依存解決に必要な manifest 群を先にコピーしてレイヤキャッシュを効かせる
COPY src/package.json src/pnpm-lock.yaml src/pnpm-workspace.yaml ./
COPY src/platform/frontend/package.json platform/frontend/
COPY src/knowledge/frontend/package.json knowledge/frontend/
# Issue #283, IADR-0070: 可変ユニット AST の frontend も workspace メンバ（--frozen-lockfile が
# pnpm-lock.yaml と突合するため manifest を先に投入する）。ソースは合成点（features/index.ts）から取り込まれる。
COPY src/ai-stock-trading/frontend/package.json ai-stock-trading/frontend/
# IADR-0121 決定 2: pnpm は package.json の packageManager フィールドから corepack が版を解決する。
RUN corepack enable && pnpm install --frozen-lockfile
COPY src/platform/frontend/ platform/frontend/
COPY src/knowledge/frontend/ knowledge/frontend/
COPY src/ai-stock-trading/frontend/ ai-stock-trading/frontend/
RUN pnpm run build

# IADR-0081: runtime も同じミラーから引く（docker.io 非依存）。BASE_REGISTRY は上の build ステージと
# 同じ ARG を FROM で再利用するため、複数ステージで有効な「最初の FROM より前で宣言した global ARG」。
FROM ${BASE_REGISTRY}/nginx:1.27-alpine AS runtime
# nginx テンプレート（BFF_UPSTREAM を envsubst で注入）と実行時 config テンプレート。
COPY src/platform/frontend/nginx.default.conf.template /etc/nginx/templates/default.conf.template
COPY src/platform/frontend/config.js.template /etc/microservices-platform/config.js.template
COPY src/platform/frontend/docker-entrypoint.d/40-render-config.sh /docker-entrypoint.d/40-render-config.sh
RUN chmod +x /docker-entrypoint.d/40-render-config.sh
COPY --from=build /app/platform/frontend/dist /usr/share/nginx/html
# 既定の BFF 上流（compose）。k8s は env で上書きする。
ENV BFF_UPSTREAM=http://bff:8080
EXPOSE 8080
