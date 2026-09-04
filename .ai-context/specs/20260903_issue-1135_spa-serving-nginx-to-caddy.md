---
title: 作業仕様書 — SPA 静的配信を nginx から Caddy へ移す（#442 の残作業）
type: spec
status: draft
related_ids: [FR-14, NFR, NFR-11, ADR-0021, ADR-0023, IADR-0076, IADR-0078, IADR-0081, IADR-0317, IADR-0348, IADR-0362]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
issue: "#1135"
---

# 作業仕様書: SPA 静的配信の Caddy 移送（#1135）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-14**（構成変更で完結する疎結合ユニット・合成点。SPA の配信入口）
- 非機能（NFR）: エッジ集約・可用性／`NFR-11`（平文 HTTP を残さない。外部 TLS は入口 Gateway が終端し、
  本コンテナは mesh 内の素の HTTP に専念する）
- 関連 ADR（計画）: **`ADR-0021`**（エッジ ＝ Istio Ingress Gateway（入口）＋ **Caddy（SPA 配信）**・`Accepted`。
  「静的配信の位置づけ」節が「SPA の実体配信は Caddy（mesh 内サービス）が担う」と明記する）／
  `ADR-0023`（エッジ証明書自動化。本 PR は入口の TLS を触らないため前提としてのみ参照）
- 前提 IADR（覆さない）:
  - `IADR-0076` 決定 1: **`/bff/*` に rewrite を張らない**（元 URI 無改変で上流へ渡す）。
    エッジ VS と compose と frontend の 3 箇所で同じ契約を保つ。
  - `IADR-0078` 決定 2: **liveness=`/`・readiness=`/config.js`**。readiness は「実行時 config の生成が
    完了したこと」を確かめる fail-safe である。本 PR で **probe と Service とポートは変えない**。
  - `IADR-0081`: base イメージは `docker.io` を直参照せず `mirror.gcr.io/library` を既定にする
    （`BASE_REGISTRY` で上書き可）。Caddy も同じ経路で引く。
  - `IADR-0317`: 入口は Istio Ingress Gateway。**Caddy はまだ入っていない**と同 IADR が明記し、
    #442 の残作業として残していた。本 PR がそれを閉じる。
  - `IADR-0348`: エッジの `/private-notes/sync/` 前置。**この前置に当たらないパスは catch-all で SPA へ落ち、
    404 ではなく index.html の 200 になる** —— その挙動を担うのが本コンテナの history fallback である。
- Issue: **#1135**（親 **#442** の受け入れ観点の最後の 1 件）

## 目的・背景

計画 `ADR-0021` はエッジを「Istio Ingress Gateway（入口・Envoy）＋ **Caddy**（SPA 静的配信）」と定めている。
#782（`IADR-0317`）で入口の側は Istio Ingress Gateway へ移ったが、**SPA を配る Web サーバは nginx のままである**。
リポジトリ側も 2 箇所（`deploy/local/edge-istio/README.md`「既知の限界」・`IADR-0317`「結果」）で
これを #442 の残作業と明記していた。本 PR はその差分を閉じる。

🔴 **「静的ファイルを配るだけ」ではない。** 現行の `nginx.default.conf.template` は次の 3 つを同時に担っており、
移送先で**同じ契約**を満たす必要がある。

1. `/bff/*` を `${BFF_UPSTREAM}` へ **元 URI 無改変**でプロキシする（`IADR-0076` 決定 1）
2. 実行時 config（`config.js`）を環境変数から生成し、`Cache-Control: no-store` で返す
3. SPA の history fallback（未知パス → `index.html`。TanStack Router のクライアントルーティング）

## 母集合（自分で引いた。issue の数えは転記していない）

`git grep -il "nginx" -- . ':!CHANGELOG.md'` が **36 ファイル**。加えて規則 9 に従い、
誤りの側の文字列（`usr/share/nginx` / `nginx.default.conf` / `docker-entrypoint.d` / `try_files` /
`proxy_pass` / `1.27-alpine`）でも走査した（`.ai-context` 除く。17 行・上の集合の部分集合）。
`caddy` 側も対で引いた（`git grep -il "caddy"` = 10 ファイル。うち live は
`deploy/local/edge-istio/README.md` 1 件だけ ＝ issue の実測と一致）。

### 追随する（live な権威文書とコード）— 15 ファイル

| ファイル | 何が nginx 前提か |
| --- | --- |
| `src/platform/frontend/Dockerfile` | runtime stage が `nginx:1.27-alpine`。配置先 `/usr/share/nginx/html`・`/etc/nginx/templates` |
| `src/platform/frontend/nginx.default.conf.template` | 配信・fallback・`/bff` プロキシの本体（→ `Caddyfile` へ置換） |
| `src/platform/frontend/docker-entrypoint.d/40-render-config.sh` | nginx 公式イメージの `/docker-entrypoint.d/*.sh` 規約に依存。出力先も nginx の root |
| `src/platform/frontend/README.md` | ファイル一覧に `nginx.default.conf.template` |
| `deploy/docker-compose.yml` | frontend サービスの説明コメント 3 箇所 |
| `deploy/helm/microservices-platform/templates/frontend.yaml` | 説明コメント 4 箇所（probe の根拠が `location = /config.js` の記法で書かれている） |
| `deploy/helm/microservices-platform/templates/edge.yaml` | 「フロント nginx の `proxy_pass` と同契約」「`try_files … /index.html`」2 箇所 |
| `deploy/helm/microservices-platform/values.yaml` | `frontend:` ブロックの説明コメント **5 箇所**（`extraEnv:` の説明が `40-render-config.sh` を名指ししていた 1 箇所を**検証中に追加で見つけた**。走査語に `40-render-config` を含めていたのに拾い切れていなかった＝**数えの誤り**であり、規則 10 の引き直しで捕まえた） |
| `deploy/local/values-local.yaml` | `bffUpstream` の説明 1 箇所 |
| `deploy/local/README.md` | port-forward 手順の説明 2 箇所 |
| `deploy/local/edge-istio/README.md` | 「既知の限界: SPA 配信はまだ nginx である」← **本 PR が消す当事者** |
| `deploy/local/edge-istio/virtualservice-app.yaml` | catch-all の説明 2 箇所（`IADR-0348` の 401/200 の対の説明） |
| `deploy/local/edge/platform-frontend-ingress.yaml` | Traefik 経路側の同型説明 1 箇所 |
| `docs/how-to/local-development.md` | 公開 URL 表と切り分け表 2 箇所 |
| `src/platform/backend/Services/LlmGateway/.../CompleteStream/Endpoint.cs` | `X-Accel-Buffering: no` のコメントが「nginx でのバッファリング抑止」。**規則 10 で引き直して見つけた**（本 PR で前段が Caddy になるため新たに不正確になる。ヘッダ自体は Caddy も解釈するので値は変えない） |

さらに索引 1 件（`.ai-context/adr/README.md`）へ IADR 行を足す。

### 追随しない（除外）— 理由つき

| 対象 | 除外理由 |
| --- | --- |
| `deploy/helm/microservices-platform/values.yaml:822` `className: nginx` | **Wiki.js の Ingress クラス名**であって SPA 配信ではない。既定 `enabled: false`。本件と無関係 |
| `deploy/local/edge-istio/gateway.yaml:4` | 計画 `ADR-0021` 本文の**引用**（不採用案「Traefik / NGINX Ingress 等」）。引用は書き換えない |
| `docs/tech/system-architecture.md:49,220` 「NGINX Ingress」 | **入口層**の記述であって SPA 配信ではない。#782（`IADR-0317`）で Istio へ移った時点で既に陳腐化しており、**本 PR の変更で新たに誤りになるわけではない**（規則 10 の対象外）。別件として報告する |
| `.ai-context/adr/IADR-00**.md`・`.ai-context/specs/2026*.md`（20 ファイル） | **確定済みの凍結記録**。本文を後から書き換えない（`traceability.repo.md`「凍結の射程」）。現況の正は新 IADR が持つ |
| `.ai-context/adr/README.md:144`（IADR-0088 行） | 同上（過去の裁定の要約）。**新規行の追加のみ**行う |
| `CHANGELOG.md` | 自動生成物。手で書き足さない（CLAUDE.md「補助成果物の自動生成」） |

## 対象範囲

- **対象**: `src/platform/frontend/**`（Dockerfile・配信設定・エントリポイント）／上表 15 ファイルの追随／
  実装 ADR（`IADR-0362`）と索引
- **対象外**: 入口（Istio Gateway / VirtualService / Traefik）のルーティング宣言の**中身**（コメント以外）／
  probe・Service・ポート（`IADR-0078` 決定 2 を維持）／`images.yml`・`check-image-mapping.js`・
  `k8s-local-images.sh` の配線（compose の build 定義から導出しており、**Dockerfile の中身が変わっても
  起動条件・必須チェック名は変わらない**。変えない）／`docs/tech/system-architecture.md` の入口層記述

## 設計

### 決定 1: runtime stage を `caddy:2.11-alpine`（`mirror.gcr.io/library` 経由）にする

`IADR-0081` の `BASE_REGISTRY` 機構をそのまま使う。タグは nginx の `1.27-alpine` と同じ**マイナー固定**に揃える
（`:2` だけだと major 内の任意のマイナーへ動く）。

### 決定 2: 配信設定は `Caddyfile` 1 枚。3 つの契約を `handle` で排他に書く

```
:8080 {
    root * /usr/share/caddy
    handle /healthz    { respond "ok" 200 }
    handle /config.js  { header Cache-Control "no-store"; file_server }
    handle /bff/*      { reverse_proxy {$BFF_UPSTREAM} }
    handle             { try_files {path} /index.html; file_server }
}
```

- **`handle_path` を使わない。** `handle_path` は一致した前置を**剥がす**。剥がすと `IADR-0076` 決定 1 の
  「rewrite を張らない」契約が破れ、BFF が `/bff` 付きで受ける前提と食い違う。`handle` は剥がさない。
- **`{$BFF_UPSTREAM}` は Caddy が設定読み込み時に環境変数から展開する。** nginx が必要としていた
  `envsubst` によるテンプレート描画（`/etc/nginx/templates/`）が不要になる。
- `Host` ヘッダは Caddy の `reverse_proxy` が既定で**元のまま上流へ渡す**（nginx の
  `proxy_set_header Host $host` と同じ）。`X-Forwarded-For` / `X-Forwarded-Proto` も既定で付く。
  **`X-Real-IP` だけは付かない**が、`git grep -ci "X-Real-IP\|X-Forwarded" -- src/platform/backend` は **0 件**
  （そもそも転送ヘッダを読む実装が無い）ため落とす。**陰性結論には陽性対照を対で置く**:
  同じ走査で `X-Accel-Buffering` は 1 件当たる ＝ 走査そのものは壊れていない。
- **`/healthz`** を足すが、**probe は変えない**（`IADR-0078` 決定 2 を維持）。手動スモークと将来の
  切り分け用の口であり、readiness の意味（config 生成完了の確認）を薄めないため readiness には使わない。
- セキュリティヘッダは **SPA を壊さない範囲**に限る: `X-Content-Type-Options: nosniff` /
  `Referrer-Policy: same-origin` / `X-Frame-Options: DENY`。**CSP は入れない**（実測の裏付けなしに
  入れると静かに機能が欠ける。別 issue の仕事である）。

### 決定 3: 実行時 config の生成はコンテナ起動時のまま。エントリポイントを自前で持つ

Caddy 公式イメージは nginx の `/docker-entrypoint.d/*.sh` 規約を持たない。`docker-entrypoint.sh` を 1 枚置き、
`config.js` を描画してから `exec "$@"` で Caddy を起動する。

- **`envsubst` は使い続ける**（`gettext` を `apk add`）。置換の意味論を現行とバイト等価に保つためで、
  `sed` で書き直すと値に含まれる `&` / `\` のエスケープという新しい壊れ方を持ち込む。
- **生成に失敗したらコンテナが起動しない**（`set -eu` ＋ `exec`）。readiness が 404 で落ちる現行の
  fail-safe より早く落ちる方向であり、弱くはならない。
- **Caddy の `templates` ディレクティブでリクエスト時に描画する案は採らない。** それだと `config.js` が
  常に 200 になり、`IADR-0078` 決定 2 の readiness が「生成完了の確認」でなくなる（静かに検査が失効する）。

### 決定 4: 圧縮は `encode zstd gzip` を入れる

`ADR-0021` が Caddy を選んだ理由に圧縮を挙げている。nginx 側は無圧縮だったので改善方向の差分である。
**`brotli` は入れない** —— Caddy v2 の標準配布バイナリに brotli エンコーダは含まれない
（`ADR-0021`「理由」の「brotli 標準装備」は現行の Caddy に対しては不正確。**決定ではなく理由の記述**なので
本 PR は決定に従い、事実誤認は報告する）。

## 受け入れ基準

- [x] SPA が Caddy から配信され、`https://localhost/` が **200 `text/html`**（**証明書検証を有効にした curl。`-k` を使わない**）
- [x] 深いパス（`/settings`）が history fallback で 200 `text/html`
- [x] **陰性対照**: 存在しないパス（`/no-such-path-xyzzy`）も index.html で 200 ＝ fallback が効いている証明
- [x] **陽性対照**: 実アセット（`/assets/index-*.js`）は fallback せず 86,583B の JS が出る
- [x] `/config.js` が 200 かつ `Cache-Control: no-store`、`window.__APP_CONFIG__` が env で描画済み
- [x] `/bff/auth/me` が **401**（元 URI 無改変で BFF へ抜ける。`IADR-0076` 決定 1）
- [x] `/private-notes/sync/manifest` が **401**（`IADR-0348` の前置が生きている）
- [x] **`scripts/verify-oidc-edge-flow.sh` の PASS 数が移送の前後で減らない**（issue #1135 の受け入れ基準。
      実測: nginx **PASS 19 / FAIL 0**・Caddy **PASS 19 / FAIL 0**。段 11/11 を完走）
- [x] `node scripts/check-static-egress.js --require src/platform/frontend/dist` が緑（外部 CDN・フォント・analytics ゼロ）
- [x] Playwright スモーク（`e2e/*.smoke.spec.ts`）が緑（**47 passed**）
- [x] `check-image-mapping` / `check-doc-links` / `check-trace-blocks` /
      `REQUIRE_REPO_TESTS=1 scripts.test.js`（**677 passed**）が緑。`check-deploy-manifests` は
      `kubeconform` が本機に無く SKIP のため、**`helm template` の HEAD 対比**で代替した（差分はコメント行のみ＝
      描画されるリソースは無改変）
- [x] compose の build 定義（`images.yml` の導出元）が新 Dockerfile で通る（`context: ..` /
      `dockerfile: src/platform/frontend/Dockerfile` のまま実ビルドが成立。**build 定義そのものは無改変**なので
      `images.yml` の起動条件も必須チェック名（`image-build`）も変わらない）
- [x] イメージサイズ・起動時間を before/after で実測して記録する
- [x] 移送の判断を `IADR-0362` に残す（`ADR-0021` の Caddy 指定への追随であることを明記）

## テスト方針

- **配信契約は稼働 k3s に対する実測**で確かめる（単体テストで置き換えられない層である）。
  frontend の Deployment **だけ**をローカルビルドの新イメージへ差し替え、他 Pod は再起動しない。
  TLS はエッジ CA を渡して検証する（Windows/schannel の制約で `--cacert` の chain 検証が効かないため、
  chain の正は `openssl s_client -CAfile` が持ち、curl は `--ssl-no-revoke`（失効照会のみ無効）で叩く。
  **`-k` は使わない**）。
- **陰性対照を陽性対照と対で置く**: 「未知パスが 200 index.html」は fallback の陽性側であると同時に
  「API に届いていない」の陰性側でもある。`IADR-0348` が定めたとおり、`/private-notes/sync/manifest` の
  401 と**対で**読む。
- Playwright スモークは `vite preview`（`playwright.config.ts` の `webServer`）に対して走る。
  **これはビルド成果物の退行検知であって Caddy の検証ではない** —— 配信サーバの検証は上の実測が担う。
  この区別を PR 本文にもそのまま書く（緑の意味を大きく言わない）。

## 計画書との差異

- 差異: **あり（軽微・理由節の事実誤認）**。`ADR-0021`「理由」が「Caddy は …… brotli 圧縮を標準装備し」と
  書くが、Caddy v2 の標準配布バイナリに brotli エンコーダは無い（`encode` は gzip / zstd）。
  **決定（SPA 配信 ＝ Caddy）には影響しない**ため本 PR は決定に従い、事実誤認は報告に載せる。
- 差異: **なし**（`ADR-0021`「静的配信の位置づけ」の指定にそのまま従う）。

## 未決事項

- `deploy/local/edge/`（Traefik 経路）と `deploy/local/edge-istio/`（Istio 経路）の**エッジ宣言が 2 つある**
  という既知の限界（`IADR-0317`）は本 PR では解消しない。コメントの追随のみ行う。
- `docs/tech/system-architecture.md` の「NGINX Ingress」（入口層）は #782 の時点で陳腐化しており、
  本 PR の射程外。別 issue の候補として報告する。
