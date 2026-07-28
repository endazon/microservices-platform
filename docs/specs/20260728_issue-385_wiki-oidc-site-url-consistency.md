---
title: "wiki-oidc README の Site URL 記述を経路別に整合させる（#401 の残件・Issue #385）"
type: spec
status: done
related_ids:
  - IADR-0095
  - IADR-0091
  - IADR-0032
  - IADR-0020
  - FR-13
  - SC-04
author: claude
created: 2026-07-28
updated: 2026-07-28
related_specs:
  - "../adr/IADR-0095_wikijs-keycloak-oidc.md"
  - "20260726_issue-385_wiki-oidc-portforward-redirect.md"
  - "20260721_issue-353_wikijs-keycloak-oidc.md"
  - "../../deploy/local/wiki-oidc/README.md"
---

# 仕様書: wiki-oidc README の Site URL 経路別整合（Issue #385 残件）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（Wiki 閲覧）／画面（SC）: SC-04（Wiki アクセス）
- 関連 ADR: [IADR-0095](../adr/IADR-0095_wikijs-keycloak-oidc.md)（Wiki.js OIDC・経路別 redirect 表＝単一情報源）／
  [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（edge 集約 50000）／
  [IADR-0032](../adr/IADR-0032_wikijs-dev-exposure-opt-in.md)（compose の 3001 公開）／
  [IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js 配備・OIDC 単一経路）
- Issue: #385（bug/documentation・priority:could）
- 先行: PR #401（`docs/specs/20260726_issue-385_wiki-oidc-portforward-redirect.md`）

## 現状把握（#401 で解決済みの分と、残っていた分）

Issue #385 の**主要因**（realm に `http://localhost:3300/*` が未登録なのに README が「登録済み」と案内していた点）は
**PR #401 で解決済み**である。本作業着手時点で以下は確認済み・不変:

| 確認項目 | 現状 | 判定 |
| --- | --- | --- |
| realm `wiki-js` の `redirectUris` | `wiki.localhost:50000/*` / `localhost:3300/*` / `localhost:3001/*` / `wiki-js:3000/*` | ✅ 3300 登録済み（#401） |
| realm 名 | `microservices-platform`（README の全記述と一致） | ✅ 不整合なし |
| edge port | `50000`（`deploy/local/edge/*` の Ingress host ＋ `k8s-local-up.sh` の `-p 127.0.0.1:50000`） | ✅ README と一致 |
| `scripts/check-realm-constraints.js` | 経路別必須 URL の欠落検査あり・実行結果 OK | ✅ 緑 |

**残っていたのは README 内部の自己矛盾**である。`deploy/local/wiki-oidc/README.md` は Site URL を
**2 箇所で無条件に edge 集約 URL と断言**する一方、末尾の「注意」節では port-forward 単独時に `localhost:3300`
にせよと述べており、読み手はどちらに従うべきか判断できない:

| 箇所 | 記述 | 問題 |
| --- | --- | --- |
| §OIDC 設定手順（管理UI）| 「**Site URL**: `http://wiki.localhost:50000` に設定する」 | 経路の条件が無い |
| §DB seed で入れる | 「`settings.host`（Site URL）は**必ず** edge 集約 URL にする」 | 「必ず」が注意節と真っ向から矛盾 |
| §DB seed の SQL | `UPDATE settings SET value='{"v":"http://wiki.localhost:50000"}'` | 非 edge 用の値に差し替える術が示されない |
| §注意 | 「port-forward で OIDC を使う場合は Site URL を `http://localhost:3300` にする」 | 上記と矛盾 |

加えて冒頭が「**本 PR で** …realm に追加し、edge に wiki route を足した」という PR 時点の語りのままで、
README（現状の説明）として読むと何が既に入っているのか判別しづらい。

## 目的

`wiki-oidc/README.md` の Site URL 記述を **経路（edge / port-forward）と 1 対 1 に対応する形へ統一**し、
どの節を先に読んでも同じ結論に至るようにする。[IADR-0095] の「追記（2026-07-26・Issue #385）」の経路別対応表を
単一情報源として参照する。

## 対象範囲

- 対象: `deploy/local/wiki-oidc/README.md` のみ（**docs 変更 1 ファイル**）
- 非対象（不変）:
  - `deploy/keycloak/microservices-platform-realm.json`（3300 は #401 で登録済み・**追加変更なし**）
  - `deploy/local/README.md` / `deploy/local/values-local.yaml`（#401 で是正済み）
  - `docs/adr/IADR-0095_...md`（経路別対応表は #401 の追記で確定済み・**書き換えない**）
  - `scripts/check-realm-constraints.js`（検査は #401 で追加済み）
  - Wiki.js manifest・chart・アプリコード・稼働中の live 環境

## 変更方針

1. **Site URL の決定規則を 1 箇所に集約する。** §OIDC 設定手順の直下に「Site URL は**利用する経路の到達 URL と
   一致させる**」旨と経路別表（edge=`wiki.localhost:50000` / port-forward=`localhost:3300`）を置き、
   realm 側の登録済み redirect と対応づける。
2. **矛盾していた 2 箇所を、この規則を参照する形へ書き換える。** §OIDC 設定手順の表と §DB seed の
   「必ず edge 集約 URL」を、いずれも「経路に一致させる（既定は edge）」へ改める。
3. **DB seed の SQL を非 edge でも使えるようにする。** ハードコードを `SITE_URL` 変数（既定＝edge 集約 URL）へ置換し、
   port-forward 運用時の上書き例を添える。既定値を変えないため **edge 経路の手順はバイト等価**。
4. **冒頭の「本 PR で」を現状記述へ改める**（何が realm/edge に入っているかを状態として述べる）。

## 受け入れ基準

- [x] README 内で Site URL の指示が経路と 1 対 1 に対応し、無条件の「必ず edge 集約 URL」が残っていない
- [x] 経路別の値（edge=50000 / port-forward=3300）が realm の登録済み redirect と一致している
- [x] DB seed 手順が非 edge 経路でもそのまま使える（変数化・既定は edge のままで後方互換）
- [x] realm / manifest / スクリプト / 他 README は無改変（docs 1 ファイルのみ）

## 検証

- `node scripts/check-realm-constraints.js`（経路別必須 URL の欠落検査）が緑
- `node scripts/check-doc-links.js`（相対リンク破損なし）が緑
- README 内の Site URL 記述を目視で突合し、`50000` / `3300` の各値が realm `wiki-js` の
  `redirectUris` に実在することを確認（`redirectUris` は本作業で変更しない）

## IADR の要否

**不要。** 経路別の port topology は既に [IADR-0095](../adr/IADR-0095_wikijs-keycloak-oidc.md) の
「追記（2026-07-26・Issue #385）」で決定・記録済みであり、本作業はその決定を README へ正しく反映する
**適用漏れの是正**にとどまる。新たな設計判断・技術選定は行わない（#401 と同じ判断）。
