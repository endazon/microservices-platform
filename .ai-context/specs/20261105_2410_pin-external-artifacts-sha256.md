---
title: 作業仕様書 — 起動器が取り込む外部の chart とマニフェストを、版に加えて内容の sha256 で固定する（#2410）
type: spec
status: done
related_ids: [NFR-18, ADR-0107, ADR-0135, IADR-0519, IADR-0547, IADR-0679]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2410"
---

# 作業仕様書 — 外部の chart とマニフェストを sha256 で固定する（#2410）

> 着手 2026-11-05（develop `31bd4833`）。判断の記録は **IADR-0679**（新規。番号は仮）。稼働中のクラスタには何も実行しない。

## 起点（トレーサビリティ）

- 非機能要求 **NFR-18**。計画 ADR **ADR-0107 決定 3**（同じタグの中身の差し替えを入れない）・**ADR-0135 決定 2**（版の固定）。
- 前提の IADR: **IADR-0519**・**IADR-0547**（版の固定と、内側のイメージの digest の固定）。

## 母集合（規則 9。走査で引いた）

走査語: `helm upgrade --install`・`helm repo add`・kustomization の `resources` の `https://`・マニフェストと values の `image:`（digest の有無）。

| 種類 | 対象 | 現状 | 本件 |
| --- | --- | --- | --- |
| helm chart | `istio/base`・`istio/istiod`（k8s-local-up.sh）・`istio/gateway`（istio-edge-up.sh）1.30.4 | 版だけ（chart のリポジトリから取る） | 固定の URL の tgz を取り、sha256 を突き合わせてからそのパスで入れる |
| helm chart | `external-secrets` 2.8.0・`reloader` 2.2.17（k8s-local-up.sh） | 同上 | 同上 |
| URL のマニフェスト | `deploy/local/edge/cert-manager`（cert-manager v1.21.1）・`deploy/argocd/install`（Argo CD v3.5.4） | 版だけ（release / tag の URL） | 取得して sha256 を突き合わせ、URL を差し替えた写しを当てる（`remote-resources.sha256`） |
| イメージ | `deploy/` のマニフェスト・values・kustomization の `images` | すでに `tag@sha256` で固定（`scripts/check-image-digests.js` が検査） | 変えない |

sha256 の値は、chart は各リポジトリの `index.yaml` の `digest` と、取得した tgz の `sha256sum` が一致することを確かめた。マニフェストは 2 回取得して一致を確かめた（2026-11-05）。

### 除外とその理由

| 対象 | 理由 |
| --- | --- |
| 自製のイメージ（`microservices-platform/*`・`k3d-local/*`） | 手元でビルドし、不変のタグ（git の sha）で入れる（IADR-0605） |
| `deploy/cloud/` | 並行の作業（#2401 / #2402）の範囲 |
| ワークフローのツールの取得（k3d・kubectl 等） | CI の版の固定は既存の検査の範囲。本件は起動器が取り込むもの |
| `deploy/docker-compose.yml` | 手元の compose の経路（イメージは digest で固定済み） |

## 方針（IADR-0679）

- 口は `scripts/lib/pinned-artifacts.sh` の 1 つ（chart の表・取得と確かめ・kustomization の写し）。
- 合わなければ何も当てずに止まる。取得物は一時ディレクトリに置き、終了のときに消す。
- `helm repo add / update` は使わない。

## 受け入れ基準

- AC-1: 起動器・入口のスクリプトが入れる chart は、固定の URL の tgz を sha256 で確かめてからそのパスで入れる（試験）。
- AC-2: sha256 が合わなければ、chart もマニフェストも当てずに止まる（試験）。
- AC-3: 版の固定（`pin_version`）の chart は全部 sha256 の表に在り、kustomization の URL は全部 `remote-resources.sha256` に在る（試験）。
- AC-4: 既存の試験が通る。

## 残余

1. 取得元が消えた・移ったときは起動器が止まる（固定の中身を取れないため。版を上げるときに表を改める）。
2. 稼働のクラスタでの初回の実行は利用者が行う（取得元への到達が要る）。
