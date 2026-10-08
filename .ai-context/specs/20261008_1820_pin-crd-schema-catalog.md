---
title: 作業仕様書 kubeconform の CRD スキーマカタログをコミット SHA で固定する（#1820）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0240
author: claude
created: 2026-10-08
updated: 2026-10-08
---

# 作業仕様書 kubeconform の CRD スキーマカタログをコミット SHA で固定する（#1820）

## 背景

`static-checks-units` の「Check charts and overlays render」が、`deploy/` に触れない PR #1815 で 2 回連続して落ちた（`ClusterSecretStore` の `could not find schema`）。原因は `scripts/check-deploy-manifests.js` が `datreeio/CRDs-catalog` を `main` で引いていることと、上流の 2026-10-08 13:01 UTC の更新（`b7e2015`）。詳細は IADR-0240 の 2026-10-08 追記。

## 受け入れ基準と試験

| 受け入れ基準 | 確かめ方 |
| --- | --- |
| カタログ参照を SHA で固定し `deploy/local/vault*` が通る | `kubeconform` を固定 SHA のカタログで実走し 4/4 Valid（手元）。CI の `static-checks-units` |
| 否定形: ブランチ参照へ戻ると落ちる | `scripts.repo.test.js`「NFR / #1820」 |
| 上げ方を IADR に書く | IADR-0240 追記 |

## 母集合（規則 9・10）

- 規則 9: `grep -rn "CRDs-catalog" scripts .github docs deploy` — 参照は `scripts/check-deploy-manifests.js` の `SCHEMA_LOCATIONS` だけ（コメント 2 か所を含む）。ai-stock-trading の `scripts`・`.github` にも参照は無い。
- 規則 10: 本変更で誤りになる記述 — IADR-0240 本文の「CRD は CRDs-catalog で解決する」は固定の有無に触れておらず誤りにはならない（追記で補う）。

## 範囲外

- kubeconform 本体の更新（新しいスキーマを扱えるかの評価は別件）。
