---
title: 作業仕様書 — ローカル k8s の稼働ホストの前提（必要メモリ・.wslconfig・電源）を README に書き、全 Pod の requests / limits を静的に棚卸しする（#1919）
type: spec
status: done
related_ids: [NFR, IADR-0066]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs: []
issue: "#1919"
---

# 作業仕様書 — 稼働ホストの前提と requests / limits の棚卸し（#1919）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は MSP `origin/develop` `966c8d94`。
> 対の作業は AST 側の作業仕様書 `20261014_msp1919_host-memory-prereq.md`（AST の Runbook に同じ前提を書く）。

## 起点

- 起点 issue: **#1919**（PoC の稼働ホストで Rancher Desktop（WSL2）の VM が 3 日で 4 回停止・一斉再起動）。
- PoC の報告（2026-10-14 のコメント）: `kubectl top node` が 13,349 Mi / allocatable 15,890 Mi（86%）。ホストは物理 31 GB で
  `.wslconfig` が無く、WSL2 の既定の上限（物理の 50%）に張り付いている。オーナーが電源接続時のスリープと休止を無効にした。
  閉場後に `.wslconfig` を `memory=24GB`・`swap=8GB` にして反映する予定。
- 計画: 該当する FR は無い（運用の前提・メタ作業）。番号付きの NFR にも当たるものが無いため無採番の `NFR` とする。計画への環流は要らない。
- 関連 IADR: [IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（ローカル k8s dev 環境）。前提を README に足すだけで決定は変えない。
  **新しい IADR は起こさない**（コードと構成の判断が無い。値の確定は実測の後で、その時に要否を判断する）。

## 範囲

| やる | やらない |
| --- | --- |
| `deploy/local/README.md` に「稼働ホストの前提」節（必要メモリと根拠・`.wslconfig` の推奨値と反映手順・スリープと休止の無効化） | requests / limits の値の変更（実測 `kubectl top pod` の到着後に確定する） |
| 両リポの全 Pod の requests / limits を静的に棚卸しした表（issue のコメントの下書き） | `k8s-local-up.sh` への `--set resources` の追加（上流チャートの limits 欠落は実測後） |
| 同 README の「Docker Desktop はメモリ 8GB+ 推奨」の記述を新しい節へ向ける（規則 10） | MSP の .NET サービスへの GC 設定の追加（AST と揃えるかは実測後） |

## 走査した母集合（規則 2・9・10）

- 棚卸しの対象: `helm template` の描画（MSP は `-f deploy/local/values-local.yaml`、opt-in の `embedding`・`syntheticMonitor` を足した描画も。
  AST は `-f values-local.yaml --set opend.enabled=true`）、`kubectl kustomize` の描画（`deploy/local/{infra,infra-persistence,observability,
  observability-persistence,vault,vault-persistence,headlamp,synthetic-monitor,platform-backup/*}`）、
  `k8s-local-up.sh` が入れる上流（istiod 1.30.4＋`deploy/istio/istiod-values-local.yaml`、ingress gateway の values、ESO 2.8.0、Reloader 2.2.17、
  Argo CD v3.5.4 の `install.yaml`、cert-manager v1.21.1 の `cert-manager.yaml`）。上流は `helm template` / 取得した manifest で既定値を確かめた。
- GC の設定: `git grep -nE 'GCHeapHardLimit|GCConserveMemory|gcServer|ServerGarbageCollect|GCHeapCount|MALLOC_ARENA'`（`.ai-context/specs/`・`CHANGELOG.md` を除く）
  → MSP は **0 件**（チャート・Dockerfile・csproj のどこにも無い）。AST は `templates/deployment.yaml` と `helm.yml` の検査。
- 追随（規則 9・10）: `git grep -nE '[0-9]+ ?GB\+?|wslconfig|memory='`（`*.md`）→ 本 README の 39 行目（Docker Desktop の「メモリ 8GB+ 推奨」）だけが
  新しい節と食い違う。他は Prometheus の保持容量などで無関係。

## 必要メモリの根拠（計算）

| 束 | 中身 | requests 合計 | limits 合計 | limits 無し |
| --- | --- | --- | --- | --- |
| 必須 | MSP チャート 18 + infra（PERSIST 既定）13 コンテナ + AST チャート（OpenD 込み）13 コンテナ | 6,056 Mi | 24,096 Mi | 0 |
| ESO 所有の経路（AST の既定） | `VAULT=1`（OpenBao）＋`ESO=1`（ESO 3・Reloader 1） | +128 Mi | +512 Mi | 4 |
| opt-in | 可観測性 7・Istio（istiod・gateway・サイドカー 18）・Headlamp・Argo CD 7・cert-manager 3 | +2,112 Mi | +8,448 Mi | 10 |

- 実測 13.3 GiB は requests の合計（必須＋ESO で約 6.0 GiB）の 2 倍を超える → **requests は実際の使用量を表していない**。
- 推奨 `memory=24GB` は、必須＋ESO の limits の合計（約 24.0 GiB）にほぼ等しい。全 Pod が同時に limits へ達しても VM の上限に収まる線である
  （k3s・containerd・イメージのビルドの分は含まない。推測）。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | README に必要メモリ・根拠（requests / limits の合計）・推測の明記がある | 目視（diff） |
| 2 | `.wslconfig` の推奨値（memory・swap・processors）と反映手順（Rancher Desktop の終了 → `wsl --shutdown` → 起動 → 確認）がある | 目視 |
| 3 | スリープと休止の無効化がある | 目視 |
| 4 | values / templates を変えていない（変えたなら `helm lint`・`helm template`） | `git diff --stat` に `deploy/helm` が無い |
| 5 | 文書系の検査器が緑 | `check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-links` ほか |
