---
title: 作業仕様書 — クラウドの realm に取引ユニットのサービスごとの機密クライアントを足し、秘密を OpenBao の中で作り、稼働中の realm へも足す
type: spec
status: in-progress
related_ids: [NFR-18, NFR-05, IADR-0700, IADR-0689, IADR-0674, IADR-0693, AST:IADR-0637]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261105_2401_deploy-cloud-minimal.md
---

# 作業仕様書 — 取引ユニットのサービスごとの機密クライアント（クラウド）

> 着手 2026-11-07（develop `30588d467`）。判断の記録は IADR-0700。取引ユニットの側（受け側の宛先・口ごとの呼び手の一覧・chart）は AST:IADR-0637 と
> AST の作業仕様書 `20261107_s2s-per-service-clients`（呼び出しの母集合）。稼働中のクラスタには何もしない。秘密の値は扱わない。

## 起点

- 利用者の裁定（2026-11-07）「全部直してから稼働」。クラウドの取引ユニットの 7 つのサービスが 1 つのサービス間の認証のクライアント（`ai-stock-trading-svc`）を
  共有している。身元をサービスごとに分け、受け側（取引ユニット）が口ごとに呼び手を限れるようにする。

## 現状（着手前に読んだもの）

- `deploy/cloud/realm-for-cloud.js`: `CLOUD_CLIENTS` は 3 つ（svc・llm-caller・owner）。宣言（`deploy/keycloak/microservices-platform-realm.json`）から残すだけで、足さない。
- 宣言の `ai-stock-trading-svc`: 機密・サービスアカウントだけ・標準フロー／直接のパスワードなし・リダイレクトなし・既定のスコープ `roles`・`platform-api-audience`。
  サービスアカウントは `trading-service` だけ。取引ユニットの宛先のスコープ `ai-stock-trading-api-audience` は owner だけが持つ（`deploy-cloud.test.js` の
  `cloudRealmAudienceProblems`）。
- `deploy/cloud/vault/seed-secrets.sh`: `cloud/shared/ast-client-secrets` に `CLOUD_CLIENTS` ごとの secret を作る。**パスが在れば何もしない**（稼働中の構成へ
  キーを足せない）。
- `deploy/cloud/vault/eso/platform-infra.yaml`: `keycloak-realm-import` は `shared/ast-client-secrets` の全キーを雛形へ差し込む（キーが増えても宣言は変わらない）。
  取引ユニットの名前空間の ExternalSecret は取引ユニットの chart が置き、policy `ast-eso` は同じパスを読める（変更なし）。
- `deploy/cloud/infra/keycloak.yaml`: `start --import-realm`。同名の realm が在ると取り込みは黙って飛ばす → 稼働中の realm へクライアントを足す手段が要る。
- `deploy/cloud/secret-inventory.js`: 参照の母集合は `deploy/cloud` の宣言（vault/eso を除く）。新しい Job が読む Secret（`keycloak-admin`・`keycloak-realm-import`）は
  区分 2・供給元あり。

## 母集合（規則 9）

- サービスごとのクライアントの集合: 取引ユニットの values-cloud で ServiceAuth を持つ 7 つ（backtest・cost-control・information-collection・market-monitor・
  order-execution・report・trade-decision）。取引ユニットの作業仕様書が呼び手の実装から引いた表を使い、取引ユニットの描画の検査
  （`helm-service-auth-clients.test.js`）が chart と一致を固定する。
- `CLOUD_CLIENTS` を引く箇所（`grep -rn CLOUD_CLIENTS --include=*.js --include=*.sh .`）: realm-for-cloud.js・seed-secrets.sh・deploy-cloud.test.js・
  scripts.repo.test.js・openbao-scripts.test.sh（キーの集合）。README §4 の「3 つの機密クライアント」の記述（`grep -rn "3 つの機密クライアント\|3 つの client" deploy docs`）。
- Keycloak の入りの一覧を持つ宣言: `deploy/cloud/infra/networkpolicy.yaml` と併存の重ね `deploy/cloud-transition/keycloak-tls-coexist/keycloak-coexist.yaml`。

## 変更

1. realm-for-cloud.js: `SERVICE_CLIENTS`（`ai-stock-trading-svc-<サービス>`）を宣言の `ai-stock-trading-svc` と同じ作りで足す。既定のスコープは `roles` と
   `ai-stock-trading-api-audience` だけ（基盤の共通の宛先は載せない）。サービスアカウントは `trading-service` だけ。宣言に同名があれば・宣言の型が崩れていれば止める。
   `CLOUD_CLIENTS` は宣言の 3 つと 7 つ。
2. seed-secrets.sh: 在るパスには無いキーだけを足す（在るキーの値は触らない。読めなければ書かずに止める）。
3. 稼働中の realm: `deploy/cloud/keycloak-clients/`（Job・入口・手順）。取り込み元（Secret `keycloak-realm-import`）の機密クライアントのうち稼働に無いものだけを
   作り、サービスアカウントに無いロールだけを足す。既定のスコープ・ロールが稼働に無ければ何も書かずに止める。値は Pod の外へ出ない。Keycloak の入りに Job の Pod を足す。
4. 試験: deploy-cloud.test.js（形・陰性対照・Job の形・入口・add-clients.test.js）、openbao-scripts.test.sh（10 のキー・在るパスに足す）、scripts.repo.test.js。

## 受け入れ基準

- [ ] AC1: クラウドの realm に 7 つのクライアントが在り、機密・サービスアカウントだけ・標準フロー／直接のパスワード／暗黙なし・リダイレクトなし・宛先は取引ユニットの API だけ・
  サービスアカウントは trading-service だけ。旧の共有クライアントは宛先のスコープを持たない。陰性対照で各条件が赤になる。
- [ ] AC2: 雛形の印は 10 で、値を差し込むと値を渡して組んだ realm と同じ。
- [ ] AC3: seed は 10 のキーを作り、旧の 3 つだけの在るパスには 7 つを足して 3 つの値を変えない。全部在れば触らない（実物の OpenBao の試験）。
- [ ] AC4: 稼働中の realm へ足す手順は、無いものだけを作り、在るものに書かず、2 回目は何も書かない。check は読むだけ。前提が無ければ何も書かずに止まる。
- [ ] AC5: `secret-inventory.js --strict` が通る。Keycloak の入りの判定（KT1〜3）が Job の Pod を含めて通る。
- [ ] AC6: 修正を外すと試験が赤になる。

## 適用の順序

基盤の realm・seed・ESO・稼働中の realm（本件）→ 取引ユニットの image と chart（移行の段）→ 確かめ → 旧の共有クライアントを外す（別の変更）。手順は
`docs/operations/cloud-s2s-per-service-clients-runbook.md`。
