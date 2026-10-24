---
title: 作業仕様書 — 起動器が ESO（Merge）の土台の Secret を dev の値で上書きせず、投入器の前に依頼の後の同期を待つ（#2195）
type: spec
status: done
related_ids:
  - NFR-18
  - ADR-0095
  - ADR-0124
  - IADR-0099
  - IADR-0332
  - IADR-0404
  - IADR-0576
  - IADR-0616
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0124 決定 1
related_specs: []
issue: "#2195"
---

# 作業仕様書 — ESO（Merge）の土台の Secret を上書きしない

## 目的と射程

稼働 PoC（#2175 §5.4）で、ESO=1 の起動器の投入器（ABACSEED・SEARCHSEED・TAGSEED）が 3 つとも 401 になった。2 回の実行の両方で起きた。
原因は、[3/7] が `platform-infra/abac-seeder-oidc` を dev の値で apply していたことである。ESO の段の Ready の待ちは前回の同期で既に通るので、Vault の値へ戻る前に投入器が走った。

**射程**: 起動器（`scripts/k8s-local-up.sh`）の土台の置き方と、ESO の段の待ち方。判断は IADR-0616 に置く。
**射程外**: #2195 の追記（投入器の有効・無効の切り替えで、Keycloak が `service_account` の client scope を足し、realm の追随の `--check` が落ちる件）。
これは realm の宣言と追随の比較の問題で、投入器の資格情報とは仕組みが別である。10/22 の secret の入れ替えでも 16 件で同じ差分が出ており、abac-seeder に限らない。
直すには、宣言に `service_account` を含めるか、追随がこの scope を余剰と見なさないようにするかを選ぶ必要がある。どちらにしても `reconcile-realm` と realm の宣言の検査器に及ぶので、**別の issue にすべき**と判断した（報告する）。

## 計画の確認（読み取りのみ）

- ADR-0095 決定 3「ESO は読み取り専用の同期を維持する」: 変えない。force-sync の注釈は、ESO に同期し直させる合図にすぎない。
- ADR-0095 は IADR-0099 決定 1（基盤の手動 apply の保持）を前提として引いている。基盤の 3 つは対象外にしたので、矛盾しない。

## 母集合（規則 9）

`grep -ln "creationPolicy: Merge" deploy/local/vault/eso/*.yaml` で引くと 6 本だった。対応する [3/7] の手動 apply と照らした。

| ExternalSecret | [3/7] の値 | 扱い |
| --- | --- | --- |
| `abac-seeder-oidc` | env か realm の宣言の dev の値 | **対象**（#2195 の本体） |
| `reset-gate-oidc` | env か dev の値（`reset-gate-dev-secret-change-me`） | **対象**。同じ欠陥がある。入れ替え後の Vault の値と違い、門は [4/7] と ESO の段の rollout で dev の値を掴む |
| `keycloak-smtp` | env か捕捉用 MTA の既定（from / user / password は空） | **対象**。同じ欠陥がある。Vault に実のリレーを入れると、近接 MTA が既定の値で作り直される |
| `postgres`・`rabbitmq`・`keycloak-admin` | 種と同じ値（IADR-0099） | 対象外。Merge は no-op で、IADR-0099 決定 1 が保持を定めている |

MSP の名前空間の Secret（`bff-oidc` ほか）は、ESO=1 では従来どおり手で置かない（`if [ "${ESO:-}" != "1" ]`）ので、対象外である。

## 方式

IADR-0616 の決定 1・2 のとおり（`apply_eso_merge_base_secret`・`eso_force_sync_wait`）。

## 受け入れ基準

- [x] ESO=1 で 3 つの Secret が既に在るとき、起動器はそれを置き直さない。env を与えても置き直さない（`keep …` を出す）。基盤の Secret は従来どおり置く。
- [x] 新規クラスタ（Secret が無い）では、ESO=1 でも土台を作る。ESO の無い経路は、Secret が在っても従来どおり置き、同期は依頼しない。
- [x] ESO=1 では、3 つの ExternalSecret の apply の後かつ投入器の前に同期を依頼し、依頼の後の同期（`refreshTime` の変化と Ready）を確かめる。
- [x] 同期が停滞したときは、期限で warn を出して続ける（synced とは出さない）。
- [x] 変異: 判定を外す（常に置く）と、上書きが観測される。待ちを Ready の待ちへ戻すと、同期を依頼しない。どちらも赤になる。
- [x] 既存の試験を追随させた。#1793（値をファイル経由で渡すことの母集合に、新しい包みを入れる）と、#1102（keycloak-smtp の同期待ち）である。

## 検証

- `node scripts/k8s-local-up.test.js`（392 件）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `shellcheck -x scripts/k8s-local-up.sh`（変更の前後で指摘の集合が同じ）

## PoC

配備前の手順・配備後の確認は PR 本文に書く。
