---
title: IADR-0573 共有ブローカ（RabbitMQ）にサービスごとのユーザーを持たせ（値は Vault の乱数・ESO で供給・起動器が冪等に合わせる）、ブローカへの到達をブローカを使うサービスの Pod だけに絞る。既定のユーザーの撤去は消費側の切り替えの後の段に分ける
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0027, ADR-0124, IADR-0066, IADR-0096, IADR-0099, IADR-0494, IADR-0522]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-18 シークレット管理）
related_specs:
  - ../specs/20261022_GHSA-pvmh_rabbitmq-per-service-users.md
---

# IADR-0573: 共有ブローカのサービスごとのユーザーと到達の制限（段 1）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー裁定 2026-10-22（Refs GHSA-pvmh-cvmf-pvcp。許可範囲・順序・vhost は 1 つのまま）。方式は実装が決めた。

## 起点・関連

- 起点: Refs GHSA-pvmh-cvmf-pvcp（多層防御の強化・最小権限）。
- 関連: [IADR-0096](./IADR-0096_vault-eso-secret-supply-k8s-auth.md)・IADR-0099（Vault ＋ ESO の供給）・IADR-0494（書いた KV の同期を促す）・
  [IADR-0522](./IADR-0522_session-store-valkey-auth-required.md)（Valkey の到達の制限と同じ形）。AST 側の消費は AST の chart（後の PR）。

## コンテキスト

- 共有ブローカ（`platform-infra` の RabbitMQ）は、基盤の 7 サービスと取引ユニット（AST）の 11 サービスが使う。
  資格情報はブローカ全体で 1 組（Secret `rabbitmq` の既定のユーザー）で、全サービスが同じ組で接続している。
- オーナー裁定（2026-10-22）:
  - 到達の許可範囲は「ブローカを使う AST と MSP のサービス全部」。ラベルで列挙し、それ以外は拒否する。
  - 順序は MSP → AST。MSP の PR はユーザーと権限・Vault の種と ExternalSecret・NetworkPolicy。vhost は 1 つのまま。
  - 移行の期間は既存の接続を壊さない（新ユーザーを足し、各サービスを切り替えてから既定のユーザーを無効化する。段は分けてよい）。
  - 既定のユーザーの Secret を既定値で作らない fail-closed 化は別の作業（GHSA-rh44）の担当で、本 IADR は二重に実装しない。

## 検討した選択肢

### ユーザーの作り方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. 起動器が `kubectl exec` で `rabbitmqctl` を呼んで合わせる（採用）** | 宣言（users.txt）の各ユーザーを add_user / change_password・set_permissions・set_user_tags・authenticate_user で合わせる。パスワードは標準入力 | 冪等。値はコマンドの引数に載らない（rabbitmqctl 3.13 は 3 コマンドとも標準入力から読む。上流の実装で確認）。Erlang cookie で動くので管理者の資格情報を要しない。Vault の値を回したら再実行で揃う |
| B. 定義ファイル（`load_definitions`）を起動時に読ませる | パスワードの hash を ConfigMap / Secret に置く | hash の生成を別に持つ必要があり、値を回すたびにブローカの再起動が要る（全サービスの接続が切れる） |
| C. 管理 API（15672）を Job から叩く | 管理者の資格情報を Job に渡す | 管理の口を Pod 網へ開く必要があり、到達の制限（決定 3）と相反する |

### 権限の粒度

| 案 | 評価 |
| --- | --- |
| **a. vhost `/` に `.*` `.*` `.*`（採用・段 1）** | 既存のトポロジ（Wolverine の規約ルーティングが作る exchange / queue 名）を変えずに切り替えられる。サービスごとの資格情報で、取り消しと帰属は 1 サービス単位になる |
| b. サービスごとの正規表現で configure / write / read を絞る | 名前の導出規則（キュー名がメッセージ型だけから導かれる等）を全サービス分正しく書けないと、切り替えた瞬間に宣言・購読が失敗する。段 1 では採らない（残る制約 1） |
| c. vhost を分ける | オーナー裁定で採らない（1 つのまま） |

## 決定

### 決定 1: 単一情報源は `deploy/local/rabbitmq-setup/users.txt`

- 1 行 = `<ユーザー名> <接続元の名前空間> <app ラベル> <Vault の KV>`。基盤は `msp-<name>`（helm の `messaging: true` の 7 サービス）、
  AST は `ast-<name>`（AST の chart の Worker 全 11 件）。
- 読み手は 3 つ（Vault の種・ユーザーの合わせ・NetworkPolicy）。一致は `scripts/scripts.repo.test.js` が、基盤の chart と AST の chart
  （submodule が無いときは `check-unit-service-ownership.js` の既知の一覧）に対して突き合わせる。

### 決定 2: 値は Vault の乱数。ESO で供給し、起動器が合わせる

- `bootstrap.sh` が KV `msp/rabbitmq-users` と `ai-stock-trading/rabbitmq-users`（プロパティ名 = ユーザー名）を、**無いときだけ乱数（48 桁の 16 進）で**作る。
  在る KV には無いユーザーだけを足し、在る値は触らない。開発用の既定値は持たない。値は標準入力で渡す。
- ExternalSecret `platform-infra/rabbitmq-users`（Owner・2 つの KV を dataFrom.extract）がブローカ側の写しを作る。読み手は合わせのスクリプトだけ。
- 起動器（`ESO=1`）が同期を待って `deploy/local/rabbitmq-setup/reconcile-users.sh` を回す。合わせられなければ止める。
  既定のユーザーと宣言の外のユーザーには触れない。タグは付けない（管理 UI・管理 API へ入れない）。`--check` で読むだけの確認もできる。
- 消費側は自分の名前空間の ExternalSecret で同じ KV を読む（AST は AST の chart。基盤のサービスは後の段）。

### 決定 3: 到達の制限（NetworkPolicy `rabbitmq-ingress-clients`・ingress だけ）

- 5672 へ届いてよいのは、`microservices-platform` の 7 サービスと `ai-stock-trading` の 11 サービスの Pod だけ（名前空間と app ラベルの組）。
  OpenD・Wiki.js・ラベルの無い Pod などはすべて拒否する。
- 管理の口（15672）はどこからも許さない（運用者は `kubectl port-forward`。Pod 網を通らない）。readiness は exec なので通信を要らない。egress は絞らない。
- 既存の接続（既定のユーザー）を使うサービスも全部この列挙に入るので、段 1 の時点で切れる接続は無い。

### 決定 4: 移行の段

| 段 | リポジトリ | 内容 | 既存の接続 |
| --- | --- | --- | --- |
| **1（本 IADR）** | MSP | ユーザー・権限・種・写し・NetworkPolicy | そのまま（既定のユーザーで接続し続ける） |
| 2 | AST | AST の chart をサービスごとの資格情報（ESO）へ。`guest` の既定を外す | AST の 11 サービスが切り替わる |
| 3 | MSP | 基盤の 7 サービスをサービスごとの資格情報へ。その後に既定のユーザーを無効化する（既定のユーザーの Secret の fail-closed 化は GHSA-rh44 の作業と合わせる） | 全サービスが切り替わった後にだけ無効化する |

## 結果

- 各サービスは自分だけの資格情報を持てるようになる（段 2・3 で切り替え）。1 つの値が漏れても、取り消しは 1 サービスで済む。
- ブローカへは、ブローカを使うサービスの Pod からしか届かない（NetworkPolicy を強制するクラスタで）。

## 残る制約

1. 権限は vhost 全体（`.*`）である。キュー・exchange の名前で絞るのは、サービスごとの名前の導出を固定してから（段 3 の後）。
2. NetworkPolicy の強制は稼働クラスタで実測していない（IADR-0522 残余 1 と同じ）。強制されないクラスタでは、防御はサービスごとの資格情報の 1 段になる（段 2・3 の後）。
3. `ESO` を使わない経路（手動の Secret）ではサービスごとのユーザーを作らない。その経路では既定のユーザーのまま（段 3 で扱いを決める）。
4. ブローカを使うサービスを足すときは、users.txt・NetworkPolicy・消費側の chart を同じ変更でそろえる（試験が食い違いを止める）。
