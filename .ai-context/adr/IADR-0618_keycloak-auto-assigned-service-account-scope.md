---
title: IADR-0618 Keycloak 26 がサービスアカウントの client へ自動で付ける既定スコープ service_account は宣言の外とし、realm の追随は名前・欄・条件を限定して稼働側の余剰に数えない（検査器は宣言に書くことを止める）
type: impl-adr
status: Accepted
related_ids: [NFR-09, ADR-0004, IADR-0369, IADR-0524, IADR-0606]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004（認証基盤 Keycloak）
related_specs:
  - ../specs/20261024_2208_keycloak-auto-assigned-service-account-scope.md
---

# IADR-0618: service_account の client scope の自動付与を realm の追随の余剰に数えない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2208 の是正。案 A / B の選定）

## 起点・関連

- 起点: #2208（#2195 の追記。稼働 PoC・2026-10-24）。
- 計画: ADR-0004。変えない。
- 前提: IADR-0369（宣言と実行時の境界。scope 割当は宣言が全集合）、IADR-0524（Keycloak 26 へ上げた。26 の `service_account` スコープは採っていない。
  宣言から作った realm では載らない）、IADR-0606（無人の主体の判定は `azp` と利用者名。`client_id` クレームを必須にしない）。

## コンテキストと課題

Keycloak 26 は、サービスアカウントを持つ client の作成・更新（PUT）のたびに、既定スコープへ `service_account` を足す。
投入器の有効・無効の切替（GET した表現のまま PUT しても足される）、secret の入れ替え、reconcile 自身の `client.update` がこれに当たる。
reconcile の scope 割当は宣言が全集合なので、これを余剰として `client.scope.remove` にし、`--check` が落ちる。apply で外しても次の PUT で戻る。

## 検討した選択肢

1. **案 A: 宣言の client（20 件）の既定スコープに `service_account` を含める** — 宣言は `clientScopes` を明示しており、import は組み込みの
   スコープを作らない（IADR-0524 の実測）。新規の import では割当の先が無く、reconcile が `scope がまだ無い` の deferred を出し続けて `--check` が落ちる。
   避けるにはスコープの実体（写像）も宣言する必要があり、Keycloak が持つ写像を宣言が二重に所有する。宣言を変えると 20 件全部のトークンの
   中身も変わる（IADR-0524 が「採っていない」とした 26 の既定を採ることになる）。採らない。
2. **案 B: 比較で、名前を限定して自動付与の分を許す**（採用）。
3. **apply では外し、check だけ許す** — apply と check の判定が食い違う。外しても次の PUT で戻るので、外す意味が無い。採らない。
4. **Keycloak の組み込みのスコープ名を全部許す** — 宣言に無い余剰を広く見なくなる。採らない。

## 決定

1. **境界の定数** `KEYCLOAK_AUTO_ASSIGNED_CLIENT_SCOPES`（`deploy/local/keycloak-setup/reconcile-realm.js`）を単一情報源とする。
   今の中身は `service_account` の 1 つで、**欄は `defaultClientScopes`、条件は宣言の client の `serviceAccountsEnabled === true`** に限る。
2. **reconcile**: 上の 3 つ（名前・欄・条件）がそろう**稼働側の余剰だけ**を `client.scope.remove` にしない。欠落の向きは見ない。
   それ以外の余剰（宣言に無い他のスコープ、サービスアカウントを持たない client の `service_account`、宣言が全集合として持つ任意スコープの
   `service_account`）は従来どおり外す。
3. **検査器**: `scripts/check-realm-constraints.js` の検査 11 が同じ定数を読み、宣言（clients の既定・任意スコープ、realm の既定、`clientScopes`）の
   どこにもそのスコープ名を書かないことを求める。所有は Keycloak の 1 か所にそろえる。
4. 定数へ足すときは、名前・欄・条件の 3 つで限定し、本 IADR を改める。

## 結果

- 良い: 投入器の切替・secret の入れ替えの後も `--check` が 0 件になる。宣言は変えないので、新規の import の振る舞い（IADR-0524）も変わらない。
- 悪い: PUT を受けたサービスアカウントの client だけ、トークンに `client_id`・`clientHost`・`clientAddress` が載る（載る client と載らない client が混ざる）。
  読み手は `azp` を先に読み（IADR-0524・IADR-0606）、AST のコードは読まないので、判定は変わらない。
- 悪い: `service_account` スコープの写像を宣言は管理しない（Keycloak の組み込みのまま）。

## 残余

1. 稼働での確かめ（投入器の有効・無効を切り替えた後の `reconcile-realm.sh --check` が exit 0）は、マージ後の PoC で行う（PR 本文の手順）。
2. Keycloak を上げて自動付与の対象が増えたら、`--check` の `client.scope.remove` で名指される。そのときに決定 4 で定数を改める。
