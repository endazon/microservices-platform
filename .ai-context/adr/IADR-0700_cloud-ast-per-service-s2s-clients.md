---
title: IADR-0700 クラウドの realm に取引ユニットのサービスごとの機密クライアント（ai-stock-trading-svc-<サービス>。宣言の ai-stock-trading-svc と同じ作り・宛先は取引ユニットの API だけ）を足し、secret は OpenBao の中で作る（在るパスには無いキーだけを足す）。稼働中の realm へは、取り込み元に在って稼働に無い機密クライアントだけを作る Job で足す
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-05, IADR-0689, IADR-0674, IADR-0693, AST:IADR-0637]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_s2s-per-service-clients.md
---

# IADR-0700: クラウドの取引ユニットのサービスごとの機密クライアント

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿）。前提: IADR-0674（クラウドの最小構成）・IADR-0689（OpenBao・ESO・realm の雛形）・IADR-0693（Keycloak の入りを
  「名前空間 かつ Pod」で名指しする）。受け手は取引ユニットの AST:IADR-0637（宛先の確かめと口ごとの呼び手の一覧）。
- 作業仕様書: `.ai-context/specs/20261107_s2s-per-service-clients.md`。

## コンテキストと課題

- クラウドの取引ユニットの 7 つのサービスは、1 つの機密クライアント（`ai-stock-trading-svc`）を共有していた。受け側が呼び手を区別できない。
- realm は `--import-realm` で取り込む。稼働中の realm は取り込み元を変えても変わらない。

## 決定

### 決定 1 — クラウドの realm にだけ、サービスごとのクライアントを足す

- `realm-for-cloud.js` の `SERVICE_CLIENTS`（7 つ）を、宣言の `ai-stock-trading-svc` とそのサービスアカウントから組む（機密・サービスアカウントだけ・標準フロー／
  直接のパスワード／暗黙なし・リダイレクトなし・サービスアカウントは `trading-service` だけ）。既定のスコープは `roles` と `ai-stock-trading-api-audience`
  （aud `ai-stock-trading-api`）だけ。基盤の共通の宛先（platform-api）は載せない。
- 宣言（ローカル・CI）には置かない（ローカルは旧の共有クライアントのまま。取引ユニットの段 Off）。宣言に同名が在る・宣言の型が崩れている・secret が無ければ組まない。
- `CLOUD_CLIENTS` は宣言の 3 つと 7 つ（seed・雛形の印・ESO の差し込みが同じ集合を使う）。

### 決定 2 — secret は OpenBao の中で作り、在るパスには無いキーだけを足す

- `seed-secrets.sh` の `perkey` は、パスが在れば無いキーだけを `kv patch` で足す（在るキーの値は読まずに残す。有無を読めなければ書かずに止める）。
  稼働中の構成（旧の 3 つだけ）へ 7 つを足せる。
- **意図して `perkey` の全体（`shared/ast-rabbitmq-users` と `shared/ast-client-secrets`）に及ぼす。** `deploy/local/rabbitmq-setup/users.txt` に取引ユニットの行が
  増えたとき、稼働中の `shared/ast-rabbitmq-users` にもその利用者のキーが足される（以前は在るパスを触らず、足りないキーは手で作るしかなかった）。
  `userpass`（`platform/*`・`shared/ast-db`・`shared/ast-db-<サービス>`）は従来どおりパスの単位で在れば触らない。
- 扱うのは「キーが無い」だけで、**在るキーの値と稼働の側（Keycloak の client の secret・ブローカの利用者のパスワード）の不一致は見ない・直さない**（対の秘密の入れ替えは別の手順）。ESO の `keycloak-realm-import` は全キーを差し込むので宣言は変わらない。取引ユニットの名前空間は、その clientId の
  プロパティだけをサービスごとの Secret へ写す（取引ユニットの chart）。

### 決定 3 — 稼働中の realm へは、無い機密クライアントだけを作る Job で足す

- `deploy/cloud/keycloak-clients/`（`keycloak-clients.sh --live check|apply`・`job.yaml`・`add-clients.js`）。取り込み元（Secret `keycloak-realm-import`）の
  機密クライアントのうち稼働に無いものだけを管理の API で作り（secret は取り込み元の値）、サービスアカウントに無いロールだけを足す。在るものの secret・設定は読まず書かない（既定のスコープとサービスアカウントのロールだけを読んで確かめる）。
  既定のスコープ・ロールが稼働に無ければ何も書かずに止める（無いスコープを Keycloak が黙って捨てると宛先が欠ける）。値は Pod の外へ出ない。
- 作ったクライアントも在るクライアントも、既定のスコープ（宛先を載せるもの）が付いているかを読んで確かめ、欠けていれば失敗にする（足しはしない。在るものは触らない）。
  流し直しで「在る」として素通りし、宛先の無いクライアントが残るのを防ぐ。直し方は、サービスごとのクライアント（この Job が足す 7 つ）なら消してから流し直す。稼働中のクライアント（旧の 3 つ）は消さず、取り込み元との差分を確かめる（消すと稼働中の取引ユニットが止まる。手順書。失敗の表示も区分を出す）。
- Keycloak の入り（`keycloak-ingress-clients` と併存の重ね）に、Job の Pod（`platform-infra` の `app=keycloak-add-clients`）の 8443 を足す。

## 結果

- 試験: `deploy-cloud.test.js`（realm の形・陰性対照・Job の形・入口・`add-clients.test.js`）、`openbao-scripts.test.sh`（実物の OpenBao で 10 のキー・在るパスへの追加）、
  `scripts.repo.test.js`（`CLOUD_CLIENTS`・雛形の印）。

## 残る制約

1. 旧の共有クライアント（`ai-stock-trading-svc`）は、取引ユニットの移行の段が終わるまで残す。外すのは別の変更（`DECLARED_CLOUD_CLIENTS` から外し、稼働の realm から消す）。
2. Job は足すだけで、消さない・直さない。稼働の realm の既存のクライアントの設定の漂流は見ない（ローカルの realm の後追いとは別）。
3. 7 つの集合は取引ユニットの values-cloud の ServiceAuth の持ち手と手で揃える（取引ユニットの描画の検査が chart の側を固定する。基盤の側は名前の集合を試験が固定する）。
