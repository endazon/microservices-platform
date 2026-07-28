---
title: "headlamp-viewer の ServiceAccount＋閲覧専用 RBAC を manifest 化し HEADLAMP=1 だけで token ログインを再現可能にする（Issue #398）"
type: spec
status: done
related_ids:
  - IADR-0108
  - IADR-0080
  - IADR-0084
  - IADR-0087
  - IADR-0105
  - NFR
author: claude
created: 2026-07-28
updated: 2026-07-28
related_specs:
  - "../adr/IADR-0108_headlamp-viewer-readonly-rbac.md"
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md"
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "20260726_issue-328_headlamp-token-login-docs.md"
  - "20260726_issue-399_remove-apiserver-oidc-flags.md"
  - "../../deploy/local/README.md"
---

# 仕様書: `headlamp-viewer` SA＋閲覧専用 RBAC の manifest 化（Issue #398）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（NFR 運用性・再現性＝ローカル k8s の管理 UI が既定手順のみで利用可能であること）
- ユースケース（UC）／画面（SC）: 該当なし（dev 環境の運用ツール配備）
- 関連 ADR: [IADR-0108](../adr/IADR-0108_headlamp-viewer-readonly-rbac.md)（本作業の決定＝閲覧専用 RBAC）／
  [IADR-0080](../adr/IADR-0080_headlamp-k8s-management-ui.md)（Headlamp 導入・opt-in・fail-safe 方針）／
  [IADR-0084](../adr/IADR-0084_headlamp-oidc-apiserver-flags.md)（apiserver OIDC 適用不能の実測＝token 方式の根拠）／
  [IADR-0105](../adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md)（apiserver OIDC 配線の除去＝復活させない）／
  [IADR-0087](../adr/IADR-0087_k8s-local-up-optin-smoke-test.md)（opt-in ゲート smoke test＝回帰固定先）
- Issue: #398（enhancement/infrastructure）。`Refs #328`（wontfix）・#393（token 方式の docs 化）・#388（OIDC 化）・#271

## 目的・背景

#393 で Headlamp のローカルログインは **token（ServiceAccount トークン）方式**が正式手順となった
（apiserver OIDC フラグは k8s 1.30+ の https-issuer 強制により成立せず、#399/[IADR-0105] で配線ごと除去済み）。

しかしログインに使う ServiceAccount `headlamp-viewer` と ClusterRoleBinding は**リポジトリに収録されておらず**、
`deploy/local/README.md` が `kubectl create serviceaccount` / `kubectl create clusterrolebinding` の暫定手順を
案内しているだけである。そのため新規クラスタでは**手動作成のステップが 1 つ残り**、`HEADLAMP=1` だけでは
token ログインまで到達できない（再現性の欠落）。

本作業は当該 SA と RBAC を既存 opt-in オーバーレイ `deploy/local/headlamp/` へ manifest として収録し、
`HEADLAMP=1` の既存 apply 経路（`kubectl apply -k deploy/local/headlamp`）でそのまま配備されるようにする。

## 対象範囲

- 対象:
  - `deploy/local/headlamp/headlamp-viewer-rbac.yaml`（新規）: SA `headlamp-viewer`＋閲覧専用 RBAC
  - `deploy/local/headlamp/kustomization.yaml`: `resources` へ 1 行追加
  - `deploy/local/README.md`「Headlamp」節: 暫定の手動作成手順を削除し、token 取得のみへ簡素化。権限範囲を明記
  - `scripts/k8s-local-up.sh`: HEADLAMP ブロックの案内文言（`NotFound` 時の手動作成への言及を削除）
  - `scripts/k8s-local-up.test.js`: overlay に viewer SA/RBAC が収録され続けることの回帰固定
  - `docs/adr/IADR-0108_...md`（新規）: 付与権限を `cluster-admin` ではなく**閲覧専用**とする決定
- 非対象（不変）:
  - apiserver への OIDC フラグ／ドロップイン（[IADR-0105] で除去済み・**復活させない**）
  - Headlamp **Pod** の SA `headlamp` とその「広域権限を bind しない」fail-safe（[IADR-0080]）
  - 既存 CRB `headlamp-developer-cluster-admin`（`oidc:developer`・現行 inert・#388 用資産）
  - `deploy/local/infra/kustomization.yaml`（headlamp overlay を含めない fail-safe を維持）
  - Keycloak realm・`headlamp-oidc` Secret・Deployment/Service・image tag
  - 稼働中の live 環境（本作業ではデプロイ・Pod 操作を行わない）

## 設計方針

### 1. Pod の SA とログイン用 SA を別ファイルに分ける

`headlamp.yaml` は Headlamp **Pod** が使う SA `headlamp`（＝**権限を一切 bind しない**のが fail-safe の要）を持つ。
ログイン用の `headlamp-viewer` は性質が正反対（＝権限を bind する）ため、同一ファイルに混在させると
「headlamp の SA には権限を付けない」という不変条件が読み取りづらくなる。別ファイル
`headlamp-viewer-rbac.yaml` に分離し、kustomization の `resources` で合成する。

### 2. 付与権限は閲覧専用（`cluster-admin` を付けない）

Issue 本文は `cluster-admin` を束ねる案だったが、用途は Pod/Deployment/Service/ログの**閲覧**であり、
恒常的に発行可能な 24h トークンへ `cluster-admin` を紐付ける必然性がない。最小権限に絞る。決定と根拠は
[IADR-0108](../adr/IADR-0108_headlamp-viewer-readonly-rbac.md)。

構成は 2 本の ClusterRoleBinding:

| bind 先 | 種別 | 範囲 |
| --- | --- | --- |
| `view`（k8s 組み込み） | ClusterRole（既存） | 名前空間リソースの読み取り（全 ns 横断）。**`secrets` は含まない**（組み込み `view` の設計） |
| `headlamp-viewer-cluster-read`（新規） | ClusterRole（新規） | `view` が持たないクラスタスコープ資源の `get`/`list`/`watch` |

verbs は `get` / `list` / `watch` のみ。`create` / `update` / `patch` / `delete` / `exec` は与えない
（UI 上の編集・削除・exec は 403 となる＝意図どおり）。

### 3. 冪等性

すべて `kubectl apply -k` で宣言的に適用され、SA・ClusterRole・ClusterRoleBinding はいずれも
再適用で差分なしに収束する（`roleRef` は immutable だが**変更しない限り**再 apply は成功する）。

## 受け入れ基準（Issue #398）

- [x] 新規クラスタに対し `HEADLAMP=1` で up した直後、SA/CRB の手動作成なしに
      `kubectl -n platform-infra create token headlamp-viewer` でトークンを取得して Headlamp へログインでき、
      クラスタリソースが閲覧できる
      （※ 実ブラウザログインは稼働クラスタ依存＝live。本 PR では manifest レンダと権限定義で担保）
- [x] `HEADLAMP` 未設定時の既定挙動は不変（headlamp overlay は `deploy/local/infra` に含めない fail-safe を維持）
- [x] 既存クラスタへ再適用しても冪等（apply の再実行でエラーにならない）
- [x] 付与がローカル dev 限定である旨をコメントで明示する（＋ `cluster-admin` ではなく閲覧専用である旨も）

## 検証

- `kubectl kustomize deploy/local/headlamp`（テンプレレンダのみ・クラスタ非接続）で
  SA `headlamp-viewer` / ClusterRole / 2 本の CRB が出力されること
- `node scripts/k8s-local-up.test.js`（stub-on-PATH・副作用ゼロ）が緑
  - `HEADLAMP` 未設定で overlay 由来トークンが出ないこと（既存アサート）
  - `HEADLAMP=1` で overlay が apply されること（既存アサート）
  - overlay が viewer SA/RBAC を収録し、書き込み verb を含まないこと（本作業で追加）
- `node scripts/scripts.test.js` / `node scripts/check-doc-links.js` が緑

## リスクと緩和

| リスク | 緩和 |
| --- | --- |
| 閲覧専用化で従来の手作り `cluster-admin` 運用と権限が変わる | README に権限範囲を明記。編集が要る場合は各自の kubeconfig（admin）を使う旨を併記 |
| 既存クラスタに手作りの CRB `headlamp-viewer`（cluster-admin）が残存 | 名前が異なる（`headlamp-viewer-view` / `-cluster-read`）ため衝突しない。README に手作り分の削除手順を記載 |
| RBAC 読み取り許可による情報開示 | ローカル閉域・dev 限定・読み取りのみ。`secrets` は `view` の設計どおり非許可 |
