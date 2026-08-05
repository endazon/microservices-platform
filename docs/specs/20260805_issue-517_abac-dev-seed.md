---
title: 経路B の ABAC 初期投入 — 宣言的シードと冪等な opt-in 投入スクリプト
type: spec
status: done
related_ids: [FR-05, FR-09, UC-05, SC-09, ADR-0004, ADR-0036, IADR-0133]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md"
related_specs:
  - ../adr/IADR-0133_abac-dev-seed.md
  - ./20260805_issue-466_oidc-edge-flow-verification.md
  - ./20260805_issue-456_abac-attribute-combination-measurement.md
  - "../../deploy/local/abac-seed/README.md"
---

# 仕様書: 経路B の ABAC 初期投入（issue #517）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC による絞り込み）／FR-09（属性辞書・ポリシー管理）
- ユースケース（UC）: UC-05（属性・ポリシーの管理。保存前の矛盾検証を含む）
- 画面（SC）: SC-09（管理者設定。**実運用の編集経路はこちら**であり、本作業は dev の初期値のみ）
- 関連 ADR（計画）: ADR-0004（ABAC）／
  [ADR-0036](../../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md)（所有者ベース裁量制御。本作業では扱わない範囲を明記する）
- 関連する技術検討（計画）:
  [07_abac-attribute-model](../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md)（属性体系・評価モデルの正）
- 実装 ADR: [IADR-0133](../adr/IADR-0133_abac-dev-seed.md)
- 実装 issue: [#517](https://github.com/endazon/microservices-platform/issues/517)（発見）／
  [#466](https://github.com/endazon/microservices-platform/issues/466)（E2E の前提）／
  [#516](https://github.com/endazon/microservices-platform/issues/516)（必須属性の欠落）

## 目的・背景

`AuthorizationService` はポリシーが 0 件だと `AbacEvaluator` が **deny-by-default** で縮退する。
仕様どおりの挙動だが、投入経路が一度も実行されていない経路B では**認証を通しても画面が常に空**になり、
故障と区別が付かない（#466 の導線検証で実測: 実トークンで `GET /bff/documents` が `200 []`、
同時刻に `document-service` 直叩きでは文書が返る）。

本作業は **dev の初期値を宣言的に持ち、冪等に投入できるようにする**。
これにより #466（E2E）が「認証後に**結果が出る**」ことを検証できる状態になる。

## 対象範囲

- 対象:
  - `deploy/local/abac-seed/`（`attributes.json` / `policies.json` / `README.md`）の新設
  - `scripts/seed-abac-policies.js`（冪等・管理 API 経由・`--dry-run` つき）の新設
  - `scripts/k8s-local-up.sh` への opt-in ゲート `ABACSEED=1`（既定オフ）
  - `scripts/k8s-local-up.test.js` への smoke test 追加（既定オフの不在・肯定側・best-effort 構造）
  - 実機投入と、利用者別の可視件数による効果の実測
- 対象外:
  - **本番環境の初期投入**（SC-09 の運用手順。IADR-0133 §トレードオフ）
  - `owner` / `shared_with` による動的束縛（ADR-0036）— 実データに `owner` が無い（#516）
  - 必須属性の付与そのもの（#516）・issuer と手順A の扱い（#466）
  - 読み取り系 BFF が匿名を許容する件（#458）

## 設計

決定の理由は [IADR-0133](../adr/IADR-0133_abac-dev-seed.md) に記す。本節は実装の形のみ。

| 決定 | 実装 |
| --- | --- |
| 単一情報源は宣言的 JSON | `deploy/local/abac-seed/{attributes,policies}.json` |
| 投入は管理 API 経由 | `POST /authz/attributes` → `POST /authz/policies`（属性が先。ポリシー検証が辞書を参照するため） |
| 冪等 | 属性は `key`+`scope`、ポリシーは `name` で既存と突合し、無いものだけ作成（更新しない） |
| opt-in・既定オフ | `ABACSEED=1 bash scripts/k8s-local-up.sh`。既定は何も実行しない |
| 投入先は稼働中サービス | `kubectl apply` するリソースを増やさない |
| 序数比較を持ち込まない | `clearance` の各段の許可集合を `policies.json` に明示列挙 |
| `required` は false | #516 の解消前に取り込みを塞がないため |

接続は `ABAC_SEED_AUTHZ_URL` / `ABAC_SEED_KC_URL` で明示でき、未指定なら**スクリプトが一時 port-forward を
自分で張り、終了時に片付ける**（手順を利用者に押し付けない）。

## 受け入れ基準

- [x] 投入前に 0 件だった文書一覧が、投入後に認証済み利用者へ返るようになる
- [x] **投入後も「権限が無い利用者は 0 件」が保たれる**（全開放になっていない）
- [x] 再実行が冪等である（2 回目は no-op）
- [x] 既定（`ABACSEED` 未設定）では何も実行されず、挙動が変わらない
- [x] `--dry-run` で副作用なく投入内容を確認できる
- [x] smoke test（`k8s-local-up.test.js`）が既定オフ・肯定側の両方を固定している
- [x] 本番 values / chart に影響しない

## テスト方針

- **smoke test**（`scripts/k8s-local-up.test.js`・stub-on-PATH）: 既定オフでシードのトークンが
  一切現れないこと／`ABACSEED=1` で投入スクリプトが実行されること／シードを `kubectl apply` しないこと／
  投入失敗が `|| echo WARN` で握られ up 全体を止めないこと。
- **実機実行**: 投入前後で利用者別の可視件数を比較する（下記 §実測結果）。

## 計画書との差異

- 差異: あり（内容・対応: 計画 `07_abac-attribute-model` は `confidentiality` / `department` / `owner` /
  `lifecycle` を**必須**とするが、投入する属性辞書では `required` をすべて `false` にした。
  実データがこれらを備えていない（#516）ため、先に必須化すると取り込みを塞ぐ。
  **計画の誤りではなく実装側の未達**であり、#516 の解消後に `required: true` へ改める）

## 未決事項

- 既定オフのままでは、`ABACSEED` を知らない利用者は従来どおり空の画面を見る。
  既定オンへ倒すかは**セキュリティ既定値の変更**にあたるため、必要になった時点で別途決める（IADR-0133 決定 4）。

## 実測結果

**測定日**: 2026-08-05 ／ **対象**: 経路B（Rancher Desktop 内蔵 k3s）／ 文書 **2,467 件**

### 投入

```
属性辞書: 既存 0 件 / 追加 5 件
ポリシー: 既存 0 件 / 追加 4 件
（再実行）属性辞書: 既存 5 件 / 追加 0 件 ・ ポリシー: 既存 4 件 / 追加 0 件 → no-op（冪等）
```

> **［2026-08-05 追記］レビュー指摘により `clearance=public` の段を追加した**（当初の 4 本は
> `internal` から始まっており、最下段を欠いていた＝`clearance=public` の利用者は public 文書すら
> 読めなかった）。追加後もポリシー 5 本で、`poc-operator`（`clearance` 無し）と無トークンは
> **0 件のまま**であることを再実測した。

### 効果（同一データに対する利用者別の可視件数）

| 利用者 | `clearance` | 投入前 | 投入後 |
| --- | --- | --- | --- |
| `developer` | `restricted` | 0 件 | **2,467 件** |
| `poc-user` | `internal` | 0 件 | **2,467 件**（実データは全件 `internal`） |
| `poc-operator` | なし | 0 件 | **0 件** |
| 無トークン | — | 0 件 | **0 件** |

**投入は「全開放」ではない。** `clearance` を持たない利用者と無トークンは投入後も 0 件であり、
deny-by-default が保たれている。`developer` と `poc-user` の件数が同じなのは、
実データの `confidentiality` が全件 `internal` の 1 通りだからである（#456 の実測と整合）。

### #466 の導線検証との突き合わせ

投入後に `scripts/verify-oidc-edge-flow.sh` を再実行し、**PASS 14 / FAIL 0** のまま
`/bff/documents` が**実データを返す**ようになったことを確認した（投入前は `200 []`）。
