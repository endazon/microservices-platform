---
title: 作業仕様書 — ingestion-service を 1 レプリカに保つ暫定の検査（#2271 の部分）
type: spec
status: in-progress
related_ids: [NFR, FR-02, IADR-0595, IADR-0640]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#2271"
---

# 作業仕様書 — ingestion を 1 レプリカに保つ暫定の検査（#2271 の部分）

> 本仕様書は実装着手前に作成した（着手 2026-10-31。develop `996e543a`）。MSP の棚卸し（2026-10-31・コーディネーター承認）の (A) の 1。

## 範囲

- **本 issue の本体は扱わない**（比較交換・分散の排他。`blocked:env`）。
- 2026-10-30 の棚卸しコメントが予告した**暫定の検査**だけを入れる。
  - 2 つの場合に fail にする（helm を使わない `scripts/` の検査）。
    - values で ingestion の replicas が 1 を超える。
    - `scaling.services`（HPA の対象）に ingestion がある。
  - 本 issue は閉じない（`Refs #2271`）。
- 理由: 版の印（Qdrant）は比較交換を持たない。ingestion が 2 レプリカ以上・並行受信になると、次の窓が開く（IADR-0640 の Q6・IADR-0595 の残余）。
  - 削除の墓石の上書き。
  - 古い版による新しい版の印の上書き。
  - 今は既定の 1 レプリカで閉じているが、閉じていることを機械が見ていない。

## 受け入れ基準

- [ ] 実データ（develop）で検査が緑になり、見た値の出どころを出力に名指す。
- [ ] 自己試験の陰性対照が、それぞれ赤になる。
  - replicas を 2 にする。
  - `scaling.services` に ingestion を足す。
  - Argo CD の `helm.parameters` で replicas を上書きする。
  - スクリプトの `--set` で replicas を上書きする。
- [ ] 0 件走査（values.yaml に `services.ingestion` が無い）で緑にしない。
- [ ] CI の `scripts-tests` が実データで回す（`scripts.repo.test.js`）。ワークフローは変えない。

## 母集合（規則 9・10）

- **規則 9**: chart の値が入る経路を全部走査した。語は「values」「--set」「helm.parameters」「scaling.services」「replicas」。
  - `deploy/helm/microservices-platform/values.yaml`: 本番像。`services.ingestion.replicas: 1`、`scaling.services` に ingestion は無い。
  - `deploy/local/values-local.yaml`: 経路 B。`scaling.enabled: false`。ingestion の上書きは無い。
  - `deploy/helm/microservices-platform/ci/*.yaml`（6 本）: CI の描画用。ingestion の上書きは無い。
  - `deploy/argocd/application.yaml` の `helm.parameters`: 今は replicas・scaling の上書きは無い。
  - `scripts/*.sh`・`.github/workflows/*.yml` の `--set`: `services.ingestion.replicas`・`scaling.services` の上書きは 0 件。
  - テンプレート `deployment.yaml`: HPA の対象（`scaling.enabled` かつ `scaling.services` に含む）なら replicas を描かず、HPA が所有する。だから `scaling.services` も見る。
- **規則 10**: IADR-0640 の残余は「スケールアウトの前に解消する」とだけ書いている。検査の存在を、日付つきの追記で書き足す。#2271 本文の「暫定」の扱いは、コメントで追随する。

## 検証

- `node scripts/check-single-replica-workers.js`（実データ）と `--self-test`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（コミットした状態で）。
- 文書系の検査。
