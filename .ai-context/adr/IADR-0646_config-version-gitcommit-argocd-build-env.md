---
title: IADR-0646 構成バージョンのコミット ID は Argo CD のビルド変数 $ARGOCD_APP_REVISION で注入し、ローカルの起動器は HEAD の完全なコミット ID を渡す（作り物の値を渡さない）
type: impl-adr
status: Accepted
related_ids: [FR-15, ADR-0018, ADR-0007, IADR-0029, IADR-0046, IADR-0069, IADR-0605]
author: claude
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_2118_introspection-ports-connectors-gitcommit.md
---

# IADR-0646: 構成バージョンのコミット ID の注入（#2118）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#2118 段 1。親 #2047 の指摘 M-21。依頼と段の分け方はコーディネーター 2026-10-26）

## 起点・関連

- 起点 issue: #2118。
- 改める実装 ADR: [IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md) フォローアップ 3（`Config:GitCommit` 等の GitOps 注入）の
  「CD が同期時に上書きする」。適用日時（`appliedAt`）はそのまま残す。
- 前提として変えない: IADR-0046 / IADR-0069（構成バージョン履歴の供給経路）、IADR-0605（自製イメージのタグの決め方。本 IADR の起動器の姿勢の型）。
- 作業仕様書: [20261026_2118_introspection-ports-connectors-gitcommit](../specs/20261026_2118_introspection-ports-connectors-gitcommit.md)。

## 背景

- 構成情報 API の構成バージョン（計画 10_composability §5「適用中の構成定義の Git コミット ID・適用日時・適用者」）は、
  chart の `config.gitCommit` を BFF の `Config__GitCommit` へ渡す。既定は空で、Argo CD の経路にも起動器の経路にも値を与える箇所が無かった。
- `deploy/argocd/application.yaml` の注記と運用文書は「ArgoCD ネイティブ Helm は build 変数をパラメータへ自動展開しないため、CD 側で SHA を供給する」としていた。
  **これは誤りである。** Argo CD の文書（User Guide の Helm §Build Environment）は「Helm apps have access to the standard build environment via
  substitution as parameters」とし、ビルド変数の一覧（Build Environment）に `ARGOCD_APP_REVISION`（解決済みの完全なリビジョン）を挙げる（2026-10-26 確認）。

## 決定

### 決定 1: Argo CD の経路は `config.gitCommit=$ARGOCD_APP_REVISION`

- `helm.parameters` に `name: config.gitCommit` / `value: $ARGOCD_APP_REVISION` / `forceString: true` を置く。
  - `forceString` は、数字だけの sha を数として読ませないため（`global.image.tag` と同じ）。
  - `$$` にしない（`$$` はビルド変数の置換を止める書き方である）。
- 適用日時に当たるビルド変数は無いので、`config.appliedAt` は従来どおり CD が与える。

### 決定 2: ローカルの起動器は HEAD の完全なコミット ID を渡し、作り物の値を渡さない

- `k8s-local-up.sh` の `[6/7]` の helm に `--set-string config.gitCommit=$(git rev-parse --verify HEAD)` を渡す。
  変更のある作業ツリー・detached HEAD でも、HEAD は実在のコミットである。
- **HEAD を読めなければ何もする前に止める。上書きの口は持たない。** 作り物の値（`dev-local` 等）を渡すと、構成ビューアが実在しないコミットを
  「適用中の構成」と表示する。イメージのタグ（IADR-0605）と同じ姿勢である。
- 適用日時は渡さない。同じコミットで起動し直すたびに BFF の env が変わって作り直されるのを避けるためである。

## 結果

- 良い影響: 構成ビューアの構成バージョンが、Argo CD の配備でもローカルの起動器でも空でなくなる。PoC の起動器でもコミット ID が出るようになる。
- 受け入れる代償: Argo CD のパラメータは Application 側の宣言なので、`kubectl apply` し直さない限り既存の Application には効かない（`global.image.tag` と同じ）。
- 試験: `scripts/helm-config-version.test.js`（Application のパラメータ・描画で `Config__GitCommit` に載る・既定は空・変異）、
  `scripts/k8s-local-up.test.js` の #2118 の節（HEAD を 1 つだけ渡す・読めなければ止まる）。
