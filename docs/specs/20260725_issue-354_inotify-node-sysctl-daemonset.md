---
title: "経路B ノード inotify 上限を特権 sysctl DaemonSet で引き上げる（#354 障害2 恒久修正）"
type: spec
status: done
related_ids:
  - IADR-0100
  - IADR-0066
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0100_local-node-inotify-sysctl-daemonset.md"
  - "../../deploy/local/infra/inotify-sysctl.yaml"
  - "../../deploy/local/infra/kustomization.yaml"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: 経路B ノード inotify 上限の引き上げ（#354 障害2）

## 起点

`#354`（ローカル立ち上げ振り返り）で観測した**障害2**の恒久修正。設計判断（特権 DaemonSet の導入・
node 側で対処する方針）を伴うため **IADR-0100** を採番。障害1（ESO apiVersion）は別 PR（#374）で対応済み。

## 背景・根本原因

全機能 ON（MSP＋AST 多数＋Loki/Promtail）を単一ノードに積むと広範な CrashLoopBackOff が発生。落ちる .NET
サービス全ての起動時例外は共通で
`IOException: The configured user limit (128) on the number of inotify instances has been reached`
（`FileSystemWatcher.StartRaisingEvents` → `WebApplication.CreateBuilder`）。実測でノード
`fs.inotify.max_user_instances=128`。原因は .NET `ConfigurationManager` の reloadOnChange 監視＋Promtail 等が
inotify インスタンスを消費し上限超過。secret 欠落・依存未起動ではない（例外は secret/DB 到達前）。

## 変更内容

1. **`deploy/local/infra/inotify-sysctl.yaml`（新規）**: 特権 initContainer を持つ DaemonSet。
   `/proc/sys/fs/inotify/max_user_instances=1024`・`max_user_watches=1048576` を直接引き上げ（safe-sysctl
   allowlist 非経由）。main は非特権の待機コンテナ（`drop:[ALL]`・非 root・read-only rootfs）。全ノードに配置
   （`tolerations: Exists`）。ノード再起動時は Pod 再作成で再適用。
2. **`deploy/local/infra/kustomization.yaml`**: `inotify-sysctl.yaml` を resources に追加（namespace 直後）。
   `PERSIST=1` の `infra-persistence` も `../infra` を base にするため両経路で適用。
3. **`scripts/k8s-local-up.sh`**: [4/7] でアプリ Pod（[6/7] MSP・後続 AST）より前に
   `rollout status ds/inotify-sysctl` を **best-effort**（`|| WARN`）で待つ。busybox pull 等の一時失敗で
   `pipefail` 下の up 全体を止めない。
4. **`docs/adr/IADR-0100_*.md` / `docs/adr/README.md`**: 決定記録と索引行。

## 非対象（無改変）

- 本番 chart（`deploy/helm`）・消費側・realm。
- 障害1（ESO apiVersion）は別 PR #374。
- アプリ側 `reloadConfigOnChange=false`（代替案・不採用。IADR-0100 参照）。

## 受け入れ基準と検証

- [x] `kubectl kustomize deploy/local/infra`（client build・クラスタ不要）で DaemonSet がレンダリングされる。
- [x] `kubectl apply --dry-run=server`（非破壊）で DaemonSet が妥当。
- [x] `node scripts/k8s-local-up.test.js` 全 green（新規回帰含む: kustomize 収録・両 sysctl キー・rollout 待ち）。
- [x] 特権は initContainer のみ。main は非特権。本番 chart・realm 無改変。gitleaks green（平文秘密なし）。

## 実証

ユーザーが手動で `sysctl -w fs.inotify.max_user_instances=1024` → 対象 Deployment を `rollout restart` した
結果、全 Pod が Running へ復旧済み（#354 コメント）。本 PR はその復旧手順を **`up` で自動適用**する形に恒常化する。
