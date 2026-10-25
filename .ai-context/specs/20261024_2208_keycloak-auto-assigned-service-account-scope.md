---
title: 作業仕様書 — Keycloak 26 が自動で付ける service_account の client scope を realm の追随の余剰と見なさない（#2208）
type: spec
status: done
related_ids:
  - NFR-09
  - ADR-0004
  - IADR-0369
  - IADR-0524
  - IADR-0606
  - IADR-0618
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004（認証基盤 Keycloak）
related_specs:
  - ./20261024_2195_eso-merge-base-secrets-keep-existing.md
issue: "#2208"
---

# 作業仕様書 — service_account の client scope の自動付与と realm の追随

## 目的と射程

稼働 PoC（#2195 の追記・2026-10-24）で、投入器が `abac-seeder` を有効→無効へ切り替える PUT のたびに、Keycloak 26 が
defaultClientScopes へ `service_account` を足した。realm の追随の `--check` は差分 1 件（`client.scope.remove abac-seeder:service_account`）で落ちた。
10/22 の secret の入れ替えの PUT でも 16 件で同じ差分が出ている。

**射程**: `deploy/local/keycloak-setup/reconcile-realm.js` の scope 割当の比較と、`scripts/check-realm-constraints.js`（realm の宣言の検査器）。
判断は IADR-0618 に置く。**射程外**: 稼働中のクラスタ（k3s・Keycloak）での確かめ（PR 本文の PoC 手順で行う）。

## 計画の確認（読み取りのみ）

- ADR-0004（認証基盤 Keycloak）: 変えない。トークンの読み手は `azp` を先に読む（IADR-0524・IADR-0606）ので、`service_account` スコープの
  写像（`client_id`・`clientHost`・`clientAddress`）が載る client と載らない client が混ざっても判定は変わらない。

## 現物の読み（案の選定の材料）

1. reconcile の scope 割当（`plan()` の 5.）は宣言が全集合で、稼働側の余剰を `client.scope.remove` にする。apply で外しても、
   サービスアカウントの client への次の PUT（投入器の切替・secret の入れ替え・**reconcile 自身の client.update**）で Keycloak が戻す。
2. realm の宣言は `clientScopes` を明示しており、`service_account` を持たない。IADR-0524 の実測では、宣言から作った realm では
   組み込みのスコープは作られず、`client_id` 等は載らない。宣言の client に `service_account` を書くと（案 A）、新規の import では
   スコープの実体が無く、reconcile は `scope がまだ無い` の deferred を出し続ける（`--check` が落ちる）。実体まで宣言すると、
   Keycloak が持つ写像を宣言が二重に所有する。
3. 検査器（check-realm-constraints.js）は宣言だけを読む。宣言の scope 割当に `service_account` を拒む規則は今は無い。

→ **案 B（名前・欄・条件を限定して、稼働側の余剰を許す）を採る。** 理由の詳細は IADR-0618。

## 母集合（規則 9。realm の宣言の走査）

`deploy/keycloak/microservices-platform-realm.json` の `clients[]` を `serviceAccountsEnabled === true` で走査した（宣言の realm はこの 1 つ）。

- 対象（20 件）: bff・abac-seeder・synthetic-monitor・retrieval-service・document-service・ingestion-service・aianalysis-service・graph-service・
  conversion-service・wiki-service・datasource-service・mcp-server・identity-admin・mcp-client-admin・reset-gate・ai-stock-trading-kb-writer・
  ai-stock-trading-kb-reader・ai-stock-trading-llm-caller・ai-stock-trading-svc・ai-stock-trading-owner
- 対象外（7 件・サービスアカウントを持たない）: wiki-js・headlamp・grafana・argocd・vault・account・account-console
- 10/22 の 16 件は、この 20 件のうち secret を入れ替えた client の部分集合である（件数は他人の数えなので転記のみ。検証は稼働の PoC で行う）。
- 宣言のどこにも `service_account` は現れない（clients の既定・任意スコープ、realm の既定、clientScopes）。検査 11 の陰性対照が固定する。
- 試験は手で並べず、宣言から同じ述語で引く（`liveWithAutoAssigned`。20 件未満なら赤）。

追随先の走査（`git grep "全集合"`・`git grep service_account`）: 境界表を持つ `deploy/local/keycloak-setup/README.md` に行を足した。
IADR-0369 の境界表・IADR-0524 の「採っていない」は凍結記録なので書き換えず、IADR-0618 から引く。

## 変更

1. reconcile-realm.js: `KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES`（`service_account` → 欄 `defaultClientScopes`・条件 宣言の `serviceAccountsEnabled: true`）
   と `isKeycloakAutoAssignedScope` を置き、scope 割当の余剰の計画で該当の組だけを飛ばす。欠落の向きは変えない。
2. check-realm-constraints.js 検査 11: 同じ定数を reconcile-realm.js から読み、宣言のどこにもそのスコープ名を書かないことを求める（所有をそろえる）。
3. README の境界表に「Keycloak（自動付与）」の行を足す。

## 受け入れ基準と試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| 陽性: 自動付与の scope を持つ模型の realm で検査が通る | `keycloak-realm-reconcile.test.js`「#2208 陽性」（20 件全部に足して計画 0 件） |
| 陰性: 宣言に無い、それ以外の余剰は違反 | 同「#2208 陰性」（abac-seeder の `email`、SA を持たない wiki-js の `service_account`、宣言が全集合として持つ optional 欄の `service_account` は remove） |
| 検査器も同じ方針 | `check-realm-constraints.js --self-test` 検査 11（実データ 0 件・5 箇所それぞれ赤・他の名前は見ない） |
| 投入器の切替の後も `--check` が exit 0 | 稼働の PoC（PR 本文の手順） |

変異: reconcile の飛ばしを外す → 陽性が赤。欄の条件を外す → 陰性が赤。検査 11 の名前集合を空にする → 自己試験が赤。いずれも確かめた。
