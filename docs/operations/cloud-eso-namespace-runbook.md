---
title: 運用 Runbook — クラウドの ESO の名前空間（Pod Security と到達の制限を、稼働中の構成へ足す）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-05, NFR-18]
iadrs: [IADR-0705, IADR-0689, IADR-0696]
specs: [20261107_cloud-egress-audit-cert-expiry]
-->

# 運用 Runbook: ESO の名前空間

> 対象はクラウドの最小構成の ESO（名前空間 `external-secrets`。helm の chart）。宣言は `deploy/cloud/external-secrets/`。
> 新しく作る構成は `deploy/cloud/README.md` §3 の順（名前空間の宣言を chart より先に当てる）のまま。本書は**稼働中の構成へ足す**手順である。
> 🔴 **本書の手順はすべてオーナーが行う。**
>
> **状態を `draft` に留めている理由**: クラウドのノードで流していない。

## 何を足すか

- 名前空間 `external-secrets` の Pod Security は enforce・warn・audit とも restricted（chart の 3 つの Pod は満たす。描画で確かめてある）。
- 入り・出とも既定で拒否。出は名前解決・OpenBao の受け口（8443）・API サーバー（443・6443）だけ。入りは webhook（10250）だけ。

## 1. 稼働中の構成へ足す

```sh
kubectl label --dry-run=server --overwrite ns external-secrets pod-security.kubernetes.io/enforce=restricted   # 警告が出なければ先へ（今の Pod が満たす）
kubectl apply -k deploy/cloud/external-secrets
kubectl -n external-secrets rollout restart deploy/external-secrets deploy/external-secrets-webhook deploy/external-secrets-cert-controller
kubectl -n external-secrets rollout status deploy/external-secrets deploy/external-secrets-webhook deploy/external-secrets-cert-controller --timeout=300s
kubectl get secretstore -A                                           # すべて Ready（OpenBao へ届く）
kubectl -n platform-infra annotate externalsecret keycloak-admin force-sync="$(date +%s)" --overwrite
kubectl -n platform-infra get externalsecret keycloak-admin          # SecretSynced（同期が通る）
```

- 1 行目で警告が出たら当てない（その Pod が restricted を満たさない。chart の版と values を確かめる）。
- ストアが Ready にならない・同期が通らないときは、`kubectl -n external-secrets logs deploy/external-secrets` で理由を読む。
  到達の制限で止まっているなら、`kubectl -n external-secrets delete networkpolicy default-deny eso-egress-dns-openbao-apiserver eso-webhook-ingress` で
  前の形へ戻し、理由を直してから当て直す。

## 2. helm の当て直し

以後の `helm upgrade --install external-secrets` には `--create-namespace` を付けない（名前空間は宣言が持つ）。
