---
title: ArgoCD install（ARGOCD=1）を server-side apply に是正し大 CRD の annotation 上限超過を回避する（Issue #348）
type: spec
status: done
related_ids:
  - ADR-0006
  - ADR-0007
  - IADR-0066
  - IADR-0077
  - IADR-0087
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../../deploy/local/argocd/README.md"
  - "../../deploy/argocd/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: ArgoCD install を server-side apply に是正（Issue #348）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006 / ADR-0007（CI/CD・GitOps）。ローカル ArgoCD opt-in ブートストラップの統括は
  [[IADR-0077]]（local observability/vault/gitops overlays）。ゲート横断 smoke test は [[IADR-0087]]（#334）。
- Issue: #348（live 検証で発見・#24/ArgoCD 関連・priority:should）。

## 背景と問題

live 検証で、`ARGOCD=1 bash scripts/k8s-local-up.sh` の ArgoCD ブートストラップが ArgoCD 公式
install manifest を **client-side の `kubectl apply`** で適用しており、`applicationsets.argoproj.io`
CRD の適用時に次のエラーで失敗する:

```
The CustomResourceDefinition "applicationsets.argoproj.io" is invalid:
metadata.annotations: Too long: may not be more than 262144 bytes
```

client-side apply は manifest 全体を `kubectl.kubernetes.io/last-applied-configuration`
annotation に格納するため、ArgoCD の巨大 CRD（ApplicationSet 等）が etcd の annotation 上限
262144 バイトを超過する既知問題。

### 該当箇所

`scripts/k8s-local-up.sh`（`ARGOCD=1` ゲート・`:150-158` 付近）:

```sh
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
```

## 対応方針

`scripts/k8s-local-up.sh` の ArgoCD 適用箇所＋関連 README＋smoke test に閉じる。他 downstream/values/
realm/frontend には触れない。

1. **install manifest の apply を server-side 化**: 当該 `kubectl apply` を
   `kubectl apply --server-side --force-conflicts` に是正する。server-side apply は
   `last-applied-configuration` annotation を作らない（managed fields で差分管理する）ため巨大 CRD が通る。
   install manifest の **URL/バージョンは現状のまま**（変更しない）。
   `--force-conflicts` は、旧 client-side 実行済みクラスタの再実行（field manager 切替）でも
   competing manager の field 所有権を server-side manager が奪取して**冪等・再実行安全**にするため付与する。
2. **Application/AppProject の apply は現状維持**: `deploy/argocd/appproject.yaml` /
   `application.yaml`（および AST 同梱分）は小さく annotation 上限問題が無いため client-side のまま
   （挙動等価・変更最小）。他 opt-in ゲート（OBSERVABILITY/VAULT/HEADLAMP）の kustomize apply 方式・
   冪等性・既定挙動は不変。
3. **README 注記**: `deploy/local/argocd/README.md` と `deploy/argocd/README.md` の install 手順に、
   server-side apply を使う旨と大 CRD（annotation 上限）の理由を注記する。
4. **smoke test 追加**: `scripts/k8s-local-up.test.js`（#334・IADR-0087）の ARGOCD 分岐に、install
   manifest の apply 行が `--server-side` を含むことの検証を追加する（bash stub-on-PATH・スクリプト無改変）。

### スコープ外

- install manifest の URL/バージョン変更（pin 等）。
- Application/AppProject の server-side 化（不要・変更最小の原則）。
- 本番 Tier 3（Hetzner 実 k3s）での ArgoCD 実同期。

## 実装ADR

client-side → server-side apply への是正は、既知問題への標準的な運用修正であり新規の設計判断を伴わない
ため **IADR は起票しない**（統括は既設 [[IADR-0077]]。純スクリプト修正）。

## 受け入れ基準

- [x] `scripts/k8s-local-up.sh` の ArgoCD install apply が `--server-side --force-conflicts` になる。
- [x] install manifest の URL/バージョンは不変。
- [x] 既定（`ARGOCD` 未設定）挙動はバイト等価（opt-in ゲート内のみの変更）。
- [x] `deploy/local/argocd/README.md` / `deploy/argocd/README.md` に server-side apply の注記が入る。
- [x] `scripts/k8s-local-up.test.js` の ARGOCD 分岐に `--server-side` 検証を追加し、smoke test 緑。
- [x] `bash -n scripts/k8s-local-up.sh` 構文検査が通る。
- [x] `check-image-mapping.js`（#275 ドリフト）/ `check-doc-links.js`（docs リンク）が緑。
- [ ] 実クラスタでの `ARGOCD=1 bash scripts/k8s-local-up.sh` による ArgoCD 正常 install 疎通は **live**
      （稼働 k3d 依存・本 issue の live 分）。
