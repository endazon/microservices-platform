# Secret ブートストラップ（k3s / microservices-platform）

> 起点: ADR-0008（k3s）/ ADR-0007（Harbor）/ IADR-0017・IADR-0026（シークレット管理）
> 関連仕様: [`docs/security/security.md`](../../docs/security/security.md)

Git を単一の真実源とする GitOps（ADR-0007）でも、**Secret 実値は Git に載せない**。
本ディレクトリのテンプレートはプレースホルダのみを含む。実値は運用者がクラスタ上で作成する。

暫定期のシークレット管理は Kubernetes Secret / 環境変数（ローテーション機構なし。計画 NFR「暫定運用の注記」）。
恒久フェーズでは Sealed Secrets / External Secrets / HashiCorp Vault の導入を検討する。

## 1. Namespace

Helm/ArgoCD が `namespace.create=true`（または ArgoCD `CreateNamespace=true`）で作成する。
手動で先に作る場合:

```sh
kubectl create namespace microservices-platform
kubectl label namespace microservices-platform istio-injection=enabled
```

## 2. アプリ Secret（LLM API キー・DB 資格情報・Wiki.js）

`secret-templates.example.yaml` の各値を実値へ置換して適用する（Git にはコミットしない）。

```sh
# 例: テンプレートをコピーし、CHANGE_ME を実値へ置換してから適用
cp deploy/bootstrap/secret-templates.example.yaml /tmp/microservices-platform-secrets.yaml
# /tmp/microservices-platform-secrets.yaml を編集（CHANGE_ME を実値へ）…
kubectl apply -n microservices-platform -f /tmp/microservices-platform-secrets.yaml
rm -f /tmp/microservices-platform-secrets.yaml
```

作成される Secret（Helm values が参照）:

| Secret 名 | 参照元 (values) | 用途 |
| --- | --- | --- |
| `wikijs-db` | `wikijs.db.existingSecret` | Wiki.js の DB パスワード |
| `wikijs-sync` | `services.wiki.extraEnv` | WikiService → Wiki.js 同期 API キー |
| `llm-provider-credentials` | LLM Gateway | 外部 LLM プロバイダ資格情報 |

## 3. Harbor レジストリ Pull Secret（ADR-0007）

`values.yaml` の `imagePullSecrets` が参照する docker-registry 型 Secret を作成する。

```sh
kubectl create secret docker-registry harbor-pull \
  --namespace microservices-platform \
  --docker-server harbor.internal \
  --docker-username '<robot-account>' \
  --docker-password '<robot-token>'
```

`values.yaml`（または環境別 values）で有効化:

```yaml
imagePullSecrets:
  - name: harbor-pull
```

## 検証

```sh
kubectl get secret -n microservices-platform
```
