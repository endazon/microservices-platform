<!-- trace:
ids: [FR-14]
iadrs: [IADR-0056, IADR-0061]
issues: [#228]
-->

# 移行 Runbook: デプロイ資産の改名（`knowledge-platform` → `microservices-platform`）— 不要（完了記録）

- 起点: Issue #228（構成の組替え可能性〔要求〕に基づくユニット構成再編のフォローアップ 2）／方式: デプロイ資産の改名を定めた実装 ADR
- 状態: **不要（superseded）**。当初は稼働環境がある前提で Blue/Green 移行手順を起草していたが、**stg/prod が未構築**で
  移行対象の稼働資産が存在しないため、移行は行わない。

## 実施内容（2026-07-12）

新名称を `microservices-platform`（リポジトリ名に一致）で確定し、**ソース／デプロイ資産の純粋な改名**として実施した
（同実装 ADR の改定・Accepted）。初回構築を新名称で行うため、Blue/Green・
データ移行・ロールバック・ingress 切替は不要。旧 `knowledge-platform` 資産（Helm チャートディレクトリ・Keycloak realm
ファイル・Grafana ダッシュボード等）はすべて撤去し、新名称で作り直した。

改名対象の全数と機械置換の詳細は同実装 ADR の「決定」節を参照。Helm チャート・
k8s Namespace・realm・イメージ接頭辞・Ingress ホスト・ArgoCD・観測資産（Grafana/Prometheus）・OTEL service_name・
OIDC issuer/authority（backend `appsettings`・frontend `config.js`）・コンテナ内パス `/etc/microservices-platform/` を
`microservices-platform` へ統一済み。

> 実際の stg/prod 構築は環境構築 issue で扱い、その際に新名称での ArgoCD 同期・OIDC 疎通・ダッシュボード表示を確認する。
