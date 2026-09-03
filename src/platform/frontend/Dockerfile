# FR-14, IADR-0056 / IADR-0121: フロントエンド（pnpm workspace: src/<unit>/frontend。platform=基盤アプリホスト、
# knowledge=可変機能）build → Caddy 配信。実行時 config で接続先を注入する（環境非依存ビルド）。
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
# build args は不要＝#275 ドリフト検査は 2 フィールドのまま緑。詳細は .ai-context/adr/IADR-0081。
ARG BASE_REGISTRY=mirror.gcr.io/library
FROM ${BASE_REGISTRY}/node:22-alpine AS build
WORKDIR /app
# 依存解決に必要な manifest 群を先にコピーしてレイヤキャッシュを効かせる
COPY src/package.json src/pnpm-lock.yaml src/pnpm-workspace.yaml ./
COPY src/platform/frontend/package.json platform/frontend/
COPY src/knowledge/frontend/package.json knowledge/frontend/
# IADR-0121 決定 4: 共有 UI パッケージ（@platform/ui）も workspace メンバ。
COPY src/packages/ui/package.json packages/ui/
# Issue #283, IADR-0070: 可変ユニット AST の frontend も workspace メンバ（--frozen-lockfile が
# pnpm-lock.yaml と突合するため manifest を先に投入する）。ソースは合成点（features/index.ts）から取り込まれる。
COPY src/ai-stock-trading/frontend/package.json ai-stock-trading/frontend/
# IADR-0121 決定 2: pnpm は package.json の packageManager フィールドから corepack が版を解決する。
RUN corepack enable && pnpm install --frozen-lockfile
COPY src/packages/ui/ packages/ui/
# ADR-0031 / IADR-0125: Lingui のマクロ（@lingui/babel-plugin-lingui-macro）はビルド時に
# workspace ルートの lingui.config.ts を読む。これが無いと Babel 変換が失敗し、
# 「gensync のスタックトレースだけが出て原因が読めない」形で pnpm run build が落ちる
# （PR #499 の CI で実測。ローカルでも本ファイルを退避すると同一のトレースを再現する）。
COPY src/lingui.config.ts ./
COPY src/platform/frontend/ platform/frontend/
COPY src/knowledge/frontend/ knowledge/frontend/
COPY src/ai-stock-trading/frontend/ ai-stock-trading/frontend/
RUN pnpm run build

# Issue #1135, ADR-0021 / IADR-0361: SPA の静的配信は **Caddy** である（計画 ADR-0021 の指定。#442 の残作業）。
# #782（IADR-0317）で入口だけが Istio Ingress Gateway へ移り、配信の側は nginx のままだった差分を閉じる。
# 配信・fallback・/bff プロキシの契約は Caddyfile が持つ（そちらを読むこと）。
#
# IADR-0081: runtime も同じミラーから引く（docker.io 非依存）。BASE_REGISTRY は上の build ステージと
# 同じ ARG を FROM で再利用するため、複数ステージで有効な「最初の FROM より前で宣言した global ARG」。
# タグは nginx 時代の 1.27-alpine と同じ**マイナー固定**に揃える（:2 だけだと major 内で任意に動く）。
FROM ${BASE_REGISTRY}/caddy:2.11-alpine AS runtime
# envsubst（gettext）は実行時 config の描画に使う。nginx 公式イメージは同梱していたが Caddy は持たない。
# sed で代替しないのは、値に含まれる & / \ のエスケープという新しい壊れ方を持ち込むためである。
RUN apk add --no-cache gettext
COPY src/platform/frontend/Caddyfile /etc/caddy/Caddyfile
COPY src/platform/frontend/config.js.template /etc/microservices-platform/config.js.template
COPY src/platform/frontend/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
COPY --from=build /app/platform/frontend/dist /usr/share/caddy
# 既定の BFF 上流（compose）。k8s は env で上書きする。Caddyfile の {$BFF_UPSTREAM} が読み込み時に展開する。
ENV BFF_UPSTREAM=http://bff:8080
EXPOSE 8080
# entrypoint が config.js を描画してから exec で Caddy へ渡す（描画に失敗したら起動しない）。
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
