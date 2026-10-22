---
title: 作業仕様書 — チャートの Pod のセキュリティ設定と Pod Security Admission（GHSA-j4qv-2w99-xg5p の 1 本目）
type: spec
status: done
related_ids: [NFR, ADR-0005, ADR-0007, ADR-0008, ADR-0021, IADR-0583]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008 （k3s 実行基盤）
  - planning:projects/microservices-platform/07_adr/ADR-0005 （サービスメッシュ）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — Pod のセキュリティ設定

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0583**（新規）に置く。
> 多層防御の強化・最小権限。参照は GHSA の ID だけとする。
> GHSA-j4qv-2w99-xg5p の対応の **1 本目**（Pod のセキュリティ設定）。残りは別の PR で行う。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR**（セキュリティ）。
- 計画 ADR: **ADR-0008**（k3s 実行基盤）・**ADR-0005**（サービスメッシュ）・**ADR-0007**（Deployment の骨格）・**ADR-0021**（SPA の配信は Caddy）。
  矛盾しない（実行基盤・メッシュ・配信の選択を変えず、Pod の設定を足すだけ）。計画に Pod のセキュリティ設定を定める記述は無い
  （`git grep -i -E "securityContext|pod security|runAsNonRoot|PodSecurity" origin/main -- projects/microservices-platform` が 0 件）。
- 実装 ADR: **IADR-0583**（新設）。

## 要件（コーディネーター指示 2026-10-22）

1. Pod のセキュリティ設定（securityContext・Pod Security の水準）を最小権限の側へ寄せる。
2. 稼働中の PoC を壊さない（既定を安全側にしつつ、配備前の手順を PR 本文に書く）。

## 受け入れ基準

| # | 基準 | 写像先（`scripts/helm-pod-security.test.js`） |
| --- | --- | --- |
| B1 | 既定・values-local・任意部品を全て有効にした描画で、Pod を持つ全ての資源が seccomp RuntimeDefault、全コンテナが権限昇格なし | `default/local/all: Pod を持つ全ての資源が…` |
| B2 | 外部 3 つ（SeaweedFS・Wiki.js・TEI）以外は非 root（runAsNonRoot・uid / gid 1 以上）・全コンテナがケーパビリティなし | 同上・`.NET の全 Deployment は aspnet の APP_UID…` |
| B3 | 専用の SA を持たない Pod はトークンを置かない。持つ Pod（BFF・datasource）は置く | 同上・`all: 想定した資源が実際に描かれている` |
| B4 | Namespace: warn / audit = restricted。enforce = baseline は注入が無いときだけ | `Namespace: …` ＋ `k8s-local-up.test.js` の IADR-0583 の 2 本 |
| B5 | uid が 0・数でない値、水準が値域の外なら描画で止まる | `描画で止まる…` |
| B6 | frontend の Dockerfile の USER ＝ chart の uid・ファイルケーパビリティを外す。合成監視の overlay ＝ chart。起動器の水準 ＝ chart の既定 | `供給側: …` の 3 本 |
| B7 | 判定が空振りしない（描画物から 1 つ抜くと赤） | `変異: …` |

## 母集合（規則 9・10）

- Pod を描くチャートの template: `git grep -l -E "kind: (Deployment|Job|StatefulSet|DaemonSet|CronJob)" -- deploy/helm` →
  `deployment.yaml`・`frontend.yaml`・`seaweedfs.yaml`・`wikijs.yaml`・`embedding.yaml`・`synthetic-monitor.yaml`・`drift-postsync-job.yaml` の 7 つ。全てに足す。
- `securityContext` の既存: `git grep -n securityContext -- deploy/helm` が 0 件（重ねる既存の設定は無い）。
- k8s API を使うサービス: `git grep -l -E "serviceaccount/token|KubernetesClient" -- src ':!*Tests*'` → BFF（Vault・ExternalSecret の同期）と
  datasource（Vault）の 2 つ。どちらも専用の SA を持つ。他の Pod のトークンを外す。
- ファイルを書くサービス: `git grep -n -E "GetTempPath|PersistKeysTo|File\.Write" -- src ':!*Tests*'` → 一時ディレクトリ（`/tmp`）と Redis だけ。
  非 root で書ける（`readOnlyRootFilesystem` は付けない）。
- 経路B の Namespace: values-local は `namespace.create=false`（起動器が作る）→ 起動器 [5/7] でラベルを貼る。
- 合成監視の overlay（`deploy/local/synthetic-monitor`）はチャートと別に Pod を描く → 同じ値を足す。
- 射程外: platform-infra・observability の名前空間の Pod（IADR-0583 §残る制約）。

## 実装

- `templates/_pod-security.tpl`（新規）: restricted / 外部のイメージ用の Pod・コンテナの `securityContext`。
- 上の 7 つの template に付ける。`values.yaml` に `podSecurity`（水準・.NET の uid）・`frontend.runAsUser`・`syntheticMonitor.runAsUser`・
  `drift.postSyncHook.runAsUser` を足す。`namespace.yaml` に Pod Security Admission のラベル。
- `src/platform/frontend/Dockerfile`: 非 root（uid 10001）・ファイルケーパビリティを外す・`config.js` `/config` `/data` だけ書ける。
- `scripts/k8s-local-up.sh` [5/7]: Namespace のラベル。`deploy/local/synthetic-monitor/synthetic-monitor.yaml`: 同じ設定。
- 試験: `scripts/helm-pod-security.test.js`（新規・CI の static-checks-units）・`scripts/k8s-local-up.test.js`（2 本）。

## 配備前の手順（PoC）

- 配備は閉場後。
- 順序: このコミットの作業ツリーで up を再実行する。up は [2/7] でイメージを作り直して取り込み（frontend は非 root の新しいイメージになる）、
  [5/7] で Namespace のラベル、[6/7] で chart を当てる。🔴 chart だけを先に当てない（古い frontend のイメージは非 root では config.js を書けずに起動しない）。
  値・Secret の変更は無い。
- 配備後の確認: `kubectl get ns microservices-platform --show-labels` に warn / audit = restricted があること。
  全 Deployment が Ready であること。SPA の `/config.js` が 200 を返すこと。
