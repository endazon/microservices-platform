# 経路B ローカル Vault + External Secrets（opt-in）

> 起点: [ADR-0006](../../../.ai-context/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST#24）

経路B（k8s）で **Vault dev モード**と External Secrets Operator の `ClusterSecretStore`（`vault-backend`）を立てる
**opt-in オーバーレイ**。AST chart 側の `ExternalSecret`（`ast-secrets` / `moomoo-*`・opt-in）がこのストアを
参照して Vault dev から同期できる状態を作る。

> **製品は OpenBao**（Vault API 互換・MPL-2.0。`openbao/openbao:2.7.1` を digest で固定。NFR-18・ADR-0132・IADR-0525・#1840）。
> HashiCorp Vault は 1.15 以降 BUSL-1.1 で、配備していた 1.16 はコミュニティ保守も終わっていた。**名前は変えていない**
> （`deploy/vault`・Service `vault`・Secret `vault-dev-token`・`VAULT=1`・アプリの `Vault__*`。計画中の「Vault」は Vault API 互換の製品と読む）。
> イメージは `vault` を `bao` へのリンクとして同梱し、CLI は `BAO_*` が無いとき `VAULT_ADDR` / `VAULT_TOKEN` を読む。
> サーバの `-dev` だけは `BAO_DEV_ROOT_TOKEN_ID` を要るので、`vault-dev.yaml` は同じ Secret を両方の名前で渡す。
> 🔴 Pod に `BAO_ADDR` / `BAO_TOKEN` を足さない（`VAULT_*` より優先され、手順書の `VAULT_TOKEN=…` が効かなくなる）。
> **旧 Vault を永続化して動かしていたクラスタ**は、データの移行が要る（OpenBao は file ストレージを持たない）:
> [`docs/operations/secret-store-openbao-migration-runbook.md`](../../../docs/operations/secret-store-openbao-migration-runbook.md)。

> ⚠️ **dev 専用・本番の Vault 化充足ではない**。既定（`PERSIST` 既定オン）は `deploy/local/vault-persistence/` が
> raft ストレージを PVC に置き（`/vault/data/raft`）、Pod 内ラッパーが init / unseal / 固定 root トークン / kv-v2 mount を毎回行う
> （unseal 鍵は PVC 上の平文・単一 Pod）。`PERSIST=0` は本ディレクトリの `-dev`（インメモリ・再起動で中身が消える）。
> 本番は unseal / 監査 / HA / ローテーションを要する（Tier 3）。
> **平文の秘密（root トークン・API 鍵）をコミットしない。** root トークンは Secret `vault-dev-token`
> （dev 既定 or `VAULT_DEV_ROOT_TOKEN` 環境変数）から注入する。

## 構成

| ファイル | 役割 |
| --- | --- |
| `vault-dev.yaml` | Vault dev サーバ（Deployment/Service・`platform-infra`） |
| `clustersecretstore.yaml` | ESO `ClusterSecretStore` `vault-backend`（KV v2 `secret/`・**token 認証**＝`VAULT=1` 既定・不変。`ESO=1` で `eso/clustersecretstore-k8s.yaml` が kubernetes 認証へ上書き・IADR-0096） |
| `oidc/` | **Keycloak OIDC(SSO) 連携**（IADR-0094・#353）: `bootstrap.sh`＋policy HCL＋手順。UI/CLI を Keycloak でログイン（`vault.localhost:50000`）。[oidc/README](oidc/README.md) |
| `eso/` | **Vault＋ESO で secret を Pod へ自動供給**（IADR-0096・#310）: `ESO=1` で ESO 導入＋k8s auth＋ExternalSecret。[eso/README](eso/README.md) |

## 適用（opt-in）

```sh
# 1) External Secrets Operator（CRD・一度だけ）
helm repo add external-secrets https://charts.external-secrets.io
helm install external-secrets external-secrets/external-secrets -n external-secrets --create-namespace

# 2) dev root トークン Secret（dev 既定 or env 上書き・k8s-local-up.sh が実施）
kubectl -n platform-infra create secret generic vault-dev-token \
  --from-literal=token="${VAULT_DEV_ROOT_TOKEN:-devroot}" --dry-run=client -o yaml | kubectl apply -f -

# 3) Vault dev + ClusterSecretStore
kubectl apply -k deploy/local/vault

# 4) 鍵を投入（例・実値は端末外に出さない・KV v2 は secret/ 配下）
kubectl -n platform-infra exec deploy/vault -- sh -lc \
  'VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN=$VAULT_DEV_ROOT_TOKEN_ID vault kv put secret/ai-stock-trading/app-secrets finnhub-api-key=...'
```

`scripts/k8s-local-up.sh` は `VAULT=1` で 2〜3 を実施する。**IADR-0096 (#310)**: `VAULT=1 ESO=1` で ESO 本体 install＋
Vault k8s auth＋ExternalSecret 供給まで自動化する（[eso/README](eso/README.md)）。

## AST 側の有効化

AST chart で `externalSecrets.enabled=true` ＋（API 鍵なら）`externalSecrets.appSecrets.enabled=true` を設定すると、
`vault-backend` を参照して Vault dev から同期する（手順は ai-stock-trading `docs/operations/vault-secrets-runbook.md`）。

## audit（監査。NFR-18・ADR-0124 決定 2・IADR-0486・#1683）

既定（永続化）の Vault は audit device を 2 つ持つ。🔴 OpenBao は API での audit device の作成を既定で拒むので、どちらも**設定で宣言する**
（`stdout/` は `deploy/local/vault-persistence/local.hcl`、`otel-collector/` は起動器 `vault-entrypoint.sh` が collector に届いてから
`/vault/audit.d/` へ書いて SIGHUP で読み直させる）。

| path | 出力先 | 止まったとき |
| --- | --- | --- |
| `stdout/` | Vault のコンテナログ（`kubectl -n platform-infra logs deploy/vault`） | 在ることを確かめられなければ Vault を起動しない |
| `otel-collector/` | collector の `tcplog/vault-audit`（`otel-collector.platform-infra.svc:9514`）→ `OBSERVABILITY=1` なら Loki の `{job="vault-audit"}` | Vault は止まらない（`stdout/` が書ける）。起動器は裏で再試行し、足せなかった宣言は消し、諦めたら WARN を出す。初めから `local.hcl` に書かないのは、collector が居ないと初期化が鍵を返さずに失敗するため（実測） |

- 値・トークンは HMAC（`hmac-sha256:…`）で残る（`log_raw=false`）。**`log_raw=true` や mount の `audit_non_hmac_*` を足さない。**
- 🔴 **`stdout/` の宣言を `local.hcl` から外さない。** 外した状態で collector が止まると、Vault は要求をすべて拒む（起動器のトークン確認すら通らない）。
- Loki のストリーム名 `{job="vault-audit"}` は製品の差し替えの後も変えていない（行の形も同じ。抽出の条件と試験がこの名前で突き合わせる）。
- 秘密の書き込みの抽出の条件は `docs/security/security.md`「保管先（Vault）の audit」。
- `PERSIST=0`（本ディレクトリの `-dev`）は audit を持たない。

## Tier 境界

Vault 本番運用（unseal/監査/HA/ローテーション）・[実弾解禁前提としての Vault 化実充足]は **Tier 3**（対象外）。
