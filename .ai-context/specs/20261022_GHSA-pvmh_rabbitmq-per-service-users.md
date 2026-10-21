---
title: 共有ブローカにサービスごとのユーザーと権限を作り（Vault の乱数・ESO・冪等な合わせ）、ブローカへの到達をブローカを使うサービスの Pod だけに絞る（移行の段 1・Refs GHSA-pvmh-cvmf-pvcp）
type: spec
status: done
related_ids: [NFR-18, IADR-0573, IADR-0096, IADR-0099, IADR-0494, IADR-0522]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-18）
---

# 仕様書: ブローカのサービスごとのユーザーと到達の制限（段 1）

> 本仕様書は実装着手前に作成した（着手 2026-10-22。MSP `origin/develop` `532f2be0`）。

## 起点

- Refs GHSA-pvmh-cvmf-pvcp（オーナー裁定 2026-10-22: 許可範囲は「ブローカを使う AST と MSP のサービス全部」、順序は MSP → AST、vhost は 1 つ、
  移行の期間は既存の接続を壊さない）。非機能 `NFR-18`（シークレット管理）。
- IADR: **IADR-0573**（方式・段の分け方）。
- 二重に実装しないもの: 既定のユーザーの Secret（`rabbitmq`）を既定値で作らない fail-closed 化（GHSA-rh44 の作業。MSP#2051 の後続）。

## 範囲（段 1）

1. `deploy/local/rabbitmq-setup/users.txt`（単一情報源。18 ユーザー）
2. `deploy/local/vault/eso/bootstrap.sh`: KV `msp/rabbitmq-users`・`ai-stock-trading/rabbitmq-users` を無いときだけ乱数で作る・無いユーザーだけ足す
3. `deploy/local/vault/eso/externalsecret-rabbitmq-users.yaml`（platform-infra・Owner）と起動器の apply
4. `deploy/local/rabbitmq-setup/reconcile-users.sh`（冪等。`--check` あり）と起動器（`ESO=1`）からの呼び出し。合わせられなければ止める
5. `deploy/local/infra/rabbitmq.yaml` に NetworkPolicy `rabbitmq-ingress-clients`
6. 試験: `scripts/k8s-local-up.test.js`（apply・順序・標準入力・guest に触れない・揃わなければ止まる）、
   `scripts/scripts.repo.test.js`（表と chart・NetworkPolicy・ExternalSecret・種の一致、種の乱数・在る値を触らない）

範囲外: 基盤のサービスの切り替えと既定のユーザーの無効化（段 3）、AST の chart（段 2・AST のリポジトリ）。

## 母集合（規則 9・10）

走査語: `rabbitmq`・`RABBITMQ_`・`messaging`（`deploy/`・`scripts/`。`src/`・`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `deploy/local/infra/rabbitmq.yaml` | NetworkPolicy を足す（Deployment・Service は変えない） |
| 2 | `scripts/k8s-local-up.sh` の `apply_secret ... rabbitmq` / `rabbitmq-app` | **触らない**（既定のユーザーの Secret。GHSA-rh44 の担当） |
| 3 | `deploy/helm/microservices-platform`（`global.messaging`） | 触らない（段 3） |
| 4 | `deploy/local/vault/eso/externalsecret-rabbitmq*.yaml`（既存 2 本） | 触らない（既定のユーザーの写し） |
| 5 | `.github/workflows/cutover-rehearsal.yml` の `rabbitmqctl list_queues` | 影響なし（`kubectl exec` は Pod 網を通らない） |
| 6 | `scripts/republish-document-updated.js` | 影響なし（到達は port-forward） |
| 7 | `deploy/local/README.md` | 段 1 の節を足す |

## 事前の確認（NetworkPolicy）

| 対象 | 結果 |
| --- | --- |
| CNI の強制 | 基盤の `k3d cluster create` は `--disable-network-policy` を付けていない。k3s は上流の既定で強制する。稼働クラスタでは未実測（IADR-0522 残余 1 と同じ） |
| DNS | 影響なし（ingress だけ） |
| health check | readiness は exec（`rabbitmq-diagnostics ping`）で通信を要らない |
| Prometheus の scrape | 該当なし（ブローカは scrape の対象ではない。唯一の対象は collector） |
| ingress（エッジ） | 該当なし（ClusterIP のみ） |
| 運用者の到達（管理 UI・`rabbitmqctl`） | `kubectl port-forward` / `kubectl exec` は Pod 網を通らない |

## 受け入れ基準

- [x] 表の基盤側は chart の `messaging: true` と、AST 側は AST の chart の Worker と 1 対 1
- [x] NetworkPolicy は表の Pod だけを 5672 で許し、15672・ipBlock・空の from を描かない
- [x] KV が無ければ全ユーザーに互いに違う乱数を標準入力で入れ、値は引数にも出力にも出ない。在れば無いユーザーだけ足す
- [x] `ESO=1` で ExternalSecret を apply し、その後に全ユーザーを合わせる（パスワードは標準入力・既定のユーザーに触れない）。値が揃わなければ止まる
- [x] 変異: NetworkPolicy から 1 件消す・種を固定値にする → それぞれ赤

## 配備前の手順（稼働中の PoC。値は書かない。配備は閉場後）

1. 閉場後に `VAULT=1 ESO=1 bash scripts/k8s-local-up.sh` を回す。種（乱数）は bootstrap が入れる。手で入れる値は無い。
2. `bash deploy/local/rabbitmq-setup/reconcile-users.sh --check` が 18 件を示す。
3. 既存の接続は既定のユーザーのまま動き続ける（切り替えは AST の PR〔段 2〕と基盤の段 3）。NetworkPolicy は既存の全クライアントを許す列挙である。
4. 強制の確認: ラベルの無い使い捨ての Pod から `nc -z -w 3 rabbitmq.platform-infra 5672` が時間切れになる（許された側のラベルを付けた Pod は作らない）。
