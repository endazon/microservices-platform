# ArgoCD GitOps 配備（ArgoCD + Helm + Harbor）

> 起点: ADR-0007（CI/CD・GitOps）/ ADR-0008（k3s）
> 受け入れ基準: ArgoCD 経由のデプロイが Git の状態と同期し、手動 kubectl 依存がない

Git を単一の真実源とし、ArgoCD が本リポジトリの Helm チャート
（`deploy/helm/knowledge-platform`）を `knowledge-platform` Namespace へ宣言的に同期する。

## 構成

| ファイル | 種別 | 役割 |
| --- | --- | --- |
| `appproject.yaml` | `AppProject` | 許可するソース Git・配備先 Namespace を制約 |
| `application.yaml` | `Application` | Helm チャートを同期（`prune`/`selfHeal` 有効） |

## 1. ArgoCD 導入

```sh
kubectl create namespace argocd
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
```

## 2. Harbor（レジストリ）連携

イメージは Harbor（`harbor.internal`）で管理する（`values.yaml` の `global.image.registry`）。
Pull 認証は Namespace の docker-registry Secret（`harbor-pull`）で行う
（作成手順は [`../bootstrap/README.md`](../bootstrap/README.md)）。CI がイメージを Harbor へ push し、
Git 上の `services.<name>.tag` を更新することで ArgoCD が新イメージを同期する。

## 3. Application 適用（ブートストラップのみ kubectl）

ArgoCD 自身への登録は一度だけ kubectl で行う（以降はすべて Git 同期）。

```sh
kubectl apply -f deploy/argocd/appproject.yaml
kubectl apply -f deploy/argocd/application.yaml
```

## 4. 独立デプロイ・ロールバック（NFR）

- **デプロイ（サービス単位）**: `deploy/helm/knowledge-platform/values.yaml` の
  `services.<name>.tag` を Git で更新 → ArgoCD が自動同期（`automated.selfHeal`）。
- **ロールバック**:
  ```sh
  argocd app rollback knowledge-platform <revision>
  # もしくは Git 上で当該コミットを revert（GitOps の原則）
  ```
- **同期状態の確認**:
  ```sh
  argocd app get knowledge-platform      # Sync/Health ステータス
  argocd app diff knowledge-platform     # Git と実クラスタの差分（0 であること）
  ```

`selfHeal: true` により、手動 `kubectl edit` 等の out-of-band 変更は Git 状態へ自動復元される
（手動 kubectl 依存の排除）。
