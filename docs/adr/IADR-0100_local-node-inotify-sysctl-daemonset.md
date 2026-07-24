---
title: IADR-0100 経路B のノード inotify 上限を特権 sysctl DaemonSet で引き上げる（#354 障害2）
type: impl-adr
status: Accepted
related_ids:
  - IADR-0066
  - IADR-0091
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md (ローカル k8s=k3s/Rancher・経路B 前提。inotify 等の node チューニングに関する計画 ADR は無し)"
author: claude
created: 2026-07-25
updated: 2026-07-25
---

# IADR-0100: 経路B のノード inotify 上限を特権 sysctl DaemonSet で引き上げる

## 背景（#354 障害2）

経路B（ローカル k8s・単一ノード）で全機能 ON（MSP 多数サービス＋AST 多数サービス＋`OBSERVABILITY` の
Loki/Promtail 等）を積むと、広範な `CrashLoopBackOff` が発生した。落ちる .NET サービス全ての起動時例外は
共通で:

```
System.IO.IOException: The configured user limit (128) on the number of inotify instances
has been reached ... at System.IO.FileSystemWatcher.StartRaisingEvents()
 ... at WebApplication.CreateBuilder(args)     ← Program.cs の最初の行
```

実測: ノードの `fs.inotify.max_user_instances=128`（既定）。

## 根本原因

.NET の `ConfigurationManager` は `appsettings.json` を `reloadOnChange=true` で監視するため、**各サービスが
起動時に inotify インスタンスを 1 つ消費**する。Promtail 等ログ収集も inotify を大量消費する。単一ノードに
Pod を積むほど消費が増え、ノード上限 128 を超過。以後、再起動した Pod は watcher を確保できず、**設定読込前
（＝secret/DB へ到達する前）にクラッシュ**する。secret 欠落・依存未起動ではない（例外は `CreateBuilder` の
最初＝それらを触る前に発生）。

## 決定

**ノードの inotify 上限を引き上げる。アプリ側（`reloadConfigOnChange=false`）ではなくノード側で対処する。**

- 理由: 枯渇は**ノード資源上限**であり、.NET だけでなく Promtail など非 .NET も消費する。アプリ側無効化は
  .NET のみ・全サービスの extraEnv 改変が必要で網羅性・保守性に劣る。ノード側の 1 箇所修正が全 Pod に効く。
- 実装: `deploy/local/infra/inotify-sysctl.yaml` に **特権 initContainer を持つ DaemonSet** を追加し、
  `/proc/sys/fs/inotify/max_user_instances=1024`・`max_user_watches=1048576` を直接引き上げる（kubelet の
  safe-sysctl allowlist を経由しない node チューニングの定石）。main は待機のみ（DaemonSet 常駐の最小コンテナ・
  非特権）。ノード再起動時は Pod 再作成で initContainer が再適用（永続化）。
- 配置: `deploy/local/infra` の kustomize に登録（`PERSIST=1` の `infra-persistence` も `../infra` を base に
  するため両経路で適用される）。`k8s-local-up.sh` の [4/7] で **アプリ Pod（[6/7] MSP・後続 AST）より前**に
  適用・rollout 待ち（**best-effort**＝busybox pull 等の一時失敗で up 全体を止めない）。

## スコープと非対象

- **dev 専用**（`deploy/local`）。本番 chart（`deploy/helm`）・消費側・realm は無改変。
- 特権は本 DaemonSet の initContainer のみに限定（blast radius を node チューニングに限定・dev クラスタ前提）。
  main コンテナは `drop: [ALL]`・非 root・read-only rootfs。
- 値（1024 / 1048576）は経路B 全機能 ON を賄う余裕値。ユーザーが手動で `sysctl -w` した実績（1024 で復旧）を
  恒常化したもの。

## 代替案（不採用）

- **アプリ側 `hostBuilder:reloadConfigOnChange=false`**: .NET のみ対象で Promtail 等に効かず、全サービスの
  extraEnv 改変が必要。ノード側修正の方が網羅的・低コスト。
- **Rancher Desktop provisioning の sysctl 永続化のみ**: コード追従されず（ユーザー手動・環境依存）、CI/再現性に
  劣る。DaemonSet なら `up` で自動適用される。

## 影響・回帰

- 既定 infra に DaemonSet が 1 つ増える（特権 initContainer・軽量 pause）。
- `k8s-local-up.test.js` で (1) infra kustomize に `inotify-sysctl.yaml` が含まれること、(2) DaemonSet が
  両 sysctl キーを特権で設定すること、(3) up-script が `ds/inotify-sysctl` の rollout を待つこと、を固定。
