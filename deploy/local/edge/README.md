# 経路B ローカルエッジ集約（opt-in・Traefik）

> 起点: [IADR-0091](../../../docs/adr/IADR-0091_local-edge-aggregation-traefik.md) /
> 作業仕様書 [`docs/specs/20260720_issue-356_local-edge-aggregation.md`](../../../docs/specs/20260720_issue-356_local-edge-aggregation.md) / Issue #356

経路B（k3d / Rancher Desktop 内蔵 k3s）で、**platform フロント（SPA/BFF）を 80/443**、**管理ツール群を単一ポート
50000** に集約する **opt-in オーバーレイ**。ローカルは Istio 未導入（`values-local` は `edge.enabled=false`）のため、
既に稼働している **k3s 内蔵 Traefik** をエッジに使う（prod の Istio `templates/edge.yaml` とは別実装）。

## 構成

| ファイル | 役割 |
| --- | --- |
| `traefik-entrypoint.yaml` | k3s Traefik に追加 entrypoint `admin:50000` を定義（`HelmChartConfig`） |
| `platform-frontend-ingress.yaml` | 80/443（web/websecure）: `/bff`→bff-service、catch-all→frontend-service |
| `admin-ingress-infra.yaml` | 50000（admin）: grafana/headlamp/vault/qdrant をホスト名ベースで公開（platform-infra） |
| `admin-ingress-minio.yaml` | 50000（admin）: MinIO Console `minio.localhost`→9001（microservices-platform ns・IADR-0093。OIDC は [minio-oidc/README](../minio-oidc/README.md)） |
| `admin-ingress-wiki.yaml` | 50000（admin）: Wiki.js `wiki.localhost`→3000（microservices-platform ns・IADR-0095。OIDC は [wiki-oidc/README](../wiki-oidc/README.md)） |
| `argocd-ingress.yaml` | 50000（admin）: argocd-server（argocd ns 存在時のみスクリプトが条件付き apply） |
| `tls/cert-manager-issuers.yaml` | エッジ TLS の CA（`ClusterIssuer(selfSigned)` → ルート CA `Certificate` → `ClusterIssuer(ca)`。IADR-0204） |
| `tls/edge-certificate.yaml` | 葉証明書 `edge-tls`（`dnsNames: localhost, *.localhost`。IADR-0204） |
| `tls/kustomization.yaml` | 上 2 つ。**親 kustomization には含めない** —— cert-manager の CRD が入る前に apply すると overlay 全体が落ちるため、スクリプトが「導入 → CRD Established 待ち → apply」の順で当てる |

## 有効化（opt-in・既定オフ）

```sh
LOCALEDGE=1 bash scripts/k8s-local-up.sh          # 必要に応じ OBSERVABILITY=1 HEADLAMP=1 VAULT=1 ARGOCD=1 を併記
```

`scripts/k8s-local-up.sh` は `LOCALEDGE=1` のとき、(1) k3d cluster を **80/443/50000 公開で作成**し、(2) 本オーバーレイを
適用する。**既定（未設定）は現行の 8080/8443・overlay 不適用でバイト等価**（後方互換・fail-safe）。

> **公開範囲（bind）**: k3d の公開は **loopback 固定**（`-p 127.0.0.1:80:80@loadbalancer` 等）とする。50000 には
> **認証なしの Qdrant** も集約されるため、既定で同一 LAN の第三者へ露出させない（閉域前提をコード側で担保）。
> LAN からアクセスさせたい場合のみ、利用者が明示的に bind host を広げる（自己責任）。Rancher Desktop は内蔵 LB の
> 公開設定に従う。

### k3d はポートが cluster 作成時固定 → 既存クラスタは再作成が必要（ユーザー実行）

ポート公開は k3d の cluster **作成時**にしか設定できない（後付け不可）。既存クラスタに `LOCALEDGE` を効かせるには
**削除→再作成**する（破壊操作のため利用者が実行する）:

```sh
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
```

### Rancher Desktop（内蔵 k3s）の差分

Rancher Desktop は k3d の `-p ...@loadbalancer` を使わず、内蔵 k3s の LoadBalancer サービスを **localhost へ自動公開**
する。`traefik-entrypoint.yaml` で admin:50000 を足せば Rancher が localhost:50000 を公開し、80/443 も Traefik LB
経由で公開される。**cluster 再作成は不要**で、overlay 適用のみでよい:

```sh
kubectl apply -k deploy/local/edge
# argocd を併用しているなら:
kubectl get ns argocd >/dev/null 2>&1 && kubectl apply -f deploy/local/edge/argocd-ingress.yaml
```

## アクセス

- **platform フロント**: `http://localhost/`（SPA）・`http://localhost/bff/...`（BFF）。`https://localhost/` は
  **cert-manager が発行する `edge-tls`** で終端する（IADR-0204・#779）。ルート CA を信頼ストアへ入れるまで
  ブラウザ警告は出るが、**CA を渡せば検証は通る**（下記「エッジ TLS」）。http(80) はそのまま残してある。
- **管理ツール（50000・ホスト名ベース）**:
  - `http://grafana.localhost:50000`（OBSERVABILITY=1）
  - `http://headlamp.localhost:50000`（HEADLAMP=1）
  - `http://vault.localhost:50000`（VAULT=1）
  - `http://qdrant.localhost:50000`（dashboard は `/dashboard`。**SSO 非対応＝認証なし**・閉域前提）
  - `http://argocd.localhost:50000`（ARGOCD=1。argocd-server の `server.insecure` は ArgoCD OIDC 実装 #353 で設定）
  - `http://minio.localhost:50000`（MinIO Console。Keycloak OIDC＝IADR-0093。ポリシー適用は [minio-oidc/README](../minio-oidc/README.md)）
  - `http://wiki.localhost:50000`（Wiki.js。Keycloak OIDC＝IADR-0095。管理UI 設定は [wiki-oidc/README](../wiki-oidc/README.md)）

### ⚠️ admin entrypoint (50000) は **平文 http のみ**（IADR-0103）

`traefik-entrypoint.yaml` が足す `admin:50000` には **TLS 終端を設定していない**。したがって
**`https://<tool>.localhost:50000` は 404 になる**（Traefik に https ルートが無いため）。管理ツールは必ず
**`http://`** で開くこと。「到達不可」に見える事象の典型原因である（実際に Vault で発生）。

platform フロントの 443（`https://localhost/`）は別扱いで、**cert-manager 発行の `edge-tls` で終端する**
（IADR-0204・#779）。**admin entrypoint の TLS 化は依然としてスコープ外**である —— 7 つの OIDC クライアントの
redirectUris 追記に波及するため、Keycloak をエッジへ出す **#780 と同時に**扱う。

### エッジ TLS（cert-manager・IADR-0204・#779）

`LOCALEDGE=1` が cert-manager を導入し、`ClusterIssuer(selfSigned)` → ルート CA → `ClusterIssuer(ca)` の
**2 段**で葉証明書 `edge-tls`（`dnsNames: localhost, *.localhost`）を発行する。
**2 段にするのは、ルート CA を Secret として安定させるため**である —— これを apiserver の `--oidc-ca-file`（#781）と
backend の信頼ストア（#780）へ渡す。Traefik 既定の自己署名は Secret 化されず再起動ごとに変わるので使えない。

ルート CA の取り出しと検証:

```bash
kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > ca.crt
openssl s_client -connect 127.0.0.1:443 -servername localhost -CAfile ca.crt </dev/null 2>/dev/null \
  | grep 'Verify return code'          # => 0 (ok)
```

**Windows の curl は schannel バックエンドで `--cacert` によるカスタム CA 検証ができない**（curl 側の制約）。
検証は上の `openssl s_client` で行い、HTTP の疎通だけを見るなら `curl --ssl-no-revoke` を使う。

ブラウザ警告を消したい場合は、取り出した `ca.crt` を OS / ブラウザの信頼ストアへ入れる（**任意**。
自動化しない —— 目的は警告を消すことではなく、検証可能な CA を k8s 内に置くことである）。
mkcert を使う手もあるが、**CA が開発者マシン固有でリポジトリから再現できない**ため既定にはしていない。

`*.localhost` のワイルドカードは **1 段のサブドメインしか覆わない**（`a.b.localhost` は対象外）。
現行のホストはすべて 1 段なので問題にならない。

### ホスト名解決の注意（`*.localhost` / CLI）

- **ブラウザ**（Chrome/Edge/Firefox/Safari）は `*.localhost` を 127.0.0.1 に自動解決するため、UI アクセスは追加設定不要。
- **CLI**（`argocd` / `vault` 等）や一部 OS リゾルバは `*.localhost` を解決しないことがある（特に Windows）。その場合は
  hosts に追記するか、ワイルドカード DNS を使う:
  - hosts（`C:\Windows\System32\drivers\etc\hosts` / `/etc/hosts`）: `127.0.0.1 grafana.localhost argocd.localhost vault.localhost headlamp.localhost qdrant.localhost`
  - もしくは `grafana.127.0.0.1.nip.io:50000` 等の `*.nip.io` / `*.sslip.io`（hosts 編集不要・ワイルドカード解決）。

## OIDC（集約後 URL）

issuer は最小案（`http://keycloak:8080`・[README 手順A](../README.md)）を維持し、ツール UI のみ 50000 に集約する。

- **Grafana（PR-2 適用済み）**: realm `grafana` client の `redirectUris`/`webOrigins` に集約後 URL
  （`http://grafana.localhost:50000/login/generic_oauth` 等）を追加し、`GF_SERVER_ROOT_URL` を
  `http://grafana.localhost:50000/` に設定済み。**Grafana は `root_url` から一意に `redirect_uri` を生成する**ため、
  OIDC ログインの実効経路は **edge（`grafana.localhost:50000`・`LOCALEDGE=1` 前提）**。
  - ⚠️ **`LOCALEDGE` を使わず `port-forward svc/grafana 3000:3000` 単独で開いた場合、Keycloak 認証後の redirect は
    `grafana.localhost:50000` を指すため edge 未起動だと到達できず、OIDC ログインは完了しない**（realm には旧
    port-forward 用 redirect も残しているが、実際に使う redirect は `root_url` 側で一意に決まる）。この場合は
    **fail-safe の local admin（`admin`/`admin`）でログインする**（機密露出等のリスクは無い）。port-forward で OIDC を
    使いたい場合は `GF_SERVER_ROOT_URL` を `http://localhost:3000/` に戻す（realm の port-forward redirect は登録済み）。
- **ArgoCD（#359 適用済み）** / これから足す **Vault** 等の OIDC client は最初から 50000 URL で登録する。
- **platform フロント（SPA）/ Headlamp（#353 適用済み）**: realm `platform-spa` client に集約後 origin
  `http://localhost/*`（SPA は `redirect_uri=<origin>/callback` を送る。callback パス＝`/callback`）を
  `redirectUris`/`webOrigins`/`post.logout.redirect.uris` へ、`headlamp` client に `http://headlamp.localhost:50000/*`
  を `redirectUris`/`webOrigins` へ追加済み（IADR-0091/0033/0080 のフォローアップ。port-forward 用 URL は後方互換で残す）。

## 切り戻し

```sh
kubectl delete -k deploy/local/edge
kubectl -n kube-system delete helmchartconfig traefik   # admin:50000 を撤去（Traefik が既定 values で再適用される）
```

k3d のポートを元（8080/8443）へ戻すにはクラスタ再作成（`LOCALEDGE` 未設定で `k8s-local-up.sh`）。

## Tier 境界

本オーバーレイはローカル検証用。**本番相当のエッジ（Istio）・稼働率は Tier 3**（対象外）。

**エッジ TLS は Tier 3 から外れた**（IADR-0204・#779）—— cert-manager 発行の `edge-tls` で 443 を終端する。
ただし CA は selfsigned であり、**公的 CA（Let's Encrypt）による証明書は依然として Tier 3** である
（`*.localhost` にはドメイン所有を検証できないため原理的に発行できない）。
本番の CA 差し替えは計画 `ADR-0023` のとおり `ClusterIssuer` を足して `issuerRef` を変えるだけで済む。
