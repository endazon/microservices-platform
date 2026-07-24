---
title: "ESO マニフェストを external-secrets.io/v1 へ移行し chart 版を pin する（Issue #310 フォローアップ・障害1 修正）"
type: spec
status: done
related_ids:
  - IADR-0096
  - IADR-0097
  - IADR-0098
  - IADR-0099
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md"
  - "../../deploy/local/vault/eso/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: ESO マニフェストの external-secrets.io/v1 移行（Issue #310 フォローアップ）

## 起点となる計画書（トレーサビリティ）

- 起点: `Refs #310`（Vault＋ESO secret 供給）の実装 IADR-0096〜0099 の**バグ修正フォローアップ**。
- 新規の設計判断は無い（apiVersion 文字列の移行 ＋ chart 版 pin のみ）。**新規 IADR は採番しない**——
  IADR-0096〜0099 の実装オーバーサイト（提供停止済み API を参照・chart 版 latest 追従）を是正する `fix`。

## 背景（障害1）

ローカル立ち上げで `kubectl apply -f deploy/local/vault/eso/clustersecretstore-k8s.yaml` が
`no matches for kind "ClusterSecretStore" in version "external-secrets.io/v1beta1"` で失敗した。
`k8s-local-up.sh` は `set -euo pipefail` のため ESO ブロックのこの失敗でスクリプト全体が中断していた。

### 根本原因（実測で確定）

- インストール済み ESO（chart `external-secrets-2.8.0` / appVersion `v2.8.0`）は
  `external-secrets.io/**v1**` を GA 提供（`served=true, storage=true`）し、`v1beta1` は
  `served=false`（提供停止）にしている。CRD の `spec.versions` に名前としては `v1beta1` が残るが
  **API は配信しない**ため、`v1beta1` を指すマニフェストは解決できず apply が失敗する。
- 誘発要因: `k8s-local-up.sh` の ESO 導入が `helm upgrade --install external-secrets/external-secrets`
  で**版無指定＝latest 追従**。latest が v1beta1 提供を落とした版を掴んだ結果、リポの v1beta1
  マニフェストと乖離した。

## 変更内容

1. **apiVersion 移行（13 ファイル・各 1 行）**: `deploy/local/vault/eso/*.yaml`（12 本）＋
   `deploy/local/vault/clustersecretstore.yaml` の `apiVersion: external-secrets.io/v1beta1` を
   `external-secrets.io/v1` へ更新。対象 kind は `ClusterSecretStore` / `ExternalSecret`。
   - スキーマ互換性: 各マニフェストは基本フィールドのみ使用（`spec.provider.vault`・
     `auth.kubernetes`/`auth.tokenSecretRef`・`data[].remoteRef.{key,property}`・`target.creationPolicy`・
     `secretStoreRef`・`refreshInterval`）。これらは v1beta1→v1 で**同一スキーマ**（v1 は v1beta1 の昇格）。
   - `vault-auth-rbac.yaml` は `rbac.authorization.k8s.io/v1`（ESO リソースでない）ため**無改変**。
2. **chart 版 pin（`k8s-local-up.sh`）**: ESO 導入を
   `helm upgrade --install … --version "$ESO_CHART_VERSION"` に変更。既定 `ESO_CHART_VERSION=2.8.0`
   （v1 GA 提供を実測で確認した安定版）。上書きは `ESO_CHART_VERSION` 環境変数で可能。latest 追従を止め
   再現性を確保する。
3. **回帰テスト（`scripts/k8s-local-up.test.js`）**:
   - ESO マニフェスト全体に `external-secrets.io/v1beta1` が残存せず、ESO kind を含むファイルは
     `external-secrets.io/v1` を持つ（検査数 ≥ 13）。
   - up-script の ESO helm install に `--version`（版 pin）が存在する。

## 非対象（無改変）

- 本番 chart（`deploy/helm`）・消費側（各サービスの `secretKeyRef`）・realm（`deploy/keycloak`）。
- secret 供給の意味論（Owner/Merge・ゲート整合・二重所有回避）は不変。
- 障害2（ノード inotify 上限）は**本 PR では扱わない**（別途判断）。
- AST サブモジュール（`src/ai-stock-trading`）の ExternalSecret は別リポ＝**フォローアップ要確認**。

## 受け入れ基準と検証

- [x] `deploy/local/vault` 配下に `external-secrets.io/v1beta1` の残存ゼロ、`external-secrets.io/v1` が 13 本。
- [x] `kubectl apply --dry-run=server`（非破壊）で 13 マニフェストが v1 CRD に対し妥当（apply 前検証）。
- [x] `helm upgrade … --version 2.8.0` が pin されている。
- [x] `node scripts/k8s-local-up.test.js` が全 green（新規回帰含む）。
- [x] 本番 chart・消費側・realm 無改変。gitleaks green（平文の秘密を追加しない）。

## 即時回避（本修正適用前のユーザー向け）

`ESO=1`（および v1beta1 store を触る `VAULT=1` の ClusterSecretStore）を外して起動すれば障害1を回避できる。
既存の手動 Secret（過去の非 ESO 起動分）が残っていれば MSP は Secret 不足にならない。
