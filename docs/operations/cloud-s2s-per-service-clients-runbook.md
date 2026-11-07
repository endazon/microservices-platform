---
title: 運用 Runbook — クラウドの取引ユニットのサービスごとの機密クライアントを入れる（新しく作る・稼働中の構成へ足す・旧の共有クライアントを外す・切り戻し）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-05]
iadrs: [IADR-0700, IADR-0689, IADR-0674, IADR-0693, AST:IADR-0637]
specs: [20261107_s2s-per-service-clients]
-->

# 運用 Runbook: 取引ユニットのサービスごとの機密クライアント

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の Keycloak の realm `platform` と、OpenBao の `cloud/shared/ast-client-secrets`。
> 秘匿管理の手順（unseal・値の作成）は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)にある。
> 取引ユニットの側（受け側の宛先の確かめ・口ごとの呼び手の一覧・chart の段）は、取引ユニットのリポジトリの同名の手順書にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 秘密の値は手元へ出さない（Pod の中で作り、Pod の中で読む）。
>
> **状態を `draft` に留めている理由**: クラウドのノードで流していない。

## 何が変わるか

| 項目 | 旧 | 新 |
| --- | --- | --- |
| 取引ユニットのサービス間の認証のクライアント | 1 つ（`ai-stock-trading-svc`。7 つのサービスが共有） | サービスごとの 7 つ（`ai-stock-trading-svc-<サービス>`。backtest・cost-control・information-collection・market-monitor・order-execution・report・trade-decision） |
| 作り | — | 旧の共有クライアントと同じ（機密・サービスアカウントだけ・標準フロー／直接のパスワード／リダイレクトなし・サービスアカウントは trading-service だけ） |
| トークンの宛先 | 基盤の共通の宛先 | 取引ユニットの API（`ai-stock-trading-api`）だけ |
| secret | `cloud/shared/ast-client-secrets` の 3 つのキー | 同じパスの 10 のキー（旧の 3 つの値は変えない） |

- 旧の共有クライアントは、取引ユニットが移行の段を終えるまで残す（§段 4）。

## 新しく作る構成

README §3 の順のまま（`seed-secrets.sh` が 10 のキーを作り、realm の取り込みが 7 つのクライアントを最初から含む）。§段 2 の確かめだけを足す。

## 稼働中の構成へ足す（取引ユニットの chart より先に）

### 段 1: secret を足す

```sh
bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <age の識別子>
```

- 出力に `足した: cloud/shared/ast-client-secrets（在るキーは触らない。足したキー: …）` が出る（7 つの clientId）。在るキーの値は変わらない。
- 同じ「無いキーだけを足す」は `cloud/shared/ast-rabbitmq-users` にも及ぶ。`deploy/local/rabbitmq-setup/users.txt` に取引ユニットの行が増えていれば、
  その利用者のキーも足される（出力の `足した: cloud/shared/ast-rabbitmq-users`）。ブローカの側の利用者は従来のブローカの手順で作る。
- 在るキーの値と稼働の側（Keycloak・ブローカ）の不一致は見ない・直さない。
- ESO が `keycloak-realm-import` を作り直すのを待つ（`refreshInterval` は 1 時間。急ぐときは ExternalSecret を force-sync する。手順書の他の節と同じ）。

### 段 2: 稼働中の realm へ足す

```sh
node deploy/cloud/realm-for-cloud.js --template deploy/keycloak/microservices-platform-realm.json \
  | kubectl -n platform-infra create configmap keycloak-realm-template --from-file=platform-realm.json=/dev/stdin --dry-run=client -o yaml \
  | kubectl apply -f -                                                       # 雛形（secret を持たない。10 の印）
kubectl apply -k deploy/cloud                                               # Keycloak の入りに Job の Pod を足す
bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live check        # 無い 7 つを名指して失敗で終わる（読むだけ）
bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live apply        # 無いものだけを作り、サービスアカウントに trading-service を足す
bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live check        # 「無かったクライアント 0 件」で成功する
```

- 雛形を当てた後、`keycloak-realm-import` が 10 の secret で作り直されたことを確かめてから `apply` する（古い取り込み元には 7 つが無い。`check` が名指さない）。
- Job は在るクライアントの secret・設定を読まず書かない。既定のスコープ（宛先）とサービスアカウントのロールだけを読んで確かめる（足りないロールは足す）。既定のスコープ（`ai-stock-trading-api-audience`）か `trading-service` が稼働の realm に無ければ、何も書かずに失敗する
  （ログの理由を読む。宛先のスコープが無い realm は、前の段の取り込みの手順に戻る）。
- `… に既定のスコープ … が付いていない` で失敗したとき、Job はそのクライアントを直さない。直し方は、失敗の表示の括弧の中の区分で分ける。
  - **サービスごとのクライアント**（`ai-stock-trading-svc-<サービス>` の 7 つ。この Job が足すもの）: 管理の画面でそのクライアントを消してから `apply` を流し直す。
    作り直したクライアントの secret は取り込み元の値で、OpenBao の値と同じ。
  - 🔴 **稼働中のクライアント**（旧の共有クライアント `ai-stock-trading-svc`・`ai-stock-trading-llm-caller`・`ai-stock-trading-owner`）: **消さない**。
    消すと稼働中の取引ユニットが止まる。まず取り込み元（Secret `keycloak-realm-import`）と稼働の realm の、そのクライアントの既定のスコープの差分を確かめる。
    直すのは、realm の取り込みの手順か、基盤の管理の画面でのスコープの付け直し（オーナーの判断）。
- Keycloak の Pod の再起動は要らない。

### 段 3: 取引ユニットを入れる

取引ユニットの手順書の段 2（image と chart。移行の段）。

### 段 4: 旧の共有クライアントを外す

取引ユニットの手順書の段 3（旧の共有クライアントを受けなくした変更）の後に、別の変更で行う: `DECLARED_CLOUD_CLIENTS` から外し、稼働の realm から消し、
OpenBao のキーを消す。本書の範囲の外。

## 稼働中への影響

- 段 1・2 は既存のクライアント・トークン・Pod に触れない（足すだけ）。取引ユニットは旧の共有クライアントのまま動き続ける。

## 切り戻し

- 段 1 の後: そのままでよい（足したキーは誰も読まない）。
- 段 2 の後: 取引ユニットを入れていなければ、そのままでよい（足したクライアントは誰も使わない）。消すなら管理の画面でクライアント 7 つを消す。
- 段 3 の後: 取引ユニットの手順書の戻し方（前の版の chart。旧の共有クライアントは残っている）。
