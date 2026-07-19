# 経路B ローカル GitOps（ArgoCD）ブートストラップ（opt-in）

> 起点: [ADR-0006](../../../docs/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST #24）

ArgoCD 本体は大きな公式 install manifest を URL 適用するため、ここでは**ブートストラップ手順**のみを置く
（既存の [`deploy/argocd`](../../argocd/README.md) の `Application`/`AppProject` を再 vendoring しない）。
すべて **opt-in**。`scripts/k8s-local-up.sh` の `ARGOCD=1` が下記を実施する。

## ブートストラップ

```sh
# 1) ArgoCD 本体（一度だけ・URL 適用）
kubectl create namespace argocd --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml

# 2) MSP の Application/AppProject
kubectl apply -f deploy/argocd/appproject.yaml
kubectl apply -f deploy/argocd/application.yaml

# 3) AST の Application/AppProject（連結時・submodule 配下）
kubectl apply -f src/ai-stock-trading/deploy/argocd/appproject.yaml
kubectl apply -f src/ai-stock-trading/deploy/argocd/application.yaml
```

以降のデプロイは Git 上の各チャート values を更新すると ArgoCD が同期する。

## 妥当性の事前確認（クラスタ非依存）

`Application`/`AppProject` は `argoproj.io` CRD のため、CRD 未導入では `kubectl --dry-run=client` は
"no matches for kind" を返す（YAML 自体は妥当）。ArgoCD install 後に同期状態を確認する。

## Tier 境界

- 本ディレクトリのスコープは**ローカル（経路B）のブートストラップ手順**まで。
- **Tier 3（対象外）**: Hetzner 実 k3s での ArgoCD 実同期・実 targetRevision の運用・稼働率99%。
