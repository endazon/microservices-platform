---
title: IADR-0577 基盤の 7 サービスはブローカ（RabbitMQ）へサービスごとのユーザー（msp-<名前>）で名乗り、共通の 1 組（rabbitmq-app）を撤去する。ESO を使わない経路も同じ形で作り、ブローカの再起動の直後にユーザーを作り直し、全サービスの切り替えを確かめてから既定のユーザーを無効化する
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0027, ADR-0124, IADR-0573, IADR-0574, IADR-0291, IADR-0286, IADR-0485]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027 （メッセージングは Wolverine ＋ RabbitMQ）
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密）
related_specs:
  - ../specs/20261022_rabbitmq-msp-per-service-users.md
---

# IADR-0577: 基盤のサービスをブローカのサービスごとのユーザーへ切り替え、既定のユーザーを無効化する（GHSA-pvmh-cvmf-pvcp / GHSA-rh44-px44-p48q）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22。IADR-0573 の移行の段 3）・claude（ESO を使わない経路・再起動の扱い・無効化の順序）

## 起点・関連

- 起点: GHSA-pvmh-cvmf-pvcp（ブローカのサービスごとの資格情報）/ GHSA-rh44-px44-p48q（既定のユーザーの無効化）（多層防御の強化。全サービスで共有する資格情報を残さない・最小権限）。
- 計画: **ADR-0027**（メッセージング）・**ADR-0124 決定 1**（対になる秘密）。要求 **NFR-18**。いずれとも矛盾しない
  （ブローカの製品・トポロジは変えない。KV `msp/rabbitmq-users` は IADR-0573 のまま SC-22 の画面の対象外）。
- 前提: [IADR-0573](./IADR-0573_rabbitmq-per-service-users-and-ingress-policy.md)（サービスごとのユーザー・決定 4 の移行の段。本 IADR は段 3）・
  [IADR-0574](./IADR-0574_infra-credentials-fail-closed.md)（既定のユーザーの Secret を公知の値で作らない）・
  [IADR-0291](./IADR-0291_rabbitmq-credentials-secret-derived.md) / [IADR-0286](./IADR-0286_default-credentials-fail-fast.md)（接続文字列を Secret から組む。手動を止めたら供給元を置く）・
  [IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（対になる秘密は無いときだけ作る）。

## コンテキストと課題

- IADR-0573 の段 1（ユーザー・権限・種・写し・到達の制限）と段 2（取引ユニットの切り替え。AST 側）は済んだ。
  基盤の 7 サービス（document / datasource / conversion / ingestion / retrieval / wiki / graph）は、まだ全サービス共通の 1 組
  （Secret `rabbitmq-app`。値はブローカの既定のユーザーと同じ）で名乗っている。
- オーナー指示（2026-10-22）: 段 3 として、基盤の 7 サービスを `msp-<名前>` のユーザーへ切り替え、その後に既定のユーザーを無効化する。
  段 1 の残る制約 3（ESO を使わない経路では作らない）の扱いもここで決める。
- ブローカは永続化していない（IADR-0066 の dev 構成。再起動のたびに空から立ち、Secret の値で既定のユーザーだけを作る）。

## 検討した選択肢

### chart の形

| 案 | 評価 |
| --- | --- |
| **A. `global.messaging.{credentialsSecretName, usernamePrefix}`。ユーザー = 接頭辞 ＋ services のキー、パスワード = Secret の同名のキー（採用）** | 取引ユニットの chart（段 2）と同じ形。サービスを足すと名前が機械的に決まり、users.txt との突き合わせが字面で済む |
| B. サービスごとに `messaging.user` / `messaging.secretKey` を書く | 7 か所に同じ規則を手で書くことになり、ずれを試験でしか止められない |

旧キー（`user` / `existingSecret` / `passwordKey`）は与えられたら描画で止める（黙って無視すると、上書きのつもりの値が効かない）。

### ESO を使わない経路

| 案 | 評価 |
| --- | --- |
| **a. 起動器が `platform-infra/rabbitmq-users`（全ユーザー）と `microservices-platform/msp-rabbitmq-users`（基盤の 7）を「既存の値 ＞ 乱数」で作り、helm の前に reconcile-users.sh で合わせる（採用）** | 経路で形が変わらない（chart は 1 つの形だけを知る）。統合スタックの CI（ESO を使わない）で実際に通る。開発用の既定値を持たない |
| b. この経路だけ共通の 1 組のまま残す | 経路ごとに chart の形が分かれ、既定のユーザーを無効化できない |

### ブローカの再起動

ブローカは永続化していないので、再起動するとサービスごとのユーザーが消える（段 1 から在る性質。ESO の経路でも同じ）。

| 案 | 評価 |
| --- | --- |
| **ア. postStart で Secret `rabbitmq-users`（マウント・optional）から作り直す（採用）** | 標準入力で値を渡す・Erlang cookie で動く（管理者の資格情報が要らない）という段 1 の採用案 A と同じ手段。postStart が終わるまで readiness を測らないので、サービスはユーザーが揃ってから届く。失敗してもブローカは止めない（0 で終わる） |
| イ. 定義ファイル（`load_definitions`）で読む | 段 1 で不採用（hash の生成を別に持つ・値を回すたびに再起動）。理由は変わらない |
| ウ. 永続化する | ブローカの構成の変更であり本件の範囲を超える（処理中のメッセージの扱いも変わる） |

### 既定のユーザーの無効化の順序

1. 宣言（users.txt）の全ユーザーがブローカに在り、その値で認証できる（`reconcile-users.sh --check`）。
2. 既定のユーザーで張られている接続が 1 本も無い（切り替わっていないサービスが無い）。在れば消さない。
3. 無効化の印（ConfigMap `platform-infra/rabbitmq-default-user`・`disabled=true`）を**先に**置き、それから既定のユーザーを消す。
   以後の再起動では、起動の直後の復元が全ユーザーを揃えた後にだけ、印に従って消し直す（揃わなければ残す）。

起動器は最後の段でこの手順（`deploy/local/rabbitmq-setup/disable-default-user.sh`）を試み、確かめられなければ消さずに WARN で通す
（切り替わっていない接続を起動器が切らない）。

## 決定

### 決定 1: chart

- `global.messaging` は `host` / `port` / `credentialsSecretName`（既定 `msp-rabbitmq-users`）/ `usernamePrefix`（既定 `msp-`）。
- `messaging: true` のサービスは、ユーザー `<usernamePrefix><services のキー>`、パスワードは同名のキーを**非 optional** な secretKeyRef で読み、
  接続文字列は `$(RABBITMQ_PASSWORD)` の補間で組む（値を描画しない）。旧キーは描画で止める。
- 描画の固定は `scripts/helm-broker-per-service-users.test.js`（CI の static-checks-units に段を足す。起動条件・必須チェックは変えない）。

### 決定 2: 供給

- ESO=1: ExternalSecret `microservices-platform/msp-rabbitmq-users`（Owner・KV `msp/rabbitmq-users` を丸ごと）。ブローカ側の写し（IADR-0573）と同じ KV なので値は揃う。
- ESO を使わない経路: 上の案 a。値は端末にも引数にも出さない（ファイル経由）。
- 旧 `rabbitmq-app`（Secret・ExternalSecret・Vault の種 `msp/rabbitmq-app`・SC-22 の `excluded[]` の項目）は撤去する。
  起動器は helm の後に、ExternalSecret → Secret の順で旧 `rabbitmq-app` を片付ける。Vault に残った旧 KV は読み手が無く、触らない（消すのは運用者）。

### 決定 3: 再起動の直後の復元と、既定のユーザーの無効化

- 上の案ア と「無効化の順序」のとおり。戻し方は印の ConfigMap を消してブローカを作り直す（スクリプトの冒頭に書く）。
- 既定のユーザーの Secret（`platform-infra/rabbitmq`）は残す（ブローカの初期化に要る。IADR-0574 の fail-closed はそのまま）。

## 結果

- 基盤の 7 サービスと取引ユニットの 11 サービスが、それぞれ自分だけの資格情報で名乗る。1 つの値の取り消しは 1 サービスで済む。
- 既定のユーザーは、全サービスの切り替えを確かめた後には常時は存在しない（再起動の直後も消し直す）。

## 残る制約

1. 権限は vhost 全体（`.*`）のまま（IADR-0573 残る制約 1）。キュー・exchange の名前で絞るのは別の作業。
2. 既定のユーザーを消した後は、管理 UI へ既定のユーザーで入れない。ブローカの操作は `kubectl exec … rabbitmqctl` で行う。
3. 再起動の直後の復元と無効化は、稼働クラスタでは統合スタックの CI（ESO を使わない経路）でだけ実測する。ESO の経路は単体の模型で固定する。
4. `deploy/docker-compose.yml`（compose の開発経路）は本 IADR の対象外（k8s の経路だけを変える）。
5. 段 2 より前の版の取引ユニットが同じブローカに繋いでいる間は、無効化の手順が接続を見つけて止まる（消さない）。
6. ブローカの再起動の直後（AMQP の待受が上がってから postStart の復元が終わるまで）は、サービスごとのユーザーが揃っていない短い窓があり、その間の接続は認証に失敗する（クライアントの再接続で回復する）。無効化の後も、既定のユーザーは起動の直後に一瞬だけ在り、復元の後に消し直される。
7. 無効化の手順は、接続・ユーザーの列挙そのものが失敗したら「無い」と読まずに止める（消さない・印も置かない）。
8. 復元（postStart）はサーバと並んで root で走るので、サーバが Erlang の cookie を作るまで rabbitmqctl を呼ばず、呼ぶときは cookie の所有者として呼ぶ（先に呼ぶと root の所有で cookie を作り、権限を落としたサーバが読めずに起動に失敗する。統合スタックの実測で判明）。
9. 合わせ（reconcile-users.sh）は、rabbitmqctl へパスワードを改行で終わる 1 行で渡し、1 回の呼び出しに上限（`RABBITMQ_CTL_TIMEOUT`、既定 60 秒）を持つ。rabbitmqctl はパスワードを 1 行（改行か EOF まで）読むので、改行が無いと kubectl exec -i 越しでは EOF を待って止まり得る（ESO を使わない経路で初めて稼働のブローカに当てた統合スタックで、起動器が [5/7] で止まった実測）。
10. 🔴 パスワードを標準入力で受けるコマンド（add_user / change_password / authenticate_user）は、rabbitmqctl へ「コマンドとユーザー名」の 2 引数だけで呼ぶ（`-q` も付けない）。rabbitmqctl（3.13）の起動スクリプトは、これらのコマンドで引数がちょうど 2 つのときだけ標準入力を開き、それ以外は Erlang を `-noinput` で起こすので、読み込みが返らない（統合スタックの 3 回目で、ブローカに add_user が届かず 60 秒の上限で切れたことと、上流の起動スクリプトで確かめた）。合わせ（reconcile-users.sh）と起動の直後の復元（postStart）の両方に当てる。段 1 の合わせ（ESO の経路）も同じ呼び方だったので、本 PR で同時に直る。
