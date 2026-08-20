---
title: 経路B「Wiki 閲覧」(SC-04) の WIKI_BASE_URL を edge 集約後 URL(wiki.localhost:50000) へ整合する（Issue #344・IADR-0091 フォローアップ）
type: spec
status: done
related_ids:
  - SC-04
  - UC-07
  - FR-13
  - IADR-0091
  - IADR-0095
  - IADR-0078
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "./20260720_issue-344_frontend-wiki-url.md"
  - "./20260721_issue-353_wikijs-keycloak-oidc.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0095_wikijs-keycloak-oidc.md"
  - "../../deploy/local/values-local.yaml"
  - "../../deploy/local/README.md"
  - "../../deploy/local/edge/README.md"
  - "../../docs/screens/SC-04_wiki-access.md"
---

# 仕様書: 「Wiki 閲覧」(SC-04) の WIKI_BASE_URL を edge 集約 URL へ整合（Issue #344・IADR-0091 フォローアップ）

## 起点となる計画書（トレーサビリティ）

- 画面(SC): SC-04 Wiki 閲覧 / UC-07 / FR-13。
- 決定: [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（ローカル edge 集約・Traefik。platform フロント=80/443・管理ツール=`*.localhost:50000`）の
  フォローアップ。Wiki.js OIDC は [IADR-0095](../adr/IADR-0095_wikijs-keycloak-oidc.md)。実行時 config 配線は [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)。
- Issue: #344（Wiki 閲覧の到達）。関連 #353/#356（edge 集約・SSO）。

## 背景と問題

先行作業 [20260720_issue-344_frontend-wiki-url](./20260720_issue-344_frontend-wiki-url.md) で、経路B の SC-04 は `values-local.yaml` の
`frontend.extraEnv` から `WIKI_BASE_URL=http://localhost:3300`（`wiki-js` の port-forward 前提）を供給していた。
その後 [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md) の edge 集約（`deploy/local/edge`・`LOCALEDGE=1`）で Wiki.js は `wiki.localhost:50000` に
公開され（`admin-ingress-wiki.yaml`・realm `wiki-js` client も `http://wiki.localhost:50000/*` 登録済み）、
edge を**ローカルの正規アクセス**とする運用に移行した。しかし `WIKI_BASE_URL` は `localhost:3300` のままで、
SC-04 の「Wiki を開く」が port-forward していない `localhost:3300` を指し到達できない不整合が残っていた
（[20260721_issue-353_wikijs-keycloak-oidc](./20260721_issue-353_wikijs-keycloak-oidc.md) の「非対象」で **任意対応として先送り**されていた分）。

本作業はこの先送り分を解消し、`WIKI_BASE_URL` を edge 集約後の正規 URL `http://wiki.localhost:50000` へ揃える。
併せて、これが先行 spec [20260720_issue-344_frontend-wiki-url](./20260720_issue-344_frontend-wiki-url.md) の受け入れ基準（`localhost:3300` を pin）および
`deploy/local/README.md` の port-forward 手順と矛盾しないよう、両ドキュメントを edge 前提へ整合改訂する。

## 対応方針

frontend コード・nginx テンプレ・config.js.template・realm.json・本番 chart には触れない純 config/docs 整合。

1. **`deploy/local/values-local.yaml`**: `frontend.extraEnv` の `WIKI_BASE_URL` を
   `http://localhost:3300` → `http://wiki.localhost:50000` へ。近傍コメントも edge 前提へ更新。
2. **`deploy/local/README.md`**「Wiki 閲覧の到達」節: LOCALEDGE 運用での `http://wiki.localhost:50000` を正規に、
   非 edge（port-forward）で使う場合は `WIKI_BASE_URL` を `http://localhost:3300` へ override する旨を併記。
3. **先行 spec [20260720_issue-344_frontend-wiki-url](./20260720_issue-344_frontend-wiki-url.md)** の受け入れ基準を、edge 集約後の値
   （`wiki.localhost:50000`）へ整合改訂し、更新の根拠（IADR-0091）を追記。
4. **[20260721_issue-353_wikijs-keycloak-oidc](./20260721_issue-353_wikijs-keycloak-oidc.md)** の「非対象」で先送りとした旨の**解消記録**を追記。
5. **本番像 `values.yaml`** は無改変（`frontend.extraEnv: []`・opt-in・後方互換）。

## 受け入れ基準

- [x] `values-local.yaml` の `WIKI_BASE_URL` が `http://wiki.localhost:50000` に配線される（realm `wiki-js` は
      同 URL 登録済み・IADR-0091 の edge 集約と一貫）。
- [x] `deploy/local/README.md` の Wiki 到達手順が LOCALEDGE 正規＋port-forward override 併記に更新される。
- [x] 先行 spec（#344）の受け入れ基準が edge URL へ整合改訂され、更新根拠が残る。
- [x] #353 spec に先送り解消の記録が残る。
- [x] 本番 `values.yaml` は無改変。`check-doc-links.js` が緑（docs リンク不変）。
- [ ] 実ブラウザでの SC-04 到達（`wiki.localhost:50000`・Wiki.js→Keycloak SSO）疎通は **live**（LOCALEDGE=1・
      稼働 k3d 依存）。

## 検証

- `node scripts/check-doc-links.js`（docs リンク整合）。
- `values-local.yaml` の YAML 妥当性（helm/kustomize の CI ジョブで parse）。
- 本番 `values.yaml` の `frontend.extraEnv` が空のまま（無改変）であること。
