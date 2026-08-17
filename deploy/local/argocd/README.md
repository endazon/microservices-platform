# 経路B ローカル GitOps（ArgoCD）ブートストラップ（opt-in）

> 起点: [ADR-0006](../../../docs/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST#24）

ArgoCD 本体は大きな公式 install manifest を URL 適用するため、ここでは**ブートストラップ手順**のみを置く
（既存の [`deploy/argocd`](../../argocd/README.md) の `Application`/`AppProject` を再 vendoring しない）。
すべて **opt-in**。`scripts/k8s-local-up.sh` の `ARGOCD=1` が下記を実施する。

## ブートストラップ

```sh
# 1) ArgoCD 本体（一度だけ・URL 適用・server-side apply）
kubectl create namespace argocd --dry-run=client -o yaml | kubectl apply -f -
kubectl apply --server-side --force-conflicts -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml

# 2) MSP の Application/AppProject
kubectl apply -f deploy/argocd/appproject.yaml
kubectl apply -f deploy/argocd/application.yaml

# 3) AST の Application/AppProject（連結時・submodule 配下）
kubectl apply -f src/ai-stock-trading/deploy/argocd/appproject.yaml
kubectl apply -f src/ai-stock-trading/deploy/argocd/application.yaml
```

> **なぜ `--server-side`（Issue #348）**: ArgoCD 公式 install manifest は巨大な CRD
> （`applicationsets.argoproj.io` 等）を含む。client-side apply は manifest 全体を
> `kubectl.kubernetes.io/last-applied-configuration` annotation に格納するため、その CRD で annotation の
> 262144 バイト上限を超過し `metadata.annotations: Too long` で失敗する。server-side apply は annotation を
> 作らず managed fields で差分管理するため大 CRD が通る。`--force-conflicts` は旧 client-side 実行済み
> クラスタの再適用時に field 所有権を server-side manager が奪取して冪等・再実行安全にする。手順 2)/3) の
> 小さい `Application`/`AppProject` は client-side のままでよい。

以降のデプロイは Git 上の各チャート values を更新すると ArgoCD が同期する。

## Keycloak OIDC(SSO) ログイン（IADR-0092・#353）

`ARGOCD=1` の起動で、`scripts/k8s-local-up.sh` が install 後に ArgoCD を Keycloak OIDC へ配線する（**dex は使わず
`oidc.config` を直接指定**）。`deploy/local/argocd/oidc/` の 3 つの ConfigMap を **merge patch**（既存キー保持）で適用し、
`argocd-secret` に client secret を merge patch、`argocd-server` を rollout restart する。

- **client secret**: `argocd-secret` の `oidc.keycloak.clientSecret`（dev 既定 `argocd-dev-secret-change-me`・
  `ARGOCD_OIDC_CLIENT_SECRET` env で上書き可・平文コミットなし）。
- **公開 URL（集約後・ホスト名ベース・#357/IADR-0091）**: `argocd-cm.url = https://argocd.localhost:50000`、redirect
  `https://argocd.localhost:50000/auth/callback`（realm には port-forward 用 `http://localhost:8083/auth/callback` も併記）。
  `server.insecure=true`（`argocd-cmd-params-cm`）は据え置く —— TLS を終端するのは Traefik であり、
  そこから argocd-server への in-cluster 転送は平文だからである（[[IADR-0220]] / #841）。`server.rootpath`（サブパス）は使わない。
- **アクセス**: `LOCALEDGE=1` で edge を有効化し `https://argocd.localhost:50000` を開く（[edge README](../edge/README.md)）。
  port-forward で開く場合は `kubectl -n argocd port-forward svc/argocd-server 8083:80` → `http://localhost:8083`。
- **RBAC**: `platform-admin`→`role:admin`、`platform-operator`→`role:readonly`、未マッピングは `policy.default=''`＝
  無権限（fail-safe・Admin へ昇格しない）。レルムロールは `argocd` client の protocolMapper が `groups` クレームへ発行。
- **フォールバック（fail-safe）**: ArgoCD 組み込み **local admin**（`argocd-initial-admin-secret`）は break-glass として残す。
- **issuer 整合（#284 手順A）**: browser も `http://keycloak:8080` を解決させる（hosts＋`port-forward svc/keycloak 8080:8080`）。
- **realm 反映**: `argocd` client は `deploy/keycloak/microservices-platform-realm.json` に定義。realm 再インポートで有効化。

## 妥当性の事前確認（クラスタ非依存）

`Application`/`AppProject` は `argoproj.io` CRD のため、CRD 未導入では `kubectl --dry-run=client` は
"no matches for kind" を返す（YAML 自体は妥当）。ArgoCD install 後に同期状態を確認する。

## Tier 境界

- 本ディレクトリのスコープは**ローカル（経路B）のブートストラップ手順**まで。
- **Tier 3（対象外）**: Hetzner 実 k3s での ArgoCD 実同期・実 targetRevision の運用・稼働率99%。
