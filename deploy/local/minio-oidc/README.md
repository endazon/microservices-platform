# MinIO Console の Keycloak OIDC(SSO)（IADR-0093・#353）

> 起点: [IADR-0093](../../../.ai-context/adr/IADR-0093_minio-keycloak-oidc.md) /
> 作業仕様書 [`.ai-context/specs/20260721_issue-353_minio-keycloak-oidc.md`](../../../.ai-context/specs/20260721_issue-353_minio-keycloak-oidc.md)

経路B の MinIO Console を Keycloak OIDC でログインできるようにする。OIDC 配線は helm の opt-in
（`minio.oidc.enabled`・`values-local.yaml` で有効化）で自動、**MinIO ポリシー作成のみ runtime 手順**（下記）。
本番 `values.yaml` は `oidc.enabled=false` のまま不変（byte 等価）。root（`minio-credentials`）は break-glass。

## OIDC 設定（自動）

`k8s-local-up.sh` が client secret 用 Secret `minio-oidc` を作成（dev 既定 `minio-dev-secret-change-me`・
`MINIO_OIDC_CLIENT_SECRET` env で上書き可・平文コミットなし）。`templates/minio.yaml` は `minio.oidc.enabled` 時に
`MINIO_IDENTITY_OPENID_*`＋`MINIO_BROWSER_REDIRECT_URL=https://minio.localhost:50000` を注入する（Secret 参照は
`optional`＝未作成でも Pod 起動）。realm client `minio` は `deploy/keycloak/microservices-platform-realm.json`。

## RBAC ポリシー（runtime 手順・**fail-safe deny**）

MinIO は id_token の `policy` クレーム（realm ロールを `minio` client の protocolMapper が発行）に**名前が一致する
MinIO ポリシー**を適用し、**一致が無ければ deny（no-access）**＝fail-safe。`platform-admin`/`platform-operator` に
対応する MinIO ポリシーを一度だけ作成する（realm import と同様、MinIO の runtime admin 操作のためマニフェスト化しない）:

```sh
# mc（MinIO Client）を root で alias 登録（port-forward 例）
kubectl -n microservices-platform port-forward svc/minio 9000:9000 &
mc alias set local http://localhost:9000 minioadmin minioadmin   # dev 既定（env 上書き時は追随）

# realm ロール名と同名の MinIO ポリシーを作成（本ディレクトリの JSON を投入）
mc admin policy create local platform-admin    deploy/local/minio-oidc/policies/platform-admin.json
mc admin policy create local platform-operator deploy/local/minio-oidc/policies/platform-operator.json
```

- `platform-admin.json` = 管理者（`admin:*`/`kms:*`/`s3:*`＝consoleAdmin 相当）、`platform-operator.json` = 読み取り専用。
- ⚠️ **共有ロール注意**: `platform-admin` は本レルムで FR-09 の ABAC「AdminOnly」判定にも使う共有ロール
  （`microservices-platform-realm.json`）。このポリシーで **MinIO の管理 API/KMS/全 S3 操作**まで付与されるため、
  `platform-admin` を広く配布する運用にする場合は MinIO 側の権限波及を意識する（必要なら MinIO 専用の細粒度ロール/
  ポリシーへ分離する）。
- 未作成のうちは全 OIDC ユーザーが deny（安全側）。適用後、`developer`（realm ロール `platform-admin`/`platform-operator`
  を保持）はこれらポリシーの合算権限になる。root は常に break-glass として利用可。

## ログイン / 到達（集約後 URL・#357/edge）

`LOCALEDGE=1` で edge を有効化し（`deploy/local/edge/README.md`）、`admin-ingress-minio.yaml` が
`minio.localhost:50000` → Console(9001) を配線する:

```sh
# edge 集約（ポート再作成が必要・破壊操作はユーザー実行）
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
#   → https://minio.localhost:50000 →「Login with SSO」→ realm ユーザー（例 developer/developer）
```

- **issuer 整合（#284 手順A）**: browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080`。
  MinIO server（MSP ns）は ExternalName alias `keycloak` で in-cluster の well-known を取得する。
- ⚠️ **port-forward 単独（`LOCALEDGE` 未使用）では OIDC は完了しない**: `MINIO_BROWSER_REDIRECT_URL` を集約 URL に
  固定しているため redirect が `minio.localhost:50000` を指す（edge 未起動だと到達不能）→ **fail-safe の root で入る**。
  realm には `http://localhost:9001/oauth_callback` も登録済みで、`MINIO_BROWSER_REDIRECT_URL` を外せば port-forward で
  OIDC 可（Grafana PR-2/IADR-0090 と同性質）。CLI で `*.localhost` 未解決なら hosts 追記 or `*.nip.io`。
- **realm 反映**: `minio` client は realm 再インポートで有効化（永続化時は管理コンソール追加 or 再作成）。
