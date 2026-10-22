# 経路B ローカルエッジ集約（opt-in・Traefik）

> 起点: [IADR-0091](../../../.ai-context/adr/IADR-0091_local-edge-aggregation-traefik.md) /
> 作業仕様書 [`.ai-context/specs/20260720_issue-356_local-edge-aggregation.md`](../../../.ai-context/specs/20260720_issue-356_local-edge-aggregation.md) / Issue #356

経路B（k3d / Rancher Desktop 内蔵 k3s）で、**platform フロント（SPA/BFF）を 80/443**、**管理ツール群を単一ポート
50000** に集約する **opt-in オーバーレイ**。ローカルは Istio 未導入（`values-local` は `edge.enabled=false`）のため、
既に稼働している **k3s 内蔵 Traefik** をエッジに使う（prod の Istio `templates/edge.yaml` とは別実装）。

> 🔴 **`ISTIO=1` を併用するときは、このオーバーレイの Traefik はエッジから降りる**（#782 / `ADR-0021`）。
> `kube-system` の Traefik はメッシュの外にあり、mesh 内の 3 Service（frontend / bff / wiki-js）へ
> **平文で入っている**。`PeerAuthentication` を STRICT にするとその平文が拒否され、**入口だけが 502 になる**。
> 計画 `ADR-0021` は「入口＝Istio Ingress Gateway・k3s 同梱 Traefik は無効化」と定めており、
> 経路B ではそれを [`../edge-istio/`](../edge-istio/) が実装する（`ISTIO=1` かつ `LOCALEDGE=1` のときだけ有効）。
> **メッシュを宣言していないクラスタ（`ISTIO=0`、または `ISTIO` 未指定の初回）なら本オーバーレイの挙動は 1 バイトも変わらない。**
> `ISTIO` 未指定でも、メッシュで動いているクラスタの再実行は `ISTIO=1` を引き継ぐ（#1713 / [IADR-0488](../../../.ai-context/adr/IADR-0488_istio-tristate-inherit-mesh-enabled-on-rerun.md)）。
> 判断と実測は [IADR-0317](../../../.ai-context/adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)。

## 構成

| ファイル | 役割 |
| --- | --- |
| `traefik-entrypoint.yaml` | k3s Traefik に追加 entrypoint `admin:50000` を定義（`HelmChartConfig`） |
| `platform-frontend-ingress.yaml` | 80/443（web/websecure）: `/bff`→bff-service、catch-all→frontend-service |
| `admin-ingress-infra.yaml` | 50000（admin）: grafana/headlamp/vault/qdrant をホスト名ベースで公開（platform-infra）。**`ADMIN_EDGE=1` のときだけ当たる**（下記「管理ツールへの入り方」） |
| `admin-ingress-wiki.yaml` | 50000（admin）: Wiki.js `wiki.localhost`→3000（microservices-platform ns・IADR-0095。OIDC は [wiki-oidc/README](../wiki-oidc/README.md)）。**`ADMIN_EDGE=1` のときだけ** |
| `argocd-ingress.yaml` | 50000（admin）: argocd-server（argocd ns 存在時、かつ **`ADMIN_EDGE=1` のときだけ**スクリプトが apply） |
| `tls/cert-manager-issuers.yaml` | エッジ TLS の CA（`ClusterIssuer(selfSigned)` → ルート CA `Certificate` → `ClusterIssuer(ca)`。IADR-0206） |
| `tls/edge-certificate.yaml` | 葉証明書 `edge-tls`（`dnsNames: localhost, *.localhost`。IADR-0206） |
| `tls/kustomization.yaml` | 上 2 つ。**親 kustomization には含めない** —— cert-manager の CRD が入る前に apply すると overlay 全体が落ちるため、スクリプトが「導入 → CRD Established 待ち → apply」の順で当てる |

## 有効化（opt-in・既定オフ）

```sh
LOCALEDGE=1 bash scripts/k8s-local-up.sh --live          # 必要に応じ OBSERVABILITY=1 HEADLAMP=1 VAULT=1 ARGOCD=1 を併記
```

`scripts/k8s-local-up.sh` は `LOCALEDGE=1` のとき、(1) k3d cluster を **80/443/50000 公開で作成**し、(2) 本オーバーレイを
適用する。**既定（未設定）は現行の 8080/8443・overlay 不適用でバイト等価**（後方互換・fail-safe）。

> **公開範囲（bind）**: k3d の公開は **loopback 固定**（`-p 127.0.0.1:80:80@loadbalancer` 等）とする。50000 には
> **認証なしの Qdrant** も集約されるため、既定で同一 LAN の第三者へ露出させない（閉域前提をコード側で担保）。
> LAN からアクセスさせたい場合のみ、利用者が明示的に bind host を広げる（自己責任）。Rancher Desktop は内蔵 LB の
> 公開設定に従う。

> 🔴 **`HelmChartConfig` の反映は待ち合わせて検査する（#953 / IADR-0258）**。`traefik-entrypoint.yaml` の効果は
> k3s の helm-controller が**非同期に**実現するため、`kubectl apply` が成功しても reconcile は失敗し得る
> （`expose` の values スキーマは traefik chart のバージョンで型が変わる）。`k8s-local-up.sh` は overlay 適用の直後に
> `kube-system/traefik` の `admin=50000` を最大 180 秒待ち、**来なければ非 0 で落ちる**（警告して続行はしない）。
> 落ちたら stderr の診断（実ポート・`helmchart` の状態・`helm-install-traefik` のログ）を読み、
> k3s の版（k3d 経路は `k8s-local-up.sh` の `K3S_IMAGE` の既定で固定する。#1843 / IADR-0519）を上書きしていないか確かめるか、
> `expose` を chart の版に合わせて切替える。
> **この門が確実に効くのはクラスタ作成直後である**（既存クラスタでは前回成功時の Service が残るため）。

### k3d はポートが cluster 作成時固定 → 既存クラスタは再作成が必要（ユーザー実行）

ポート公開は k3d の cluster **作成時**にしか設定できない（後付け不可）。既存クラスタに `LOCALEDGE` を効かせるには
**削除→再作成**する（破壊操作のため利用者が実行する）:

```sh
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh --live
```

### Rancher Desktop（内蔵 k3s）の差分

Rancher Desktop は k3d の `-p ...@loadbalancer` を使わず、内蔵 k3s の LoadBalancer サービスを **localhost へ自動公開**
する。`traefik-entrypoint.yaml` で admin:50000 を足せば Rancher が localhost:50000 を公開し、80/443 も Traefik LB
経由で公開される。**cluster 再作成は不要**で、overlay 適用のみでよい:

```sh
kubectl apply -k deploy/local/edge
# 管理ツールの経路は既定で置かない（下記「管理ツールへの入り方」）。置くのは明示の opt-in のときだけ:
#   kubectl apply -f deploy/local/edge/admin-ingress-infra.yaml -f deploy/local/edge/admin-ingress-wiki.yaml
#   kubectl get ns argocd >/dev/null 2>&1 && kubectl apply -f deploy/local/edge/argocd-ingress.yaml
```

## アクセス

- **platform フロント**: `https://localhost/`（SPA）・`https://localhost/bff/...`（BFF）。
  **cert-manager が発行する `edge-tls`** で終端する（IADR-0206・#779）。ルート CA を信頼ストアへ入れるまで
  ブラウザ警告は出るが、**CA を渡せば検証は通る**（下記「エッジ TLS」）。
  **`http://localhost/`（80）は https へ恒久リダイレクトする**（IADR-0220・#841。平文は残さない）。
- **管理ツール（50000・ホスト名ベース）**: 🔴 **既定では経路を置かない**（50000 は 404 / Istio のエッジは 403）。
  下の URL は **`ADMIN_EDGE=1` を明示したときだけ**使える。既定の入り方は次節「管理ツールへの入り方」の port-forward。
  - `https://grafana.localhost:50000`（OBSERVABILITY=1）
  - `https://headlamp.localhost:50000`（HEADLAMP=1）
  - `https://vault.localhost:50000`（VAULT=1）
  - `https://qdrant.localhost:50000`（dashboard は `/dashboard`。**SSO 非対応＝認証なし**・閉域前提）
  - `https://argocd.localhost:50000`（ARGOCD=1。argocd-server の `server.insecure` は ArgoCD OIDC 実装 #353 で設定）
  - `https://wiki.localhost:50000`（Wiki.js。Keycloak OIDC＝IADR-0095。管理UI 設定は [wiki-oidc/README](../wiki-oidc/README.md)）

### 管理ツールへの入り方（既定は port-forward。IADR-0586）

管理ツールの集約入口（50000）には、**既定で経路を置かない**（多層防御の強化。GHSA-j4qv-2w99-xg5p）。
`LOCALEDGE=1` の起動器（`k8s-local-up.sh`・`istio-edge-up.sh`・`istio-edge-down.sh`）は、`ADMIN_EDGE=1` が無ければ
6 host の経路を**消す**（以前の実行で置いた経路も、再実行で閉じる）。入口（50000 の listener）とポートの公開は残る。

**既定（推奨）: 各ツールへ `kubectl port-forward` で入る。** kube の資格情報（port-forward の権限）を持つ人だけが入れる。

```sh
kubectl -n platform-infra port-forward svc/grafana 3000:3000            # → http://localhost:3000（local admin でログイン。下記）
kubectl -n platform-infra port-forward svc/headlamp 4466:80             # → http://localhost:4466（SA トークン。realm にも登録済み）
kubectl -n platform-infra port-forward svc/vault 8200:8200              # → http://localhost:8200（root トークン。CLI の OIDC は localhost:8250）
kubectl -n platform-infra port-forward svc/qdrant 6333:6333             # → http://localhost:6333/dashboard
kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000    # → http://localhost:3300（realm に登録済み。Site URL は wiki-oidc/README）
kubectl -n argocd port-forward svc/argocd-server 8083:80                # → http://localhost:8083（realm に登録済み）
```

- Grafana は `root_url` が 50000 の URL に固定されているため、port-forward では SSO が完了しない。local admin（break-glass）で入る
  （[observability/README](../observability/README.md)）。Keycloak の管理（Admin REST）は下記「Keycloak の管理コンソール」と同じ port-forward。

**明示の opt-in: 従来どおり 50000 の URL（SSO つき）で使う。** 再実行のたびに付ける（付けない再実行は経路を消す）:

```sh
ADMIN_EDGE=1 LOCALEDGE=1 bash scripts/k8s-local-up.sh --live            # Istio のエッジなら ISTIO=1 も（istio-edge-up.sh へ渡る）
```

- 値は `1`（置く）・`0` か未指定（置かない。既定）だけを受け、それ以外は**クラスタに触る前に止まる**。
- opt-in で開けた入口は**認証なしの Qdrant を含む**。ホストの公開範囲（k3d は loopback 固定。Rancher Desktop は内蔵 LB の公開設定）を
  確かめてから使い、用が済んだら `ADMIN_EDGE` なしで再実行して閉じる。
- 送信元のアドレスで絞る形（許可リスト）は採らない。k3s の ServiceLB・k3d のロードバランサ・Rancher Desktop の転送を経ると、
  エッジに見える送信元は同じ内側のアドレスになり、区別できないためである（比較は IADR-0586）。
- 閉じていることは `scripts/check-stack-ready.js` の G4c がエッジ越しに測る（CI の integration-stack）。

### admin entrypoint (50000) も TLS 終端である（IADR-0220・#841）

`traefik-entrypoint.yaml` が `--entryPoints.admin.http.tls=true` を渡すため、**`admin:50000` は https である**。
管理ツールは必ず **`https://`** で開くこと。**平文 `http://<tool>.localhost:50000` で叩くと TLS ハンドシェイクに
失敗する**（「到達不可」に見える事象の典型原因である）。

計画 `NFR-11`（全経路の HTTPS 化・平文 HTTP を残さない）の適用範囲は利用者裁定 2026-08-16
（裁定依頼 planning#383）で**環境を問わない**と確定し、経路B も適用内になった。証明書の発行方式は
計画 `ADR-0047`（`*.localhost` では selfsigned CA を許容）が定める。**7 つの OIDC クライアントの
redirectUris は https へ揃えてある**（realm・`values-local.yaml`・`grafana.yaml`・
`argocd-cm-patch.yaml`・`vault/oidc/bootstrap.sh`）。

証明書は namespace ごとに要る（`spec.tls.secretName` は同 namespace の Secret しか参照できない）——
`microservices-platform` / `platform-infra` は `tls/edge-certificate.yaml`、`argocd` は
`tls/argocd-certificate.yaml`（ns 存在時のみ apply）が持つ。**Secret 名はいずれも `edge-tls`** である。

### エッジ TLS（cert-manager・IADR-0206・#779）

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

## Keycloak の管理コンソール（エッジに出さない。IADR-0575）

エッジ（Traefik の `keycloak-edge-admin-deny`・Istio の `msp-keycloak-edge` の先頭の route・chart の `keycloak-master-realm-deny`）は、
`keycloak.localhost` の **`/admin` と `/realms/master` を 403 で拒む**。利用者が使う realm の経路（`/realms/platform/...` のログイン・
トークン・アカウントコンソール、`/resources/...`）はこれまでどおり通る（`scripts/k8s-local-up.test.js` の模型と
`node scripts/check-stack-ready.js --live` の G4b が固定する）。

管理者はエッジを通らず、**port-forward 越しの Admin REST** か **Pod の中の `kcadm.sh`** で操作する:

```bash
kubectl -n platform-infra port-forward svc/keycloak 8080:8080
# → master realm の管理者のトークンを http://localhost:8080/realms/master/protocol/openid-connect/token で取り、
#   http://localhost:8080/admin/realms/<realm>/... を呼ぶ（資格情報は Secret platform-infra/keycloak-admin。端末に出さない）。
#   形は docs/operations/paired-secret-rotation-runbook.md の手順 1（port-forward と curl）と同じ。
```

- 🔴 **管理コンソール（ブラウザの画面）はエッジからは開けない。** 画面は `KC_HOSTNAME`（エッジ）の URL へ誘導されるため、
  port-forward で `http://localhost:8080/admin/` を開いても、ログインの途中でエッジ（拒否）へ飛ぶ。`KC_HOSTNAME_ADMIN` で
  画面だけを別の URL に出す形は、Admin REST のトークンの発行者を変えて in-cluster の Admin REST（realm の後追い・各サービス）を
  拒ませ得るので採らない（IADR-0575）。画面が要る作業は、Admin REST か kcadm.sh の手順に置き換える。
- CLI は Pod の中の `kcadm.sh` を使う（`kubectl -n platform-infra exec deploy/keycloak -- /opt/keycloak/bin/kcadm.sh …`。Pod の中から `http://localhost:8080` へ繋ぐ。
  資格情報は Pod の env から読み、端末に出さない。`docs/operations/paired-secret-rotation-runbook.md` の手順と同じ形）。
- realm の後追い（`deploy/local/keycloak-setup/reconcile-realm.sh`）と各サービスの Admin REST は in-cluster の
  `http://keycloak:8080` を使うので、エッジの拒否の影響を受けない。

## OIDC（集約後 URL）

issuer は **`https://keycloak.localhost/realms/platform`（エッジ host）**である（[IADR-0243](../../../.ai-context/adr/IADR-0243_keycloak-edge-issuer-migration.md)・#780。[IADR-0091](../../../.ai-context/adr/IADR-0091_local-edge-aggregation-traefik.md) 決定 5 の最小案「`keycloak:8080` 維持」は Supersede された）。ツール UI は従来どおり 50000 に集約する。

> **ブラウザが開く URL だけをエッジ host にし、ツールがサーバ側で叩く URL は in-cluster のまま**にする（Grafana の `TOKEN_URL`/`API_URL`・Wiki.js の `tokenURL`/`userInfoURL`）。
> ローカル CA を各コンテナへ配らずに済むためで、IADR-0086 が .NET へ入れた metadata / issuer 分離の一般化である。
> **分離できないツール**（ArgoCD・Vault）は 1 つの値が両方を決めるため、エッジ host ＋ CA を渡す設定を使う。

- **Grafana（PR-2 適用済み）**: realm `grafana` client の `redirectUris`/`webOrigins` に集約後 URL
  （`https://grafana.localhost:50000/login/generic_oauth` 等）を追加し、`GF_SERVER_ROOT_URL` を
  `https://grafana.localhost:50000/` に設定済み。**Grafana は `root_url` から一意に `redirect_uri` を生成する**ため、
  OIDC ログインの実効経路は **edge（`grafana.localhost:50000`・`LOCALEDGE=1` 前提）**。
  - ⚠️ **`LOCALEDGE` を使わず `port-forward svc/grafana 3000:3000` 単独で開いた場合、Keycloak 認証後の redirect は
    `grafana.localhost:50000` を指すため edge 未起動だと到達できず、OIDC ログインは完了しない**（realm には旧
    port-forward 用 redirect も残しているが、実際に使う redirect は `root_url` 側で一意に決まる）。この場合は
    **fail-safe の local admin（`admin`/`admin`）でログインする**（機密露出等のリスクは無い）。port-forward で OIDC を
    使いたい場合は `GF_SERVER_ROOT_URL` を `http://localhost:3000/` に戻す（realm の port-forward redirect は登録済み）。
- **ArgoCD（#359 適用済み）** / これから足す **Vault** 等の OIDC client は最初から 50000 URL で登録する。
- **platform フロント（SPA）/ Headlamp（#353 適用済み）**: 集約後 origin `https://localhost` の OIDC は
  **`bff` client が受ける**（`redirect_uri=https://localhost/bff/auth/callback`。BFF セッション方式・
  ADR-0032 / IADR-0273。**80 は https へ恒久リダイレクトするため origin は https である**・IADR-0220 / #841）。
  🔴 **［2026-09-11 更新 / #1393］従前ここは `platform-spa` client（SPA が `redirect_uri=<origin>/callback` を
  自分で送る形）だった。同 client は realm から撤去した**（IADR-0429）。`headlamp` client には
  `https://headlamp.localhost:50000/*` を `redirectUris`/`webOrigins` へ追加済み
  （IADR-0091/0033/0080 のフォローアップ。port-forward 用 URL は後方互換で残す）。

## 切り戻し

```sh
# TLS 資産は親 kustomization に含まれていないので個別に消す（IADR-0206 決定 5）。
# ClusterIssuer は cluster-scoped なので、これを飛ばすと残留する。
kubectl delete -k deploy/local/edge/tls

# ★ Certificate を消しても Secret は消えない（下記）。CA 秘密鍵が残るので明示的に消す。
kubectl -n cert-manager delete secret local-edge-root-ca
kubectl -n microservices-platform delete secret edge-tls

kubectl delete -k deploy/local/edge
kubectl -n kube-system delete helmchartconfig traefik   # admin:50000 を撤去（Traefik が既定 values で再適用される）

# cert-manager 本体まで戻すなら（CRD・webhook・Deployment を撤去する。他の用途で使っていないことを確認してから）
kubectl delete -k deploy/local/edge/cert-manager   # 版の URL ＋ 内側のイメージの digest（入れたときと同じ定義）
```

> **`kubectl delete -k deploy/local/edge` だけでは TLS 資産が残る。**
> `tls/kustomization.yaml` は**意図的に親へ含めていない**（cert-manager の CRD が入る前に
> 親を apply すると overlay 全体が落ちるため。`IADR-0206` 決定 5）。その裏返しとして、
> 切り戻しでも**個別に消す必要がある** —— `ClusterIssuer`（`local-edge-selfsigned` /
> `local-edge-ca`）は cluster-scoped、`Certificate`（`local-edge-root-ca` / `edge-tls`）は
> それぞれ `cert-manager` / `microservices-platform` namespace に残る。

> **★ さらに、`Certificate` を消しても `Secret` は消えない。** cert-manager は
> `--enable-certificate-owner-ref`（既定 `false`）を付けない限り、発行した Secret へ
> `Certificate` への `ownerReference` を張らない。**本オーバーレイはこのフラグを付けていない。**
> **実測（2026-08-16）**: `kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.metadata.ownerReferences}'`
> と `kubectl -n microservices-platform get secret edge-tls -o …` はいずれも**空**を返し、
> `cert-manager` Deployment の args にも `--enable-certificate-owner-ref` は無い。
> つまり **`local-edge-root-ca`（ルート CA の秘密鍵を含む）と `edge-tls` は cascade delete されず残る。**
> 上のコマンドで明示的に消すこと。

k3d のポートを元（8080/8443）へ戻すにはクラスタ再作成（`LOCALEDGE` 未設定で `k8s-local-up.sh`）。
**クラスタを作り直すなら上の個別削除は要らない**（全部消える）。

## Tier 境界

本オーバーレイはローカル検証用。**本番相当のエッジ（Istio）・稼働率は Tier 3**（対象外）。

**エッジ TLS は Tier 3 から外れた**（IADR-0206・#779）—— cert-manager 発行の `edge-tls` で 443 を終端する。
**admin(50000) の TLS 化も Tier 3 から外れた**（IADR-0220・#841。計画 `NFR-11` の適用範囲が経路B にも及ぶと確定したため）。
ただし CA は selfsigned であり、**公的 CA（Let's Encrypt）による証明書は依然として Tier 3** である
（`*.localhost` にはドメイン所有を検証できないため原理的に発行できない）。
本番の CA 差し替えは計画 `ADR-0023` のとおり `ClusterIssuer` を足して `issuerRef` を変えるだけで済む。
