---
title: 秘密情報のローテーション手順を Runbook にする（#458 の退行防止「ローテーション手順の Runbook 化とリハーサル記録」）
issue: "#458"
type: spec
status: draft
related_ids:
  - NFR-18
  - SC-22
  - ADR-0095
  - ADR-0005
  - IADR-0096
  - IADR-0099
  - IADR-0369
  - IADR-0433
  - IADR-0456
  - IADR-0457
adr_refs:
  - IADR-0096
  - IADR-0099
  - IADR-0369
  - IADR-0433
  - IADR-0456
  - IADR-0457
author: Claude Opus 5.5 (worker)
created: 2026-09-25
updated: 2026-09-25
---

# 作業仕様書: 秘密情報のローテーション手順を Runbook にする（#458 のうち Runbook 部分）

## 起点

- issue #458 §退行防止「ローテーション手順の Runbook 化とリハーサル記録」。2026-09-09 / 2026-09-11 の監査がともに
  「シークレットのローテーション手順を主題とする Runbook は 0 本」「リハーサルは一度も実施していない（環境が要る）」と記録した。
- 計画の要件は NFR-18「認証情報・API キーを集中管理・**ローテーション**」（HashiCorp Vault）。投入の面は ADR-0095
  （Git に置けない秘密は製品の画面 SC-22 から投入）。**周期は計画に定めが無い**（`02_requirements` を「ローテーション」で
  走査して確認。本書も周期を定めない）。
- **本 PR の射程は Runbook の文書だけ**。#458 の他の残射程（コネクタ資格情報の Vault 化・リハーサルの実施）は触らない
  （`Refs #458`。閉じない）。**`docs/operations/secret-item-console-injection-runbook.md` は別主題**（画面が使えないときの
  1 項目投入）であり、同書「限界」節が「本手順は回転の手順ではない」と明記している —— 本書はその穴を埋める。

## 走査した母集合

### 軸 1: Vault に入っている秘密の全数（誤りの側＝「回すと壊れるもの」を含めて全部引く）

```
$ grep -n -E "kv put|kv patch" deploy/local/vault/eso/bootstrap.sh
（MSP の KV 28 本 ＋ AST の ai-stock-trading/app-secrets）
```

`deploy/bootstrap/sc22-secret-items.json` の分類と突き合わせた（MSP 28 本は 3 + 18 + 7 = 28 で一致）:

| 分類 | 本数 | 中身 | 回せるか（経路B・2026-09-25） |
| --- | --- | --- | --- |
| `items[]` | 6（MSP 3・AST 3） | 外部の発行元がある値（LLM API キー・SMTP・Wiki.js API キー・AST の外部 API キー / Discord・moomoo・OpenD RSA 鍵） | **回せる**（画面から） |
| `excluded[]` | 7 | データストアの資格情報（postgres・postgres-app・rabbitmq・rabbitmq-app・keycloak-admin・minio-credentials・wikijs-db） | **ストア側と同時なら回せる**（手順を書く。未実測） |
| `deferred[]` | 18 | Keycloak のクライアントシークレット（OIDC 9・s2s 9） | 🔴 **恒久的には回せない**（下の軸 2） |

### 軸 2: 回した値を元へ戻す経路（誤りの側＝「回した後に何が既定値へ戻すか」から引く）

```
$ grep -n "apply_secret" scripts/k8s-local-up.sh           # env 未指定なら dev 既定で Secret を作る
$ grep -n "kv put" deploy/local/vault/eso/bootstrap.sh     # env 未指定なら dev 既定で KV を全置換する
$ grep -n "secret" deploy/local/keycloak-setup/reconcile-realm.js   # realm JSON の client secret を稼働 realm へ当てる
$ grep -c '"secret": "' deploy/keycloak/microservices-platform-realm.json   → 24
```

- **`k8s-local-up.sh` の再実行が 3 つの経路で値を戻す**:
  1. `apply_secret`（手動作成の Secret。`postgres` / `rabbitmq` / `keycloak-admin` は `ESO=1` でも作る）
  2. `bootstrap.sh` の `vault kv put`（`items[]` のうち seed-if-absent にした 4 KV **以外**の 24 本は毎回全置換）
  3. realm の追随（`reconcile-realm.sh`）: realm JSON の `secret` を**正**として稼働 realm へ当て直す
- ⇒ `excluded[]` は **env で新しい値を渡し続ける限り**戻らない（env 名は `deploy/local/README.md`「機密情報」表）。
- ⇒ 🔴 `deferred[]` は env を渡しても **3 が realm JSON の開発用既定値へ戻す**。しかもその既定値は Git に在る
  （＝秘密として機能していない）。**Runbook には「回せない」と書き、前提の変更（realm JSON から secret を外し、
  Keycloak 側と Vault 側を対で書く経路を作る）を明記する。** 手順をでっち上げない。

### 軸 3: Vault の外にある秘密（射程の外として理由を書く）

| 対象 | 置き場 | 本書で扱わない理由 |
| --- | --- | --- |
| Vault の root トークン・unseal 鍵 | Secret `vault-dev-token`・PVC 上の init ファイル | 開発専用の既知値（`docs/security/security.md`「開発専用の平文認証情報」）。本番の Vault 運用は未配備 |
| AST の DB 利用者 `ai`・MSP の `kp` の**初期**パスワード | `deploy/local/infra/postgres.yaml` の init SQL（直書き） | init は空の PVC にしか効かない。回した後は init の値と無関係（本書の postgres-app 手順で扱う） |
| AST の RabbitMQ 接続文字列 | AST チャートの `values.yaml` に `amqp://guest:guest@…` 直書き（submodule pin `471cbf31` で実測） | 🔴 **ブローカの資格情報を回すと AST の Worker が接続できなくなる。** Runbook の rabbitmq 手順に「回さない」条件として書く |
| データソースの接続資格情報 | DocumentService / DataSourceService の DB（Vault 化は設計のみ） | データソース管理画面の更新で差し替える（Vault に無い） |
| Wiki.js 管理者パスワード | Secret `wikijs-admin`（乱数生成） | Vault に無い。Wiki.js の管理画面で変える |
| mesh の証明書・エッジの TLS 証明書 | istiod / cert-manager | 自動で更新される（計画 ADR-0023 が East-West を istiod の自動ローテーションとしている） |
| 利用者のパスワード・OTP | Keycloak | 利用者本人の操作であり運用のローテーションではない |

### 軸 4: 既存文書のローテーション記述（追随先）

```
$ grep -rn -E "ローテーション|rotation|回転" docs
docs/operations/operations.md:456 / :472          … Wiki.js API キー（compose / Helm の手順）
docs/operations/keycloak-smtp-relay-setup-runbook.md:40 … アプリパスワードの再投入（起動条件の 1 行）
docs/operations/secret-item-console-injection-runbook.md:267 … 「本手順は回転の手順ではない」
docs/security/security.md:185 / :258 / :284 / :287 … 節の注記・dev Vault の限界・データソース資格情報
（ほか FR-01 / openapi / UC-04 のデータソース資格情報の更新 API と SC-21 のアイコン。別主題）
```

- 追随: `operations.md` の秘密情報の節から本書へリンクを張る。console runbook の「限界」節の該当 bullet から本書へリンクを張る。
- 除外: `operations.md:472`（Wiki.js キーの compose / Helm 手順）は ESO を使わない経路の手順であり、そのまま残す
  （経路B の手順は本書が持つ）。`security.md` は状態の記述であり手順ではないので触らない。

## 対象範囲

- **対象**: `docs/operations/secret-rotation-runbook.md`（新設）／`docs/operations/operations.md`（リンク 1 か所）／
  `docs/operations/secret-item-console-injection-runbook.md`（「限界」節の 1 bullet にリンク）。
- **対象外**: リハーサルの実施（稼働クラスタは利用者の PoC を動かしており、本作業では触らない）。`deferred[]` を回せるように
  する実装。コネクタ資格情報の Vault 化。スクリプト・マニフェストの変更。

## 設計（Runbook の構成）

テンプレート `docs/templates/runbook_template.md` の節に従う。

1. 実行する条件（周期は計画に無い／漏洩の疑い・担当者の離任・発行元の強制失効・既定値のまま運用している値の差し替え）
2. 分類表（上の 3 分類 ＋ 射程外）と「回せるか」
3. 共通の原則（新旧の重なり＝発行 → 投入 → 反映確認 → 旧の失効／値を画面・引数に出さない／記録）
4. 手順 A: `items[]`（画面から。項目ごとの注意 —— Discord の token は再発行した瞬間に旧が失効する等）
5. 手順 B: `excluded[]`（ストアごと。**`kp` は postgres-app と wikijs-db が共有**・**rabbitmq と rabbitmq-app は同値**・
   **AST が guest:guest 直書き**・MinIO の root 変更は未実測）と、**以後の up 再実行で env を渡し続ける**こと
6. 手順 C: `deferred[]` は回せない理由と、回すための前提
7. 確認（値を出さず長さで。console runbook の「確認」節へリンク）／失敗時の分岐（KV v2 の `rollback` で直前の版へ戻す）
8. 記録（リハーサル記録の表。現時点は「未実施」）／限界

## 受け入れ基準

1. Vault の全 KV（MSP 28 ＋ AST）が分類され、分類ごとに「回せるか」と手順（または回せない理由）がある。
2. 回した値を戻す 3 経路（`apply_secret`・`bootstrap.sh`・realm の追随）が明記され、`excluded[]` は env を渡し続けることが手順にある。
3. 共有・同値の制約（`kp`・rabbitmq / rabbitmq-app・AST の直書き）が手順の前提に書かれている。
4. リハーサル記録の置き場と形式があり、未実施であることが書かれている。
5. `docs/` の表示テキストに計画 ID・IADR・仕様書名・修飾付き issue 参照を書かない（trace ブロックへ）。
   `check-trace-blocks` / `check-doc-links` / `check-doc-type-vocabulary` ほかが通る。

## 検証

- scripts/README.md の既定検査（trace ブロック・リンク・文書の型・更新日・コミット件名・必読予算・知識グラフ）
- 手順そのものは**未実測**であり、Runbook の「限界」節と「リハーサル記録」にその旨を書く。
